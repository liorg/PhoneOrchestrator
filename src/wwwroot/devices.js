/* PhoneOrchestrator - physical (rented) phones.
   Device list → SIM drill-down, with re-verify and release-from-customer.
   Same conventions as app.js: no build step, no framework. */

const REFRESH_MS = 10000;

const state = {
  view: 'devices',
  page: 1,
  deviceId: null,
  deviceRow: null,
  busy: new Set()          // sim ids with an action in flight
};

const $ = (id) => document.getElementById(id);

/* ------------------------------------------------------------- utilities */

const esc = (v) =>
  String(v).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

function ago(iso) {
  if (!iso) return '—';
  const secs = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (secs < 60) return secs + 's';
  if (secs < 3600) return Math.round(secs / 60) + 'm';
  if (secs < 86400) return Math.round(secs / 3600) + 'h';
  return Math.round(secs / 86400) + 'd';
}

// Time left until iso, as h:mm:ss. Past → 0:00:00.
function left(iso) {
  if (!iso) return '—';
  const s = Math.max(0, Math.round((new Date(iso).getTime() - Date.now()) / 1000));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
  return `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}`;
}

const cell = (v) =>
  (v === null || v === undefined || v === '') ? '<span class="nil">—</span>' : esc(v);

async function get(url) {
  const res = await fetch(url);
  if (res.status === 401) {
    location.href = '/login.html';
    throw new Error('unauthenticated');
  }
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.json();
}

// POST. 409 carries {ok:false, reason} from the RPC - returned, not thrown.
async function post(url) {
  const res = await fetch(url, { method: 'POST' });
  if (res.status === 401) {
    location.href = '/login.html';
    throw new Error('unauthenticated');
  }
  const body = await res.json().catch(() => ({}));
  if (!res.ok && res.status !== 409) throw new Error(`${res.status} ${JSON.stringify(body)}`);
  return body;
}

function fail(msg) {
  const b = $('banner');
  b.hidden = false;
  b.textContent = msg;
}
const clearFail = () => { $('banner').hidden = true; };

let noticeTimer = null;
function notice(msg) {
  const n = $('notice');
  n.hidden = false;
  n.textContent = msg;
  clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => { n.hidden = true; }, 6000);
}

const REASONS = {
  in_progress:      'כבר יש אימות פעיל — מחכים שיסתיים או יפוג',
  wa_not_installed: 'WhatsApp לא מותקן על ה-SIM',
  not_rentable:     'ה-SIM לא מקושר לטלפון',
  no_phone:         'אין טלפון ל-SIM (עדיין אין מספר)',
  already_system:   'הטלפון כבר לא אצל לקוח',
  not_rented:       'אין השכרה פעילה'
};
const reasonText = (r) => REASONS[r] || r || 'נכשל';

/* ------------------------------------------------------------------ tags */

function usbTag(d) {
  if (d.status === 'remove') return '<span class="tag tag--bad">usb נותק</span>';
  if (d.adb_state && d.adb_state !== 'device')
    return `<span class="tag tag--warn">${esc(d.adb_state)}</span>`;
  return '<span class="tag tag--ok">מחובר</span>';
}

const WA = { whatsapp: 'WhatsApp', business: 'Business' };

const PULL = {
  pending:    ['tag--warn', 'ממתין'],
  delete_old: ['tag--warn', 'נותק · מחכה לקוד'],
  ack:        ['tag--warn', 'מחכה לקוד'],
  success:    ['tag--ok',   'הצליח'],
  failed:     ['tag--bad',  'נכשל'],
  expired:    ['tag--bad',  'פג']
};

function pullTag(p) {
  if (!p) return '<span class="nil">—</span>';
  const [cls, txt] = PULL[p.status] || ['', p.status];
  const when = p.finished_at || p.created_at;
  const title = p.error ? ` title="${esc(p.error)}"` : '';
  return `<span class="tag ${cls}"${title}>${esc(txt)}</span> <bdi class="nil">${ago(when)}</bdi>`;
}

const isOpen = (p) => p && ['pending', 'delete_old', 'ack'].includes(p.status);

/* ---------------------------------------------------------- devices view */

