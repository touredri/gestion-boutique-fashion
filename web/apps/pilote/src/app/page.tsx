"use client";

import Link from "next/link";
import { BottomNav } from "@/components/BottomNav";
import { ResourceState, StaleNote, useResource } from "@/components/DataScreen";
import { Badge, Card, Figure, Screen, SectionLabel } from "@/components/ui";
import { compactMoney, isStale, money, sinceNow, time } from "@/lib/format";
import type { Overview, ShopOverview } from "@/lib/types";

/**
 * L'écran qu'on ouvre vingt fois par jour. Il répond à trois questions, dans cet ordre :
 * combien ai-je vendu, mes caisses sont-elles ouvertes, et y a-t-il quelque chose qui cloche.
 *
 * Pas de graphique ici : sur un téléphone, un chiffre lisible en une seconde vaut mieux qu'une
 * courbe qu'il faut interpréter.
 */
export default function AujourdhuiPage() {
  const { data, error, loading, offline, fetchedAt } = useResource<Overview>("/api/overview", "cache.overview");

  return (
    <>
      <Screen eyebrow="Aujourd'hui" title={greeting()}>
        <StaleNote offline={offline} fetchedAt={fetchedAt} />

        <ResourceState loading={loading && !data} error={error} skeleton={2}>
          {data && (
            <>
              <Card className="mb-4">
                <div className="flex items-start justify-between gap-4">
                  <Figure label="Ventes du jour" value={money(data.salesXof)} hint={`${data.salesCount} vente(s)`} />
                  <Figure label="Encaissé" value={compactMoney(data.collectedXof)} tone="success" />
                </div>
              </Card>

              <SectionLabel>Boutiques</SectionLabel>
              <div className="space-y-3">
                {data.shops.map((shop) => (
                  <ShopCard key={shop.shopId} shop={shop} />
                ))}
                {data.shops.length === 0 && (
                  <Card>
                    <p className="text-sm text-muted">
                      Aucune boutique enregistrée.{" "}
                      <Link href="/boutiques/" className="font-semibold text-terracotta underline underline-offset-2">
                        Créez la première
                      </Link>{" "}
                      pour y rattacher un terminal.
                    </p>
                  </Card>
                )}
              </div>
            </>
          )}
        </ResourceState>
      </Screen>
      <BottomNav />
    </>
  );
}

function ShopCard({ shop }: { shop: ShopOverview }) {
  const stale = isStale(shop.lastSeenAt);
  const alerts = [
    shop.lowStockCount > 0 ? `${shop.lowStockCount} article(s) en alerte stock` : null,
    shop.reservedAdvances > 0 ? `${shop.reservedAdvances} avance(s) en attente` : null,
    // Un terminal muet est l'anomalie la plus coûteuse : elle veut dire qu'on ne voit plus
    // cette boutique, pas qu'elle ne vend pas.
    stale ? `Dernière synchronisation ${sinceNow(shop.lastSeenAt)}` : null,
  ].filter(Boolean) as string[];

  return (
    <Card>
      <div className="mb-3 flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="truncate font-display text-lg leading-tight text-ink">{shop.name}</h3>
          {shop.city && <p className="text-xs text-muted">{shop.city}</p>}
        </div>
        {shop.isCashOpen ? (
          <Badge tone="success">Caisse ouverte</Badge>
        ) : (
          <Badge tone="neutral">Caisse fermée</Badge>
        )}
      </div>

      {shop.isCashOpen && shop.operatorName && (
        <p className="mb-3 text-xs text-muted">
          Tenue par <span className="font-semibold text-ink">{shop.operatorName}</span> depuis {time(shop.openedAt)}
        </p>
      )}

      <div className="grid grid-cols-3 gap-3 border-t border-line pt-3">
        <Compact label="Ventes" value={compactMoney(shop.salesXof)} />
        <Compact label="Encaissé" value={compactMoney(shop.collectedXof)} />
        <Compact label="Dépenses" value={compactMoney(shop.expensesXof)} />
      </div>

      {alerts.length > 0 && (
        <ul className="mt-3 space-y-1 border-t border-line pt-3">
          {alerts.map((alert) => (
            <li key={alert} className="flex items-start gap-2 text-xs text-muted">
              <span aria-hidden className="mt-1.5 h-1 w-1 shrink-0 rounded-full bg-terracotta" />
              {alert}
            </li>
          ))}
        </ul>
      )}

      {shop.outstandingCreditXof > 0 && (
        <p className="mt-3 border-t border-line pt-3 text-xs text-muted">
          Encours clients <span className="tabular font-semibold text-ink">{money(shop.outstandingCreditXof)}</span>
        </p>
      )}
    </Card>
  );
}

function Compact({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <p className="text-[10px] font-semibold uppercase tracking-[0.1em] text-faint">{label}</p>
      <p className="tabular text-sm font-semibold text-ink">{value}</p>
    </div>
  );
}

/** Une salutation datée plutôt qu'un titre figé : l'écran dit d'emblée de quel jour il parle. */
function greeting(): string {
  const now = new Date();
  const moment = now.getHours() < 12 ? "Bonjour" : now.getHours() < 18 ? "Bon après-midi" : "Bonsoir";
  return `${moment}, ${now.toLocaleDateString("fr-FR", { weekday: "long", day: "numeric", month: "long" })}`;
}
