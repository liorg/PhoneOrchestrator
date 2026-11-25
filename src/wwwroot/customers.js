/* Customers & debts: paged list (biggest debt first) → customer detail with months, payments,
   plan change and a manual payment. Balance = charges − paid payments. */

const state = { page: 1, userId: null, plans: [] };

const STATUS = { paid: ['ok', 'שולם'], partial: ['warn', 'חלקי'], open: ['bad', 'פתוח'] };
const monthLabel = (p) => new Date(p + 'T00:00:00').toLocaleDateString('he-IL', { month: 'long', year: 'numeric' });

const usage = (used, limit) => `${used ?? 0}${limit == null ? ' / ∞' : ' / ' + limit}`;

/* ------------------------------------------------------------------ list */

function custRow(c) {
  const plan = state.plans.find((p) => p.id === c.plan_id);
  const debt = Number(c.balance) > 0;
  return `<tr class="click" data-id="${esc(c.user_id)}">
    <td>${cell(c.email)}</td>
    <td>${cell(c.name)}</td>
    <td><code>${esc(c.plan_id)}</code></td>
    <td class="num">${usage(c.phones, plan?.phones)}</td>
    <td class="num">${usage(c.bots, plan?.bots)}</td>
    <td class="num">${usage(c.schedules, plan?.schedules)}</td>
    <td class="num ${debt ? 'bad-t' : ''}">${money(Math.max(0, c.balance))}</td>
    <td>${when(c.last_paid_at)}</td>
    <td>${when(c.joined_at)}</td>
  </tr>`;
}

async function loadList() {
  try {
    const qs = new URLSearchParams({ page: state.page });
    if ($('q').value.trim()) qs.set('q', $('q').value.trim());
    if ($('plan').value) qs.set('plan', $('plan').value);
    if ($('debt').checked) qs.set('debt', 'true');

    const d = await get('/api/admin/customers?' + qs);

    $('list').innerHTML = d.items.length ? `
      <div class="wrap"><table class="tbl">
        <thead><tr>
          <th>אימייל</th><th>שם</th><th>תוכנית</th><th>טלפונים</th><th>בוטים</th>
          <th>תזמונים</th><th>חוב</th><th>תשלום אחרון</th><th>הצטרף</th>
        </tr></thead>
        <tbody>${d.items.map(custRow).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין לקוחות תואמים.</p>';

    $('summary').textContent = `${d.total} לקוחות · סה"כ חוב פתוח ${money(d.debt_total)}`;
    pager('pager', d.page, d.pages, d.total, (n) => { state.page = n; loadList(); });

    document.querySelectorAll('#list .click').forEach((el) =>
      el.addEventListener('click', () => openDetail(el.dataset.id)));
    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת הלקוחות נכשלה: ' + e.message);
  }
}

/* ---------------------------------------------------------------- detail */

function monthsTable(months) {
  if (!months.length) return '<p class="empty">אין חיובים.</p>';
  return `<div class="wrap"><table class="tbl">
    <thead><tr><th>חודש</th><th>תוכנית</th><th>השכרת טלפונים</th><th>סה"כ</th><th>סטטוס</th><th>פירוט</th></tr></thead>
    <tbody>${months.map((m) => {
      const [tone, label] = STATUS[m.status] || STATUS.open;
      const items = (m.items || []).map((i) =>
        `${i.kind === 'plan' ? 'תוכנית ' + esc(i.description) : 'השכרה ' + esc(i.description)}: ${money(i.amount)}`).join('<br>');
      return `<tr>
        <td>${monthLabel(m.period)}</td>
        <td class="num">${money(m.plan)}</td>
        <td class="num">${money(m.rent)}</td>
        <td class="num"><b>${money(m.total)}</b></td>
        <td><span class="tag tag--${tone}">${label}</span></td>
        <td class="hint">${items}</td>
      </tr>`;
    }).join('')}</tbody>
  </table></div>`;
}

