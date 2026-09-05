"use client";

import { useState } from "react";
import { placeOrder, price, swatch, type ShowcaseItem, type ShowcaseShop } from "@/lib/showcase";

/**
 * Feuille de réservation.
 *
 * Rien n'est encaissé ici. La cliente choisit sa taille, sa boutique, laisse un numéro : la
 * boutique la rappelle, elle essaie, elle paie sur place. Demander une carte bancaire pour une
 * robe qu'on n'a pas essayée ferait fuir la moitié des visiteuses.
 */
export function OrderSheet({
  product,
  shops,
  onClose,
}: {
  product: { lead: ShowcaseItem; variants: ShowcaseItem[] };
  shops: ShowcaseShop[];
  onClose: () => void;
}) {
  const { lead, variants } = product;
  const [variant, setVariant] = useState<ShowcaseItem>(variants.find((v) => v.inStock) ?? variants[0]);
  const [quantity, setQuantity] = useState(1);
  const [shopId, setShopId] = useState(() => variant.shopIds[0] ?? shops[0]?.id ?? "");
  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<{ number: string; shopName: string } | null>(null);

  const unit = variant.promotionalPriceXof ?? variant.priceXof;

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const result = await placeOrder({
        shopId,
        customerName: name.trim(),
        phone: phone.trim(),
        note: note.trim() || undefined,
        lines: [{ variantId: variant.variantId, quantity }],
      });
      setDone({ number: result.number, shopName: result.shopName });
    } catch (e) {
      setError(e instanceof Error ? e.message : "Envoi impossible.");
      setBusy(false);
    }
  }

  // La confirmation remplace le formulaire au lieu de s'y ajouter : la référence est la seule
  // chose qui compte à cet instant, et c'est ce qu'elle donnera au téléphone.
  if (done) {
    return (
      <Sheet onClose={onClose}>
        <div className="py-4 text-center">
          <div className="mx-auto mb-5 flex h-14 w-14 items-center justify-center rounded-full bg-terracotta/10 text-2xl text-terracotta">
            ✓
          </div>
          <h2 className="font-display text-2xl text-ink">C’est réservé</h2>
          <p className="mt-2 text-sm text-muted">
            {done.shopName} vous rappelle pour confirmer. Gardez cette référence.
          </p>
          <p className="my-5 font-display text-2xl tracking-[0.12em] text-terracotta">{done.number}</p>
          <p className="text-xs text-faint">Le paiement se fait en boutique, après essayage.</p>
          <button
            onClick={onClose}
            className="mt-6 min-h-12 w-full rounded-xl bg-ink text-sm font-semibold text-white"
          >
            Continuer à parcourir
          </button>
        </div>
      </Sheet>
    );
  }

  return (
    <Sheet onClose={onClose}>
      <div className="mb-5 flex gap-4">
        <div
          className="h-24 w-20 shrink-0 rounded-xl"
          style={{ background: swatch(variant.color, lead.productId) }}
        />
        <div className="min-w-0">
          <h2 className="font-display text-xl leading-tight text-ink">{lead.name}</h2>
          {lead.brand && <p className="text-xs text-faint">{lead.brand}</p>}
          <p className="mt-2 flex items-baseline gap-2">
            <span className="text-lg font-semibold text-ink">{price(unit)}</span>
            {variant.promotionalPriceXof !== null && (
              <span className="text-xs text-faint line-through">{price(variant.priceXof)}</span>
            )}
          </p>
          {lead.description && <p className="mt-2 text-xs leading-relaxed text-muted">{lead.description}</p>}
        </div>
      </div>

      <form onSubmit={submit} className="space-y-4">
        {variants.length > 1 && (
          <Group label="Taille et couleur">
            <div className="flex flex-wrap gap-2">
              {variants.map((v) => (
                <button
                  key={v.variantId}
                  type="button"
                  onClick={() => setVariant(v)}
                  className={`min-h-11 rounded-xl border px-3 text-sm transition-colors ${
                    v.variantId === variant.variantId
                      ? "border-terracotta bg-terracotta/5 font-semibold text-ink"
                      : "border-line text-muted"
                  }`}
                >
                  {[v.size, v.color].filter(Boolean).join(" · ") || "Unique"}
                  {/* Dire « sur commande » plutôt que masquer : l'article existe, il faut juste
                      le faire venir, et la boutique préfère le savoir. */}
                  {!v.inStock && <span className="ml-1 text-[10px] text-faint">sur commande</span>}
                </button>
              ))}
            </div>
          </Group>
        )}

        <Group label="Quantité">
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={() => setQuantity(Math.max(1, quantity - 1))}
              className="h-11 w-11 rounded-xl border border-line text-lg"
              aria-label="Diminuer"
            >
              −
            </button>
            <span className="w-8 text-center text-lg font-semibold">{quantity}</span>
            <button
              type="button"
              onClick={() => setQuantity(quantity + 1)}
              className="h-11 w-11 rounded-xl border border-line text-lg"
              aria-label="Augmenter"
            >
              +
            </button>
            <span className="ml-auto text-sm font-semibold text-ink">{price(unit * quantity)}</span>
          </div>
        </Group>

        <Group label="Retrait en boutique">
          <select value={shopId} onChange={(e) => setShopId(e.target.value)} className={input} required>
            {shops.map((shop) => (
              <option key={shop.id} value={shop.id}>
                {shop.name}
                {shop.city ? ` · ${shop.city}` : ""}
              </option>
            ))}
          </select>
        </Group>

        <Group label="Votre nom">
          <input className={input} value={name} onChange={(e) => setName(e.target.value)} required autoComplete="name" />
        </Group>

        <Group label="Votre téléphone">
          <input
            className={input}
            value={phone}
            onChange={(e) => setPhone(e.target.value)}
            required
            inputMode="tel"
            autoComplete="tel"
            placeholder="07 00 00 00 00"
          />
        </Group>

        <Group label="Un mot pour la boutique (facultatif)">
          <textarea className={`${input} min-h-20 py-2`} value={note} onChange={(e) => setNote(e.target.value)} />
        </Group>

        {error && (
          <p role="alert" className="rounded-xl bg-terracotta/10 px-3 py-2.5 text-sm text-terracotta-dark">
            {error}
          </p>
        )}

        <button
          type="submit"
          disabled={busy || !name.trim() || !phone.trim() || !shopId}
          className="min-h-13 w-full rounded-xl bg-terracotta py-3.5 text-sm font-semibold text-white disabled:opacity-45"
        >
          {busy ? "Envoi…" : "Réserver cette pièce"}
        </button>
        <p className="text-center text-xs text-faint">
          Aucun paiement en ligne. La boutique vous rappelle pour confirmer.
        </p>
      </form>
    </Sheet>
  );
}

function Sheet({ children, onClose }: { children: React.ReactNode; onClose: () => void }) {
  return (
    <div className="fixed inset-0 z-50 flex items-end bg-ink/50" onClick={onClose}>
      <div
        className="max-h-[92dvh] w-full overflow-y-auto rounded-t-3xl bg-paper p-5"
        style={{ paddingBottom: "calc(env(safe-area-inset-bottom) + 1.25rem)" }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mx-auto mb-4 h-1 w-10 rounded-full bg-line" />
        {children}
      </div>
    </div>
  );
}

function Group({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-xs font-semibold text-muted">{label}</span>
      {children}
    </label>
  );
}

// 16 px : en dessous, iOS agrandit la page dès qu'on touche un champ.
const input =
  "w-full min-h-11 rounded-xl border border-line bg-paper px-3 text-[16px] text-ink outline-none focus:border-terracotta";
