"use client";

import { useState } from "react";
import { Button, ErrorNote, Field, SectionLabel, inputClass } from "@/components/ui";
import { api } from "@/lib/api";
import type { Category, Shop } from "@/lib/types";

type Line = { size: string; color: string; sku: string; cost: string; price: string };

/**
 * Création d'un article. Le catalogue n'étant plus modifiable depuis les caisses, c'est le seul
 * endroit où un produit naît.
 *
 * Un article et ses déclinaisons se saisissent d'un seul geste : créer la robe puis revenir
 * ajouter ses tailles une à une serait le meilleur moyen d'en oublier.
 */
export function NewProduct({
  categories,
  shops,
  onClose,
  onCreated,
}: {
  categories: Category[];
  shops: Shop[];
  onClose: () => void;
  onCreated: () => void;
}) {
  const [name, setName] = useState("");
  const [categoryName, setCategoryName] = useState(categories[0]?.name ?? "Vêtements");
  const [scope, setScope] = useState("");
  const [lines, setLines] = useState<Line[]>([{ size: "", color: "", sku: "", cost: "", price: "" }]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  function update(index: number, patch: Partial<Line>) {
    setLines(lines.map((line, i) => (i === index ? { ...line, ...patch } : line)));
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const filled = lines.filter((l) => l.price.trim() !== "");
      if (filled.length === 0) throw new Error("Indiquez au moins un prix de vente.");

      const existing = categories.find((c) => c.name.toLowerCase() === categoryName.trim().toLowerCase());
      const categoryId = existing?.id ?? crypto.randomUUID();
      const productId = crypto.randomUUID();

      await api.put("/api/catalog", {
        // La catégorie n'est envoyée que si elle est nouvelle : la réécrire ferait avancer son
        // curseur et la ferait redescendre sur toutes les caisses pour rien.
        categories: existing ? [] : [{ id: categoryId, name: categoryName.trim(), isActive: true }],
        products: [
          {
            id: productId,
            categoryId,
            name: name.trim(),
            brand: null,
            description: null,
            subCategory: null,
            gender: null,
            season: null,
            type: 0,
            isActive: true,
            shopId: scope === "" ? null : scope,
          },
        ],
        variants: filled.map((line, index) => ({
          id: crypto.randomUUID(),
          productId,
          // Le SKU est dérivé du nom si on ne le saisit pas : personne n'a envie d'en inventer un
          // debout dans une boutique.
          sku: line.sku.trim() || buildSku(name, line, index),
          barcode: null,
          size: line.size.trim() || null,
          color: line.color.trim() || null,
          material: null,
          supplier: null,
          costXof: Number(line.cost || 0),
          priceXof: Number(line.price),
          promotionalPriceXof: null,
          promotionStartsAt: null,
          promotionEndsAt: null,
          lowStockThreshold: 2,
          isActive: true,
        })),
      });
      onCreated();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Création impossible.");
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-end bg-ink/40" onClick={onClose}>
      <form
        onSubmit={submit}
        className="max-h-[90dvh] w-full overflow-y-auto rounded-t-3xl bg-paper p-5"
        style={{ paddingBottom: "calc(env(safe-area-inset-bottom) + 1.25rem)" }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mx-auto mb-4 h-1 w-10 rounded-full bg-line-strong" />
        <h2 className="mb-1 font-display text-xl text-ink">Nouvel article</h2>
        <p className="mb-4 text-sm text-muted">Il descendra sur les caisses concernées à leur prochaine synchronisation.</p>

        <div className="space-y-4">
          <Field label="Nom de l’article">
            <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} required autoFocus placeholder="Robe longue en wax" />
          </Field>

          <Field label="Catégorie" hint="Saisissez un nom nouveau pour créer la catégorie.">
            <input className={inputClass} value={categoryName} onChange={(e) => setCategoryName(e.target.value)} list="categories" required />
            <datalist id="categories">
              {categories.map((c) => (
                <option key={c.id} value={c.name} />
              ))}
            </datalist>
          </Field>

          <Field label="Disponibilité">
            <select className={inputClass} value={scope} onChange={(e) => setScope(e.target.value)}>
              <option value="">Toutes les boutiques</option>
              {shops.map((shop) => (
                <option key={shop.id} value={shop.id}>
                  {shop.name} seulement
                </option>
              ))}
            </select>
          </Field>

          <SectionLabel>Déclinaisons</SectionLabel>
          {lines.map((line, index) => (
            <div key={index} className="rounded-xl border border-line bg-ivory p-3">
              <div className="grid grid-cols-2 gap-2">
                <Field label="Taille">
                  <input className={inputClass} value={line.size} onChange={(e) => update(index, { size: e.target.value })} placeholder="M" />
                </Field>
                <Field label="Couleur">
                  <input className={inputClass} value={line.color} onChange={(e) => update(index, { color: e.target.value })} placeholder="Rouge" />
                </Field>
                <Field label="Coût d’achat">
                  <input
                    className={inputClass}
                    inputMode="numeric"
                    value={line.cost}
                    onChange={(e) => update(index, { cost: e.target.value.replace(/\D/g, "") })}
                  />
                </Field>
                <Field label="Prix de vente">
                  <input
                    className={inputClass}
                    inputMode="numeric"
                    value={line.price}
                    onChange={(e) => update(index, { price: e.target.value.replace(/\D/g, "") })}
                    required={index === 0}
                  />
                </Field>
              </div>
              {lines.length > 1 && (
                <button
                  type="button"
                  onClick={() => setLines(lines.filter((_, i) => i !== index))}
                  className="mt-2 text-xs font-semibold text-danger underline underline-offset-2"
                >
                  Retirer cette déclinaison
                </button>
              )}
            </div>
          ))}

          <Button onClick={() => setLines([...lines, { size: "", color: "", sku: "", cost: "", price: "" }])} className="w-full">
            Ajouter une déclinaison
          </Button>

          {error && <ErrorNote>{error}</ErrorNote>}

          <div className="flex gap-2">
            <Button onClick={onClose} className="flex-1">
              Annuler
            </Button>
            <Button type="submit" variant="primary" disabled={busy || !name.trim()} className="flex-1">
              {busy ? "Création…" : "Créer"}
            </Button>
          </div>
        </div>
      </form>
    </div>
  );
}

/** Référence dérivée du nom, majuscules sans accents, suffixée pour rester unique. */
function buildSku(name: string, line: Line, index: number): string {
  const slug = (value: string) =>
    value
      .normalize("NFD")
      .replace(/[̀-ͯ]/g, "")
      .toUpperCase()
      .replace(/[^A-Z0-9]+/g, "-")
      .replace(/^-|-$/g, "");
  const parts = [slug(name).slice(0, 20), slug(line.size), slug(line.color)].filter(Boolean);
  return `${parts.join("-") || "ART"}-${index + 1}`;
}
