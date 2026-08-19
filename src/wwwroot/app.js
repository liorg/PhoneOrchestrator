/* PhoneOrchestrator dashboard - no build step, no framework.
   Two views: a host grid, and a per-host phone list reached by drill-down. */

const REFRESH_MS = 10000;

const state = {
  view: 'hosts',
  hostPage: 1,
  phonePage: 1,
  hostId: null,
  hostRow: null
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

const cell = (v) =>
  (v === null || v === undefined || v === '') ? '<span class="nil">—</span>' : esc(v);

async function get(url) {
  const res = await fetch(url);
  // Session expired - re-authenticate rather than leave stale numbers on screen.
  if (res.status === 401) {
    location.href = '/login.html';
    throw new Error('unauthenticated');
  }
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.json();
}

function fail(msg) {
  const b = $('banner');
  b.hidden = false;
  b.textContent = msg;
}

const clearFail = () => { $('banner').hidden = true; };

/* ---------------------------------------------------------------- gauges */

// Semicircular arc gauge drawn inline as SVG - no chart library.
// Radius 26 centred at (34,34) sweeping 180deg, so arc length is PI*26.
// The value arc is that same path clipped with stroke-dasharray.
const ARC_LEN = Math.PI * 26;

function gauge(label, sub, pct, warnAt) {
  const has = pct !== null && pct !== undefined && !isNaN(pct);
  const v = has ? Math.max(0, Math.min(100, Number(pct))) : 0;

  let tone = 'ok';
  if (has && v >= warnAt) tone = 'bad';
  else if (has && v >= warnAt - 15) tone = 'warn';

  const rad = Math.PI * (1 - v / 100);
  const nx = 34 + 21 * Math.cos(rad);
  const ny = 34 - 21 * Math.sin(rad);

  return `
    <div class="gauge" data-tone="${tone}">
      <span class="gauge__lbl">${esc(label)}</span>
      <svg viewBox="0 0 68 46" aria-hidden="true">
        <path class="gauge__track" d="M 8 34 A 26 26 0 0 1 60 34"/>
        <path class="gauge__fill" d="M 8 34 A 26 26 0 0 1 60 34"
              stroke-dasharray="${(v / 100 * ARC_LEN).toFixed(2)} ${ARC_LEN.toFixed(2)}"/>
        ${has ? `<line class="gauge__needle" x1="34" y1="34" x2="${nx.toFixed(1)}" y2="${ny.toFixed(1)}"/>
                 <circle class="gauge__hub" cx="34" cy="34" r="2.5"/>` : ''}
      </svg>
      <span class="gauge__val">${has ? v.toFixed(0) + '%' : '—'}</span>
      <span class="gauge__sub">${esc(sub)}</span>
    </div>`;
}

// Absolute figures under each dial - a percentage alone does not tell you
// whether 93% is 2GB free or 200GB free.
function gaugesFor(h) {
  const ramSub  = (h.ram_used_mb != null && h.ram_total_mb != null)
    ? `${h.ram_used_mb} / ${h.ram_total_mb} MB` : '—';
  const diskSub = (h.disk_used_gb != null && h.disk_total_gb != null)
    ? `${h.disk_used_gb} / ${h.disk_total_gb} GB` : '—';
  const cpuSub  = h.cpu_percent != null ? Number(h.cpu_percent).toFixed(2) + '% load' : '—';

  return `<div class="gauges">
    ${gauge('CPU',  cpuSub,  h.cpu_percent, 85)}
    ${gauge('RAM',  ramSub,  h.ram_pct,     85)}
    ${gauge('DISK', diskSub, h.disk_pct,    90)}
  </div>`;
}

/* ----------------------------------------------------------------- gates */

// The five gates of rpc_orch_pick_host, in order.
function gateStrip(h) {
  const p = h.probe || null;
  const gates = [
    { k: 'H', pass: h.status === 'active',                   t: 'status = active' },
    { k: 'B', pass: !!h.heartbeat_ok && (!p || p.reachable), t: 'heartbeat תקין' },
    { k: 'R', pass: h.ram_ok !== false,                      t: 'RAM מתחת לסף' },
    { k: 'C', pass: h.cpu_ok !== false,                      t: 'CPU מתחת לסף' },
    { k: 'S', pass: (h.phone_count ?? 0) < (h.max_containers ?? 0), t: 'יש מקום פנוי' }
  ];
  return '<div class="gates">' + gates.map((g) =>
    `<span class="gate" data-pass="${g.pass}" title="${esc(g.t)}">${g.k}</span>`
  ).join('') + '</div>';
}

function statusTag(h) {
  const p = h.probe;
  if (p && p.drainPending)    return '<span class="tag tag--bad">מפנה</span>';
  if (p && !p.reachable)      return `<span class="tag tag--bad">לא נענה ×${p.consecutiveFailures}</span>`;
  if (!h.eligible)            return '<span class="tag tag--warn">לא מקבל</span>';
  return '<span class="tag tag--ok">כשיר</span>';
}

/* ------------------------------------------------------------ hosts view */

function hostCard(h) {
  const p = h.probe;
  const err = p && p.error ? `<div class="host__err">${esc(p.error)}</div>` : '';

  return `
    <button class="host" data-id="${esc(h.id)}">
      <div class="host__top">
        <span class="host__name">${esc(h.host_name)}</span>
        ${statusTag(h)}
        <span class="host__ip">${cell(h.ip_address)}</span>
      </div>

      ${gaugesFor(h)}

      <div class="host__stats">
        <span>טלפונים <b>${h.phone_count ?? 0}</b> / ${h.max_containers ?? '?'}</span>
        <span>heartbeat <b>${ago(h.last_heartbeat)}</b></span>
        ${p ? `<span>rtt <b>${p.elapsedMs}ms</b></span>` : ''}
      </div>

      ${gateStrip(h)}
      ${err}
      <span class="host__go">הצג טלפונים ←</span>
    </button>`;
}

async function loadHosts() {
  try {
    const d = await get(`/api/hosts?page=${state.hostPage}`);

    $('hosts').innerHTML = d.items.length
      ? d.items.map(hostCard).join('')
      : '<p class="empty">אין שרתים רשומים.</p>';

    pager('hostPager', state.hostPage, d.pages || 1, d.total, (n) => {
      state.hostPage = n;
      loadHosts();
    });

    document.querySelectorAll('.host').forEach((el) => {
      el.addEventListener('click', () => {
        const row = d.items.find((x) => x.id === el.dataset.id);
        openPhones(row);
      });
    });

    // Keep the drill-down header fresh while it is open.
    if (state.view === 'phones' && state.hostId) {
      const row = d.items.find((x) => x.id === state.hostId);
      if (row) {
        state.hostRow = row;
        renderHostbar();
      }
    }

    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת השרתים נכשלה: ' + e.message);
  }
}

/* ----------------------------------------------------------- phones view */

function renderHostbar() {
  const h = state.hostRow;
  if (!h) return;
  $('hostbar').innerHTML = `
    <div class="hostbar__top">
      <span class="hostbar__name">${esc(h.host_name)}</span>
      ${statusTag(h)}
      <span class="host__ip">${cell(h.ip_address)}</span>
    </div>
    ${gaugesFor(h)}`;
}

function phoneRow(p) {
  const docker = p.docker_status
    ? `<span class="tag ${p.docker_status === 'running' ? 'tag--ok' : 'tag--warn'}">${esc(p.docker_status)}</span>`
    : '<span class="nil">—</span>';

  return `<tr>
    <td>${cell(p.number)}</td>
    <td>${cell(p.label)}</td>
    <td>${cell(p.status)}</td>
    <td>${docker}</td>
    <td>${cell(p.container_name)}</td>
    <td class="num">${cell(p.api_port)}</td>
    <td class="num">${cell(p.auth_revision)}</td>
    <td>${p.has_creds ? '<span class="tag tag--ok">yes</span>' : '<span class="nil">no</span>'}</td>
    <td class="num">${p.last_health_check ? ago(p.last_health_check) : '<span class="nil">—</span>'}</td>
  </tr>`;
}

async function loadPhones() {
  if (!state.hostId) return;
  try {
    const d = await get(`/api/hosts/${state.hostId}/phones?page=${state.phonePage}`);

    $('phones').innerHTML = d.items.length ? `
      <div class="wrap"><table class="tbl">
        <thead><tr>
          <th>מספר</th><th>שם</th><th>status</th><th>docker</th>
          <th>container</th><th>port</th><th>rev</th><th>creds</th><th>health</th>
        </tr></thead>
        <tbody>${d.items.map(phoneRow).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין טלפונים על השרת הזה.</p>';

    pager('phonePager', state.phonePage, d.pages || 1, d.total, (n) => {
      state.phonePage = n;
      loadPhones();
    });

    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת הטלפונים נכשלה: ' + e.message);
  }
}

/* ------------------------------------------------------------ navigation */

function openPhones(row) {
  if (!row) return;
  state.view = 'phones';
  state.hostId = row.id;
  state.hostRow = row;
  state.phonePage = 1;

  $('view-hosts').hidden = true;
  $('view-phones').hidden = false;
  window.scrollTo(0, 0);

  renderHostbar();
  $('phones').innerHTML = '<p class="empty">טוען…</p>';
  loadPhones();
}

function openHosts() {
  state.view = 'hosts';
  state.hostId = null;
  state.hostRow = null;

  $('view-phones').hidden = true;
  $('view-hosts').hidden = false;
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
  $('m-count').textContent  = s.scanCount ?? '—';
  $('m-env').textContent    = s.env ?? '—';
  $('m-marker').textContent = s.marker ?? '—';

  const pill = $('m-auto');
  if (s.inGrace) {
    pill.textContent = 'grace';
    pill.dataset.on = 'false';
  } else {
    pill.textContent = s.autoDrain ? 'auto-drain on' : 'auto-drain off';
    pill.dataset.on = String(!!s.autoDrain);
  }

  const dot = $('pulse');
  if (s.lastError) dot.dataset.state = 'down';
  else if (!s.lastScanUtc) dot.dataset.state = 'stale';
  else delete dot.dataset.state;

  if (s.lastError) fail('הסריקה האחרונה נכשלה: ' + s.lastError);
}

/* ------------------------------------------------------------------ boot */

$('back').addEventListener('click', openHosts);

$('logout').addEventListener('click', async () => {
  try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (e) {}
  location.href = '/login.html';
});

async function tick() {
  try { await loadStatus(); } catch (e) { /* banner already set */ }
  await loadHosts();
  if (state.view === 'phones') await loadPhones();
}

tick();
setInterval(tick, REFRESH_MS);
