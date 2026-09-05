"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { BottomNav } from "@/components/BottomNav";
import { ResourceState, StaleNote, useResource } from "@/components/DataScreen";
import { Badge, Button, Card, ErrorNote, Field, Row, Screen, SectionLabel, inputClass } from "@/components/ui";
import { api, readIdentity, signOut } from "@/lib/api";
import { dateAndTime, isStale, sinceNow } from "@/lib/format";
import type { Device, Shop, StockRow } from "@/lib/types";

/**
 * Boutiques. Création, appairage des terminaux, stock, et compte.
 *
 * L'appairage est le geste le plus délicat de tout le produit : il se fait une fois, souvent
 * debout dans une boutique, avec un code recopié à la main sur un écran tactile. L'écran ne
 * montre donc qu'une chose à la fois et dit à voix haute ce qu'il faut faire ensuite.
 */
export default function BoutiquesPage() {
  const shops = useResource<Shop[]>("/api/shops", "cache.shops");
  const [creating, setCreating] = useState(false);
  const [open, setOpen] = useState<string | null>(null);

  return (
    <>
      <Screen
        eyebrow="Boutiques"
        title="Vos points de vente"
        action={
          <Button onClick={() => setCreating(true)} variant="primary">
            Ajouter
          </Button>
        }
      >
        <StaleNote offline={shops.offline} fetchedAt={shops.fetchedAt} />

        <ResourceState loading={shops.loading && !shops.data} error={shops.error} empty={(shops.data ?? []).length === 0} skeleton={2}>
          <div className="space-y-3">
            {(shops.data ?? []).map((shop) => (
              <ShopPanel key={shop.id} shop={shop} expanded={open === shop.id} onToggle={() => setOpen(open === shop.id ? null : shop.id)} />
            ))}
          </div>
        </ResourceState>

        {creating && <CreateShop onClose={() => setCreating(false)} onCreated={() => { setCreating(false); void shops.reload(); }} />}

        <AccountCard />
      </Screen>
      <BottomNav />
    </>
  );
}

function ShopPanel({ shop, expanded, onToggle }: { shop: Shop; expanded: boolean; onToggle: () => void }) {
  const stale = isStale(shop.lastSeenAt);
  return (
    <Card>
      <button onClick={onToggle} className="flex w-full items-start justify-between gap-3 text-left">
        <div className="min-w-0">
          <h3 className="truncate font-display text-lg leading-tight text-ink">{shop.name}</h3>
          <p className="text-xs text-muted">
            {shop.city ? `${shop.city} · ` : ""}
            {shop.devices} terminal(aux) · {shop.lastSeenAt ? `vu ${sinceNow(shop.lastSeenAt)}` : "jamais synchronisé"}
          </p>
        </div>
        {shop.devices === 0 ? <Badge tone="warning">À appairer</Badge> : stale ? <Badge tone="danger">Silencieux</Badge> : <Badge tone="success">En ligne</Badge>}
      </button>

      {expanded && <ShopDetail shopId={shop.id} />}
    </Card>
  );
}

function ShopDetail({ shopId }: { shopId: string }) {
  const devices = useResource<Device[]>(`/api/shops/${shopId}/devices`, `cache.devices.${shopId}`);
  const stock = useResource<StockRow[]>(`/api/shops/${shopId}/stock-detail?lowOnly=true`, `cache.lowstock.${shopId}`);
  const [code, setCode] = useState<{ code: string; expiresAt: string } | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function generateCode() {
    setError(null);
    try {
      setCode(await api.post<{ code: string; expiresAt: string }>(`/api/shops/${shopId}/enrollment-codes`));
    } catch (e) {
      setError(e instanceof Error ? e.message : "Code impossible à générer.");
    }
  }

  async function revoke(deviceId: string) {
    if (!confirm("Détacher ce terminal ? Il cessera de synchroniser jusqu’à un nouvel appairage.")) return;
    await api.delete(`/api/devices/${deviceId}`);
    void devices.reload();
  }

  return (
    <div className="mt-4 border-t border-line pt-4">
      <SectionLabel>Terminaux</SectionLabel>
      {(devices.data ?? []).map((device) => (
        <Row
          key={device.id}
          label={device.name}
          hint={device.revoked ? "détaché" : `vu ${sinceNow(device.lastSeenAt)}`}
          value={
            device.revoked ? (
              <span className="text-faint">—</span>
            ) : (
              <button onClick={() => void revoke(device.id)} className="text-xs font-semibold text-danger underline underline-offset-2">
                Détacher
              </button>
            )
          }
        />
      ))}
      {(devices.data ?? []).length === 0 && <p className="py-2 text-sm text-muted">Aucun terminal rattaché.</p>}

      {/* Le code est gros, espacé et à durée limitée : il va être recopié à la main. */}
      {code ? (
        <div className="mt-3 rounded-xl border border-terracotta/30 bg-terracotta-soft p-4 text-center">
          <p className="text-[11px] font-semibold uppercase tracking-[0.1em] text-terracotta-dark">Code d’appairage</p>
          <p className="tabular my-2 font-display text-2xl tracking-[0.15em] text-ink">{code.code}</p>
          <p className="text-xs text-terracotta-dark">
            À saisir sur la caisse, dans Paramètres → Boutique en ligne. Valable jusqu’à {dateAndTime(code.expiresAt)}, une seule fois.
          </p>
        </div>
      ) : (
        <Button onClick={() => void generateCode()} className="mt-3 w-full">
          Appairer un terminal
        </Button>
      )}
      {error && <div className="mt-2"><ErrorNote>{error}</ErrorNote></div>}

      <SectionLabel>Stock en alerte</SectionLabel>
      {(stock.data ?? []).slice(0, 10).map((row) => (
        <Row
          key={row.variantId}
          label={row.productName}
          hint={[row.size, row.color, row.sku].filter(Boolean).join(" · ")}
          value={`${row.available} / ${row.threshold}`}
          tone={row.available <= 0 ? "danger" : "warning"}
        />
      ))}
      {(stock.data ?? []).length === 0 && <p className="py-2 text-sm text-muted">Aucune alerte de stock.</p>}
    </div>
  );
}

function CreateShop({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const [name, setName] = useState("");
  const [city, setCity] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.post("/api/shops", { name: name.trim(), city: city.trim() || null });
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
        className="w-full rounded-t-3xl bg-paper p-5"
        style={{ paddingBottom: "calc(env(safe-area-inset-bottom) + 1.25rem)" }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="mx-auto mb-4 h-1 w-10 rounded-full bg-line-strong" />
        <h2 className="mb-1 font-display text-xl text-ink">Nouvelle boutique</h2>
        <p className="mb-4 text-sm text-muted">
          Créez-la ici, puis appairez sa caisse avec le code que cet écran vous donnera.
        </p>

        <div className="space-y-4">
          <Field label="Nom">
            <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} required autoFocus />
          </Field>
          <Field label="Quartier ou ville">
            <input className={inputClass} value={city} onChange={(e) => setCity(e.target.value)} />
          </Field>
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

function AccountCard() {
  const router = useRouter();
  const identity = typeof window === "undefined" ? null : readIdentity();

  return (
    <>
      <SectionLabel>Compte</SectionLabel>
      <Card>
        <Row label={identity?.displayName ?? "—"} hint={identity?.username} value="" />
        <div className="pt-3">
          <Button
            variant="danger"
            className="w-full"
            onClick={async () => {
              await signOut();
              router.replace("/connexion/");
            }}
          >
            Se déconnecter
          </Button>
        </div>
      </Card>
    </>
  );
}
