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

  // ── install: "התקן אפליקציה" in the bar + "הורד אפליקציה" card on the home page ──
  const standalone = () => window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
  const ua = navigator.userAgent;
  const isIOS = /iPhone|iPad|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
  const isFirefox = /Firefox\//.test(ua);
  const isAndroid = /Android/.test(ua);
  const store = { get: (k) => { try { return localStorage.getItem(k); } catch (e) { return null; } },
                  set: (k, v) => { try { localStorage.setItem(k, v); } catch (e) {} } };

  // manual steps when the browser gives no install prompt
  const howTo = () =>
    isIOS      ? 'ב-Safari: כפתור השיתוף ⎋ ← "הוסף למסך הבית".'
  : isFirefox  ? 'פיירפוקס במחשב לא תומך בהתקנה. פתח את הכתובת ב-Chrome או Edge.'
  : isAndroid  ? 'תפריט ⋮ של הדפדפן ← "התקנת אפליקציה" (או "הוספה למסך הבית" ← "התקנה").'
  :              'תפריט ⋮ של הדפדפן ← "העברה, שמירה ושיתוף" ← "התקנת הדף כאפליקציה", או אייקון ההתקנה בשורת הכתובת.';

  let deferred = null;
  const card = document.getElementById('getApp');
  const cardBtn = document.getElementById('getAppBtn');
  const cardHint = document.getElementById('getAppHint');

  const install = async () => {
    if (deferred) {
      deferred.prompt();
      const choice = await deferred.userChoice.catch(() => null);
      deferred = null;
      bar.querySelector('.bar__install')?.remove();
      if (choice?.outcome === 'accepted') installed();
      return;
    }
    if (cardHint) { cardHint.textContent = howTo(); cardHint.classList.add('getapp__how'); }
  };

  const installed = () => {
    bar.querySelector('.bar__install')?.remove();
    if (card) card.hidden = true;
  };

  if (card) {
    card.hidden = standalone() || store.get('po.getapp.hidden') === '1';
    cardBtn?.addEventListener('click', install);
    document.getElementById('getAppX')?.addEventListener('click', () => { card.hidden = true; store.set('po.getapp.hidden', '1'); });
  }

  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    deferred = e;
    if (card && store.get('po.getapp.hidden') !== '1') card.hidden = false;
    if (bar.querySelector('.bar__install')) return;
    const b = document.createElement('button');
    b.className = 'bar__out bar__install';
    b.type = 'button';
    b.textContent = 'התקן אפליקציה';
    b.addEventListener('click', install);
    (bar.querySelector('.bar__end') || bar).prepend(b);
  });
  window.addEventListener('appinstalled', installed);
})();
