/* bot_config: paged, searchable. Value + description editable per row; a changed row is marked until saved. */

const state = { page: 1 };

function cfgRow(c, isNew = false) {
  return `<tr data-key="${esc(c.key || '')}" class="${isNew ? 'new' : ''}">
    <td class="k">${isNew
      ? '<input type="text" data-f="key" placeholder="namespace.key">'
      : `<code>${esc(c.key)}</code>`}</td>
    <td><input type="text" data-f="value" value="${esc(c.value ?? '')}" dir="ltr"></td>
    <td><textarea data-f="description" placeholder="מה המפתח עושה, יחידות, טווח, ברירת מחדל">${esc(c.description ?? '')}</textarea></td>
    <td>${isNew ? '' : when(c.updated_at)}</td>
    <td><button class="back" data-save>שמור</button></td>
  </tr>`;
}

async function load(withNew = false) {
  try {
    const qs = new URLSearchParams({ page: state.page });
    if ($('q').value.trim()) qs.set('q', $('q').value.trim());
    const d = await get('/api/admin/config?' + qs);

    $('config').innerHTML = (d.items.length || withNew) ? `
      <div class="wrap"><table class="tbl">
        <thead><tr><th>מפתח</th><th style="width:22%">ערך</th><th style="width:44%">הסבר</th><th>עודכן</th><th></th></tr></thead>
        <tbody>
          ${withNew ? cfgRow({}, true) : ''}
          ${d.items.map((c) => cfgRow(c)).join('')}
        </tbody>
      </table></div>`
      : '<p class="empty">אין מפתחות תואמים.</p>';

    pager('pager', d.page, d.pages, d.total, (n) => { state.page = n; load(); });

    document.querySelectorAll('#config tbody tr').forEach((tr) => {
      tr.querySelectorAll('[data-f]').forEach((el) =>
        el.addEventListener('input', () => tr.classList.add('dirty')));
      tr.querySelector('[data-save]').onclick = () => save(tr);
    });
    clearFail();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('טעינת ההגדרות נכשלה: ' + e.message);
  }
}

async function save(tr) {
  const f = (n) => tr.querySelector(`[data-f="${n}"]`);
  const key = tr.dataset.key || f('key').value.trim();
  if (!key) { fail('חסר שם מפתח'); return; }
  const description = f('description').value.trim();
  try {
    await api('PUT', '/api/admin/config', {
      key,
      value: f('value').value,
      description: description || null      // empty = keep the existing description
    });
    note(`נשמר: ${key}`);
    load();
  } catch (e) {
    if (e.message !== 'unauthenticated') fail('שמירה נכשלה: ' + e.message);
  }
}

$('q').addEventListener('input', debounce(() => { state.page = 1; load(); }));
$('addKey').onclick = () => load(true);
load();
