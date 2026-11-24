/* PhoneOrchestrator - WhatsApp of the canary phone (the prober).
   Top: prober health from canary_control (what the runner last saw).
   Below: canary_wa_events - everything the prober posted to the runner
   (incoming, ACK, echoes, delivery statuses, connection) + every send.
   Same conventions as devices.js: no build step, no framework. */

const LIVE_MS = 5000;

const state = { view: 'list', page: 1, dir: '', q: '', seen: new Set(), first: true };

const $ = (id) => document.getElementById(id);

/* ------------------------------------------------------------- utilities */

const esc = (v) =>
  String(v).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const cell = (v) =>
  (v === null || v === undefined || v === '') ? '<span class="nil">—</span>' : esc(v);

function when(iso) {
  if (!iso) return '—';
  return new Date(iso).toLocaleString('he-IL', { dateStyle: 'short', timeStyle: 'medium' });
}

function ago(iso) {
  if (!iso) return '—';
  const secs = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  const a = Math.abs(secs);
  const t = a < 60 ? a + 's' : a < 3600 ? Math.round(a / 60) + 'm' : a < 86400 ? Math.round(a / 3600) + 'h' : Math.round(a / 86400) + 'd';
  return secs < 0 ? 'בעוד ' + t : t;
}

// 972541234567@s.whatsapp.net → +972541234567
const num = (v) => !v ? v : (/^\d{8,15}(@|$)/.test(v) ? '+' + v.split('@')[0] : v);

async function get(url) {
  const res = await fetch(url);
  if (res.status === 401) { location.href = '/login.html'; throw new Error('unauthenticated'); }
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.json();
}

function fail(msg) { const b = $('banner'); b.hidden = false; b.textContent = msg; }
const clearFail = () => { $('banner').hidden = true; };

/* ---------------------------------------------------------------- prober */

// Same checks as CanaryProber.HealthAsync - a field missing from an older image is "—", not red.
const GATES = [
  ['connected',       'conn'],
  ['token_ok',        'token'],
  ['registered',      'reg'],
  ['nats_connected',  'nats'],
  ['override_set',    'override'],
  ['redis_connected', 'redis'],
];

function gate(h, key, label) {
  const v = h ? h[key] : undefined;
  const pass = v === true ? 'true' : v === false ? 'false' : '';
  return `<span class="gate" ${pass ? `data-pass="${pass}"` : ''} title="${esc(key)}=${esc(v ?? '—')}">${esc(label)}</span>`;
}

async function loadProber() {
  try {
    const { control: c, runnerAlive } = await get('/api/canary/control');
    const h = c.prober_health || null;
    const wc = h && typeof h.webhooks_count === 'number' ? h.webhooks_count : null;

    const ready = c.prober_ready === true
      ? '<span class="tag tag--ok">prober מוכן</span>'
      : c.prober_ready === false ? '<span class="tag tag--bad">prober לא מוכן</span>'
      : '<span class="tag">prober —</span>';
    const runner = runnerAlive
      ? '<span class="tag tag--ok">runner חי</span>'
      : '<span class="tag tag--bad">runner לא עונה</span>';

    $('prober').innerHTML = `
      <div class="hostbar__top">
        <span class="hostbar__name">${cell(h?.phone_number || h?.phone || '+972507251926')}</span>
        ${ready} ${runner}
        <span class="host__ip">${cell(c.runner_mode)}</span>
      </div>
      <div class="gates">
        ${GATES.map(([k, l]) => gate(h, k, l)).join('')}
        <span class="gate" ${wc === null ? '' : `data-pass="${wc === 1}"`} title="webhooks_count=${esc(wc ?? '—')}">hooks ${wc ?? '—'}</span>
      </div>
      <div class="host__stats">
        <span>runner נראה <b><bdi>${ago(c.runner_seen_at)}</bdi></b></span>
        <span>סבב הבא <b><bdi>${ago(c.next_sweep_at)}</bdi></b></span>
        <span>תוצאה אחרונה <b><bdi>${cell(c.last_result)}</bdi></b></span>
        <span>בדיקות <b><bdi>${c.enabled ? 'פעיל' : 'כבוי'}</bdi></b></span>
      </div>
      ${h?.nats_error || h?.last_error ? `<p class="host__err">${esc(h.nats_error || h.last_error)}</p>` : ''}`;
  } catch (e) {
    if (e.message !== 'unauthenticated')
      $('prober').innerHTML = `<p class="host__err">מצב ה-prober לא נטען: ${esc(e.message)}</p>`;
  }
}

/* ------------------------------------------------------------ events list */

const DIR = {
  in:     ['tag--ok',   'נכנס'],
  out:    ['tag--warn', 'יוצא'],
  status: ['',          'סטטוס'],
  system: ['tag--bad',  'מערכת'],
};
const DELIVERY = { 1: 'sent', 2: 'delivered', 3: 'read', 4: 'played', '-1': 'failed' };

function dirTag(e) {
  const [cls, txt] = DIR[e.direction] || ['', e.direction];
  return `<span class="tag ${cls}">${esc(txt)}</span>`;
}

function what(e) {
  if (e.direction === 'status')
    return `<span class="${e.status === -1 ? 'bad-t' : ''}">${esc(DELIVERY[e.status] ?? e.status)}</span>` +
           (e.wa_message_id ? ` <span class="nil">${esc(e.wa_message_id.slice(-10))}</span>` : '');
  if (e.text) return `<bdi dir="auto">${esc(e.text)}</bdi>`;
  return cell(e.event);
}

function verdictCell(e) {
  if (!e.verdict && !e.note) return '<span class="nil">—</span>';
  const bad = ['send_failed', 'error', 'failed'].includes(e.verdict);
  const good = ['passed', 'sent', 'matched'].includes(e.verdict);
  return `<span class="${bad ? 'bad-t' : good ? 'ok-t' : ''}" title="${esc(e.note || '')}">${cell(e.verdict || e.note)}</span>`;
}

