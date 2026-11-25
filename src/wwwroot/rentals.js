/* Rentals: lengths + price (rent_options), the rentals themselves (paged), change history. */

const state = { page: 1, status: 'active' };
let options = [];
let reloadHistory = () => {};

const hoursLabel = (h) => (h % 24 === 0 ? `${h / 24} ימים · ${h}h` : `${h} שעות`);

/* -------------------------------------------------------------- options */

function optRow(o, isNew = false) {
  return `<tr data-hours="${isNew ? '' : o.hours}" class="${isNew ? 'new' : ''}">
    <td>${isNew
      ? '<input class="n" type="number" min="1" max="744" data-f="hours" placeholder="שעות">'
      : `<b>${esc(hoursLabel(o.hours))}</b>`}</td>
    <td><input class="n" type="number" min="0" step="0.01" data-f="price" value="${o.price ?? ''}"></td>
    <td class="num">${isNew ? '' : money(o.per_hour)}</td>
    <td><input class="n" type="number" data-f="sort" value="${o.sort ?? 0}"></td>
    <td class="ck"><input type="checkbox" data-f="is_active" ${isNew || o.is_active ? 'checked' : ''}></td>
    <td class="num">${isNew ? '' : (o.rentals ?? 0)}</td>
    <td>${isNew ? '' : when(o.updated_at)}</td>
    <td><button class="back" data-save>שמור</button></td>
  </tr>`;
}

function renderOptions(withNew = false) {
  $('options').innerHTML = `
    <div class="wrap"><table class="tbl">
      <thead><tr><th>משך</th><th>מחיר כולל</th><th>לשעה</th><th>סדר</th><th>פעיל</th><th>השכרות</th><th>עודכן</th><th></th></tr></thead>
      <tbody>
        ${withNew ? optRow({ sort: (options.at(-1)?.sort ?? 0) + 1 }, true) : ''}
        ${options.map((o) => optRow(o)).join('')}
      </tbody>
    </table></div>`;
  document.querySelectorAll('#options tbody tr').forEach((tr) => {
    tr.querySelectorAll('[data-f]').forEach((el) => el.addEventListener('input', () => tr.classList.add('dirty')));
    tr.querySelector('[data-save]').onclick = () => saveOption(tr);
  });
}

async function loadOptions() {
  try { options = await get('/api/admin/rent/options'); renderOptions(); clearFail(); }
  catch (e) { if (e.message !== 'unauthenticated') fail('טעינת אפשרויות ההשכרה נכשלה: ' + e.message); }
}

async function saveOption(tr) {
  const f = (n) => tr.querySelector(`[data-f="${n}"]`);
  const hours = Number(tr.dataset.hours || f('hours').value);
  const price = numOrNull(f('price').value);
  if (!hours || hours < 1) { fail('משך חסר'); return; }
  if (price === null) { fail('מחיר חסר'); return; }
  try {
    await api('PUT', `/api/admin/rent/options/${hours}`, {
      price, sort: Number(f('sort').value || 0), is_active: f('is_active').checked
    });
    note(`נשמר: ${hoursLabel(hours)}`);
    loadOptions();
    reloadHistory();
  } catch (e) { if (e.message !== 'unauthenticated') fail('שמירה נכשלה: ' + e.message); }
}

/* -------------------------------------------------------------- rentals */

function rentalRow(r) {
  return `<tr>
    <td dir="ltr">${cell(r.number)}</td>
    <td>${cell(r.email)}</td>
    <td>${when(r.start_time)}</td>
    <td>${when(r.ended_at || r.end_time)}</td>
    <td class="num">${r.hours ?? '—'}h</td>
    <td class="num">${r.amount == null ? '—' : money(r.amount)}</td>
    <td><span class="tag tag--${r.is_active ? 'ok' : 'warn'}">${r.is_active ? 'פעילה' : 'הסתיימה'}</span>
      ${r.end_reason ? `<span class="hint">${esc(r.end_reason)}</span>` : ''}</td>
  </tr>`;
}

async function loadRentals() {
  try {
    const qs = new URLSearchParams({ page: state.page });
    if (state.status) qs.set('status', state.status);
    if ($('q').value.trim()) qs.set('q', $('q').value.trim());
    const d = await get('/api/admin/rentals?' + qs);
    $('rentals').innerHTML = d.items.length ? `
      <div class="wrap"><table class="tbl">
        <thead><tr><th>טלפון</th><th>לקוח</th><th>התחלה</th><th>סיום</th><th>משך</th><th>סכום</th><th>סטטוס</th></tr></thead>
        <tbody>${d.items.map(rentalRow).join('')}</tbody>
      </table></div>`
      : '<p class="empty">אין השכרות תואמות.</p>';
    pager('rentPager', d.page, d.pages, d.total, (n) => { state.page = n; loadRentals(); });
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת ההשכרות נכשלה: ' + e.message);
  }
}

/* ----------------------------------------------------------------- boot */

document.querySelectorAll('.chip').forEach((chip) => {
  chip.addEventListener('click', () => {
    document.querySelectorAll('.chip').forEach((c) => c.setAttribute('aria-current', 'false'));
    chip.setAttribute('aria-current', 'true');
    state.status = chip.dataset.status;
    state.page = 1;
    loadRentals();
  });
});
$('q').addEventListener('input', debounce(() => { state.page = 1; loadRentals(); }));
$('addOpt').onclick = () => renderOptions(true);

loadOptions();
loadRentals();
reloadHistory = historyPanel('rent', 'history', 'histPager');
