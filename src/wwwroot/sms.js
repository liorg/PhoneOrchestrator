/* PhoneOrchestrator - inbound SMS (table sms). Debug of the whole chain:
   phone → hub → whqueue → NATS → SmsRelay → Manager.
   forward_status = what SmsRelay decided; empty = legacy POST /api/hub/sms.
   Same conventions as devices.js: no build step, no framework. */

const LIVE_MS = 5000;

const params = new URLSearchParams(location.search);
const state = {
  view: 'list',
  page: 1,
  status: params.get('status') || '',
  q: params.get('q') || '',
  device: params.get('device') || '',
  simId: params.get('sim') || '',
  phoneId: params.get('phone') || '',
  seen: new Set(),        // ids already shown - new ones get highlighted
  first: true
};

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
  const secs = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (secs < 60) return secs + 's';
  if (secs < 3600) return Math.round(secs / 60) + 'm';
  if (secs < 86400) return Math.round(secs / 3600) + 'h';
  return Math.round(secs / 86400) + 'd';
}

// b - a, in seconds with one decimal. null when either is missing.
function gap(a, b) {
  if (!a || !b) return null;
  return Math.round((new Date(b) - new Date(a)) / 100) / 10;
}
const gapTxt = (s) => s === null ? '' : (s < 0 ? s : '+' + s) + 's';

async function get(url) {
  const res = await fetch(url);
  if (res.status === 401) { location.href = '/login.html'; throw new Error('unauthenticated'); }
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.json();
}

function fail(msg) { const b = $('banner'); b.hidden = false; b.textContent = msg; }
const clearFail = () => { $('banner').hidden = true; };

/* ---------------------------------------------------------------- status */

const STATUS = {
  forwarded:  ['tag--ok',   'הועבר'],
  retrying:   ['tag--warn', 'בניסיון חוזר'],
  rejected:   ['tag--bad',  'נדחה'],
  gave_up:    ['tag--bad',  'ויתר'],
  no_phone:   ['tag--warn', 'אין טלפון'],
  no_manager: ['tag--bad',  'אין manager'],
  too_old:    ['tag--warn', 'ישן'],
};

function statusTag(m) {
  if (!m.forward_status) return '<span class="tag" title="POST /api/hub/sms (HubController)">legacy</span>';
  const [cls, txt] = STATUS[m.forward_status] || ['', m.forward_status];
  const title = m.forward_error ? ` title="${esc(m.forward_error)}"` : '';
  const n = m.forward_attempts > 1 ? ` <bdi class="nil">×${m.forward_attempts}</bdi>` : '';
  return `<span class="tag ${cls}"${title}>${esc(txt)}</span>${n}`;
}

/* ------------------------------------------------------------- list view */

function row(m) {
  const isNew = !state.first && !state.seen.has(m.id);
  const dev = m.device_id ? `${esc(m.device_id)}<span class="nil">·${m.slot ?? '?'}</span>` : '<span class="nil">—</span>';
  const delay = gap(m.received_at, m.hub_received_at);

  return `<tr class="click${isNew ? ' fresh' : ''}" data-id="${esc(m.id)}">
    <td title="${esc(when(m.received_at || m.sort_at))}">${ago(m.received_at || m.sort_at)}</td>
    <td>${dev}</td>
    <td>${cell(m.number)}</td>
    <td>${cell(m.from)}</td>
    <td class="sms-body"><bdi dir="auto">${cell(m.body)}</bdi></td>
    <td class="num">${delay === null ? '<span class="nil">—</span>' : gapTxt(delay)}</td>
    <td>${statusTag(m)}</td>
  </tr>`;
}

function scopeText() {
  const parts = [];
  if (state.simId)   parts.push('SIM ' + state.simId.slice(0, 8));
  if (state.phoneId) parts.push('טלפון ' + state.phoneId.slice(0, 8));
  return parts.length ? 'מסונן: ' + parts.join(' · ') : '';
}