function deviceRow(d) {
  return `<tr class="click" data-id="${esc(d.id)}">
    <td>${cell(d.device_id)}</td>
    <td>${usbTag(d)}</td>
    <td>${cell(d.model)}</td>
    <td class="num">${cell(d.android)}</td>
    <td>${cell(d.update_from)}</td>
    <td class="num">${d.verified ?? 0} / ${d.sims ?? 0}</td>
    <td class="num">${d.rented ?? 0}</td>
    <td>${d.open_pulls ? `<span class="tag tag--warn">${d.open_pulls}</span>` : '<span class="nil">—</span>'}</td>
    <td class="num">${ago(d.last_seen_at)}</td>
  </tr>`;
}

async function loadDevices() {
  try {
    const d = await get(`/api/devices?page=${state.page}`);
    state.page = d.page || state.page;   // the RPC clamps past-the-end pages

    $('devices').innerHTML = d.items.length ? `
      <div class="wrap"><table class="tbl">
        <thead><tr>
          <th>serial</th><th>usb</th><th>דגם</th><th>android</th><th>docker</th>
          <th>מאומתים / sim</th><th>מושכרים</th><th>אימות פתוח</th><th>עדכון</th>
        </tr></thead>
        <tbody>${d.items.map(deviceRow).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין מכשירים. מכשיר נרשם כשה-docker רואה אותו ב-USB.</p>';

    pager('devicePager', state.page, d.pages || 1, d.total, (n) => {
      state.page = n;
      loadDevices();
    });

    document.querySelectorAll('#devices tr.click').forEach((el) => {
      el.addEventListener('click', () => openSims(d.items.find((x) => x.id === el.dataset.id)));
    });

    if (state.view === 'sims' && state.deviceId) {
      const row = d.items.find((x) => x.id === state.deviceId);
      if (row) { state.deviceRow = row; renderDevicebar(); }
    }

    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת המכשירים נכשלה: ' + e.message);
  }
}

/* -------------------------------------------------------------- sims view */

function renderDevicebar() {
  const d = state.deviceRow;
  if (!d) return;
  $('devicebar').innerHTML = `
    <div class="hostbar__top">
      <span class="hostbar__name">${esc(d.device_id)}</span>
      ${usbTag(d)}
      <span class="host__ip">${cell(d.update_from)}</span>
    </div>
    <div class="host__stats">
      <span>דגם <b>${cell(d.model)}</b></span>
      <span>android <b>${cell(d.android)}</b></span>
      <span>מאומתים <b>${d.verified ?? 0}</b> / ${d.sims ?? 0}</span>
      <span>עדכון <b>${ago(d.last_seen_at)}</b></span>
    </div>`;
}

function ownerCell(s) {
  if (!s.owner) return '<span class="nil">—</span>';
  if (s.owner.is_system) return '<span class="tag tag--ok">זמין</span>';
  const rent = s.rent ? ` <bdi class="nil" data-left="${esc(s.rent.end_time)}">${left(s.rent.end_time)}</bdi>` : '';
  return `${esc(s.owner.email || s.owner.user_id)}${rent}`;
}

function verifiedCell(s) {
  if (!s.verified) return '<span class="tag tag--bad">לא</span>';
  return `<span class="tag tag--ok">כן</span> <bdi class="nil">${ago(s.verified_at)}</bdi>`;
}

function simRow(s) {
  const busy = state.busy.has(s.id);
  const canVerify = s.phone_id && s.wa_type && !isOpen(s.pull) && !busy;
  const canRelease = s.owner && !s.owner.is_system && !busy;

  return `<tr>
    <td class="num">${s.slot + 1}</td>
    <td>${cell(s.phone_number)}</td>
    <td>${s.wa_type ? esc(WA[s.wa_type] || s.wa_type) : '<span class="nil">לא מותקן</span>'}</td>
    <td>${verifiedCell(s)}</td>
    <td>${cell(s.linked_name)}</td>
    <td>${ownerCell(s)}</td>
    <td>${pullTag(s.pull)}</td>
    <td><div class="row">
      <button class="chip" data-act="reverify" data-sim="${esc(s.id)}" ${canVerify ? '' : 'disabled'}>אימות מחדש</button>
      <button class="chip chip--bad" data-act="release" data-sim="${esc(s.id)}"
              data-who="${esc(s.owner?.email || '')}" ${canRelease ? '' : 'disabled'}>ניתוק מהלקוח</button>
    </div></td>
  </tr>`;
}

async function loadSims() {
  if (!state.deviceId) return;
  try {
    const sims = await get(`/api/devices/${state.deviceId}/sims`);

    $('sims').innerHTML = sims.length ? `
      <div class="wrap"><table class="tbl">
        <thead><tr>
          <th>sim</th><th>מספר</th><th>whatsapp</th><th>מאומת</th><th>מכשיר מקושר</th>
          <th>לקוח</th><th>אימות אחרון</th><th></th>
        </tr></thead>
        <tbody>${sims.map(simRow).join('')}</tbody>
      </table></div>`
      : '<p class="empty">עוד לא זוהה SIM במכשיר.</p>';

    document.querySelectorAll('#sims [data-act]').forEach((b) => {
      b.addEventListener('click', () => act(b.dataset.act, b.dataset.sim, b.dataset.who));
    });

    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת ה-SIM נכשלה: ' + e.message);
  }
}

async function act(kind, simId, who) {
  if (kind === 'release' &&
      !confirm(`לנתק מהלקוח ${who || ''}?\nכל אנשי הקשר, התרחישים, התזמונים וה-SMS של הטלפון יימחקו, והוא יחזור ל-system.`))
    return;

  state.busy.add(simId);
  loadSims();
  let error = null;
  try {
    const r = await post(`/api/devices/sims/${simId}/${kind}`);
    if (r.ok) {
      if (kind === 'reverify') notice(r.created === false ? 'כבר יש בקשת אימות ממתינה' : 'בקשת אימות נשלחה — ה-docker יבצע בסבב הבא');
      else notice('הטלפון נותק מהלקוח והוחזר ל-system');
    } else {
      error = reasonText(r.reason);
    }
  } catch (e) {
    if (e.message !== 'unauthenticated') error = 'הפעולה נכשלה: ' + e.message;
  } finally {
    state.busy.delete(simId);
    await loadSims();
    await loadDevices();
    if (error) fail(error);   // after the reloads - they clear the banner
  }
}

/* ------------------------------------------------------------ navigation */

function openSims(row) {
  if (!row) return;
  state.view = 'sims';
  state.deviceId = row.id;
  state.deviceRow = row;

  $('view-devices').hidden = true;
  $('view-sims').hidden = false;
  window.scrollTo(0, 0);

  renderDevicebar();
  $('sims').innerHTML = '<p class="empty">טוען…</p>';
  loadSims();
}

function openDevices() {
  state.view = 'devices';
  state.deviceId = null;
  state.deviceRow = null;

  $('view-sims').hidden = true;
  $('view-devices').hidden = false;
  window.scrollTo(0, 0);
}

/* ---------------------------------------------------------------- paging */

function pager(elId, page, pages, total, onGo) {
  const el = $(elId);
  if (pages <= 1) {
    el.innerHTML = total ? `<span>${total}</span>` : '';
    return;
  }
  el.innerHTML = `
    <button data-go="prev" ${page <= 1 ? 'disabled' : ''}>הקודם</button>
    <span>${page} / ${pages} · ${total}</span>
    <button data-go="next" ${page >= pages ? 'disabled' : ''}>הבא</button>`;

  el.querySelector('[data-go="prev"]').onclick = () => onGo(page - 1);
  el.querySelector('[data-go="next"]').onclick = () => onGo(page + 1);
}

/* ---------------------------------------------------------------- header */

async function loadStatus() {
  const s = await get('/api/orchestrator/status');
  $('m-scan').textContent   = s.lastScanUtc ? ago(s.lastScanUtc) + ' ago' : '—';
  $('m-env').textContent    = s.env ?? '—';
  $('m-marker').textContent = s.marker ?? '—';

  const dot = $('pulse');
  if (s.lastError) dot.dataset.state = 'down';
  else if (!s.lastScanUtc) dot.dataset.state = 'stale';
  else delete dot.dataset.state;
}

/* ------------------------------------------------------------------ boot */

$('back').addEventListener('click', openDevices);

$('logout').addEventListener('click', async () => {
  try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (e) {}
  location.href = '/login.html';
});

async function tick() {
  try { await loadStatus(); } catch (e) { /* header only */ }
  await loadDevices();
  if (state.view === 'sims') await loadSims();
}

// Rent countdown - every second, without refetching.
setInterval(() => {
  document.querySelectorAll('[data-left]').forEach((el) => { el.textContent = left(el.dataset.left); });
}, 1000);

tick();
setInterval(tick, REFRESH_MS);
