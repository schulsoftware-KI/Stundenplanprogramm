// Service Worker der Stundenplan-PWA.
//  - App-Dateien: zuerst vom Server (damit Updates sofort ankommen), ohne
//    Verbindung aus dem Cache – die App startet also auch offline.
//  - API-Aufrufe gehen immer ans Netz; offline lesbare Wochen speichert
//    app.js selbst (im Browser-Speicher, mit Hinweis „Offline").
//  Neue Version = neuer Cache-Name; alte Caches werden beim Aktivieren gelöscht.

const CACHE = "stundenplan-shell-v2";
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
  // Jede Datei einzeln: Fehlt eine (z. B. ein Icon), wird der Rest trotzdem gespeichert.
  e.waitUntil(
    caches.open(CACHE)
      .then((c) => Promise.all(SHELL.map((u) => c.add(u).catch(() => {}))))
      .then(() => self.skipWaiting())
  );
});

self.addEventListener("activate", (e) => {
  e.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", (e) => {
  const req = e.request;
  const url = new URL(req.url);
  if (req.method !== "GET" || url.origin !== self.location.origin) return;

  // API: immer übers Netz (kein Zwischenspeichern alter Pläne im Service Worker).
  if (url.pathname.includes("/api/")) return;

  // App-Dateien: Netz zuerst, bei Erfolg Cache auffrischen, sonst Cache.
  e.respondWith(
    fetch(req, { cache: "no-cache" })
      .then((resp) => {
        if (resp.ok && resp.type === "basic") {
          const kopie = resp.clone();
          caches.open(CACHE).then((c) => c.put(req, kopie));
        }
        return resp;
      })
      .catch(() =>
        caches.match(req).then((hit) => hit || (req.mode === "navigate" ? caches.match("index.html") : undefined))
      )
  );
});
