"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { readToken, signIn } from "@/lib/api";
import { Button, ErrorNote, Field, inputClass } from "@/components/ui";

export default function ConnexionPage() {
  const router = useRouter();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Déjà connectée : on ne lui redemande pas son mot de passe pour rien.
  useEffect(() => {
    if (readToken()) router.replace("/");
  }, [router]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await signIn(username.trim(), password);
      router.replace("/");
    } catch {
      // Le serveur ne distingue pas les motifs de refus ; l'écran non plus.
      setError("Identifiant ou mot de passe incorrect.");
      setBusy(false);
    }
  }

  return (
    <main className="mx-auto flex min-h-dvh max-w-sm flex-col justify-center px-6 py-12">
      <div className="mb-10">
        <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-gold">Bana Shop</p>
        <h1 className="mt-1 font-display text-[32px] leading-tight text-ink">Pilotage des boutiques</h1>
        <p className="mt-2 text-sm text-muted">Ventes, caisse, stock et commandes, depuis votre téléphone.</p>
      </div>

      <form onSubmit={submit} className="space-y-4">
        <Field label="Identifiant">
          <input
            className={inputClass}
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
            autoCapitalize="none"
            autoCorrect="off"
            required
          />
        </Field>
        <Field label="Mot de passe">
          <input
            className={inputClass}
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
        </Field>

        {error && <ErrorNote>{error}</ErrorNote>}

        <Button type="submit" variant="primary" disabled={busy} className="w-full">
          {busy ? "Connexion…" : "Se connecter"}
        </Button>
      </form>

      <p className="mt-8 text-center text-xs text-faint">
        La session reste ouverte 30 jours. Elle se ferme immédiatement depuis n’importe quel appareil.
      </p>
    </main>
  );
}
