"use client";

import { useState } from "react";
import { BottomNav } from "@/components/BottomNav";
import { ResourceState, StaleNote, useResource } from "@/components/DataScreen";
import { Badge, Button, Card, ErrorNote, Empty, Screen, SectionLabel } from "@/components/ui";
import { api } from "@/lib/api";
import { dateAndTime, money } from "@/lib/format";
import type { Order, Shop } from "@/lib/types";

/**
 * Commandes du site vitrine.
 *
 * Une commande n'est pas une vente : rien n'est encaissé, rien ne sort du stock. Elle le devient
 * quand la caisse crée la vente correspondante — c'est pourquoi cet écran ne propose pas de la
 * marquer « traitée ». Cocher une case ici laisserait croire qu'un article est vendu alors qu'il
 * est encore en rayon.
 */
export default function CommandesPage() {
  const [showClosed, setShowClosed] = useState(false);
  const orders = useResource<Order[]>(
    `/api/orders?includeClosed=${showClosed}`,
    `cache.orders.${showClosed}`,
  );
  const shops = useResource<Shop[]>("/api/shops", "cache.shops");
  const [error, setError] = useState<string | null>(null);

  const pending = (orders.data ?? []).filter((o) => o.status === 0);
  const processed = (orders.data ?? []).filter((o) => o.status === 1);
  const closed = (orders.data ?? []).filter((o) => o.status >= 2);

  async function act(path: string, body?: unknown) {
    setError(null);
    try {
      await api.post(path, body);
      void orders.reload();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Action impossible.");
    }
  }

  return (
    <>
      <Screen
        eyebrow="Commandes"
        title={pending.length > 0 ? `${pending.length} à traiter` : "Depuis le site vitrine"}
        action={
          <Button onClick={() => setShowClosed(!showClosed)}>{showClosed ? "En cours" : "Historique"}</Button>
        }
      >
        <StaleNote offline={orders.offline} fetchedAt={orders.fetchedAt} />
        {error && <div className="mb-3"><ErrorNote>{error}</ErrorNote></div>}

        <ResourceState
          loading={orders.loading && !orders.data}
          error={orders.error}
          empty={(orders.data ?? []).length === 0}
          skeleton={2}
        >
          {pending.length > 0 && <SectionLabel>À rappeler</SectionLabel>}
          <div className="space-y-3">
            {pending.map((order) => (
              <OrderCard key={order.id} order={order} shops={shops.data ?? []} onAct={act} />
            ))}
          </div>

          {processed.length > 0 && <SectionLabel>Encaissées, à remettre</SectionLabel>}
          <div className="space-y-3">
            {processed.map((order) => (
              <OrderCard key={order.id} order={order} shops={shops.data ?? []} onAct={act} />
            ))}
          </div>

          {showClosed && closed.length > 0 && <SectionLabel>Terminées</SectionLabel>}
          <div className="space-y-3">
            {showClosed && closed.map((order) => (
              <OrderCard key={order.id} order={order} shops={shops.data ?? []} onAct={act} />
            ))}
          </div>

          {(orders.data ?? []).length === 0 && (
            <Empty>Aucune commande. Elles arriveront ici dès qu’une cliente en passera une depuis le site.</Empty>
          )}
        </ResourceState>
      </Screen>
      <BottomNav />
    </>
  );
}

const STATUS: Record<number, { label: string; tone: "warning" | "success" | "neutral" | "danger" }> = {
  0: { label: "En cours", tone: "warning" },
  1: { label: "Traitée", tone: "success" },
  2: { label: "Livrée", tone: "neutral" },
  3: { label: "Annulée", tone: "danger" },
};

function OrderCard({
  order,
  shops,
  onAct,
}: {
  order: Order;
  shops: Shop[];
  onAct: (path: string, body?: unknown) => void;
}) {
  const [rerouting, setRerouting] = useState(false);
  const status = STATUS[order.status] ?? STATUS[0];

  return (
    <Card>
      <div className="mb-2 flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="truncate font-display text-lg leading-tight text-ink">{order.customerName}</h3>
          <a href={`tel:${order.phone}`} className="text-xs font-semibold text-terracotta underline underline-offset-2">
            {order.phone}
          </a>
        </div>
        <Badge tone={status.tone}>{status.label}</Badge>
      </div>

      <p className="mb-3 text-xs text-muted">
        {order.number} · {dateAndTime(order.createdAt)} · {order.shopName}
      </p>

      <div className="border-y border-line py-2">
        {order.lines.map((line) => (
          <div key={line.variantId} className="flex items-baseline justify-between gap-3 py-1">
            <p className="truncate text-sm text-ink">
              <span className="tabular font-semibold">{line.quantity}×</span> {line.description}
            </p>
            <p className="tabular shrink-0 text-sm">{money(line.unitPriceXof * line.quantity)}</p>
          </div>
        ))}
      </div>

      <div className="flex items-baseline justify-between gap-3 pt-2">
        <span className="text-xs text-muted">Total indicatif</span>
        <span className="tabular font-display text-lg text-ink">{money(order.totalXof)}</span>
      </div>

      {order.note && <p className="mt-2 rounded-xl bg-ivory px-3 py-2 text-xs text-muted">« {order.note} »</p>}

      {order.status === 0 && (
        <>
          <p className="mt-3 border-t border-line pt-3 text-xs text-muted">
            Rappelez la cliente pour confirmer. La commande passera en « traitée » quand la caisse
            aura encaissé la vente.
          </p>

          {rerouting ? (
            <div className="mt-3 space-y-2">
              <p className="text-xs font-semibold text-muted">Envoyer vers</p>
              {shops
                .filter((s) => s.id !== order.shopId)
                .map((shop) => (
                  <Button
                    key={shop.id}
                    className="w-full"
                    onClick={() => {
                      setRerouting(false);
                      onAct(`/api/orders/${order.id}/reroute`, { shopId: shop.id });
                    }}
                  >
                    {shop.name}
                  </Button>
                ))}
              <Button className="w-full" onClick={() => setRerouting(false)}>
                Annuler
              </Button>
            </div>
          ) : (
            <div className="mt-3 flex gap-2">
              {shops.length > 1 && (
                <Button className="flex-1" onClick={() => setRerouting(true)}>
                  Changer de boutique
                </Button>
              )}
              <Button
                variant="danger"
                className="flex-1"
                onClick={() => {
                  const reason = prompt("Motif de l’annulation ?") ?? "";
                  if (reason !== null) onAct(`/api/orders/${order.id}/cancel`, { reason });
                }}
              >
                Annuler
              </Button>
            </div>
          )}
        </>
      )}

      {order.cancelReason && <p className="mt-3 text-xs text-danger">Annulée : {order.cancelReason}</p>}
    </Card>
  );
}
