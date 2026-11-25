/* Plans: one editable row per plan, save per row. Empty limit = unlimited, empty price = custom. */

let plans = [];

const limitIn = (name, v) =>
  `<input class="n" type="number" min="0" data-f="${name}" value="${v ?? ''}" placeholder="∞">`;
const checkIn = (name, v) =>
  `<input type="checkbox" data-f="${name}" ${v ? 'checked' : ''}>`;

function planRow(p, isNew = false) {
  return `<tr data-id="${esc(p.id || '')}" class="${isNew ? 'new' : ''}">
    <td>${isNew
      ? '<input type="text" data-f="id" placeholder="id (a-z, 0-9, _)">'
      : `<code>${esc(p.id)}</code>${p.is_default ? ' <span class="tag tag--ok">ברירת מחדל</span>' : ''}`}</td>
    <td><input class="n" type="number" min="0" step="0.01" data-f="price" value="${p.price ?? ''}" placeholder="custom"></td>
    <td>${limitIn('phones', p.phones)}</td>
    <td>${limitIn('bots', p.bots)}</td>
    <td>${limitIn('schedules', p.schedules)}</td>
    <td class="ck">${checkIn('api', p.api)}</td>
    <td class="ck">${checkIn('load_test', p.load_test)}</td>
    <td class="ck">${checkIn('monitoring', p.monitoring)}</td>
    <td><select data-f="cta">
      <option value="signup" ${p.cta !== 'contact' ? 'selected' : ''}>signup</option>
      <option value="contact" ${p.cta === 'contact' ? 'selected' : ''}>contact</option>
    </select></td>
    <td class="ck">${checkIn('highlight', p.highlight)}</td>
    <td><input class="n" type="number" data-f="sort" value="${p.sort ?? 0}"></td>
    <td class="ck">${checkIn('is_active', isNew ? true : p.is_active)}</td>
    <td class="num">${isNew ? '' : (p.customers ?? 0)}</td>
    <td><button class="back" data-save>שמור</button></td>
  </tr>`;
}

function render(withNew = false) {
  $('plans').innerHTML = `
    <div class="wrap"><table class="tbl">
      <thead><tr>
        <th>id</th><th>מחיר / חודש</th><th>טלפונים</th><th>בוטים</th><th>תזמונים</th>
        <th>API</th><th>הרצות מקביליות</th><th>ניטור</th><th>כפתור</th><th>מומלץ</th>
        <th>סדר</th><th>פעיל</th><th>לקוחות</th><th></th>
      </tr></thead>
      <tbody>
        ${withNew ? planRow({ cta: 'signup', sort: (plans.at(-1)?.sort ?? 0) + 1 }, true) : ''}
        ${plans.map((p) => planRow(p)).join('')}
      </tbody>
    </table></div>`;

  document.querySelectorAll('#plans tbody tr').forEach((tr) => {
    tr.querySelectorAll('[data-f]').forEach((el) =>
      el.addEventListener('input', () => tr.classList.add('dirty')));
    tr.querySelector('[data-save]').onclick = () => save(tr);
  });
}

function rowData(tr) {
  const v = (f) => tr.querySelector(`[data-f="${f}"]`);
  return {
    id:         tr.dataset.id || v('id').value.trim(),
    price:      numOrNull(v('price').value),
    phones:     numOrNull(v('phones').value),
    bots:       numOrNull(v('bots').value),
    schedules:  numOrNull(v('schedules').value),
    api:        v('api').checked,
    load_test:  v('load_test').checked,
    monitoring: v('monitoring').checked,
    cta:        v('cta').value,
    highlight:  v('highlight').checked,
    sort:       Number(v('sort').value || 0),
    is_active:  v('is_active').checked
  };
}

async function save(tr) {
  const d = rowData(tr);
  if (!d.id) { fail('חסר id לתוכנית'); return; }
  try {
    await api('PUT', `/api/admin/plans/${encodeURIComponent(d.id)}`, d);
    note(`נשמר: ${d.id}`);
    load();
    reloadHistory();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('שמירה נכשלה: ' + e.message);
  }
}

async function load() {
  try {
    plans = itemsOf(await get('/api/admin/plans'));
    render();
    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת התוכניות נכשלה: ' + e.message);
  }
}

const reloadHistory = historyPanel('plan', 'history', 'histPager');

$('addPlan').onclick = () => render(true);
load();