function paymentsTable(payments) {
  if (!payments.length) return '<p class="empty">אין תשלומים.</p>';
  return `<div class="wrap"><table class="tbl">
    <thead><tr><th>תאריך</th><th>סכום</th><th>אמצעי</th><th>סטטוס</th></tr></thead>
    <tbody>${payments.map((p) => `<tr>
      <td>${when(p.paid_at || p.created_at)}</td>
      <td class="num">${money(p.amount)}</td>
      <td>${esc(p.provider || '')}</td>
      <td><span class="tag tag--${p.status === 'paid' ? 'ok' : 'bad'}">${esc(p.status)}</span></td>
    </tr>`).join('')}</tbody>
  </table></div>`;
}

const SOURCE = { default: 'ברירת מחדל', dialog: 'הלקוח', payment: 'תשלום' };

function planHistoryTable(rows) {
  if (!rows.length) return '<p class="empty">אין שינויים.</p>';
  return `<div class="wrap"><table class="tbl">
    <thead><tr><th>מתאריך</th><th>תוכנית</th><th>מחיר היום</th><th>על ידי</th></tr></thead>
    <tbody>${rows.map((h, i) => `<tr>
      <td>${when(h.from_at)}${i === 0 ? ' <span class="tag tag--ok">נוכחית</span>' : ''}</td>
      <td><code>${esc(h.plan_id)}</code></td>
      <td class="num">${h.price == null ? '—' : money(h.price)}</td>
      <td>${esc(SOURCE[h.source] || h.source || '')}</td>
    </tr>`).join('')}</tbody>
  </table></div>`;
}

function rentalsTable(rows) {
  if (!rows.length) return '<p class="empty">אין השכרות.</p>';
  return `<div class="wrap"><table class="tbl">
    <thead><tr><th>טלפון</th><th>התחלה</th><th>סיום</th><th>סכום</th><th>סטטוס</th></tr></thead>
    <tbody>${rows.map((r) => `<tr>
      <td dir="ltr">${cell(r.number)}</td>
      <td>${when(r.start_time)}</td>
      <td>${when(r.ended_at || r.end_time)}${r.end_reason ? ` <span class="hint">${esc(r.end_reason)}</span>` : ''}</td>
      <td class="num">${r.amount == null ? '—' : money(r.amount)}</td>
      <td>${cell(r.status)}</td>
    </tr>`).join('')}</tbody>
  </table></div>`;
}

