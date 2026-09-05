"use client";

import { useEffect, useMemo, useState } from "react";
import { APERCU, APERCU_CATEGORIES } from "@/lib/apercu";
import type { ApercuItem } from "@/lib/apercu";

/** Ce qu'une carte sait afficher : un article du catalogue, ou une pièce d'aperçu sans prix. */
type CardItem = ShowcaseItem | ApercuItem;
import { OrderSheet } from "@/components/OrderSheet";
import {
  groupByProduct,
  loadShowcase,
  price,
  swatch,
  type Showcase as ShowcaseData,
  type ShowcaseItem,
  type ShowcaseShop,
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
  const [apercuOuvert, setApercuOuvert] = useState(false);

  useEffect(() => {
    loadShowcase().then(setData).catch(() => setFailed(true));
  }, []);

  const groups = useMemo(() => (data ? groupByProduct(data.items) : []), [data]);
  const categories = useMemo(() => [...new Set(groups.map((g) => g.lead.category))].sort(), [groups]);
  const promos = useMemo(() => groups.filter((g) => g.variants.some((v) => v.promotionalPriceXof !== null)), [groups]);

  // Vide seulement une fois le chargement terminé : pendant l'attente, ni aperçu ni page nue.
  const vide = data !== null && groups.length === 0;
  const apercuGroups = useMemo(() => APERCU.map((item) => ({ lead: item, variants: [item] })), []);

  // La ville n'est pas un réglage : elle vient des boutiques. « Deux adresses à Bamako »
  // aujourd'hui, juste après une troisième boutique demain — une ville écrite en dur est
  // exactement ce qui nous a fait annoncer le mauvais pays pendant des semaines.
  const villes = useMemo(
    () => [...new Set((data?.shops ?? []).map((s) => s.city).filter(Boolean))] as string[],
    [data],
  );

  return (
    <>
      <Hero depuis={data?.settings?.["Vitrine.Depuis"] ?? "2019"} accroche={data?.settings?.["Vitrine.Accroche"]} villes={villes} />
      <Manifesto />

      {/* L'ancre vit ici et non sur un rayon : « Voir la sélection » de la couverture pointait
          vers un identifiant porté par Rail, qui ne se rendait pas quand le catalogue était
          vide — l'unique appel à l'action de la page ne menait donc nulle part. Et plusieurs
          rayons produisaient plusieurs fois le même identifiant. */}
      <div id="selection" className="scroll-mt-6">
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

        {/* Catalogue encore vide : on montre ce qu'on vendra plutôt qu'une page nue. Ces pièces
            ne sont pas en base — elles disparaissent d'elles-mêmes au premier article
            enregistré, sans rien à nettoyer. */}
        {vide &&
          APERCU_CATEGORIES.map((category) => (
            <Rail
              key={category}
              title={category}
              eyebrow="Bientôt en boutique"
              groups={apercuGroups.filter((g) => g.lead.category === category)}
              onPick={() => setApercuOuvert(true)}
              apercu
            />
          ))}
      </div>

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

      <Shops shops={data?.shops ?? []} villes={villes} />
      <Footer villes={villes} />

      {selected && (
        <OrderSheet
          product={selected}
          shops={data?.shops ?? []}
          onClose={() => setSelected(null)}
        />
      )}

      {apercuOuvert && <NoteApercu shops={data?.shops ?? []} onClose={() => setApercuOuvert(false)} />}
    </>
  );
}

/**
 * Couverture. Deux plans qui se décalent au défilement : le fond recule, le titre monte plus
 * vite. La profondeur naît de l'écart entre les deux, pas d'une image qui bouge toute seule.
 */
/** Formule « Bamako » ou « Bamako et Ségou ». Deux villes se lisent, cinq se comptent. */
function libelleVilles(villes: string[], defaut = "Bamako"): string {
  if (villes.length === 0) return defaut;
  if (villes.length === 1) return villes[0];
  if (villes.length === 2) return `${villes[0]} et ${villes[1]}`;
  return villes.slice(0, -1).join(", ") + ` et ${villes[villes.length - 1]}`;
}

function Hero({ depuis, accroche, villes }: { depuis: string; accroche?: string; villes: string[] }) {
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
        <p className="mb-4 text-[11px] font-semibold uppercase tracking-[0.32em] text-clay">
          {libelleVilles(villes)} · depuis {depuis}
        </p>
        <h1 className="font-display text-[clamp(2.75rem,13vw,4.5rem)] leading-[0.95] text-white">
          La pièce
          <br />
          qu’on vous
          <br />
          <span className="text-clay italic">remarquera</span>
        </h1>
        <p className="mt-6 max-w-sm text-[15px] leading-relaxed text-white/70">
          {accroche ??
            "Vêtements, chaussures et accessoires choisis un par un. Deux boutiques, une même exigence : rien que vous ne porteriez pas vous-même."}
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

function Rail<T extends CardItem>({
  title,
  eyebrow,
  groups,
  onPick,
  accent,
  apercu,
}: {
  title: string;
  eyebrow: string;
  groups: { lead: T; variants: T[] }[];
  onPick: (group: { lead: T; variants: T[] }) => void;
  accent?: boolean;
  apercu?: boolean;
}) {
  if (groups.length === 0) return null;
  return (
    <section className="py-12">
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
          <ProductCard key={group.lead.productId} group={group} onPick={() => onPick(group)} apercu={apercu} />
        ))}
        <span aria-hidden className="w-2 shrink-0" />
      </div>
    </section>
  );
}

