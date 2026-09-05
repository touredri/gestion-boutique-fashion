import type { NextConfig } from "next";

const config: NextConfig = {
  // Export statique, comme l'application de pilotage : servi par Caddy depuis le même domaine
  // que l'API, donc sans origine croisée et sans runtime à surveiller.
  output: "export",
  trailingSlash: true,
  images: { unoptimized: true },
  reactStrictMode: true,
};

export default config;
