"use client";

import { useEffect } from "react";

/**
 * Enregistrement du service worker. Après le chargement, jamais pendant : la première ouverture
 * doit être aussi rapide que possible, et l'installation hors ligne peut attendre une seconde.
 */
export function ServiceWorker() {
  useEffect(() => {
    if (!("serviceWorker" in navigator)) return;
    const register = () => void navigator.serviceWorker.register("/sw.js").catch(() => {});
    if (document.readyState === "complete") register();
    else window.addEventListener("load", register, { once: true });
  }, []);

  return null;
}