function ProductCard({
  group,
  onPick,
  apercu,
}: {
  group: { lead: CardItem; variants: CardItem[] };
  onPick: () => void;
  apercu?: boolean;
}) {
  const { lead, variants } = group;
  const promo = variants.find((v) => v.promotionalPriceXof !== null);
  // Les pièces d'aperçu n'ont pas de prix : en annoncer un serait promettre un tarif qu'on ne
  // tiendrait pas au comptoir.
  const tarifs = variants.map((v) => v.promotionalPriceXof ?? v.priceXof).filter((x): x is number => x !== null);
  const lowest = tarifs.length > 0 ? Math.min(...tarifs) : null;
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
            {apercu ? "Bientôt" : "Sur commande"}
          </span>
        )}
      </div>

      <h3 className="font-display text-lg leading-tight text-ink">{lead.name}</h3>
      {lead.brand && <p className="text-xs text-faint">{lead.brand}</p>}

      <p className="mt-1 flex items-baseline gap-2">
        {lowest === null ? (
          <span className="text-[15px] font-semibold text-muted">Bientôt en boutique</span>
        ) : (
          <>
            <span className="text-[15px] font-semibold text-ink">{price(lowest)}</span>
            {promo?.priceXof != null && (
              <span className="text-xs text-faint line-through">{price(promo.priceXof)}</span>
            )}
          </>
        )}
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

function Shops({ shops, villes }: { shops: ShowcaseShop[]; villes: string[] }) {
  // Le nombre s'accorde tout seul : « Deux adresses » aujourd'hui, « Trois » le jour où elle en
  // ouvrira une troisième, sans que personne n'ait à se souvenir de venir corriger ce titre.
  const nombres = ["Nos", "Une", "Deux", "Trois", "Quatre", "Cinq", "Six"];
  const titre = shops.length === 0
    ? "Nos adresses"
    : `${nombres[shops.length] ?? "Nos"} adresse${shops.length > 1 ? "s" : ""} à ${libelleVilles(villes)}`;

  return (
    <section id="boutiques" className="bg-ink px-6 py-16 text-white">
      <p className="reveal text-[11px] font-semibold uppercase tracking-[0.24em] text-clay">Nous trouver</p>
      <h2 className="reveal mb-8 font-display text-[28px] leading-tight">{titre}</h2>

      <div className="space-y-6">
        {shops.map((shop) => (
          <div key={shop.id} className="reveal border-t border-white/15 pt-5">
            <h3 className="font-display text-xl">{shop.name}</h3>
            {shop.city && <p className="text-sm text-white/60">{shop.city}</p>}
            {shop.address && <p className="mt-1 text-sm text-white/60">{shop.address}</p>}
            {shop.hours && <p className="mt-1 text-sm text-white/45">{shop.hours}</p>}
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

/**
 * Ce qu'on montre quand on touche une pièce d'aperçu. Jamais le formulaire de commande : on ne
 * prend pas commande d'un article qui n'existe pas encore, et le dire clairement vaut mieux
 * qu'un bouton qui échouerait.
 */
function NoteApercu({ shops, onClose }: { shops: ShowcaseShop[]; onClose: () => void }) {
  return (
    <div className="fixed inset-0 z-50 flex items-end bg-ink/60 backdrop-blur-sm" onClick={onClose}>
      <div
        className="w-full rounded-t-3xl bg-paper px-6 pb-10 pt-6"
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-label="Pièce à venir"
      >
        <span aria-hidden className="mx-auto mb-5 block h-1 w-10 rounded-full bg-line" />
        <h2 className="font-display text-2xl leading-tight text-ink">Cette pièce arrive bientôt</h2>
        <p className="mt-3 text-sm leading-relaxed text-muted">
          Notre sélection est en cours de mise en ligne. En attendant, passez nous voir : nous
          avons déjà de quoi vous habiller.
        </p>

        {shops.length > 0 && (
          <div className="mt-5 space-y-3">
            {shops.map((shop) => (
              <div key={shop.id} className="border-t border-line pt-3">
                <p className="font-display text-base text-ink">{shop.name}</p>
                {shop.address && <p className="text-sm text-muted">{shop.address}</p>}
                {shop.phone && (
                  <a href={`tel:${shop.phone}`} className="mt-1 inline-block text-sm font-semibold text-terracotta underline underline-offset-4">
                    {shop.phone}
                  </a>
                )}
              </div>
            ))}
          </div>
        )}

        <button
          onClick={onClose}
          className="mt-7 min-h-12 w-full rounded-xl bg-ink text-sm font-semibold text-white"
        >
          Continuer la visite
        </button>
      </div>
    </div>
  );
}

function Footer({ villes }: { villes: string[] }) {
  return (
    <footer className="px-6 py-10 text-center">
      <p className="font-display text-lg text-ink">Bana Shop</p>
      <p className="mt-1 text-xs text-faint">{libelleVilles(villes)} · Mali</p>
      <p className="mt-4 text-xs text-muted">
        Réservez en ligne, essayez en boutique. Le paiement se fait sur place.
      </p>
    </footer>
  );
}
