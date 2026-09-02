/* Canary report: runs → checks → events. Three levels, each its own screen. */

const state = {
  view: 'runs',
  runPage: 1,
  checkPage: 1,
  runId: null,
  runRow: null,
  checkId: null,
  status: ''
};

const $ = (id) => document.getElementById(id);

const esc = (v) =>
  String(v).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const cell = (v) =>
  (v === null || v === undefined || v === '') ? '<span class="nil">—</span>' : esc(v);

function when(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  return d.toLocaleString('he-IL', { dateStyle: 'short', timeStyle: 'medium' });
}

async function get(url) {
  const res = await fetch(url);
  if (res.status === 401) { location.href = '/login.html'; throw new Error('unauthenticated'); }
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.json();
}

function fail(msg) { const b = $('banner'); b.hidden = false; b.textContent = msg; }
const clearFail = () => { $('banner').hidden = true; };

/* ------------------------------------------------------------------ runs */

function runRow(r) {
  const pct  = r.pass_pct == null ? '—' : r.pass_pct + '%';
  const tone = r.failed > 0 ? 'bad' : (r.status === 'running' ? 'warn' : 'ok');

  return `<tr class="click" data-id="${esc(r.id)}">
    <td>${when(r.started_at)}</td>
    <td><span class="tag tag--${tone}">${esc(r.status)}</span></td>
    <td class="num">${r.total}</td>
    <td class="num ok-t">${r.passed}</td>
    <td class="num ${r.failed > 0 ? 'bad-t' : ''}">${r.failed}</td>
    <td class="num">${r.skipped}</td>
    <td class="num">${pct}</td>
    <td class="num">${r.duration_sec == null ? '—' : r.duration_sec + 's'}</td>
    <td>${r.error ? `<span class="bad-t">${esc(r.error)}</span>` : '<span class="nil">—</span>'}</td>
  </tr>`;
}

