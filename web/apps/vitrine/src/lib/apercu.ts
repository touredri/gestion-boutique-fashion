import type { ShowcaseItem } from "./showcase";

/**
 * Pièces d'aperçu, affichées **uniquement** tant que le catalogue réel est vide.
 *
 * Elles ne sont pas en base et ne le seront jamais. C'est ce qui fait qu'elles disparaissent
 * d'elles-mêmes dès le premier article enregistré depuis l'application de pilotage : la
 * condition d'affichage est l'absence de contenu réel, il n'y a rien à nettoyer ensuite. Et rien
 * de fictif ne peut redescendre sur une caisse à la synchronisation.
 *
 * Elles ne portent **pas de prix** et ne sont **pas commandables** : un site neuf a le droit de
 * montrer ce qu'il vendra, pas d'annoncer un tarif qu'on ne tiendra pas au comptoir ni de prendre
 * une commande pour une pièce qui n'existe pas encore.
 */
export type ApercuItem = Omit<ShowcaseItem, "priceXof" | "promotionalPriceXof"> & {
  priceXof: null;
  promotionalPriceXof: null;
};

function piece(
  id: string,
  name: string,
  category: string,
  gender: string | null,
  type: number,
  color: string,
  description: string,
): ApercuItem {
  return {
    variantId: id,
    productId: id,
    name,
    brand: null,
    description,
    category,
    gender,
    type,
    size: null,
    color,
    priceXof: null,
    promotionalPriceXof: null,
    inStock: false,
    shopIds: [],
  };
}

/**
 * Choisies pour Bamako : le bazin riche et le wax d'abord, parce que ce sont eux qu'on vient
 * chercher. Les couleurs sont réelles — c'est d'elles que se déduit la teinte de chaque carte,
 * faute de photographies.
 */
export const APERCU: ApercuItem[] = [
  piece("a1", "Grand boubou en bazin riche", "Vêtements", "Femme", 0, "indigo", "Bazin teint et damassé, coupe ample, broderie au col."),
  piece("a2", "Complet bazin brodé", "Vêtements", "Homme", 0, "blanc", "Trois pièces, broderie main sur le devant."),
  piece("a3", "Robe longue en wax", "Vêtements", "Femme", 0, "terracotta", "Wax pagne, taille marquée, manches ballon."),
  piece("a4", "Ensemble pagne tissé", "Vêtements", "Femme", 0, "doré", "Coton tissé à la main, jupe et haut assortis."),
  piece("a5", "Chemise wax manches courtes", "Vêtements", "Homme", 0, "vert", "Coupe droite, col ouvert, pour tous les jours."),
  piece("a6", "Sandales en cuir tressé", "Chaussures", "Femme", 1, "marron", "Cuir tanné, semelle cousue."),
  piece("a7", "Babouches cousues main", "Chaussures", "Homme", 1, "noir", "Cuir souple, finition artisanale."),
  piece("a8", "Sac en cuir et bogolan", "Accessoires", null, 2, "beige", "Bandoulière réglable, doublure en bogolan."),
  piece("a9", "Collier en argent touareg", "Accessoires", "Femme", 2, "argent", "Argent martelé, pendentif croix du Sud."),
  piece("a10", "Écharpe en coton bogolan", "Accessoires", null, 2, "ocre", "Coton teint à la terre, motifs traditionnels."),
];

/** Regroupées comme les vraies, pour que les rayons se rendent avec le même gabarit. */
export const APERCU_CATEGORIES = [...new Set(APERCU.map((x) => x.category))].sort();
