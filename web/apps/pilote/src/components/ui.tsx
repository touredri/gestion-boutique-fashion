"use client";

import type { ReactNode } from "react";

/**
 * Vocabulaire visuel commun. Peu de composants, volontairement : cinq écrans qui se ressemblent
 * valent mieux que cinq écrans chacun inventé.
 */

export function Screen({ title, eyebrow, action, children }: { title: string; eyebrow?: string; action?: ReactNode; children: ReactNode }) {
  return (
    // Le rembourrage bas dégage la barre de navigation, qui est en position fixe.
    <main className="mx-auto max-w-lg px-4 pb-28 pt-6">
      <header className="mb-5 flex items-end justify-between gap-3">
        <div>
          {eyebrow && <p className="text-[11px] font-semibold uppercase tracking-[0.12em] text-faint">{eyebrow}</p>}
          <h1 className="font-display text-[26px] leading-tight text-ink">{title}</h1>
        </div>
        {action}
      </header>
      {children}
    </main>
  );
}

export function Card({ children, className = "" }: { children: ReactNode; className?: string }) {
  return <section className={`rounded-2xl border border-line bg-paper p-4 ${className}`}>{children}</section>;
}

/** Libellé de section. Discret : il structure sans concurrencer les chiffres. */
export function SectionLabel({ children }: { children: ReactNode }) {
  return <h2 className="mb-2 mt-6 px-1 text-[11px] font-semibold uppercase tracking-[0.12em] text-faint">{children}</h2>;
}

/**
 * Chiffre mis en avant. La serif porte la valeur, la légende reste en sans : le contraste de
 * familles fait la hiérarchie sans avoir à colorer quoi que ce soit.
 */
export function Figure({ label, value, hint, tone = "neutral" }: { label: string; value: string; hint?: string; tone?: Tone }) {
  return (
    <div>
      <p className="text-[11px] font-semibold uppercase tracking-[0.1em] text-faint">{label}</p>
      <p className={`tabular font-display text-[28px] leading-tight ${TONE_TEXT[tone]}`}>{value}</p>
      {hint && <p className="mt-0.5 text-xs text-muted">{hint}</p>}
    </div>
  );
}

type Tone = "neutral" | "success" | "danger" | "warning" | "gold";

const TONE_TEXT: Record<Tone, string> = {
  neutral: "text-ink",
  success: "text-success",
  danger: "text-danger",
  warning: "text-warning",
  gold: "text-gold",
};

const TONE_BADGE: Record<Tone, string> = {
  neutral: "bg-ivory text-muted border-line",
  success: "bg-success-soft text-success border-success/30",
  danger: "bg-danger-soft text-danger border-danger/30",
  warning: "bg-warning-soft text-warning border-warning/30",
  gold: "bg-gold-soft text-gold border-gold/30",
};

export function Badge({ children, tone = "neutral" }: { children: ReactNode; tone?: Tone }) {
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] font-semibold ${TONE_BADGE[tone]}`}>
      {children}
    </span>
  );
}

/** Ligne libellé / valeur, l'unité de base de presque tous ces écrans. */
export function Row({ label, value, hint, tone = "neutral" }: { label: ReactNode; value: ReactNode; hint?: ReactNode; tone?: Tone }) {
  return (
    <div className="flex items-baseline justify-between gap-3 border-b border-line py-2.5 last:border-0">
      <div className="min-w-0">
        <p className="truncate text-sm text-ink">{label}</p>
        {hint && <p className="truncate text-xs text-muted">{hint}</p>}
      </div>
      <p className={`tabular shrink-0 text-sm font-semibold ${TONE_TEXT[tone]}`}>{value}</p>
    </div>
  );
}

export function Button({
  children,
  onClick,
  type = "button",
  variant = "quiet",
  disabled,
  className = "",
}: {
  children: ReactNode;
  onClick?: () => void;
  type?: "button" | "submit";
  variant?: "primary" | "quiet" | "danger";
  disabled?: boolean;
  className?: string;
}) {
  // 44 px de haut au minimum : en dessous, la cible devient incertaine au pouce.
  const base = "inline-flex min-h-11 items-center justify-center gap-2 rounded-xl px-4 text-sm font-semibold transition-colors disabled:opacity-45";
  const variants = {
    primary: "bg-terracotta text-white hover:bg-terracotta-dark",
    quiet: "border border-line-strong bg-paper text-ink hover:border-terracotta",
    danger: "border border-danger/30 bg-danger-soft text-danger hover:border-danger",
  };
  return (
    <button type={type} onClick={onClick} disabled={disabled} className={`${base} ${variants[variant]} ${className}`}>
      {children}
    </button>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-semibold text-muted">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-xs text-faint">{hint}</span>}
    </label>
  );
}

export const inputClass =
  // 16 px de police : en dessous, iOS agrandit la page au moment de la saisie.
  "w-full min-h-11 rounded-xl border border-line-strong bg-paper px-3 text-[16px] text-ink outline-none placeholder:text-faint focus:border-terracotta focus:ring-2 focus:ring-terracotta/20";

/** Ce qu'on affiche quand il n'y a rien : une phrase qui explique, jamais un vide muet. */
export function Empty({ children }: { children: ReactNode }) {
  return <p className="px-1 py-8 text-center text-sm text-muted">{children}</p>;
}

export function ErrorNote({ children }: { children: ReactNode }) {
  return (
    <p role="alert" className="rounded-xl border border-danger/30 bg-danger-soft px-3 py-2.5 text-sm text-danger">
      {children}
    </p>
  );
}

/** Silhouette de chargement plutôt qu'un tournis : la page garde sa forme et ne saute pas. */
export function Skeleton({ className = "h-20" }: { className?: string }) {
  return <div className={`animate-pulse rounded-2xl bg-ivory ${className}`} />;
}

/**
 * Barre de proportion. Sans axe ni grille : sur un téléphone, une longueur relative se compare
 * mieux qu'un graphique miniature dont on ne lit ni les échelles ni les libellés.
 */
export function Bar({ value, max, tone = "neutral" }: { value: number; max: number; tone?: Tone }) {
  const width = max > 0 ? Math.max(2, Math.round((Math.abs(value) / max) * 100)) : 0;
  const fill = { neutral: "bg-ink/70", success: "bg-success", danger: "bg-danger", warning: "bg-warning", gold: "bg-gold" }[tone];
  return (
    <div className="mt-1 h-1 w-full overflow-hidden rounded-full bg-ivory">
      <div className={`h-full rounded-full ${fill}`} style={{ width: `${width}%` }} />
    </div>
  );
}
