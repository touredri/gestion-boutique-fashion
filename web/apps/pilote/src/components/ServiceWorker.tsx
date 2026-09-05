"use client";

import { useEffect } from "react";

/**
 * Enregistrement du service worker. Après le chargement, jamais pendant : la première ouverture
 * doit être aussi rapide que possible, et l'installation hors ligne peut attendre une seconde.
 */
export function ServiceWorker() {
  useEffect(() => {
    if (!("serviceWorker" in navigator)) return;
    // Chemin déduit de la page courante plutôt qu'écrit en dur : l'application est montée sous
    // /pilote, et un service worker ne peut pas contrôler un périmètre plus large que son propre
    // dossier. Enregistré à la racine, il n'aurait rien contrôlé du tout.
    const base = new URL(".", window.location.href).pathname.replace(/[^/]*$/, "");
    const scope = base.startsWith("/pilote") ? "/pilote/" : "/";
    const register = () => void navigator.serviceWorker.register(`${scope}sw.js`, { scope }).catch(() => {});
    if (document.readyState === "complete") register();
    else window.addEventListener("load", register, { once: true });
  }, []);

  return null;
}
