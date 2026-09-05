"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { BottomNav } from "@/components/BottomNav";
import { ResourceState, StaleNote, useResource } from "@/components/DataScreen";
import { Badge, Button, Card, ErrorNote, Field, Row, Screen, SectionLabel, inputClass } from "@/components/ui";
import Link from "next/link";
import { ShopSettings } from "@/components/ShopSettings";
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
              <ShopPanel key={shop.id} shop={shop} expanded={open === shop.id} onToggle={() => setOpen(open === shop.id ? null : shop.id)} onSaved={() => void shops.reload()} />
            ))}
          </div>
        </ResourceState>

        {creating && <CreateShop onClose={() => setCreating(false)} onCreated={() => { setCreating(false); void shops.reload(); }} />}

        <VitrineCard />
        <AccountCard />
      </Screen>
      <BottomNav />
    </>
  );
}

function ShopPanel({ shop, expanded, onToggle, onSaved }: { shop: Shop; expanded: boolean; onToggle: () => void; onSaved: () => void }) {
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

      {expanded && <ShopDetail shopId={shop.id} onSaved={onSaved} />}
    </Card>
  );
}

type Panel = "terminaux" | "stock" | "adresse" | "reglages";

function ShopDetail({ shopId, onSaved }: { shopId: string; onSaved: () => void }) {
  const devices = useResource<Device[]>(`/api/shops/${shopId}/devices`, `cache.devices.${shopId}`);
  const stock = useResource<StockRow[]>(`/api/shops/${shopId}/stock-detail?lowOnly=true`, `cache.lowstock.${shopId}`);
  const [code, setCode] = useState<{ code: string; expiresAt: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [panel, setPanel] = useState<Panel>("terminaux");

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
      {/* Trois volets plutôt qu'une longue page : les réglages d'une boutique sont nombreux et
          ne se consultent pas en même temps que son stock. */}
      <div className="mb-3 flex rounded-xl border border-line bg-ivory p-0.5">
        {(["terminaux", "stock", "adresse", "reglages"] as const).map((key) => (
          <button
            key={key}
            onClick={() => setPanel(key)}
            className={`min-h-9 flex-1 rounded-[10px] text-xs font-semibold capitalize transition-colors ${
              panel === key ? "bg-paper text-ink shadow-sm" : "text-muted"
            }`}
          >
            {key === "reglages" ? "réglages" : key}
          </button>
        ))}
      </div>

      {panel === "adresse" && <ShopIdentity shopId={shopId} onSaved={onSaved} />}
      {panel === "reglages" && <ShopSettings shopId={shopId} />}

      {panel === "terminaux" && (<>
      <SectionLabel>Terminaux</SectionLabel>
      {(devices.data ?? []).map((device) => (
        <div key={device.id}>
          <Row
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
          {!device.revoked && <VersionLine device={device} />}
        </div>
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
      </>)}

      {panel === "stock" && (<>
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
      </>)}
    </div>
  );
}

/**
 * Version du logiciel en service sur un terminal. Purement informatif : la propriétaire n'a
 * rien à décider ici. Juger si un build est sûr n'est pas de son ressort — c'est la même
 * logique que le catalogue, dont elle n'a pas la main sur la structure.
 *
 * Ce qu'elle doit pouvoir constater, en revanche : que les deux boutiques ne tournent pas sur
 * deux versions différentes depuis trois semaines, et qu'aucune mise à jour n'échoue en boucle.
 */
function VersionLine({ device }: { device: Device }) {
  if (!device.appVersion && !device.updateError) return null;

  return (
    <div className="-mt-1 mb-2 pl-1 text-[11px] leading-relaxed">
      <span className="tabular text-muted">
        Version {device.appVersion ?? "inconnue"}
        {device.appVersionSince ? ` · depuis ${sinceNow(device.appVersionSince)}` : ""}
      </span>
      {device.pendingVersion && (
        <span className="ml-2 text-terracotta-dark">
          {device.pendingVersion} prête, s’installera à la fermeture
        </span>
      )}
      {device.updateError && (
        <p className="mt-0.5 text-danger">Dernière mise à jour en échec : {device.updateError}</p>
      )}
    </div>
  );
}

/**
 * Coordonnées de la boutique : ce que le site public affiche, et ce que la caisse imprime sur
 * ses tickets. Un seul endroit pour les deux — elles vivaient auparavant à deux endroits
 * différents, si bien qu'en corriger une ne corrigeait pas l'autre.
 */
function ShopIdentity({ shopId, onSaved }: { shopId: string; onSaved: () => void }) {
  const shops = useResource<Shop[]>("/api/shops", "cache.shops");
  const shop = (shops.data ?? []).find((x) => x.id === shopId);

  const [form, setForm] = useState<{ name: string; city: string; address: string; phone: string; hours: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  const valeurs = form ?? {
    name: shop?.name ?? "",
    city: shop?.city ?? "",
    address: shop?.address ?? "",
    phone: shop?.phone ?? "",
    hours: shop?.hours ?? "",
  };

  function set(champ: keyof typeof valeurs, valeur: string) {
    setSaved(false);
    setForm({ ...valeurs, [champ]: valeur });
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.put(`/api/shops/${shopId}`, {
        name: valeurs.name.trim(),
        city: valeurs.city.trim() || null,
        address: valeurs.address.trim() || null,
        phone: valeurs.phone.trim() || null,
        hours: valeurs.hours.trim() || null,
      });
      setSaved(true);
      setForm(null);
      onSaved();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Enregistrement impossible.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="space-y-4">
      <SectionLabel>Coordonnées</SectionLabel>
      <p className="-mt-2 text-xs text-muted">
        Affichées sur le site et imprimées sur les tickets de cette boutique.
      </p>

      <Field label="Nom">
        <input className={inputClass} value={valeurs.name} onChange={(e) => set("name", e.target.value)} required />
      </Field>
      <Field label="Quartier ou ville" hint="Sert à composer le titre du site : « Deux adresses à Bamako ».">
        <input className={inputClass} value={valeurs.city} onChange={(e) => set("city", e.target.value)} />
      </Field>
      <Field label="Adresse">
        <input className={inputClass} value={valeurs.address} onChange={(e) => set("address", e.target.value)} />
      </Field>
      <Field label="Téléphone" hint="Avec l’indicatif. Exemple : +223 70 00 00 11">
        <input className={inputClass} inputMode="tel" value={valeurs.phone} onChange={(e) => set("phone", e.target.value)} />
      </Field>
      <Field label="Horaires" hint="Texte libre. Exemple : Lun–Sam 9h–19h">
        <input className={inputClass} value={valeurs.hours} onChange={(e) => set("hours", e.target.value)} />
      </Field>

      {error && <ErrorNote>{error}</ErrorNote>}
      {saved && <p className="text-xs font-semibold text-success">Enregistré. Le site et la caisse suivront.</p>}

      <Button type="submit" variant="primary" disabled={busy} className="w-full">
        {busy ? "Enregistrement…" : "Enregistrer"}
      </Button>
    </form>
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

/**
 * Textes du site public. Ils n'appartiennent à aucune boutique en particulier, d'où leur place
 * ici plutôt que dans le détail de l'une d'elles — et un sixième onglet pour deux champs aurait
 * été disproportionné.
 */
function VitrineCard() {
  const settings = useResource<{ key: string; value: string }[]>("/api/site-settings", "cache.sitesettings");
  const [form, setForm] = useState<Record<string, string> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  const stored = Object.fromEntries((settings.data ?? []).map((x) => [x.key, x.value]));
  const valeurs = form ?? {
    "Vitrine.Depuis": stored["Vitrine.Depuis"] ?? "2019",
    "Vitrine.Accroche": stored["Vitrine.Accroche"] ?? "",
  };

  function set(cle: string, valeur: string) {
    setSaved(false);
    setForm({ ...valeurs, [cle]: valeur });
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.put("/api/site-settings", Object.entries(valeurs).map(([key, value]) => ({ key, value })));
      setSaved(true);
      setForm(null);
      void settings.reload();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Enregistrement impossible.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card className="mt-3">
      <form onSubmit={submit} className="space-y-4">
        <div>
          <SectionLabel>Site public</SectionLabel>
          <p className="text-xs text-muted">Ce que voient les clientes en arrivant sur le site.</p>
        </div>

        <Field label="Ouvert depuis" hint="Année affichée en haut du site.">
          <input
            className={inputClass}
            inputMode="numeric"
            value={valeurs["Vitrine.Depuis"]}
            onChange={(e) => set("Vitrine.Depuis", e.target.value)}
          />
        </Field>
        <Field label="Accroche" hint="Laissée vide, le texte par défaut s’affiche.">
          <textarea
            className={`${inputClass} min-h-24`}
            value={valeurs["Vitrine.Accroche"]}
            onChange={(e) => set("Vitrine.Accroche", e.target.value)}
          />
        </Field>

        {error && <ErrorNote>{error}</ErrorNote>}
        {saved && <p className="text-xs font-semibold text-success">Enregistré. Le site se met à jour aussitôt.</p>}

        <Button type="submit" variant="primary" disabled={busy} className="w-full">
          {busy ? "Enregistrement…" : "Enregistrer"}
        </Button>
      </form>
    </Card>
  );
}

function AccountCard() {
  const router = useRouter();
  const identity = typeof window === "undefined" ? null : readIdentity();

  return (
    <>
      <SectionLabel>Alertes</SectionLabel>
      <Card>
        <Link href="/boutiques/alertes/" className="flex items-center justify-between gap-3 py-1">
          <span className="min-w-0">
            <span className="block text-sm text-ink">Être prévenue</span>
            <span className="block text-xs text-muted">WhatsApp et notifications du téléphone</span>
          </span>
          <span aria-hidden className="text-faint">›</span>
        </Link>
      </Card>

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