async function loadList() {
  try {
    const qs = new URLSearchParams({ page: state.page });
    if (state.q)       qs.set('q', state.q);
    if (state.device)  qs.set('device', state.device);
    if (state.status)  qs.set('status', state.status);
    if (state.simId)   qs.set('simId', state.simId);
    if (state.phoneId) qs.set('phoneId', state.phoneId);

    const d = await get('/api/sms?' + qs);
    state.page = d.page || state.page;

    $('list').innerHTML = d.items.length ? `
      <div class="wrap"><table class="tbl">
        <thead><tr>
          <th>מתי</th><th>מכשיר·sim</th><th>המספר שלנו</th><th>שולח</th><th>טקסט</th>
          <th title="מהמכשיר עד ה-hub">השהייה</th><th>Manager</th>
        </tr></thead>
        <tbody>${d.items.map(row).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין SMS לפילטר הזה.</p>';

    d.items.forEach((m) => state.seen.add(m.id));
    state.first = false;

    pager('smsPager', state.page, d.pages || 1, d.total, (n) => { state.page = n; loadList(); });

    document.querySelectorAll('#list tr.click').forEach((el) => {
      el.addEventListener('click', () => openOne(el.dataset.id));
    });

    $('scope').textContent = scopeText();
    $('m-refresh').textContent = new Date().toLocaleTimeString('he-IL');
    $('pulse').removeAttribute('data-state');
    clearFail();
  } catch (e) {
    $('pulse').dataset.state = 'down';
    if (e.message !== 'unauthenticated') fail('טעינת ה-SMS נכשלה: ' + e.message);
  }
}

/* ----------------------------------------------------------- detail view */

function step(label, iso, base, detail) {
  if (!iso && !detail) return '';
  const g = gap(base, iso);
  return `<li>
    <span class="trail__when">${iso ? esc(when(iso)) : '—'}${g === null ? '' : ' · ' + gapTxt(g)}</span>
    <span class="trail__kind">${esc(label)}</span>
    ${detail ? `<pre class="trail__detail">${esc(detail)}</pre>` : ''}
  </li>`;
}

async function openOne(id) {
  state.view = 'one';
  $('view-list').hidden = true;
  $('view-one').hidden = false;
  window.scrollTo(0, 0);
  $('onebar').innerHTML = '<p class="empty">טוען…</p>';
  $('trail').innerHTML = '';

  try {
    const m = await get('/api/sms/' + id);
    $('oneId').textContent = m.msg_id || '';

    $('onebar').innerHTML = `
      <div class="hostbar__top">
        <span class="hostbar__name">${cell(m.number)}</span>
        ${statusTag(m)}
        <span class="host__ip">${cell(m.device_id)} · slot ${m.slot ?? '?'}</span>
      </div>
      <div class="host__stats">
        <span>שולח <b><bdi>${cell(m.target_phone)}</bdi></b></span>
        <span>לקוח <b><bdi>${cell(m.owner)}</bdi></b></span>
        <span>phone_id <b><bdi>${cell(m.phone_id)}</bdi></b></span>
        <span>ניסיונות <b><bdi>${cell(m.forward_attempts)}</bdi></b></span>
      </div>
      <p class="sms-full"><bdi dir="auto">${cell(m.body)}</bdi></p>
      ${m.forward_error ? `<p class="host__err">${esc(m.forward_error)}</p>` : ''}`;

    const base = m.received_at;
    const fwd = m.forward_status
      ? `${m.forward_status}${m.forward_error ? '\n' + m.forward_error : ''}`
      : 'legacy — POST /api/hub/sms (HubController). אין סטטוס העברה.';

    $('trail').innerHTML = [
      step('המפעיל שלח (sent_at)', m.delivery_date, base),
      step('הגיע למכשיר (SmsAgent)', m.received_at, base),
      step('הגיע ל-hub', m.hub_received_at, base),
      step('Manager — ' + (STATUS[m.forward_status]?.[1] || m.forward_status || 'legacy'), m.forwarded_at, base, fwd),
      step('raw (מה שה-hub שלח)', null, null, JSON.stringify(m.raw, null, 2)),
    ].join('');
    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת ה-SMS נכשלה: ' + e.message);
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

$('q').value = state.q;
$('device').value = state.device;
document.querySelectorAll('#statusChips .chip').forEach((b) => {
  b.setAttribute('aria-current', String(b.dataset.status === state.status));
  b.addEventListener('click', () => {
    state.status = b.dataset.status;
    state.page = 1;
    document.querySelectorAll('#statusChips .chip').forEach((x) => x.setAttribute('aria-current', String(x === b)));
    loadList();
  });
});

function apply() {
  state.q = $('q').value.trim();
  state.device = $('device').value.trim();
  state.page = 1;
  loadList();
}
$('apply').addEventListener('click', apply);
$('q').addEventListener('keydown', (e) => { if (e.key === 'Enter') apply(); });
$('device').addEventListener('keydown', (e) => { if (e.key === 'Enter') apply(); });
$('clear').addEventListener('click', () => {
  history.replaceState(null, '', '/sms.html');
  Object.assign(state, { q: '', device: '', status: '', simId: '', phoneId: '', page: 1 });
  $('q').value = ''; $('device').value = '';
  document.querySelectorAll('#statusChips .chip').forEach((x) => x.setAttribute('aria-current', String(x.dataset.status === '')));
  loadList();
});
$('back').addEventListener('click', openList);
$('logout').addEventListener('click', async () => {
  try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (e) {}
  location.href = '/login.html';
});

(async () => {
  try { const s = await get('/api/orchestrator/status'); $('m-marker').textContent = s.marker ?? '—'; } catch (e) {}
})();

loadList();
// live: page 1 of the list only - never yanks the user out of a detail or an older page
setInterval(() => {
  if ($('live').checked && state.view === 'list' && state.page === 1) loadList();
}, LIVE_MS);
