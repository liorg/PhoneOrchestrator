/* Shared by every page: service worker, mobile menu, "install app" button. */
(() => {
  // ── service worker ──────────────────────────────────────────────────
  if ('serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === 'localhost')) {
    window.addEventListener('load', () => {
      navigator.serviceWorker.register('/sw.js', { scope: '/' }).catch((e) => console.warn('[pwa] sw', e));
    });
  }

  const bar = document.querySelector('header.bar');
  const nav = bar && bar.querySelector('.nav');

  // ── mobile menu: ☰ toggles the nav under the bar ────────────────────
  if (nav) {
    nav.id = nav.id || 'mainnav';
    const btn = document.createElement('button');
    btn.className = 'bar__menu';
    btn.type = 'button';
    btn.setAttribute('aria-controls', nav.id);
    btn.setAttribute('aria-expanded', 'false');
    btn.setAttribute('aria-label', 'תפריט');
    const cur = nav.querySelector('[aria-current="page"]');
    btn.innerHTML = `<span aria-hidden="true">☰</span>${cur ? `<b>${cur.textContent}</b>` : ''}`;
    bar.querySelector('.bar__id').after(btn);

    const set = (open) => { bar.classList.toggle('bar--open', open); btn.setAttribute('aria-expanded', String(open)); };
    btn.addEventListener('click', () => set(!bar.classList.contains('bar--open')));
    nav.addEventListener('click', (e) => { if (e.target.closest('a')) set(false); });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') set(false); });
  }

  // ── install prompt (Chrome / Edge / Android) ────────────────────────
  let deferred = null;
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    deferred = e;
    if (!bar || bar.querySelector('.bar__install')) return;
    const b = document.createElement('button');
    b.className = 'bar__out bar__install';
    b.type = 'button';
    b.textContent = 'התקן אפליקציה';
    b.addEventListener('click', async () => {
      if (!deferred) return;
      deferred.prompt();
      await deferred.userChoice.catch(() => {});
      deferred = null;
      b.remove();
    });
    const out = bar.querySelector('.bar__out');
    out ? out.before(b) : bar.append(b);
  });
  window.addEventListener('appinstalled', () => document.querySelector('.bar__install')?.remove());
})();