function row(e) {
  const isNew = !state.first && !state.seen.has(e.id);
  const peer = e.direction === 'out' ? num(e.wa_to) : num(e.wa_from);
  return `<tr class="click${isNew ? ' fresh' : ''}" data-id="${e.id}">
    <td title="${esc(when(e.created_at))}">${ago(e.created_at)}</td>
    <td>${dirTag(e)}</td>
    <td>${e.direction === 'out' ? '→ ' : ''}${cell(peer)}</td>
    <td class="sms-body">${what(e)}</td>
    <td>${cell(e.check_code)}</td>
    <td>${verdictCell(e)}</td>
  </tr>`;
}

async function loadList() {
  try {
    const qs = new URLSearchParams({ page: state.page });
    if (state.dir) qs.set('dir', state.dir);
    if (state.q)   qs.set('q', state.q);
    const d = await get('/api/canary/wa?' + qs);
    state.page = d.page || state.page;

    $('list').innerHTML = d.items.length ? `
      <div class="wrap"><table class="tbl">
        <thead><tr>
          <th>מתי</th><th>כיוון</th><th>מספר</th><th>תוכן</th><th>קוד</th><th>תוצאה</th>
        </tr></thead>
        <tbody>${d.items.map(row).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין אירועים. כל מה שה-prober שולח ל-runner מופיע כאן (נכנסות, ACK, סטטוסים, שליחות).</p>';

    d.items.forEach((e) => state.seen.add(e.id));
    state.first = false;

    pager('waPager', state.page, d.pages || 1, d.total, (n) => { state.page = n; loadList(); });
    document.querySelectorAll('#list tr.click').forEach((el) => el.addEventListener('click', () => openOne(el.dataset.id)));

    $('m-refresh').textContent = new Date().toLocaleTimeString('he-IL');
    $('pulse').removeAttribute('data-state');
    clearFail();
  } catch (e) {
    $('pulse').dataset.state = 'down';
    if (e.message !== 'unauthenticated') fail('טעינת האירועים נכשלה: ' + e.message);
  }
}

/* ----------------------------------------------------------- detail view */

async function openOne(id) {
  state.view = 'one';
  $('view-list').hidden = true;
  $('view-one').hidden = false;
  window.scrollTo(0, 0);
  $('onebar').innerHTML = '<p class="empty">טוען…</p>';
  $('raw').textContent = '';
  try {
    const e = await get('/api/canary/wa/' + id);
    $('oneId').textContent = '#' + e.id;
    $('onebar').innerHTML = `
      <div class="hostbar__top">
        ${dirTag(e)}
        <span class="hostbar__name">${cell(e.event)}</span>
        <span class="host__ip">${esc(when(e.created_at))}</span>
      </div>
      <div class="host__stats">
        <span>מאת <b><bdi>${cell(num(e.wa_from))}</bdi></b></span>
        <span>אל <b><bdi>${cell(num(e.wa_to))}</bdi></b></span>
        <span>קוד <b><bdi>${cell(e.check_code)}</bdi></b></span>
        <span>wamid <b><bdi>${cell(e.wa_message_id)}</bdi></b></span>
        ${e.status != null ? `<span>סטטוס <b><bdi>${esc(DELIVERY[e.status] ?? e.status)}</bdi></b></span>` : ''}
        <span>תוצאה <b><bdi>${cell(e.verdict)}</bdi></b></span>
      </div>
      ${e.text ? `<p class="sms-full"><bdi dir="auto">${esc(e.text)}</bdi></p>` : ''}
      ${e.note ? `<p class="host__err">${esc(e.note)}</p>` : ''}`;
    $('raw').textContent = JSON.stringify(e.raw, null, 2);
    clearFail();
  } catch (err) {
    if (err.message !== 'unauthenticated') fail('טעינת האירוע נכשלה: ' + err.message);
  }
}

function openList() {
  state.view = 'list';
  $('view-one').hidden = true;
  $('view-list').hidden = false;
  loadList();
}

/* ---------------------------------------------------------------- paging */

function pager(elId, page, pages, total, onGo) {
  const el = $(elId);
  if (pages <= 1) { el.innerHTML = total ? `<span>${total}</span>` : ''; return; }
  el.innerHTML = `
    <button data-go="prev" ${page <= 1 ? 'disabled' : ''}>הקודם</button>
    <span>${page} / ${pages} · ${total}</span>
    <button data-go="next" ${page >= pages ? 'disabled' : ''}>הבא</button>`;
  el.querySelector('[data-go="prev"]').onclick = () => onGo(page - 1);
  el.querySelector('[data-go="next"]').onclick = () => onGo(page + 1);
}

/* ------------------------------------------------------------------ boot */

document.querySelectorAll('#dirChips .chip').forEach((b) => b.addEventListener('click', () => {
  state.dir = b.dataset.dir;
  state.page = 1;
  document.querySelectorAll('#dirChips .chip').forEach((x) => x.setAttribute('aria-current', String(x === b)));
  loadList();
}));
function apply() { state.q = $('q').value.trim(); state.page = 1; loadList(); }
$('apply').addEventListener('click', apply);
$('q').addEventListener('keydown', (e) => { if (e.key === 'Enter') apply(); });
$('back').addEventListener('click', openList);
$('logout').addEventListener('click', async () => {
  try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (e) {}
  location.href = '/login.html';
});

loadProber();
loadList();
setInterval(() => {
  if (!$('live').checked || state.view !== 'list') return;
  loadProber();
  if (state.page === 1) loadList();
}, LIVE_MS);
