/**
 * Mise en forme. Le franc CFA n'a pas de décimales et se lit par groupes de trois ; l'espace
 * insécable étroite évite qu'un montant se coupe en fin de ligne sur un écran étroit.
 */
const amountFormatter = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 0 });

export function money(value: number): string {
  return `${amountFormatter.format(value)} F`;
}

/** Pour les tuiles : 1 240 000 devient « 1,24 M », lisible d'un coup d'œil sur un téléphone. */
export function compactMoney(value: number): string {
  const abs = Math.abs(value);
  if (abs >= 1_000_000) return `${(value / 1_000_000).toLocaleString("fr-FR", { maximumFractionDigits: 2 })} M`;
  if (abs >= 10_000) return `${(value / 1_000).toLocaleString("fr-FR", { maximumFractionDigits: 0 })} k`;
  return amountFormatter.format(value);
}

export function quantity(value: number): string {
  return value.toLocaleString("fr-FR", { maximumFractionDigits: 2 });
}

export function shortDate(value: string | null): string {
  if (!value) return "—";
  return new Date(value).toLocaleDateString("fr-FR", { day: "2-digit", month: "2-digit" });
}

export function dateAndTime(value: string | null): string {
  if (!value) return "—";
  return new Date(value).toLocaleString("fr-FR", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
}

export function time(value: string | null): string {
  if (!value) return "—";
  return new Date(value).toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" });
}

/**
 * Fraîcheur d'une synchronisation. « il y a 3 min » dit ce qu'une heure exacte ne dit pas :
 * si la boutique est encore en ligne.
 */
export function sinceNow(value: string | null): string {
  if (!value) return "jamais";
  const minutes = Math.round((Date.now() - new Date(value).getTime()) / 60_000);
  if (minutes < 1) return "à l'instant";
  if (minutes < 60) return `il y a ${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `il y a ${hours} h`;
  return `il y a ${Math.round(hours / 24)} j`;
}

/** Vrai quand le terminal n'a pas donné signe de vie depuis assez longtemps pour s'en inquiéter. */
export function isStale(lastSeenAt: string | null): boolean {
  if (!lastSeenAt) return true;
  return Date.now() - new Date(lastSeenAt).getTime() > 30 * 60_000;
}

/** Bornes de période, en heure locale : « aujourd'hui » veut dire la journée de la commerçante. */
export function period(days: number): { from: string; to: string } {
  const end = new Date();
  end.setHours(0, 0, 0, 0);
  end.setDate(end.getDate() + 1);
  const start = new Date(end);
  start.setDate(start.getDate() - days);
  return { from: start.toISOString(), to: end.toISOString() };
}
