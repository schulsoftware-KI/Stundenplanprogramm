// Service Worker: cached die App-Shell (für Installierbarkeit/Offline-Start).
// API-Aufrufe laufen immer übers Netz (Plan soll aktuell sein).

const CACHE = "vertretung-shell-v1";
const SHELL = [
  "./",
  "index.html",
  "style.css",
  "app.js",
  "manifest.webmanifest",
  "icons/icon-192.png",
  "icons/icon-512.png",
];

self.addEventListener("install", (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (e) => {
  e.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))
    ).then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", (e) => {
  const url = new URL(e.request.url);

  // API immer vom Netz (kein Cache alter Pläne).
  if (url.pathname.startsWith("/api/")) {
    e.respondWith(fetch(e.request));
    return;
  }

  // Shell: Cache-first, sonst Netz.
  e.respondWith(
    caches.match(e.request).then((hit) => hit || fetch(e.request))
  );
});
