"use client";

import { useEffect, useMemo, useState } from "react";
import { OrderSheet } from "@/components/OrderSheet";
import {
  groupByProduct,
  loadShowcase,
  price,
  swatch,
  type Showcase as ShowcaseData,
  type ShowcaseItem,
} from "@/lib/showcase";

/**
 * Vitrine.
 *
 * Elle est conçue pour un pouce et un réseau incertain : le contenu éditorial est dans la page
 * dès le premier octet, le catalogue arrive derrière. Une vitrine qui ne montre rien tant que
 * l'API n'a pas répondu perd le visiteur avant de lui avoir montré une seule robe.
 */
export function Showcase() {
  const [data, setData] = useState<ShowcaseData | null>(null);
  const [failed, setFailed] = useState(false);
  const [selected, setSelected] = useState<{ lead: ShowcaseItem; variants: ShowcaseItem[] } | null>(null);

  useEffect(() => {
    loadShowcase().then(setData).catch(() => setFailed(true));
  }, []);

  const groups = useMemo(() => (data ? groupByProduct(data.items) : []), [data]);
  const categories = useMemo(() => [...new Set(groups.map((g) => g.lead.category))].sort(), [groups]);
  const promos = useMemo(() => groups.filter((g) => g.variants.some((v) => v.promotionalPriceXof !== null)), [groups]);

  return (
    <>
      <Hero />
      <Manifesto />

      {promos.length > 0 && <Rail title="En promotion" eyebrow="Prix doux" groups={promos} onPick={setSelected} accent />}

      {categories.map((category) => (
        <Rail
          key={category}
          title={category}
          eyebrow="La sélection"
          groups={groups.filter((g) => g.lead.category === category)}
          onPick={setSelected}
        />
      ))}

      {/* Ni squelette ni tournis : une phrase qui dit ce qui se passe, et le reste de la page
          continue de vivre. */}
      {!data && !failed && (
        <p className="px-6 py-16 text-center text-sm text-muted">Chargement de la sélection…</p>
      )}
      {failed && (
        <p className="px-6 py-16 text-center text-sm text-muted">
          La sélection n’a pas pu être chargée. Passez nous voir en boutique, ou écrivez-nous.
        </p>
      )}

      <Shops shops={data?.shops ?? []} />
      <Footer />

      {selected && (
        <OrderSheet
          product={selected}
          shops={data?.shops ?? []}
          onClose={() => setSelected(null)}
        />
      )}
    </>
  );
}

/**
 * Couverture. Deux plans qui se décalent au défilement : le fond recule, le titre monte plus
 * vite. La profondeur naît de l'écart entre les deux, pas d'une image qui bouge toute seule.
 */
function Hero() {
  return (
    <header className="relative flex min-h-[92dvh] items-end overflow-hidden bg-ink">
      <div
        aria-hidden
        className="parallax-back absolute inset-0"
        style={{
          background:
            "radial-gradient(120% 80% at 15% 5%, #b35f4a 0%, transparent 55%), radial-gradient(100% 70% at 85% 20%, #a9824a 0%, transparent 50%), linear-gradient(160deg, #2a211d 0%, #1a1d1b 60%)",
        }}
      />
      {/* Trame textile : quelques traits obliques suffisent à donner une matière sans image. */}
      <div
        aria-hidden
        className="absolute inset-0 opacity-[0.07]"
        style={{
          backgroundImage: "repeating-linear-gradient(58deg, #fff 0 1px, transparent 1px 9px)",
        }}
      />

      <div className="parallax-front relative w-full px-6 pb-20">
        <p className="mb-4 text-[11px] font-semibold uppercase tracking-[0.32em] text-clay">Abidjan · depuis 2019</p>
        <h1 className="font-display text-[clamp(2.75rem,13vw,4.5rem)] leading-[0.95] text-white">
          La pièce
          <br />
          qu’on vous
          <br />
          <span className="text-clay italic">remarquera</span>
        </h1>
        <p className="mt-6 max-w-sm text-[15px] leading-relaxed text-white/70">
          Vêtements, chaussures et accessoires choisis un par un. Deux boutiques, une même
          exigence : rien que vous ne porteriez pas vous-même.
        </p>
        <a
          href="#selection"
          className="mt-8 inline-flex min-h-12 items-center gap-3 border-b border-clay/50 pb-1 text-sm font-semibold text-white transition-colors hover:border-clay"
        >
          Voir la sélection
          <span aria-hidden className="text-clay">↓</span>
        </a>
      </div>
    </header>
  );
}

function Manifesto() {
  const words = ["Vêtements", "Chaussures", "Accessoires", "Femme", "Homme", "Wax", "Prêt-à-porter"];
  return (
    <section className="overflow-hidden border-y border-line bg-sand py-4" aria-hidden>
      {/* Dupliqué une fois : la translation de -50 % boucle alors sans saut visible. */}
      <div className="marquee flex w-max gap-8 whitespace-nowrap">
        {[...words, ...words].map((word, i) => (
          <span key={i} className="font-display text-xl text-terracotta-dark/70">
            {word} <span className="text-gold">·</span>
          </span>
        ))}
      </div>
    </section>
  );
}

