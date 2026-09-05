"use client";

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { api, SessionExpired, readToken } from "@/lib/api";
import { Empty, ErrorNote, Skeleton } from "@/components/ui";

/**
 * Chargement de données avec cache local.
 *
 * La propriétaire consulte cette application dans la rue, dans un taxi, dans une boutique au
 * réseau incertain. Un écran qui ne montre rien tant que la requête n'a pas abouti serait
 * inutilisable la moitié du temps : on affiche donc immédiatement le dernier état connu, on
 * rafraîchit derrière, et on dit quand la donnée date.
 */
export function useResource<T>(path: string, cacheKey: string) {
  const router = useRouter();
  const [data, setData] = useState<T | null>(null);
  const [fetchedAt, setFetchedAt] = useState<Date | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [offline, setOffline] = useState(false);

  const load = useCallback(async () => {
    if (!readToken()) {
      router.replace("/connexion/");
      return;
    }
    setLoading(true);
    try {
      const fresh = await api.get<T>(path);
      setData(fresh);
      setFetchedAt(new Date());
      setError(null);
      setOffline(false);
      window.localStorage.setItem(cacheKey, JSON.stringify({ at: Date.now(), data: fresh }));
    } catch (e) {
      if (e instanceof SessionExpired) {
        router.replace("/connexion/");
        return;
      }
      // Une erreur réseau sur des données déjà en cache n'est pas une panne : c'est un écran
      // qui vieillit. On le signale sans effacer ce qu'on sait.
      setOffline(true);
      setError(data ? null : e instanceof Error ? e.message : "Chargement impossible.");
    } finally {
      setLoading(false);
    }
  }, [path, cacheKey, router, data]);

  useEffect(() => {
    const cached = window.localStorage.getItem(cacheKey);
    if (cached) {
      try {
        const parsed = JSON.parse(cached) as { at: number; data: T };
        setData(parsed.data);
        setFetchedAt(new Date(parsed.at));
        setLoading(false);
      } catch {
        /* cache illisible : on repart du réseau */
      }
    }
    void load();
    // `load` change à chaque rendu par sa dépendance à `data` ; on ne veut recharger qu'au
    // changement de ressource.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path, cacheKey]);

  return { data, error, loading, offline, fetchedAt, reload: load };
}

export function ResourceState({
  loading,
  error,
  empty,
  children,
  skeleton = 3,
}: {
  loading: boolean;
  error: string | null;
  empty?: boolean;
  children: React.ReactNode;
  skeleton?: number;
}) {
  if (error) return <ErrorNote>{error}</ErrorNote>;
  if (loading)
    return (
      <div className="space-y-3">
        {Array.from({ length: skeleton }, (_, i) => (
          <Skeleton key={i} />
        ))}
      </div>
    );
  if (empty) return <Empty>Rien à afficher pour l’instant.</Empty>;
  return <>{children}</>;
}

/** Bandeau discret quand l'écran montre des données qui ne viennent pas d'être rafraîchies. */
export function StaleNote({ offline, fetchedAt }: { offline: boolean; fetchedAt: Date | null }) {
  if (!offline || !fetchedAt) return null;
  return (
    <p className="mb-3 rounded-xl border border-warning/30 bg-warning-soft px-3 py-2 text-xs text-warning">
      Hors ligne · données du {fetchedAt.toLocaleString("fr-FR", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" })}
    </p>
  );
}
