"use client";

import { useEffect, useState } from "react";
import { Button, ErrorNote, Field, SectionLabel, inputClass } from "@/components/ui";
import { api } from "@/lib/api";

/**
 * Réglages d'une boutique, indépendants d'une boutique à l'autre : chacune a son adresse, son
 * cachet, ses séquences de numérotation et ses seuils.
 *
 * Ils descendent vers la caisse concernée à sa prochaine synchronisation. Les clés reprennent
 * exactement celles du logiciel de caisse — c'est ce qui permet à ces valeurs de remplacer
 * celles saisies localement au lieu de coexister avec elles.
 */
type Setting = { key: string; value: string };

type FieldSpec = { key: string; label: string; hint?: string; numeric?: boolean; multiline?: boolean };

const GROUPS: { title: string; fields: FieldSpec[] }[] = [
  {
    title: "Identité sur les documents",
    fields: [
      // Nom, adresse et téléphone ne sont plus ici : ils vivent dans le volet « adresse », d'où
      // ils partent à la fois vers le site et vers les tickets. Les laisser en double aurait
      // laissé deux vérités possibles pour la même information.
      { key: "Shop.Email", label: "E-mail" },
      { key: "Shop.TaxId", label: "NIF / RCCM" },
      { key: "Shop.Slogan", label: "Slogan" },
      { key: "Shop.Footer", label: "Pied de ticket", multiline: true },
      { key: "Shop.ReturnPolicy", label: "Politique de retour", multiline: true },
    ],
  },
  {
    title: "Caisse",
    fields: [
      {
        key: "Cash.VarianceToleranceXof",
        label: "Tolérance d’écart",
        hint: "Au-delà, la clôture exige le code gérant.",
        numeric: true,
      },
      {
        key: "Cash.MovementLimitXof",
        label: "Plafond de sortie d’espèces",
        hint: "Au-delà, un retrait exige le code gérant. Les petits achats de monnaie passent seuls.",
        numeric: true,
      },
    ],
  },
  {
    title: "Clients",
    fields: [
      { key: "Loyalty.VipRevenueXof", label: "Seuil client VIP", numeric: true },
      { key: "Loyalty.LoyalPurchases", label: "Achats pour être fidèle", numeric: true },
      { key: "Loyalty.InactiveDays", label: "Jours avant inactif", numeric: true },
    ],
  },
  {
    title: "Numérotation des documents",
    fields: [
      { key: "Seq.Receipt", label: "Tickets", hint: "Préfixe, par exemple TIC." },
      { key: "Seq.Invoice", label: "Factures" },
      { key: "Seq.Proforma", label: "Proformas" },
    ],
  },
];

export function ShopSettings({ shopId }: { shopId: string }) {
  const [values, setValues] = useState<Record<string, string> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void api
      .get<Setting[]>(`/api/shops/${shopId}/settings`)
      .then((rows) => {
        if (!cancelled) setValues(Object.fromEntries(rows.map((r) => [r.key, r.value])));
      })
      .catch(() => {
        if (!cancelled) setValues({});
      });
    return () => {
      cancelled = true;
    };
  }, [shopId]);

  async function save() {
    if (!values) return;
    setBusy(true);
    setError(null);
    try {
      // On n'envoie que ce qui est renseigné : une valeur vide effacerait le réglage local de la
      // caisse au lieu de la laisser sur son défaut.
      const payload = Object.entries(values)
        .filter(([, value]) => value.trim() !== "")
        .map(([key, value]) => ({ key, value: value.trim() }));
      await api.put(`/api/shops/${shopId}/settings`, payload);
      setStatus("Réglages envoyés. Ils s’appliqueront à la prochaine synchronisation de la caisse.");
    } catch (e) {
      setError(e instanceof Error ? e.message : "Enregistrement impossible.");
    } finally {
      setBusy(false);
    }
  }

  if (!values) return <p className="py-3 text-sm text-muted">Chargement des réglages…</p>;

  return (
    <div className="mt-2">
      {GROUPS.map((group) => (
        <div key={group.title}>
          <SectionLabel>{group.title}</SectionLabel>
          <div className="space-y-3">
            {group.fields.map((field) => (
              <Field key={field.key} label={field.label} hint={field.hint}>
                {field.multiline ? (
                  <textarea
                    className={`${inputClass} min-h-20 py-2`}
                    value={values[field.key] ?? ""}
                    onChange={(e) => setValues({ ...values, [field.key]: e.target.value })}
                  />
                ) : (
                  <input
                    className={inputClass}
                    inputMode={field.numeric ? "numeric" : "text"}
                    value={values[field.key] ?? ""}
                    onChange={(e) =>
                      setValues({
                        ...values,
                        [field.key]: field.numeric ? e.target.value.replace(/\D/g, "") : e.target.value,
                      })
                    }
                  />
                )}
              </Field>
            ))}
          </div>
        </div>
      ))}

      {error && <div className="mt-4"><ErrorNote>{error}</ErrorNote></div>}
      {status && <p className="mt-4 rounded-xl border border-success/30 bg-success-soft px-3 py-2 text-sm text-success">{status}</p>}

      <Button onClick={() => void save()} variant="primary" disabled={busy} className="mt-4 w-full">
        {busy ? "Enregistrement…" : "Enregistrer les réglages"}
      </Button>
    </div>
  );
}
