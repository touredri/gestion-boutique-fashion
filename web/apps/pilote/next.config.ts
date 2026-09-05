import type { NextConfig } from "next";

/** Chemin de montage. Caddy sert la vitrine à la racine et l'application de pilotage ici. */
const BASE_PATH = "/pilote";

const config: NextConfig = {
  // Sans cela, chaque lien, chaque ressource et le service worker viseraient la racine — c'est
  // à dire la vitrine. La navigation renverrait des pages publiques et la PWA n'aurait pas de
  // service worker du tout. Le chemin vaut aussi en développement, pour que ce qu'on teste soit
  // ce qu'on déploie.
  basePath: BASE_PATH,
  // Export statique : servi par Caddy à la racine du même domaine que l'API. Aucun runtime Node
  // en production, aucune requête d'origine croisée, et une PWA hors ligne devient triviale —
  // il n'y a plus que des fichiers.
  output: "export",
  // Caddy sert des dossiers : /rapports/index.html plutôt que /rapports.html.
  trailingSlash: true,
  images: { unoptimized: true },
  reactStrictMode: true,
};

export default config;
