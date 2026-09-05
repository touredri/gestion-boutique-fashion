import type { Metadata, Viewport } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Bana Shop · Mode femme, homme et accessoires à Abidjan",
  description:
    "Vêtements, chaussures et accessoires sélectionnés à Abidjan. Découvrez les pièces disponibles dans nos boutiques de Marcory et Yopougon, et réservez la vôtre en un message.",
  openGraph: {
    title: "Bana Shop",
    description: "Mode femme, homme et accessoires à Abidjan.",
    locale: "fr_CI",
    type: "website",
  },
  icons: { icon: "/favicon.png", apple: "/apple-touch-icon.png" },
};

export const viewport: Viewport = {
  themeColor: "#f7f5f1",
  width: "device-width",
  initialScale: 1,
  viewportFit: "cover",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="fr">
      <body>{children}</body>
    </html>
  );
}
