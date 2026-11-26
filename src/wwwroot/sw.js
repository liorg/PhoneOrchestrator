/* PhoneOrchestrator service worker.
   Live dashboard → network first for everything. The cache is only a fallback
   for when the network is gone, so a stale page never hides a real reading.
   /api/*, /canary/*, /health, /version are never touched (data + auth).
   Redirected / non-200 answers (e.g. → login.html) are never cached. */

const VERSION = 'po-v6';
const SHELL = [
  '/offline.html', '/styles.css', '/admin.css', '/admin.js', '/pwa.js',
  '/manifest.webmanifest', '/icons/icon-192.png', '/icons/icon-512.png', '/logo2.png'
];

self.addEventListener('install', (e) => {
  // one file per request: a single 404 / redirect must not fail the whole install
  // (addAll is all-or-nothing → no active worker → the browser never offers "install")
  e.waitUntil((async () => {
    const c = await caches.open(VERSION);
    await Promise.all(SHELL.map(async (u) => {
      try {
        const res = await fetch(u, { cache: 'no-cache' });
        if (res.ok && !res.redirected) await c.put(u, res);
        else console.warn('[sw] skip', u, res.status);
      } catch (err) { console.warn('[sw] skip', u, err); }
    }));
    await self.skipWaiting();
  })());
});

self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== VERSION).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()));
});

const BYPASS = /^\/(api|canary)\/|^\/(health|version)$/i;

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== location.origin || BYPASS.test(url.pathname)) return;

  e.respondWith((async () => {
    try {
      const res = await fetch(req);
      if (res.ok && res.type === 'basic' && !res.redirected) {
        const copy = res.clone();
        caches.open(VERSION).then((c) => c.put(req, copy));
      }
      return res;
    } catch (err) {
      const hit = await caches.match(req, { ignoreSearch: req.mode === 'navigate' });
      if (hit) return hit;
      if (req.mode === 'navigate') return caches.match('/offline.html');
      throw err;
    }
  })());
});
