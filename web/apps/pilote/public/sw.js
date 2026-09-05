/*
  Service worker écrit à la main plutôt qu'engendré par une bibliothèque.

  L'application est un export statique de quelques fichiers : une chaîne de génération apporterait
  surtout des dépendances et un fichier illisible. Ici la stratégie tient en trois règles, et
  chacune correspond à une situation réelle de la propriétaire.
*/

const VERSION = "bana-pilote-v1";
const SHELL = `${VERSION}-shell`;

// Coquille minimale : de quoi ouvrir l'application dans un taxi sans réseau. Les données, elles,
// viennent du cache local que gère l'application (voir useResource).
const PRECACHE = ["/", "/commandes/", "/catalogue/", "/rapports/", "/boutiques/", "/manifest.webmanifest"];

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches
      .open(SHELL)
      // addAll échoue en bloc si une seule ressource manque : on tolère les absences plutôt
      // que de laisser l'installation entière échouer.
      .then((cache) => Promise.allSettled(PRECACHE.map((url) => cache.add(url))))
      .then(() => self.skipWaiting()),
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((key) => !key.startsWith(VERSION)).map((key) => caches.delete(key))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const { request } = event;
  if (request.method !== "GET") return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  // 1. L'API n'est jamais servie depuis le cache. Un chiffre d'affaires périmé présenté comme
  //    actuel est pire que pas de chiffre du tout : l'application gère elle-même son cache de
  //    données et sait dire de quand il date.
  if (url.pathname.startsWith("/api/")) return;

  // 2. Navigation : réseau d'abord, coquille en secours. C'est ce qui permet d'ouvrir
  //    l'application hors ligne.
  if (request.mode === "navigate") {
    event.respondWith(
      fetch(request)
        .then((response) => {
          const copy = response.clone();
          caches.open(SHELL).then((cache) => cache.put(request, copy));
          return response;
        })
        .catch(() => caches.match(request).then((hit) => hit ?? caches.match("/"))),
    );
    return;
  }

  // 3. Ressources statiques : cache d'abord, rafraîchi en arrière-plan. Elles portent une
  //    empreinte dans leur nom, donc une version périmée n'existe pas.
  event.respondWith(
    caches.match(request).then((hit) => {
      const network = fetch(request)
        .then((response) => {
          if (response.ok) {
            const copy = response.clone();
            caches.open(SHELL).then((cache) => cache.put(request, copy));
          }
          return response;
        })
        .catch(() => hit);
      return hit ?? network;
    }),
  );
});
