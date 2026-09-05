import type { NextConfig } from "next";

const config: NextConfig = {
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
