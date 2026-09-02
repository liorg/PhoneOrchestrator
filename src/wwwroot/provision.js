/* Prober provisioning: register, log off, switch, and upload the two
   handshake scenarios. */

const $ = (id) => document.getElementById(id);

const esc = (v) =>
  String(v).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const cell = (v) =>
  (v === null || v === undefined || v === '') ? '<span class="nil">—</span>' : esc(v);

function when(iso) {
  if (!iso) return '—';
  return new Date(iso).toLocaleString('he-IL', { dateStyle: 'short', timeStyle: 'short' });
}

function fail(msg) { const b = $('banner'); b.hidden = false; b.textContent = msg; }
function note(msg) { const b = $('banner'); b.hidden = false; b.textContent = msg; }
const clearFail = () => { $('banner').hidden = true; };

async function api(url, opts) {
  const res = await fetch(url, opts);
  if (res.status === 401) { location.href = '/login.html'; throw new Error('unauthenticated'); }
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.error || `${res.status}`);
  return body;
}

let active = null;

/* ----------------------------------------------------------------- load */

async function load() {
  try {
    const d = await api('/api/canary/prober');
    active = d.active || null;

    $('active').innerHTML = active ? `
      <div class="hostbar__top">
        <span class="hostbar__name">${esc(active.phone_number)}</span>
        <span class="tag tag--ok">${esc(active.status)}</span>
        <span class="host__ip">uid ${cell(active.uid)}</span>
      </div>
      <div class="host__stats">
        <span>זיהוי <b class="big">${esc(active.identification_type || '—')}</b></span>
        <span>נרשם <b>${when(active.created_at)}</b></span>
      </div>
      <div class="row">
        <button class="back" id="logoff">ניתוק (LOGOFF)</button>
      </div>`
      : '<p class="empty">אין בודק פעיל. רשום אחד למטה.</p>';

    if (active) {
      $('sendState').innerHTML  = scenarioState(active.send_scenario, 'scheduler');
      $('checkState').innerHTML = scenarioState(active.check_scenario, 'trigger');
      $('logoff').addEventListener('click', logoff);
    }

    renderHistory(d.history || []);
    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינה נכשלה: ' + e.message);
  }
}

function scenarioState(s, expected) {
  if (!s) return '<span class="tag tag--bad">חסר</span>';
  const steps = Array.isArray(s.canvas) ? s.canvas.length : 0;
  const type  = s.eventType || '?';
  const ok    = type === expected && steps > 0;
  return `<span class="tag tag--${ok ? 'ok' : 'warn'}">${esc(type)} · ${steps} שלבים</span>`;
}

function renderHistory(rows) {
  $('history').innerHTML = rows.length ? `
    <div class="wrap"><table class="tbl">
      <thead><tr>
        <th>מספר</th><th>uid</th><th>זיהוי</th><th>status</th><th>נוצר</th><th></th>
      </tr></thead>
      <tbody>${rows.map((r) => `
        <tr>
          <td>${cell(r.phone_number)}</td>
          <td>${cell(r.uid)}</td>
          <td class="big">${cell(r.identification_type)}</td>
          <td><span class="tag tag--${r.status === 'CONNECT' ? 'ok' : 'warn'}">${esc(r.status)}</span></td>
          <td>${when(r.created_at)}</td>
          <td>${r.status === 'CONNECT' ? ''
              : `<button class="back sw" data-id="${esc(r.id)}">הפוך לפעיל</button>`}</td>
        </tr>`).join('')}</tbody>
    </table></div>`
    : '<p class="empty">אין היסטוריה.</p>';

  document.querySelectorAll('.sw').forEach((b) => {
    b.addEventListener('click', () => switchTo(b.dataset.id));
  });
}

/* --------------------------------------------------------------- actions */

async function logoff() {
  if (!active) return;
  if (!confirm(`לנתק את ${active.phone_number}? הבדיקות ייעצרו עד שיירשם בודק אחר.`)) return;
  try {
    await api(`/api/canary/prober/${active.id}/logoff`, { method: 'POST' });
    note('הבודק נותק.');
    load();
  } catch (e) { fail('הניתוק נכשל: ' + e.message); }
}

async function switchTo(id) {
  if (!confirm('להפוך את הבודק הזה לפעיל? הבודק הנוכחי ינותק.')) return;
  try {
    await api(`/api/canary/prober/${id}/switch`, { method: 'POST' });
    note('הבודק הוחלף.');
    load();
  } catch (e) { fail('ההחלפה נכשלה: ' + e.message); }
}

async function create() {
  const phoneNumber        = $('newNumber').value.trim();
  const uid                = $('newUid').value.trim();
  const identificationType = $('newIdent').value.trim();

  if (!phoneNumber || !identificationType) {
    fail('מספר טלפון ואימוג׳י זיהוי הם שדות חובה.');
    return;
  }

  try {
    await api('/api/canary/prober', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ phoneNumber, uid, identificationType })
    });
    $('newNumber').value = $('newUid').value = $('newIdent').value = '';
    note('הבודק נרשם. כעת יש להעלות לו את שני התרחישים.');
    load();
  } catch (e) { fail('הרישום נכשל: ' + e.message); }
}

/* --------------------------------------------------------- file uploads */

function wireUpload(inputId, kind) {
  $(inputId).addEventListener('change', async (ev) => {
    const file = ev.target.files[0];
    if (!file) return;
    if (!active) { fail('אין בודק פעיל להעלות אליו.'); ev.target.value = ''; return; }

    try {
      const scenario = JSON.parse(await file.text());

      const r = await api(`/api/canary/prober/${active.id}/scenario/${kind}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ scenario })
      });

      note(`נשמר: ${r.column}, ${r.steps} שלבים.`);
      load();
    } catch (e) {
      // A JSON parse failure and a rejected eventType look the same to the
      // user otherwise, so say which one it was.
      fail(e instanceof SyntaxError
        ? 'הקובץ אינו JSON תקין.'
        : 'ההעלאה נכשלה: ' + e.message);
    }

    ev.target.value = '';
  });
}

/* ------------------------------------------------------------------ boot */

$('create').addEventListener('click', create);
wireUpload('sendFile', 'send');
wireUpload('checkFile', 'check');

$('logout').addEventListener('click', async () => {
  try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (e) {}
  location.href = '/login.html';
});

load();