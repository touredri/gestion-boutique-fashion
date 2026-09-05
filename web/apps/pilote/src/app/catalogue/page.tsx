"use client";

import { useMemo, useState } from "react";
import { BottomNav } from "@/components/BottomNav";
import { ResourceState, StaleNote, useResource } from "@/components/DataScreen";
import { Badge, Button, Card, ErrorNote, Field, Screen, SectionLabel, inputClass } from "@/components/ui";
import { api } from "@/lib/api";
import { money } from "@/lib/format";
import type { Catalog, Product, Shop, Variant } from "@/lib/types";

/**
 * Catalogue. Le serveur en est la seule source : les terminaux le reçoivent et ne l'écrivent
 * plus. C'est donc ici, et nulle part ailleurs, qu'on change un prix.
 *
 * Un article est global ou exclusif à une boutique — une pièce qu'on ne vend qu'à Marcory n'a
 * rien à faire sur la caisse de Yopougon.
 */
export default function CataloguePage() {
  const catalog = useResource<Catalog>("/api/catalog", "cache.catalog");
  const shops = useResource<Shop[]>("/api/shops", "cache.shops");
  const [search, setSearch] = useState("");
  const [editing, setEditing] = useState<Product | null>(null);

  const grouped = useMemo(() => {
    if (!catalog.data) return [];
    const term = search.trim().toLowerCase();
    return catalog.data.products
      .filter((p) => !term || p.name.toLowerCase().includes(term) || (p.brand ?? "").toLowerCase().includes(term))
      .map((product) => ({
        product,
        variants: catalog.data!.variants.filter((v) => v.productId === product.id),
      }))
      .sort((a, b) => a.product.name.localeCompare(b.product.name, "fr"));
  }, [catalog.data, search]);

  return (
    <>
      <Screen eyebrow="Catalogue" title="Articles et prix">
        <StaleNote offline={catalog.offline} fetchedAt={catalog.fetchedAt} />

        <input
          className={`${inputClass} mb-4`}
          placeholder="Rechercher un article…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          type="search"
        />

        <ResourceState loading={catalog.loading && !catalog.data} error={catalog.error} empty={grouped.length === 0} skeleton={4}>
          <div className="space-y-3">
            {grouped.map(({ product, variants }) => (
              <ProductCard
                key={product.id}
                product={product}
                variants={variants}
                shopName={shops.data?.find((s) => s.id === product.shopId)?.name}
                onEdit={() => setEditing(product)}
              />
            ))}
          </div>
        </ResourceState>
      </Screen>

      {editing && catalog.data && (
        <EditSheet
          product={editing}
          variants={catalog.data.variants.filter((v) => v.productId === editing.id)}
          shops={shops.data ?? []}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            void catalog.reload();
          }}
        />
      )}

      <BottomNav />
    </>
  );
}

function ProductCard({
  product,
  variants,
  shopName,
  onEdit,
}: {
  product: Product;
  variants: Variant[];
  shopName?: string;
  onEdit: () => void;
}) {
  const prices = variants.map((v) => v.priceXof);
  const range =
    prices.length === 0
      ? "—"
      : Math.min(...prices) === Math.max(...prices)
        ? money(prices[0])
        : `${money(Math.min(...prices))} – ${money(Math.max(...prices))}`;

  return (
    <Card>
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="truncate font-display text-lg leading-tight text-ink">{product.name}</h3>
          <p className="text-xs text-muted">
            {variants.length} variante(s)
            {product.brand ? ` · ${product.brand}` : ""}
          </p>
        </div>
        <button onClick={onEdit} className="shrink-0 text-xs font-semibold text-terracotta underline underline-offset-2">
          Modifier
        </button>
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-2 border-t border-line pt-3">
        <span className="tabular text-sm font-semibold text-ink">{range}</span>
        {shopName ? <Badge tone="gold">{shopName} seulement</Badge> : <Badge tone="neutral">Toutes les boutiques</Badge>}
        {!product.isActive && <Badge tone="danger">Retiré</Badge>}
      </div>
    </Card>
  );
}

/**
 * Feuille d'édition. Elle monte du bas plutôt que de s'ouvrir en plein écran : le pouce reste
 * près des champs, et le contexte derrière ne disparaît pas.
 */
function EditSheet({
  product,
  variants,
  shops,
  onClose,
  onSaved,
}: {
  product: Product;
  variants: Variant[];
  shops: Shop[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const [name, setName] = useState(product.name);
  const [scope, setScope] = useState<string>(product.shopId ?? "");
  const [active, setActive] = useState(product.isActive);
  const [prices, setPrices] = useState<Record<string, string>>(
    Object.fromEntries(variants.map((v) => [v.id, String(v.priceXof)])),
  );
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await api.put("/api/catalog", {
        products: [{ ...product, name: name.trim(), isActive: active, shopId: scope === "" ? null : scope }],
        variants: variants.map((v) => ({ ...v, priceXof: Number(prices[v.id] ?? v.priceXof) })),
      });
      onSaved();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Enregistrement impossible.");
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-end bg-ink/40" onClick={onClose}>
      <div
        className="max-h-[88dvh] w-full overflow-y-auto rounded-t-3xl bg-paper p-5"
        style={{ paddingBottom: "calc(env(safe-area-inset-bottom) + 1.25rem)" }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mx-auto mb-4 h-1 w-10 rounded-full bg-line-strong" />
        <h2 className="mb-4 font-display text-xl text-ink">Modifier l’article</h2>

        <div className="space-y-4">
          <Field label="Nom">
            <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} />
          </Field>

          <Field label="Disponibilité" hint="Un article exclusif ne descend que sur la caisse de sa boutique.">
            <select className={inputClass} value={scope} onChange={(e) => setScope(e.target.value)}>
              <option value="">Toutes les boutiques</option>
              {shops.map((shop) => (
                <option key={shop.id} value={shop.id}>
                  {shop.name} seulement
                </option>
              ))}
            </select>
          </Field>

          <SectionLabel>Prix de vente</SectionLabel>
          <div className="space-y-3">
            {variants.map((variant) => (
              <Field key={variant.id} label={[variant.size, variant.color].filter(Boolean).join(" · ") || variant.sku}>
                <input
                  className={inputClass}
                  inputMode="numeric"
                  value={prices[variant.id] ?? ""}
                  onChange={(e) => setPrices({ ...prices, [variant.id]: e.target.value.replace(/\D/g, "") })}
                />
              </Field>
            ))}
          </div>

          <label className="flex items-center gap-3 rounded-xl border border-line px-3 py-3">
            <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} className="h-4 w-4 accent-[#b35f4a]" />
            <span className="text-sm text-ink">Article en vente</span>
          </label>

          {error && <ErrorNote>{error}</ErrorNote>}

          <div className="flex gap-2 pt-1">
            <Button onClick={onClose} className="flex-1">
              Annuler
            </Button>
            <Button onClick={save} variant="primary" disabled={busy} className="flex-1">
              {busy ? "Enregistrement…" : "Enregistrer"}
            </Button>
          </div>
          <p className="text-center text-xs text-faint">
            La modification redescend sur les caisses concernées à leur prochaine synchronisation.
          </p>
        </div>
      </div>
    </div>
  );
}