function Rail({
  title,
  eyebrow,
  groups,
  onPick,
  accent,
}: {
  title: string;
  eyebrow: string;
  groups: { lead: ShowcaseItem; variants: ShowcaseItem[] }[];
  onPick: (group: { lead: ShowcaseItem; variants: ShowcaseItem[] }) => void;
  accent?: boolean;
}) {
  if (groups.length === 0) return null;
  return (
    <section id="selection" className="scroll-mt-6 py-12">
      <div className="reveal mb-5 px-6">
        <p className={`text-[11px] font-semibold uppercase tracking-[0.24em] ${accent ? "text-terracotta" : "text-gold"}`}>
          {eyebrow}
        </p>
        <h2 className="font-display text-[28px] leading-tight text-ink">{title}</h2>
      </div>

      {/* Rembourrage latéral porté par des cales : un padding sur le conteneur défilant
          n'apparaît pas à la fin du rail, et la dernière carte colle au bord. */}
      <div className="rail flex gap-4 overflow-x-auto pb-2">
        <span aria-hidden className="w-2 shrink-0" />
        {groups.map((group) => (
          <ProductCard key={group.lead.productId} group={group} onPick={() => onPick(group)} />
        ))}
        <span aria-hidden className="w-2 shrink-0" />
      </div>
    </section>
  );
}

function ProductCard({
  group,
  onPick,
}: {
  group: { lead: ShowcaseItem; variants: ShowcaseItem[] };
  onPick: () => void;
}) {
  const { lead, variants } = group;
  const promo = variants.find((v) => v.promotionalPriceXof !== null);
  const lowest = Math.min(...variants.map((v) => v.promotionalPriceXof ?? v.priceXof));
  const available = variants.some((v) => v.inStock);
  const colors = [...new Set(variants.map((v) => v.color).filter(Boolean))] as string[];

  return (
    <button
      onClick={onPick}
      className="reveal w-[68vw] max-w-[260px] shrink-0 text-left transition-transform active:scale-[0.98]"
    >
      <div
        className="relative mb-3 aspect-[3/4] w-full overflow-hidden rounded-2xl"
        style={{ background: swatch(lead.color, lead.productId) }}
      >
        <div
          aria-hidden
          className="absolute inset-0 opacity-25"
          style={{ backgroundImage: "repeating-linear-gradient(122deg, #fff 0 1px, transparent 1px 7px)" }}
        />
        <span className="absolute bottom-3 left-3 font-display text-[13px] text-white/80">
          {lead.gender ?? lead.category}
        </span>
        {promo && (
          <span className="absolute right-3 top-3 rounded-full bg-white px-2.5 py-1 text-[10px] font-bold uppercase tracking-wider text-terracotta">
            Promo
          </span>
        )}
        {!available && (
          <span className="absolute inset-x-3 top-3 rounded-full bg-ink/70 py-1 text-center text-[10px] font-bold uppercase tracking-wider text-white">
            Sur commande
          </span>
        )}
      </div>

      <h3 className="font-display text-lg leading-tight text-ink">{lead.name}</h3>
      {lead.brand && <p className="text-xs text-faint">{lead.brand}</p>}

      <p className="mt-1 flex items-baseline gap-2">
        <span className="text-[15px] font-semibold text-ink">{price(lowest)}</span>
        {promo && <span className="text-xs text-faint line-through">{price(promo.priceXof)}</span>}
      </p>

      {colors.length > 0 && (
        <div className="mt-2 flex gap-1.5">
          {colors.slice(0, 5).map((color) => (
            <span
              key={color}
              title={color}
              className="h-3 w-3 rounded-full border border-line"
              style={{ background: swatch(color, color) }}
            />
          ))}
        </div>
      )}
    </button>
  );
}

function Shops({ shops }: { shops: { id: string; name: string; city: string | null; address: string | null; phone: string | null }[] }) {
  return (
    <section id="boutiques" className="bg-ink px-6 py-16 text-white">
      <p className="reveal text-[11px] font-semibold uppercase tracking-[0.24em] text-clay">Nous trouver</p>
      <h2 className="reveal mb-8 font-display text-[28px] leading-tight">Deux adresses à Abidjan</h2>

      <div className="space-y-6">
        {shops.map((shop) => (
          <div key={shop.id} className="reveal border-t border-white/15 pt-5">
            <h3 className="font-display text-xl">{shop.name}</h3>
            {shop.city && <p className="text-sm text-white/60">{shop.city}</p>}
            {shop.address && <p className="mt-1 text-sm text-white/60">{shop.address}</p>}
            {shop.phone && (
              <a href={`tel:${shop.phone}`} className="mt-3 inline-block text-sm font-semibold text-clay underline underline-offset-4">
                {shop.phone}
              </a>
            )}
          </div>
        ))}
        {shops.length === 0 && <p className="text-sm text-white/50">Nos adresses arrivent très bientôt.</p>}
      </div>
    </section>
  );
}

function Footer() {
  return (
    <footer className="px-6 py-10 text-center">
      <p className="font-display text-lg text-ink">Bana Shop</p>
      <p className="mt-1 text-xs text-faint">Abidjan · Côte d’Ivoire</p>
      <p className="mt-4 text-xs text-muted">
        Réservez en ligne, essayez en boutique. Le paiement se fait sur place.
      </p>
    </footer>
  );
}
