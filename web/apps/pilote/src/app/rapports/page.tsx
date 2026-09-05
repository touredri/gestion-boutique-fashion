"use client";

import { useState } from "react";
import { BottomNav } from "@/components/BottomNav";
import { ResourceState, StaleNote, useResource } from "@/components/DataScreen";
import { Badge, Bar, Card, Figure, Row, Screen, SectionLabel } from "@/components/ui";
import { compactMoney, money, quantity, shortDate } from "@/lib/format";
import type { CashClosing, LabelledAmount, ReportSummary } from "@/lib/types";

const PERIODS = [
  { key: "today", label: "Jour", days: 0 },
  { key: "7", label: "7 j", days: 6 },
  { key: "30", label: "30 j", days: 29 },
] as const;

/**
 * Rapports. L'ordre suit la question qu'on se pose vraiment : combien est entré, ce qu'il en
 * reste, ce qui se vend, et qui vend.
 */
export default function RapportsPage() {
  const [period, setPeriod] = useState<(typeof PERIODS)[number]>(PERIODS[0]);
  const range = window_(period.days);
  const query = `from=${encodeURIComponent(range.from)}&to=${encodeURIComponent(range.to)}`;

  const report = useResource<ReportSummary>(`/api/reports?${query}`, `cache.reports.${period.key}`);
  const closings = useResource<CashClosing[]>(`/api/cash-closings?${query}`, `cache.closings.${period.key}`);

  return (
    <>
      <Screen
        eyebrow="Rapports"
        title={period.days === 0 ? "La journée" : `Les ${period.days + 1} derniers jours`}
        action={
          <div className="flex rounded-xl border border-line bg-paper p-0.5">
            {PERIODS.map((p) => (
              <button
                key={p.key}
                onClick={() => setPeriod(p)}
                className={`min-h-9 rounded-[10px] px-3 text-xs font-semibold transition-colors ${
                  p.key === period.key ? "bg-ink text-white" : "text-muted"
                }`}
              >
                {p.label}
              </button>
            ))}
          </div>
        }
      >
        <StaleNote offline={report.offline} fetchedAt={report.fetchedAt} />

        <ResourceState loading={report.loading && !report.data} error={report.error} skeleton={3}>
          {report.data && (
            <>
              <Card className="mb-3">
                <div className="grid grid-cols-2 gap-4">
                  <Figure label="Chiffre d'affaires" value={money(report.data.salesXof)} hint={`${report.data.salesCount} vente(s)`} />
                  <Figure label="Encaissé" value={compactMoney(report.data.collectedXof)} tone="success" />
                </div>
                <div className="mt-4 grid grid-cols-2 gap-4 border-t border-line pt-4">
                  <Figure label="Marge brute" value={compactMoney(report.data.grossMarginXof)} tone="gold" hint={`Coût ${compactMoney(report.data.costXof)}`} />
                  <Figure
                    label="Bénéfice estimé"
                    value={compactMoney(report.data.estimatedProfitXof)}
                    tone={report.data.estimatedProfitXof >= 0 ? "neutral" : "danger"}
                    hint={`Dépenses ${compactMoney(report.data.expensesXof)}`}
                  />
                </div>
              </Card>

              {/* Dire qu'on ne sait pas vaut mieux qu'afficher un bénéfice flatteur et faux. */}
              {report.data.costWarning && (
                <p className="mb-3 rounded-xl border border-warning/30 bg-warning-soft px-3 py-2.5 text-xs text-warning">
                  Certaines ventes n’ont pas de coût d’achat renseigné : la marge et le bénéfice sont
                  calculés partiellement et sont donc surévalués.
                </p>
              )}

              {report.data.bestSeller && (
                <Card className="mb-3 border-success/30 bg-success-soft">
                  <p className="text-[11px] font-semibold uppercase tracking-[0.1em] text-success">Article le mieux vendu</p>
                  <p className="mt-1 font-display text-xl leading-tight text-ink">{report.data.bestSeller.label}</p>
                  <p className="tabular mt-0.5 text-sm text-success">
                    {quantity(report.data.bestSeller.quantity)} pièce(s) · {money(report.data.bestSeller.valueXof)}
                  </p>
                </Card>
              )}

              <Ranking title="Par boutique" rows={report.data.byShop} suffix="vente(s)" />
              <Ranking title="Par vendeur" rows={report.data.byOperator} suffix="vente(s)" />
              <Ranking title="Meilleures ventes" rows={report.data.topProducts} suffix="pièce(s)" />

              <SectionLabel>Modes de paiement</SectionLabel>
              <Card>
                {report.data.byPaymentMode.map((row) => (
                  <Row key={row.label} label={row.label} value={money(row.valueXof)} />
                ))}
                {report.data.byPaymentMode.length === 0 && <p className="py-2 text-sm text-muted">Aucun encaissement.</p>}
              </Card>

              <SectionLabel>Clôtures de caisse</SectionLabel>
              <Card>
                {(closings.data ?? []).map((closing) => (
                  <Row
                    key={closing.id}
                    label={`${closing.shopName} · ${closing.operatorName}`}
                    hint={`${shortDate(closing.closedAt)} · attendu ${compactMoney(closing.expectedCashXof)}${
                      closing.differenceReason ? ` · ${closing.differenceReason}` : ""
                    }`}
                    value={closing.differenceXof === 0 ? "juste" : money(closing.differenceXof)}
                    tone={closing.differenceXof === 0 ? "success" : "danger"}
                  />
                ))}
                {(closings.data ?? []).length === 0 && <p className="py-2 text-sm text-muted">Aucune clôture sur la période.</p>}
              </Card>
            </>
          )}
        </ResourceState>
      </Screen>
      <BottomNav />
    </>
  );
}

/**
 * Classement à barres proportionnelles. On compare des longueurs, pas des nombres : c'est ce
 * qu'un pouce et un coup d'œil savent faire sur un écran de téléphone.
 */
function Ranking({ title, rows, suffix }: { title: string; rows: LabelledAmount[]; suffix: string }) {
  if (rows.length === 0) return null;
  const max = Math.max(...rows.map((r) => Math.abs(r.valueXof)));
  return (
    <>
      <SectionLabel>{title}</SectionLabel>
      <Card>
        {rows.slice(0, 8).map((row) => (
          <div key={row.label} className="border-b border-line py-2.5 last:border-0">
            <div className="flex items-baseline justify-between gap-3">
              <p className="truncate text-sm text-ink">{row.label}</p>
              <p className="tabular shrink-0 text-sm font-semibold text-ink">{money(row.valueXof)}</p>
            </div>
            <Bar value={row.valueXof} max={max} />
            {row.quantity > 0 && (
              <p className="tabular mt-1 text-[11px] text-faint">
                {quantity(row.quantity)} {suffix}
              </p>
            )}
          </div>
        ))}
      </Card>
    </>
  );
}

/** Bornes locales : « la journée » commence à minuit chez la commerçante, pas en UTC. */
function window_(days: number): { from: string; to: string } {
  const to = new Date();
  to.setHours(0, 0, 0, 0);
  to.setDate(to.getDate() + 1);
  const from = new Date(to);
  from.setDate(from.getDate() - (days + 1));
  return { from: from.toISOString(), to: to.toISOString() };
}
