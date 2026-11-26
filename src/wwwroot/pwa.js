/* Shared by every page: service worker, menu + submenus, mobile drawer, "install app" button. */
(() => {
  // ── service worker ──────────────────────────────────────────────────
  if ('serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === 'localhost')) {
    window.addEventListener('load', () => {
      navigator.serviceWorker.register('/sw.js', { scope: '/' }).catch((e) => console.warn('[pwa] sw', e));
    });
  }

  const bar = document.querySelector('header.bar');
  if (!bar) return;
  const nav = bar.querySelector('.nav');
  const menuBtn = bar.querySelector('.bar__menu');
  const groups = [...bar.querySelectorAll('.nav__grp')];
  const narrow = window.matchMedia('(max-width: 720px)');

  // ── submenus: click toggles (touch, keyboard); hover opens on desktop via CSS ──
  const closeAll = (except) => groups.forEach((g) => {
    if (g === except) return;
    g.classList.remove('open');
    g.querySelector('.nav__top').setAttribute('aria-expanded', 'false');
  });
  groups.forEach((g) => {
    const top = g.querySelector('.nav__top');
    top.addEventListener('click', (e) => {
      if (narrow.matches) return;                 // drawer: groups are always expanded
      e.stopPropagation();
      const open = !g.classList.contains('open');
      closeAll(g);
      g.classList.toggle('open', open);
      top.setAttribute('aria-expanded', String(open));
    });
    g.addEventListener('mouseleave', () => {
      if (!narrow.matches && g.classList.contains('open') && !g.contains(document.activeElement)) closeAll();
    });
  });
  document.addEventListener('click', (e) => { if (!e.target.closest('.nav__grp')) closeAll(); });

  // ── mobile drawer ──
  const setDrawer = (open) => {
    bar.classList.toggle('bar--open', open);
    menuBtn?.setAttribute('aria-expanded', String(open));
  };
  menuBtn?.addEventListener('click', () => setDrawer(!bar.classList.contains('bar--open')));
  nav?.addEventListener('click', (e) => { if (e.target.closest('a')) { setDrawer(false); closeAll(); } });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') { setDrawer(false); closeAll(); } });
  narrow.addEventListener('change', () => { setDrawer(false); closeAll(); });

  // ── install prompt (Chrome / Edge / Android) ──
  let deferred = null;
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    deferred = e;
    if (bar.querySelector('.bar__install')) return;
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
    (bar.querySelector('.bar__end') || bar).prepend(b);
  });
  window.addEventListener('appinstalled', () => document.querySelector('.bar__install')?.remove());
})();
