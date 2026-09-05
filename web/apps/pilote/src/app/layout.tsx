import type { Metadata, Viewport } from "next";
import { ServiceWorker } from "@/components/ServiceWorker";
import "./globals.css";

/** Doit correspondre à basePath dans next.config.ts. Les métadonnées ne sont pas réécrites
 *  par Next avec la même fiabilité que les liens : on préfixe explicitement. */
const BASE = "/pilote";

export const metadata: Metadata = {
  title: "Bana Shop · Pilotage",
  description: "Suivi des boutiques, ventes, stock et caisse.",
  manifest: `${BASE}/manifest.webmanifest`,
  appleWebApp: { capable: true, statusBarStyle: "default", title: "Bana Shop" },
  icons: { icon: `${BASE}/favicon.png`, apple: `${BASE}/apple-touch-icon.png` },
};

export const viewport: Viewport = {
  themeColor: "#f1f3f0",
  // Pas de zoom : l'application se comporte comme une application installée, et les champs de
  // saisie sont déjà à 16 px pour qu'iOS n'agrandisse pas la page de lui-même.
  width: "device-width",
  initialScale: 1,
  maximumScale: 1,
  viewportFit: "cover",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="fr">
      <body>
        {children}
        <ServiceWorker />
      </body>
    </html>
  );
}
