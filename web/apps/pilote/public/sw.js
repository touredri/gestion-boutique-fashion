/*
  Service worker écrit à la main plutôt qu'engendré par une bibliothèque.

  L'application est un export statique de quelques fichiers : une chaîne de génération apporterait
  surtout des dépendances et un fichier illisible. Ici la stratégie tient en trois règles, et
  chacune correspond à une situation réelle de la propriétaire.
*/

const VERSION = "bana-pilote-v2";
const SHELL = `${VERSION}-shell`;

// Racine de montage, déduite de l'emplacement du fichier plutôt qu'écrite en dur : ce worker vit
// sous /pilote/ en production, et à la racine si on le sert autrement. L'écrire en dur ici aurait
// été la troisième copie de la même information — et celle qu'on aurait oublié de changer.
const BASE = self.location.pathname.replace(/sw\.js$/, "");

// Coquille minimale : de quoi ouvrir l'application dans un taxi sans réseau. Les données, elles,
// viennent du cache local que gère l'application (voir useResource).
const PRECACHE = ["", "commandes/", "catalogue/", "rapports/", "boutiques/", "boutiques/alertes/", "manifest.webmanifest"]
  .map((path) => BASE + path);

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
        .catch(() => caches.match(request).then((hit) => hit ?? caches.match(BASE))),
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

/*
  Notifications. Le message reçu ne porte aucune charge utile — voir Notifier côté serveur pour
  la raison — donc la notification est volontairement sobre : elle signale, l'application détaille.
*/
self.addEventListener("push", (event) => {
  event.waitUntil(
    self.registration.showNotification("Bana Shop", {
      body: "Nouvelle activité dans vos boutiques.",
      icon: `${BASE}icon-192.png`,
      badge: `${BASE}icon-192.png`,
      // Une seule notification empilée : cinq ventes ne doivent pas produire cinq bandeaux.
      tag: "bana-activite",
      renotify: true,
    }),
  );
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  event.waitUntil(
    self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((windows) => {
      // Une fenêtre déjà ouverte est ramenée au premier plan plutôt que dupliquée.
      const existing = windows.find((w) => w.url.includes(self.location.origin));
      if (existing) return existing.focus();
      return self.clients.openWindow(BASE);
    }),
  );
});
