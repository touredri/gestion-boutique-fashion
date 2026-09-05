const BASE = process.env.NEXT_PUBLIC_API_BASE ?? "";

export type ShowcaseShop = { id: string; name: string; city: string | null; address: string | null; phone: string | null };

export type ShowcaseItem = {
  variantId: string;
  productId: string;
  name: string;
  brand: string | null;
  description: string | null;
  category: string;
  gender: string | null;
  type: number;
  size: string | null;
  color: string | null;
  priceXof: number;
  promotionalPriceXof: number | null;
  inStock: boolean;
  shopIds: string[];
};

export type Showcase = { shops: ShowcaseShop[]; items: ShowcaseItem[] };

export async function loadShowcase(): Promise<Showcase> {
  const response = await fetch(`${BASE}/api/public/showcase`, { cache: "no-store" });
  if (!response.ok) throw new Error("Catalogue indisponible");
  return (await response.json()) as Showcase;
}

export type CartLine = { variantId: string; quantity: number };

export async function placeOrder(input: {
  shopId: string;
  customerName: string;
  phone: string;
  note?: string;
  lines: CartLine[];
}): Promise<{ number: string; totalXof: number; shopName: string }> {
  const response = await fetch(`${BASE}/api/public/orders`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(input),
  });
  if (!response.ok) {
    const detail = await response
      .json()
      .then((b: { error?: string }) => b?.error)
      .catch(() => undefined);
    throw new Error(detail ?? "Votre demande n’a pas pu être envoyée.");
  }
  return (await response.json()) as { number: string; totalXof: number; shopName: string };
}

const formatter = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 0 });
export const price = (value: number) => `${formatter.format(value)} F`;

/**
 * Teinte dérivée du nom de la couleur de l'article.
 *
 * Il n'y a pas encore de photos produit. Plutôt qu'un cadre gris marqué « image manquante », on
 * compose un aplat qui reprend la couleur annoncée : la vitrine reste habitée, et le jour où les
 * photos arriveront elles prendront exactement cette place.
 */
const SWATCHES: Record<string, string> = {
  rouge: "#a8412f", bordeaux: "#6d2230", rose: "#d08a92", fuchsia: "#a83b6a",
  bleu: "#2f4a6d", marine: "#22314a", ciel: "#7d9cbd", turquoise: "#2f7d78",
  vert: "#3d6b4a", kaki: "#6b6a45", olive: "#5c5c33",
  jaune: "#c99a34", moutarde: "#a87c2a", orange: "#c1743a",
  beige: "#cbb79b", crème: "#e0d5c2", creme: "#e0d5c2", camel: "#a9824a",
  marron: "#6b4a34", chocolat: "#4c3324", noir: "#232624", gris: "#6f7570",
  blanc: "#efeae2", ivoire: "#e8e2d6", doré: "#a9824a", dore: "#a9824a",
  argent: "#9aa2a6", violet: "#5b4470", wax: "#b35f4a", multicolore: "#b35f4a",
};

export function swatch(color: string | null, seed: string): string {
  const key = (color ?? "").toLowerCase().trim();
  for (const [name, hex] of Object.entries(SWATCHES)) if (key.includes(name)) return hex;
  // À défaut, une teinte stable tirée du nom : le même article gardera toujours la même.
  let hash = 0;
  for (let i = 0; i < seed.length; i += 1) hash = (hash * 31 + seed.charCodeAt(i)) % 360;
  return `hsl(${hash} 22% 42%)`;
}

/** Regroupe les déclinaisons sous leur article : la vitrine montre une robe, pas cinq tailles. */
export function groupByProduct(items: ShowcaseItem[]) {
  const map = new Map<string, { lead: ShowcaseItem; variants: ShowcaseItem[] }>();
  for (const item of items) {
    const entry = map.get(item.productId);
    if (entry) entry.variants.push(item);
    else map.set(item.productId, { lead: item, variants: [item] });
  }
  return [...map.values()];
}
