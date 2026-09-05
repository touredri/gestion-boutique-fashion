"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { BottomNav } from "@/components/BottomNav";
import { ResourceState, useResource } from "@/components/DataScreen";
import { Button, Card, ErrorNote, Field, Screen, SectionLabel, inputClass } from "@/components/ui";
import { api } from "@/lib/api";
import { isSubscribed, pushAvailability, subscribe, unsubscribe, type PushAvailability } from "@/lib/push";

type Settings = {
  whatsAppNumber: string | null;
  onCashOpened: boolean;
  onCashClosed: boolean;
  onCashVariance: boolean;
  onNewOrder: boolean;
  vapidPublicKey: string | null;
  whatsAppConfigured: boolean;
  subscriptions: number;
};

const EVENTS = [
  { key: "onCashOpened", label: "Ouverture de caisse", hint: "Quand une boutique commence sa journée." },
  { key: "onCashClosed", label: "Clôture de caisse", hint: "Le compte du soir, même sans écart." },
  { key: "onCashVariance", label: "Écart de caisse", hint: "Quand le compte ne tombe pas juste." },
  { key: "onNewOrder", label: "Nouvelle commande", hint: "Depuis le site vitrine." },
] as const;

/**
 * Alertes. Deux canaux, et l'écran dit franchement ce que chacun sait faire : WhatsApp porte le
 * détail, la notification du téléphone ne fait que signaler. Laisser croire l'inverse serait
 * découvrir la limite un soir d'écart de caisse.
 */
export default function AlertesPage() {
  const settings = useResource<Settings>("/api/notifications/settings", "cache.notifications");
  const [form, setForm] = useState<Settings | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [availability, setAvailability] = useState<PushAvailability>("unsupported");
  const [subscribed, setSubscribed] = useState(false);

  useEffect(() => {
    if (settings.data && !form) setForm(settings.data);
  }, [settings.data, form]);

  useEffect(() => {
    setAvailability(pushAvailability());
    void isSubscribed().then(setSubscribed);
  }, []);

  async function save() {
    if (!form) return;
    setBusy(true);
    setError(null);
    try {
      await api.put("/api/notifications/settings", {
        whatsAppNumber: form.whatsAppNumber,
        onCashOpened: form.onCashOpened,
        onCashClosed: form.onCashClosed,
        onCashVariance: form.onCashVariance,
        onNewOrder: form.onNewOrder,
      });
      setStatus("Réglages enregistrés.");
      void settings.reload();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Enregistrement impossible.");
    } finally {
      setBusy(false);
    }
  }

  async function toggleSubscription() {
    setError(null);
    try {
      if (subscribed) {
        await unsubscribe();
        setSubscribed(false);
        setStatus("Ce téléphone ne recevra plus de notifications.");
      } else {
        if (!form?.vapidPublicKey) throw new Error("Les notifications web ne sont pas configurées sur le serveur.");
        await subscribe(form.vapidPublicKey, navigator.userAgent.slice(0, 60));
        setSubscribed(true);
        setStatus("Ce téléphone recevra les alertes.");
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : "Abonnement impossible.");
    }
  }

  return (
    <>
      <Screen
        eyebrow="Alertes"
        title="Être prévenue"
        action={
          <Link href="/boutiques/" className="text-sm font-semibold text-terracotta underline underline-offset-2">
            Retour
          </Link>
        }
      >
        <ResourceState loading={settings.loading && !form} error={settings.error} skeleton={2}>
          {form && (
            <>
              <SectionLabel>WhatsApp</SectionLabel>
              <Card>
                <p className="mb-3 text-sm text-muted">
                  Le message complet arrive ici : quelle boutique, qui tenait la caisse, et le
                  montant de l’écart s’il y en a un.
                </p>
                <Field label="Numéro WhatsApp" hint="Avec l’indicatif pays. Exemple : +225 07 00 00 00 11">
                  <input
                    className={inputClass}
                    inputMode="tel"
                    value={form.whatsAppNumber ?? ""}
                    onChange={(e) => setForm({ ...form, whatsAppNumber: e.target.value })}
                    placeholder="+225 …"
                  />
                </Field>
                {!form.whatsAppConfigured && (
                  <p className="mt-3 rounded-xl border border-warning/30 bg-warning-soft px-3 py-2 text-xs text-warning">
                    La passerelle WhatsApp n’est pas encore branchée sur le serveur. Le numéro est
                    enregistré, les messages partiront dès qu’elle le sera.
                  </p>
                )}
              </Card>

              <SectionLabel>Notification sur ce téléphone</SectionLabel>
              <Card>
                {availability === "ready" ? (
                  <>
                    <p className="mb-3 text-sm text-muted">
                      Un signal court qui ouvre l’application. Le détail reste sur WhatsApp.
                    </p>
                    <Button onClick={() => void toggleSubscription()} variant={subscribed ? "danger" : "quiet"} className="w-full">
                      {subscribed ? "Ne plus recevoir sur ce téléphone" : "Recevoir sur ce téléphone"}
                    </Button>
                  </>
                ) : availability === "needs-install" ? (
                  // Sur iPhone, l'API n'existe pas tant que l'application n'est pas installée :
                  // mieux vaut l'expliquer qu'afficher un bouton qui échouera.
                  <p className="text-sm text-muted">
                    Sur iPhone, les notifications n’existent qu’une fois l’application installée.
                    Touchez <span className="font-semibold text-ink">Partager</span> puis{" "}
                    <span className="font-semibold text-ink">Sur l’écran d’accueil</span>, puis revenez ici.
                  </p>
                ) : (
                  <p className="text-sm text-muted">
                    Ce navigateur ne gère pas les notifications. WhatsApp reste disponible.
                  </p>
                )}
              </Card>

              <SectionLabel>Ce qui déclenche une alerte</SectionLabel>
              <Card>
                {EVENTS.map((event) => (
                  <label key={event.key} className="flex items-start gap-3 border-b border-line py-3 last:border-0">
                    <input
                      type="checkbox"
                      className="mt-0.5 h-4 w-4 accent-[#b35f4a]"
                      checked={form[event.key]}
                      onChange={(e) => setForm({ ...form, [event.key]: e.target.checked })}
                    />
                    <span className="min-w-0">
                      <span className="block text-sm text-ink">{event.label}</span>
                      <span className="block text-xs text-muted">{event.hint}</span>
                    </span>
                  </label>
                ))}
              </Card>

              {error && <div className="mt-4"><ErrorNote>{error}</ErrorNote></div>}
              {status && <p className="mt-4 rounded-xl border border-success/30 bg-success-soft px-3 py-2 text-sm text-success">{status}</p>}

              <div className="mt-4 flex gap-2">
                <Button
                  onClick={async () => {
                    setError(null);
                    try {
                      await api.post("/api/notifications/test");
                      setStatus("Message de test envoyé. S’il n’arrive pas, vérifiez le numéro.");
                    } catch {
                      setError("Test impossible : le serveur n’a pas pu envoyer.");
                    }
                  }}
                  className="flex-1"
                >
                  Envoyer un test
                </Button>
                <Button onClick={() => void save()} variant="primary" disabled={busy} className="flex-1">
                  {busy ? "Enregistrement…" : "Enregistrer"}
                </Button>
              </div>
            </>
          )}
        </ResourceState>
      </Screen>
      <BottomNav />
    </>
  );
}
