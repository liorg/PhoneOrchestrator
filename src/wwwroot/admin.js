/* Shared helpers for the back office pages (plans, customers, config) — same style as canary.js. */

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

const money = (n) => '$' + Number(n || 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined
  });
  if (res.status === 401) { location.href = '/login.html'; throw new Error('unauthenticated'); }
  if (!res.ok) {
    let msg = await res.text();
    try { const j = JSON.parse(msg); msg = j.error || j.reason || msg; } catch (e) {}
    throw new Error(`${res.status} ${msg}`);
  }
  return res.json();
}
const get = (url) => api('GET', url);

function fail(msg) { const b = $('banner'); b.hidden = false; b.className = 'banner'; b.textContent = msg; }
function note(msg) {
  const b = $('banner'); b.hidden = false; b.className = 'banner banner--ok'; b.textContent = msg;
  clearTimeout(note._t); note._t = setTimeout(() => { b.hidden = true; }, 2500);
}
const clearFail = () => { $('banner').hidden = true; };

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

/* catalog_history rows → table. A changed column shows old → new; an INSERT shows the new values. */
const OPS = { INSERT: ['ok', 'נוסף'], UPDATE: ['warn', 'עודכן'], DELETE: ['bad', 'נמחק'] };
const showVal = (v) => (v === null || v === undefined) ? '∞' : (typeof v === 'boolean' ? (v ? '✓' : '✗') : String(v));

function historyTable(items) {
  if (!items.length) return '<p class="empty">אין שינויים.</p>';
  return `<div class="wrap"><table class="tbl">
    <thead><tr><th>מתי</th><th>פריט</th><th>פעולה</th><th>מה השתנה</th><th>מי</th></tr></thead>
    <tbody>${items.map((h) => {
      const [tone, label] = OPS[h.op] || ['warn', h.op];
      const cols = h.op === 'UPDATE'
        ? (h.changed || []).map((k) => `<b>${esc(k)}</b>: ${esc(showVal(h.old?.[k]))} → ${esc(showVal(h.new?.[k]))}`)
        : Object.entries(h.new || h.old || {}).filter(([k]) => k !== 'updated_at')
            .map(([k, v]) => `<b>${esc(k)}</b>: ${esc(showVal(v))}`);
      return `<tr>
        <td>${when(h.at)}</td>
        <td><code>${esc(h.ref)}</code></td>
        <td><span class="tag tag--${tone}">${label}</span></td>
        <td class="hint" style="white-space:normal">${cols.join(' · ')}</td>
        <td>${cell(h.actor)}</td>
      </tr>`;
    }).join('')}</tbody>
  </table></div>`;
}

/* loads /api/admin/history/{kind} into elId with its own pager */
function historyPanel(kind, elId, pagerId) {
  let page = 1;
  const load = async () => {
    try {
      const d = await get(`/api/admin/history/${kind}?page=${page}`);
      $(elId).innerHTML = historyTable(d.items);
      pager(pagerId, d.page, d.pages, d.total, (n) => { page = n; load(); });
    } catch (e) {
      if (e.message !== 'unauthenticated') $(elId).innerHTML = `<p class="bad-t">${esc(e.message)}</p>`;
    }
  };
  load();
  return load;
}

// "" → null (unlimited / custom), else a number
const numOrNull = (v) => (String(v).trim() === '' ? null : Number(v));

// debounce for search boxes
function debounce(fn, ms = 350) { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; }

document.addEventListener('DOMContentLoaded', () => {
  const out = $('logout');
  if (out) out.addEventListener('click', async () => {
    try { await fetch('/api/auth/logout', { method: 'POST' }); } catch (e) {}
    location.href = '/login.html';
  });
});
