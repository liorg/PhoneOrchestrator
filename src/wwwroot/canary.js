/* Canary report: runs → checks. The check row carries everything - code, delivery, ACK, error. */

const state = {
  view: 'runs',
  runPage: 1,
  checkPage: 1,
  runId: null,
  runRow: null,
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
  const tone = (r.failed > 0 || r.status === 'aborted') ? 'bad' : (r.status === 'running' ? 'warn' : 'ok');

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
             : (c.status === 'skipped' || c.status === 'late_ack' ? 'warn' : 'bad');

  return `<tr>
    <td>${cell(c.phone_number)}</td>
    <td><span class="tag tag--${tone}">${esc(c.status)}</span></td>
    <td><code>${cell(c.check_code)}</code></td>
    <td>${({1: 'נשלח', 2: 'נמסר', 3: 'נקרא', 4: 'נקרא', '-1': 'נדחה'})[c.delivery_status] || '<span class="nil">—</span>'}</td>
    <td class="num">${c.latency_ms == null ? '—' : c.latency_ms + 'ms'}</td>
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
          <th>טלפון</th><th>status</th><th>CHECK ID</th><th>מסירה</th>
          <th>זמן ACK</th><th>הותרע</th><th>שגיאה</th>
        </tr></thead>
        <tbody>${d.items.map(checkRow).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין בדיקות תואמות.</p>';

    pager('checkPager', state.checkPage, d.pages || 1, d.total, (n) => {
      state.checkPage = n; loadChecks();
    });

    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת הבדיקות נכשלה: ' + e.message);
  }
}

/* ------------------------------------------------------------ navigation */

function show(view) {
  state.view = view;
  ['runs', 'checks'].forEach((v) => {
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

/* --------------------------------------------------------------- control */

const MODE = {
  disabled: 'כבוי', outside_hours: 'מחוץ לשעות', scheduled: 'מתוזמן',
  running: 'סבב רץ', running_manual: 'סבב ידני רץ', starting: 'עולה'
};

async function send(method, url, body) {
  const res = await fetch(url, {
    method, headers: { 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined
  });
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.json();
}

const hhmm = (t) => (t ? String(t).slice(0, 5) : '');

async function loadControl() {
  try {
    const { control: c, runnerAlive } = await get('/api/canary/control');
    const prober = c.prober_ready === true ? ['ok', 'מוכן']
                 : (c.prober_ready === false ? ['bad', 'לא מוכן'] : ['warn', 'לא נבדק']);
    const runner = runnerAlive ? ['ok', MODE[c.runner_mode] || c.runner_mode] : ['bad', 'runner לא עונה'];

    $('proberbar').innerHTML = `
      <div class="hostbar__top">
        <span class="hostbar__name">Canary</span>
        <span class="tag tag--${c.enabled ? 'ok' : 'warn'}">${c.enabled ? 'פעיל' : 'כבוי'}</span>
        <span class="tag tag--${runner[0]}">${esc(runner[1])}</span>
        <span class="tag tag--${prober[0]}">בודק: ${prober[1]}</span>
        ${c.prober_reason ? `<span class="bad-t">${esc(c.prober_reason)}</span>` : ''}
      </div>
      <div class="filters">
        <label>כל <input type="number" id="cInterval" min="60" step="60" value="${c.interval_sec}"> שנ׳</label>
        <label>timeout <input type="number" id="cTimeout" min="15" value="${c.timeout_sec}"> שנ׳</label>
        <label>משעה <input type="time" id="cFrom" value="${hhmm(c.active_from)}"></label>
        <label>עד <input type="time" id="cTo" value="${hhmm(c.active_to)}"></label>
        <button class="back" id="cSave">שמור</button>
        <button class="back" id="cToggle">${c.enabled ? 'עצור' : 'הפעל'}</button>
        <button class="back" id="cRun" ${c.run_now_at ? 'disabled' : ''}>${c.run_now_at ? 'ממתין…' : 'הרץ סבב עכשיו'}</button>
      </div>
      <div class="hint">
        ${c.last_sweep_at ? 'אחרון: ' + when(c.last_sweep_at) : ''}
        ${c.last_result ? ' · ' + esc(c.last_result) : ''}
        ${c.enabled && c.next_sweep_at ? ' · הבא: ' + when(c.next_sweep_at) : ''}
        ${c.runner_version ? ' · ' + esc(c.runner_version) : ''}
      </div>`;

    $('cToggle').onclick = async () => {
      try { await send('PATCH', '/api/canary/control', { enabled: !c.enabled }); } catch (e) { fail(e.message); }
      loadControl();
    };
    $('cSave').onclick = async () => {
      try {
        await send('PATCH', '/api/canary/control', {
          intervalSec: +$('cInterval').value, timeoutSec: +$('cTimeout').value,
          activeFrom: $('cFrom').value, activeTo: $('cTo').value
        });
        clearFail();
      } catch (e) { fail('שמירה נכשלה: ' + e.message); }
      loadControl();
    };
    $('cRun').onclick = async () => {
      try { await send('POST', '/api/canary/run'); } catch (e) { fail(e.message); }
      loadControl();
    };
  } catch (e) {
    if (e.message !== 'unauthenticated') $('proberbar').innerHTML = `<p class="bad-t">${esc(e.message)}</p>`;
  }
}

/* ------------------------------------------------------------------ boot */

$('apply').addEventListener('click', () => { state.runPage = 1; loadRuns(); });
$('backRuns').addEventListener('click', () => show('runs'));

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

loadControl();
loadRuns();
setInterval(loadControl, 15000);