async function openDetail(userId) {
  state.userId = userId;
  show('detail');
  $('detail').innerHTML = '<p class="empty">מחשב חיובים וטוען…</p>';
  try {
    const d = await get(`/api/admin/customers/${userId}`);
    const u = d.user, us = d.usage, b = d.billing;
    const lim = us.limits || {}, use = us.usage || {}, cd = us.cooldown || {};
    const due = Number(b.balance) > 0;

    $('detail').innerHTML = `
      <div class="hostbar"><div class="hostbar__top">
        <span class="hostbar__name">${esc(u.email || '')}</span>
        ${u.name ? `<span>${esc(u.name)}</span>` : ''}
        <span class="tag tag--${due ? 'bad' : 'ok'}">${due ? 'חוב ' + money(b.balance) : 'אין חוב'}</span>
        <span class="host__ip">הצטרף ${when(u.joined_at)} · כניסה אחרונה ${when(u.last_sign_in_at)}</span>
      </div></div>

      <div class="grid2">
        <div class="box">
          <h3>תוכנית</h3>
          <div class="inline">
            <select id="dPlan">${state.plans.map((p) =>
              `<option value="${esc(p.id)}" ${p.id === us.plan.id ? 'selected' : ''}>${esc(p.id)}${p.price != null ? ' · ' + money(p.price) : ''}</option>`).join('')}</select>
            <button class="back" id="dPlanSave">שנה תוכנית</button>
          </div>
          <p class="help">השינוי מיידי. החודש הנוכחי מחויב לפי התוכנית היקרה ביותר שהייתה פעילה בו.</p>
          <div>טלפונים ${usage(use.phones, lim.phones)} · בוטים ${usage(use.bots, lim.bots)}
            · תזמונים ${usage(use.schedules, lim.schedules)}</div>
          ${cd.cooling ? `<div class="hint">${cd.cooling} מקומות בוטים בהמתנה עד ${when(cd.next_free_at)}</div>` : ''}
        </div>

        <div class="box">
          <h3>יתרה</h3>
          <div class="big ${due ? 'bad-t' : ''}">${money(Math.max(0, b.balance))}</div>
          <div class="hint">חויב ${money(b.charged)} · שולם ${money(b.paid)}</div>
          <div class="hint">${d.method ? `כרטיס שמור ••${esc(d.method.last4 || '')} (${esc(d.method.exp_month || '')}/${esc(d.method.exp_year || '')})` : 'אין כרטיס שמור'}</div>
        </div>

        <div class="box">
          <h3>רישום תשלום ידני</h3>
          <div class="inline">
            <input type="number" id="dAmount" step="0.01" placeholder="סכום" style="width:110px">
            <input type="text" id="dNote" placeholder="הערה (העברה בנקאית, מזומן…)">
            <button class="back" id="dPay">רשום</button>
          </div>
          <p class="help">העברה בנקאית, מזומן או מחיקת חוב. סכום שלילי מבטל תשלום שנרשם בטעות.</p>
        </div>
      </div>

      <div class="grid2">
        <div><h3>היסטוריית תוכניות</h3>${planHistoryTable(d.plan_history || [])}</div>
        <div><h3>השכרות</h3>${rentalsTable(d.rentals || [])}</div>
      </div>

      <h3>חיובים לפי חודש</h3>
      ${monthsTable(b.months || [])}
      <h3>תשלומים</h3>
      ${paymentsTable(b.payments || [])}`;

    $('dPlanSave').onclick = async () => {
      const planId = $('dPlan').value;
      if (!confirm(`להחליף ל-${planId}?`)) return;
      try { await api('PUT', `/api/admin/customers/${userId}/plan`, { planId }); note('התוכנית עודכנה'); openDetail(userId); }
      catch (e) { fail('החלפת תוכנית נכשלה: ' + e.message); }
    };
    $('dPay').onclick = async () => {
      const amount = Number($('dAmount').value);
      if (!amount) { fail('סכום חסר'); return; }
      if (!confirm(`לרשום תשלום של ${money(amount)}?`)) return;
      try {
        await api('POST', `/api/admin/customers/${userId}/payments`, { amount, note: $('dNote').value.trim() || null });
        note('התשלום נרשם'); openDetail(userId);
      } catch (e) { fail('רישום התשלום נכשל: ' + e.message); }
    };
    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת הלקוח נכשלה: ' + e.message);
  }
}

/* ------------------------------------------------------------ navigation */

function show(view) {
  $('view-list').hidden = view !== 'list';
  $('view-detail').hidden = view !== 'detail';
  window.scrollTo(0, 0);
}

$('backList').onclick = () => { show('list'); loadList(); };
$('q').addEventListener('input', debounce(() => { state.page = 1; loadList(); }));
$('plan').onchange = () => { state.page = 1; loadList(); };
$('debt').onchange = () => { state.page = 1; loadList(); };
$('accrue').onclick = async () => {
  $('accrue').disabled = true;
  try { const r = await api('POST', '/api/admin/billing/accrue'); note(`חושבו חיובים ל-${r.customers} לקוחות`); loadList(); }
  catch (e) { fail('חישוב החיובים נכשל: ' + e.message); }
  finally { $('accrue').disabled = false; }
};

(async () => {
  try {
    state.plans = await get('/api/admin/plans');
    $('plan').innerHTML += state.plans.map((p) => `<option value="${esc(p.id)}">${esc(p.id)}</option>`).join('');
  } catch (e) { /* the list still loads */ }
  loadList();
})();
