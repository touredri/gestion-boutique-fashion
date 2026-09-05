import { api } from "@/lib/api";

/**
 * Abonnement du navigateur aux notifications.
 *
 * Sur iPhone, le Web Push n'existe que si l'application a été ajoutée à l'écran d'accueil : dans
 * Safari, l'API est tout simplement absente. On le détecte pour pouvoir l'expliquer, plutôt que
 * de laisser un bouton qui échoue sans dire pourquoi.
 */
export type PushAvailability = "ready" | "needs-install" | "unsupported";

export function pushAvailability(): PushAvailability {
  if (typeof window === "undefined") return "unsupported";
  if ("Notification" in window && "serviceWorker" in navigator && "PushManager" in window) return "ready";

  const isApple = /iPad|iPhone|iPod/.test(navigator.userAgent);
  const installed = window.matchMedia("(display-mode: standalone)").matches || "standalone" in navigator;
  return isApple && !installed ? "needs-install" : "unsupported";
}

export async function isSubscribed(): Promise<boolean> {
  if (pushAvailability() !== "ready") return false;
  const registration = await navigator.serviceWorker.ready;
  return (await registration.pushManager.getSubscription()) !== null;
}

export async function subscribe(vapidPublicKey: string, label: string): Promise<void> {
  if (Notification.permission !== "granted") {
    const permission = await Notification.requestPermission();
    if (permission !== "granted") throw new Error("Notifications refusées dans les réglages du téléphone.");
  }

  const registration = await navigator.serviceWorker.ready;
  const existing = await registration.pushManager.getSubscription();
  const subscription =
    existing ??
    (await registration.pushManager.subscribe({
      // Obligatoire sur toutes les plateformes : un abonnement silencieux serait refusé.
      userVisibleOnly: true,
      applicationServerKey: decodeKey(vapidPublicKey),
    }));

  const raw = subscription.toJSON() as { keys?: { p256dh?: string; auth?: string } };
  await api.post("/api/notifications/subscriptions", {
    endpoint: subscription.endpoint,
    p256dh: raw.keys?.p256dh ?? "",
    auth: raw.keys?.auth ?? "",
    label,
  });
}

export async function unsubscribe(): Promise<void> {
  const registration = await navigator.serviceWorker.ready;
  const subscription = await registration.pushManager.getSubscription();
  if (!subscription) return;
  await api.delete(`/api/notifications/subscriptions?endpoint=${encodeURIComponent(subscription.endpoint)}`);
  await subscription.unsubscribe();
}

/**
 * La clé publique voyage en base64url ; PushManager attend des octets bruts.
 *
 * Le tableau est alloué puis rempli plutôt que construit par Uint8Array.from : cette dernière
 * renvoie un tableau dont le tampon peut être partagé, que la signature de PushManager refuse.
 */
function decodeKey(value: string): Uint8Array<ArrayBuffer> {
  const padded = (value + "=".repeat((4 - (value.length % 4)) % 4)).replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i);
  return bytes;
}
