"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

/**
 * Barre de navigation basse. Cinq destinations, jamais plus : au-delà, les cibles deviennent
 * trop étroites pour un pouce et l'ensemble se lit comme une liste plutôt qu'une carte mentale.
 *
 * L'onglet actif n'est pas un aplat de couleur mais un trait fin et une encre pleine. Un onglet
 * peint attire l'œil en permanence sur un écran qu'on consulte toute la journée ; ici c'est le
 * contenu qui doit retenir l'attention, pas le châssis.
 */
const TABS = [
  { href: "/", label: "Aujourd'hui", icon: PulseIcon },
  { href: "/commandes/", label: "Commandes", icon: BagIcon },
  { href: "/catalogue/", label: "Catalogue", icon: TagIcon },
  { href: "/rapports/", label: "Rapports", icon: ChartIcon },
  { href: "/boutiques/", label: "Boutiques", icon: StoreIcon },
] as const;

export function BottomNav() {
  const pathname = usePathname();

  return (
    <nav
      aria-label="Navigation principale"
      className="fixed inset-x-0 bottom-0 z-40 border-t border-line bg-paper/95 backdrop-blur"
      // La zone d'accueil des iPhone mange le bas de l'écran : sans cette marge, le dernier
      // onglet devient inatteignable.
      style={{ paddingBottom: "env(safe-area-inset-bottom)" }}
    >
      <ul className="mx-auto flex max-w-lg">
        {TABS.map(({ href, label, icon: Icon }) => {
          const active = href === "/" ? pathname === "/" : pathname.startsWith(href);
          return (
            <li key={href} className="flex-1">
              <Link
                href={href}
                aria-current={active ? "page" : undefined}
                className="relative flex flex-col items-center gap-1 px-1 py-2.5 transition-colors"
              >
                <span
                  aria-hidden
                  className={`absolute inset-x-4 top-0 h-px transition-colors ${active ? "bg-terracotta" : "bg-transparent"}`}
                />
                <Icon className={active ? "text-terracotta" : "text-faint"} />
                <span className={`text-[11px] leading-none ${active ? "font-semibold text-ink" : "text-muted"}`}>
                  {label}
                </span>
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}

/* Icônes dessinées à la main plutôt qu'une bibliothèque : cinq traits suffisent, et un paquet
   d'icônes générique se reconnaît au premier coup d'œil. */

type IconProps = { className?: string };
const stroke = {
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 1.6,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
};

function PulseIcon({ className }: IconProps) {
  return (
    <svg viewBox="0 0 24 24" className={`h-5 w-5 ${className}`} aria-hidden {...stroke}>
      <path d="M3 12h4l2.5-6 5 12 2.5-6h4" />
    </svg>
  );
}

function BagIcon({ className }: IconProps) {
  return (
    <svg viewBox="0 0 24 24" className={`h-5 w-5 ${className}`} aria-hidden {...stroke}>
      <path d="M5 8h14l-1 12H6L5 8Z" />
      <path d="M9 8V6a3 3 0 0 1 6 0v2" />
    </svg>
  );
}

function TagIcon({ className }: IconProps) {
  return (
    <svg viewBox="0 0 24 24" className={`h-5 w-5 ${className}`} aria-hidden {...stroke}>
      <path d="M4 12.5V5a1 1 0 0 1 1-1h7.5L20 11.5 12.5 19 4 12.5Z" />
      <circle cx="8.5" cy="8.5" r="1.2" />
    </svg>
  );
}

function ChartIcon({ className }: IconProps) {
  return (
    <svg viewBox="0 0 24 24" className={`h-5 w-5 ${className}`} aria-hidden {...stroke}>
      <path d="M4 20V10M10 20V4M16 20v-7M22 20H2" />
    </svg>
  );
}

function StoreIcon({ className }: IconProps) {
  return (
    <svg viewBox="0 0 24 24" className={`h-5 w-5 ${className}`} aria-hidden {...stroke}>
      <path d="M4 10v10h16V10" />
      <path d="M3 10 5 4h14l2 6a3 3 0 0 1-6 0 3 3 0 0 1-6 0 3 3 0 0 1-6 0Z" />
    </svg>
  );
}
