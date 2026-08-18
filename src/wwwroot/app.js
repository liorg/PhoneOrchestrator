/* PhoneOrchestrator dashboard - no build step, no framework. */

const REFRESH_MS = 10000;

const state = {
  hostPage: 1,
  hostPages: 1,
  phonePage: 1,
  phonePages: 1,
  selectedHost: null,
  selectedName: ''
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

function cell(v) {
  if (v === null || v === undefined || v === '') return '<span class="nil">—</span>';
  return esc(v);
}

async function get(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.json();
}

function fail(msg) {
  const b = $('banner');
  b.hidden = false;
  b.textContent = msg;
}

function clearFail() { $('banner').hidden = true; }

/* ---------------------------------------------------------------- header */

async function loadStatus() {
  const s = await get('/api/orchestrator/status');

  $('m-scan').textContent   = s.lastScanUtc ? ago(s.lastScanUtc) + ' ago' : '—';
  $('m-count').textContent  = s.scanCount ?? '—';
  $('m-env').textContent    = s.env ?? '—';
  $('m-marker').textContent = s.marker ?? '—';

  const pill = $('m-auto');
  pill.textContent = s.autoDrain ? 'auto-drain on' : 'auto-drain off';
  pill.dataset.on  = String(!!s.autoDrain);

  const dot = $('pulse');
  if (s.lastError) dot.dataset.state = 'down';
  else if (!s.lastScanUtc) dot.dataset.state = 'stale';
  else delete dot.dataset.state;

  if (s.lastError) fail('הסריקה האחרונה נכשלה: ' + s.lastError);
  else clearFail();
}

/* ----------------------------------------------------------------- hosts */

// The five gates, in the order they appear in rpc_orch_pick_host.
function gateStrip(h) {
  const p = h.probe || null;
  const gates = [
    { k: 'H', pass: h.status === 'active',                     t: 'status = active' },
    { k: 'B', pass: !!h.heartbeat_ok && (!p || p.reachable),   t: 'heartbeat תקין' },
    { k: 'R', pass: h.ram_ok !== false,                        t: 'RAM מתחת לסף' },
    { k: 'C', pass: h.cpu_ok !== false,                        t: 'CPU מתחת לסף' },
    { k: 'S', pass: (h.phone_count ?? 0) < (h.max_containers ?? 0), t: 'יש מקום פנוי' }
  ];
  return `<div class="gates">` + gates.map((g) =>
    `<span class="gate" data-pass="${g.pass}" title="${esc(g.t)}">${g.k}</span>`
  ).join('') + `</div>`;
}

function hostCard(h) {
  const p = h.probe;
  let tag = '<span class="tag tag--ok">כשיר</span>';
  if (p && p.drainPending)      tag = '<span class="tag tag--bad">מפנה</span>';
  else if (p && !p.reachable)   tag = `<span class="tag tag--bad">לא נענה ×${p.consecutiveFailures}</span>`;
  else if (!h.eligible)         tag = '<span class="tag tag--warn">לא מקבל</span>';

  const ram = h.ram_pct == null ? '—' : h.ram_pct + '%';
  const cpu = h.cpu_percent == null ? '—' : Number(h.cpu_percent).toFixed(1) + '%';
  const err = p && p.error
    ? `<div class="host__err">${esc(p.error)}</div>` : '';

  return `
    <button class="host" data-id="${esc(h.id)}" data-name="${esc(h.host_name)}"
            aria-current="${state.selectedHost === h.id}">
      <div class="host__top">
        <span class="host__name">${esc(h.host_name)}</span>
        ${tag}
        <span class="host__ip">${cell(h.ip_address)}</span>
      </div>
      <div class="host__stats">
        <span>phones <b>${h.phone_count ?? 0}</b>/${h.max_containers ?? '?'}</span>
        <span>cpu <b>${cpu}</b></span>
        <span>ram <b>${ram}</b></span>
        <span>hb <b>${ago(h.last_heartbeat)}</b></span>
        ${p ? `<span>rtt <b>${p.elapsedMs}ms</b></span>` : ''}
      </div>
      ${gateStrip(h)}
      ${err}
    </button>`;
}

async function loadHosts() {
  try {
    const d = await get(`/api/hosts?page=${state.hostPage}`);
    state.hostPages = d.pages || 1;

    $('hosts').innerHTML = d.items.length
      ? d.items.map(hostCard).join('')
      : '<p class="empty">אין שרתים רשומים.</p>';

    pager('hostPager', state.hostPage, state.hostPages, d.total, (n) => {
      state.hostPage = n;
      loadHosts();
    });

    document.querySelectorAll('.host').forEach((el) => {
      el.addEventListener('click', () => selectHost(el.dataset.id, el.dataset.name));
    });
  } catch (e) {
    fail('טעינת השרתים נכשלה: ' + e.message);
  }
}

/* ---------------------------------------------------------------- phones */

function selectHost(id, name) {
  state.selectedHost = id;
  state.selectedName = name;
  state.phonePage = 1;
  document.querySelectorAll('.host').forEach((el) => {
    el.setAttribute('aria-current', String(el.dataset.id === id));
  });
  $('h-phones').textContent = `טלפונים · ${name}`;
  loadPhones();
}

function phoneRow(p) {
  const dockerClass =
    p.docker_status === 'running' ? 'tag tag--ok'
      : p.docker_status ? 'tag tag--warn' : 'nil';
  const docker = p.docker_status
    ? `<span class="${dockerClass}">${esc(p.docker_status)}</span>`
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
  if (!state.selectedHost) return;
  try {
    const d = await get(`/api/hosts/${state.selectedHost}/phones?page=${state.phonePage}`);
    state.phonePages = d.pages || 1;

    $('phones').innerHTML = d.items.length ? `
      <div class="wrap"><table class="tbl">
        <thead><tr>
          <th>מספר</th><th>שם</th><th>status</th><th>docker</th>
          <th>container</th><th>port</th><th>rev</th><th>creds</th><th>health</th>
        </tr></thead>
        <tbody>${d.items.map(phoneRow).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין טלפונים על השרת הזה.</p>';

    pager('phonePager', state.phonePage, state.phonePages, d.total, (n) => {
      state.phonePage = n;
      loadPhones();
    });
  } catch (e) {
    fail('טעינת הטלפונים נכשלה: ' + e.message);
  }
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

/* ------------------------------------------------------------------ boot */

async function tick() {
  await loadStatus().catch((e) => fail('סטטוס לא זמין: ' + e.message));
  await loadHosts();
  if (state.selectedHost) await loadPhones();
}

tick();
setInterval(tick, REFRESH_MS);
