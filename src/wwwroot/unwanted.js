/* Opt-outs (contact_optouts): who is blocked platform-wide, and a manual unblock. */

const state = { page: 1, status: 'active' };

const SOURCES = { stop: ['bad', 'STOP'], link: ['warn', 'קישור'], manual: ['warn', 'ידני'] };

function row(o) {
  const [tone, label] = SOURCES[o.source] || ['warn', o.source];
  const status = o.is_active
    ? '<span class="tag tag--bad">חסום</span>'
    : `<span class="tag tag--ok">בוטל</span> <span class="hint">${when(o.revoked_at)}${o.revoked_actor ? ' · ' + esc(o.revoked_actor) : ''}</span>`;
  return `<tr>
    <td data-label="מספר" class="nw"><b dir="ltr">+${esc(o.number)}</b> <span class="hint">${esc(o.ref)}</span></td>
    <td data-label="מקור"><span class="tag tag--${tone}">${label}</span></td>
    <td data-label="לקוח">${cell(o.email)}</td>
    <td data-label="טלפון" dir="ltr">${cell(o.phone_number)}</td>
    <td data-label="הודעה" class="sms-body" title="${esc(o.stop_text || '')}">${cell(o.stop_text)}</td>
    <td data-label="אנשי קשר" class="num">${o.contacts ?? '—'}</td>
    <td data-label="מתי" class="nw">${when(o.created_at)}</td>
    <td data-label="סטטוס">${status}</td>
    <td class="act">${o.is_active
      ? `<button class="chip chip--bad" data-revoke="${esc(o.number)}">בטל חסימה</button>` : ''}</td>
  </tr>`;
}

async function load() {
  try {
    const qs = new URLSearchParams({ page: state.page, status: state.status });
    const q = $('q').value.trim();
    if (q) qs.set('q', q);
    const d = await get('/api/admin/optouts?' + qs);
    $('list').innerHTML = d.items.length ? `
      <div class="wrap"><table class="tbl tbl--cards">
        <thead><tr><th>מספר</th><th>מקור</th><th>לקוח</th><th>טלפון</th><th>הודעה</th>
          <th>אנשי קשר</th><th>מתי</th><th>סטטוס</th><th></th></tr></thead>
        <tbody>${d.items.map(row).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין מספרים תואמים.</p>';
    $('summary').textContent = `חסומים כרגע: ${d.active_total ?? 0}`;
    pager('pager', d.page, d.pages, d.total, (n) => { state.page = n; load(); });
    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת החסומים נכשלה: ' + e.message);
  }
}

async function revoke(btn) {
  const number = btn.dataset.revoke;
  if (!confirm(`לבטל את החסימה של +${number}?\nאנשי הקשר יחזרו כ-inactive ואפשר יהיה לשלוח אליהם שוב.`)) return;
  btn.disabled = true;
  try {
    const r = await api('POST', '/api/admin/optouts/revoke', { number });
    note(`החסימה בוטלה: +${number} (${r.ref})`);
    load();
  } catch (e) {
    btn.disabled = false;
    if (e.message !== 'unauthenticated') fail('ביטול החסימה נכשל: ' + e.message);
  }
}

/* ----------------------------------------------------------------- boot */

$('list').addEventListener('click', (e) => {
  const btn = e.target.closest('[data-revoke]');
  if (btn) revoke(btn);
});
document.querySelectorAll('.chip[data-status]').forEach((chip) => {
  chip.addEventListener('click', () => {
    document.querySelectorAll('.chip[data-status]').forEach((c) => c.setAttribute('aria-current', 'false'));
    chip.setAttribute('aria-current', 'true');
    state.status = chip.dataset.status;
    state.page = 1;
    load();
  });
});
$('q').addEventListener('input', debounce(() => { state.page = 1; load(); }));

load();