async function loadRuns() {
  try {
    const qs = new URLSearchParams({ page: state.runPage });
    if ($('from').value) qs.set('from', $('from').value);
    if ($('to').value)   qs.set('to', $('to').value + 'T23:59:59');

    const d = await get('/api/canary/runs?' + qs);

    $('runs').innerHTML = d.items.length ? `
      <div class="wrap"><table class="tbl">
        <thead><tr>
          <th>התחיל</th><th>status</th><th>סה"כ</th><th>עבר</th>
          <th>נכשל</th><th>דולג</th><th>%</th><th>משך</th><th>שגיאה</th>
        </tr></thead>
        <tbody>${d.items.map(runRow).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין סבבים בטווח הזה.</p>';

    pager('runPager', state.runPage, d.pages || 1, d.total, (n) => {
      state.runPage = n; loadRuns();
    });

    document.querySelectorAll('#runs .click').forEach((el) => {
      el.addEventListener('click', () => {
        openChecks(d.items.find((x) => x.id === el.dataset.id));
      });
    });

    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת הסבבים נכשלה: ' + e.message);
  }
}

/* ---------------------------------------------------------------- checks */

function checkRow(c) {
  const tone = c.status === 'passed' ? 'ok'
             : (c.status === 'skipped' ? 'warn' : 'bad');

  return `<tr class="click" data-id="${esc(c.id)}">
    <td>${cell(c.phone_number)}</td>
    <td><span class="tag tag--${tone}">${esc(c.status)}</span></td>
    <td>${c.scenario_ok === false ? '<span class="bad-t">חסר</span>'
        : (c.scenario_ok ? '<span class="ok-t">✓</span>' : '<span class="nil">—</span>')}</td>
    <td class="num">${c.latency_ms == null ? '—' : c.latency_ms + 'ms'}</td>
    <td class="num">${c.event_count}</td>
    <td>${c.notified ? '<span class="ok-t">✓</span>' : '<span class="nil">—</span>'}</td>
    <td>${c.error ? `<span class="bad-t">${esc(c.error)}</span>` : '<span class="nil">—</span>'}</td>
  </tr>`;
}

async function loadChecks() {
  if (!state.runId) return;
  try {
    const qs = new URLSearchParams({ page: state.checkPage });
    if (state.status) qs.set('status', state.status);

    const d = await get(`/api/canary/runs/${state.runId}/checks?` + qs);

    $('checks').innerHTML = d.items.length ? `
      <div class="wrap"><table class="tbl">
        <thead><tr>
          <th>טלפון</th><th>status</th><th>תרחיש</th>
          <th>זמן תגובה</th><th>אירועים</th><th>הותרע</th><th>שגיאה</th>
        </tr></thead>
        <tbody>${d.items.map(checkRow).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין בדיקות תואמות.</p>';

    pager('checkPager', state.checkPage, d.pages || 1, d.total, (n) => {
      state.checkPage = n; loadChecks();
    });

    document.querySelectorAll('#checks .click').forEach((el) => {
      el.addEventListener('click', () => openEvents(el.dataset.id));
    });

    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת הבדיקות נכשלה: ' + e.message);
  }
}

/* ---------------------------------------------------------------- events */

async function loadEvents() {
  if (!state.checkId) return;
  try {
    const d = await get(`/api/canary/checks/${state.checkId}/events`);

    if (!d.ok) { fail('הבדיקה לא נמצאה.'); return; }

    const c = d.check;
    $('checkbar').innerHTML = `
      <div class="hostbar__top">
        <span class="hostbar__name">${esc(c.phone_number || c.phone_id)}</span>
        <span class="tag tag--${c.status === 'passed' ? 'ok' : 'bad'}">${esc(c.status)}</span>
        ${c.latency_ms != null ? `<span class="host__ip">${c.latency_ms}ms</span>` : ''}
      </div>
      ${c.error ? `<div class="host__err">${esc(c.error)}</div>` : ''}`;

    $('events').innerHTML = d.events.length ? `
      <ol class="trail">${d.events.map((e) => `
        <li>
          <span class="trail__when">${when(e.created_at)}</span>
          <span class="trail__kind">${esc(e.kind)}</span>
          ${e.detail ? `<pre class="trail__detail">${esc(JSON.stringify(e.detail, null, 2))}</pre>` : ''}
        </li>`).join('')}</ol>`
      : '<p class="empty">אין תת-אירועים.</p>';

    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת האירועים נכשלה: ' + e.message);
  }
}

/* ------------------------------------------------------------ navigation */

function show(view) {
  state.view = view;
  ['runs', 'checks', 'events'].forEach((v) => {
    $('view-' + v).hidden = v !== view;
  });
  window.scrollTo(0, 0);
}

function openChecks(row) {
  if (!row) return;
  state.runId = row.id;
  state.runRow = row;
  state.checkPage = 1;
  state.status = '';

  document.querySelectorAll('.chip').forEach((c) =>
    c.setAttribute('aria-current', String(c.dataset.status === '')));

  $('runbar').innerHTML = `
    <div class="hostbar__top">
      <span class="hostbar__name">${when(row.started_at)}</span>
      <span class="tag tag--${row.failed > 0 ? 'bad' : 'ok'}">${row.passed}/${row.total}</span>
      <span class="host__ip">${row.duration_sec == null ? '' : row.duration_sec + 's'}</span>
    </div>`;

  show('checks');
  $('checks').innerHTML = '<p class="empty">טוען…</p>';
  loadChecks();
}

function openEvents(checkId) {
  state.checkId = checkId;
  show('events');
  $('events').innerHTML = '<p class="empty">טוען…</p>';
  loadEvents();
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

$('apply').addEventListener('click', () => { state.runPage = 1; loadRuns(); });
$('backRuns').addEventListener('click', () => show('runs'));
$('backChecks').addEventListener('click', () => show('checks'));

document.querySelectorAll('.chip').forEach((chip) => {
  chip.addEventListener('click', () => {
    document.querySelectorAll('.chip').forEach((c) => c.setAttribute('aria-current', 'false'));
    chip.setAttribute('aria-current', 'true');
    state.status = chip.dataset.status;
    state.checkPage = 1;
    loadChecks();
  });
});

$('logout').addEventListener('click', async () => {
  try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (e) {}
  location.href = '/login.html';
});

loadRuns();