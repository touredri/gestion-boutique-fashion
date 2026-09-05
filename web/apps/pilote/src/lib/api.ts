/**
 * Client de l'API de pilotage.
 *
 * L'application est exportée en statique et servie par le même domaine que l'API : les chemins
 * sont donc relatifs, il n'y a ni origine croisée ni adresse à configurer. En développement,
 * NEXT_PUBLIC_API_BASE permet de viser un serveur lancé à part.
 */
const BASE = process.env.NEXT_PUBLIC_API_BASE ?? "";

const TOKEN_KEY = "bana.token";
const IDENTITY_KEY = "bana.identity";

export type Identity = { username: string; displayName: string; expiresAt: string };

export function readToken(): string | null {
  if (typeof window === "undefined") return null;
  return window.localStorage.getItem(TOKEN_KEY);
}

export function readIdentity(): Identity | null {
  if (typeof window === "undefined") return null;
  const raw = window.localStorage.getItem(IDENTITY_KEY);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as Identity;
  } catch {
    return null;
  }
}

function storeSession(token: string, identity: Identity) {
  window.localStorage.setItem(TOKEN_KEY, token);
  window.localStorage.setItem(IDENTITY_KEY, JSON.stringify(identity));
}

export function clearSession() {
  window.localStorage.removeItem(TOKEN_KEY);
  window.localStorage.removeItem(IDENTITY_KEY);
}

/** Levée sur 401 : la session a expiré ou été révoquée, il faut se reconnecter. */
export class SessionExpired extends Error {
  constructor() {
    super("Session expirée");
  }
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = readToken();
  const response = await fetch(`${BASE}${path}`, {
    ...init,
    headers: {
      ...(init?.body ? { "Content-Type": "application/json" } : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init?.headers,
    },
  });

  if (response.status === 401) {
    clearSession();
    throw new SessionExpired();
  }
  if (!response.ok) {
    // Le serveur renvoie { error } sur les refus métier ; à défaut, le code suffit.
    const detail = await response
      .json()
      .then((body: { error?: string }) => body?.error)
      .catch(() => undefined);
    throw new ApiError(detail ?? `Le serveur a répondu ${response.status}.`, response.status);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const api = {
  get: <T,>(path: string) => request<T>(path),
  post: <T,>(path: string, body?: unknown) =>
    request<T>(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) }),
  put: <T,>(path: string, body: unknown) => request<T>(path, { method: "PUT", body: JSON.stringify(body) }),
  delete: <T,>(path: string) => request<T>(path, { method: "DELETE" }),
};

export async function signIn(username: string, password: string): Promise<Identity> {
  const response = await fetch(`${BASE}/api/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password }),
  });
  if (!response.ok) throw new ApiError("Identifiant ou mot de passe incorrect.", response.status);

  const body = (await response.json()) as { token: string } & Identity;
  const identity: Identity = {
    username: body.username,
    displayName: body.displayName,
    expiresAt: body.expiresAt,
  };
  storeSession(body.token, identity);
  return identity;
}

export async function signOut() {
  // Le serveur d'abord : révoquer la session est le geste utile, oublier le jeton local n'en est
  // que la conséquence. Si le réseau manque, on oublie quand même.
  try {
    await api.post("/api/auth/logout");
  } catch {
    /* hors ligne : la session expirera d'elle-même */
  }
  clearSession();
}
