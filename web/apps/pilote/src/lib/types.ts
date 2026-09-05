/** Miroir des formes renvoyées par le serveur. */

export type ShopOverview = {
  shopId: string;
  name: string;
  city: string | null;
  isCashOpen: boolean;
  operatorName: string | null;
  openedAt: string | null;
  salesXof: number;
  collectedXof: number;
  salesCount: number;
  expensesXof: number;
  outstandingCreditXof: number;
  lowStockCount: number;
  reservedAdvances: number;
  lastSeenAt: string | null;
};

export type Overview = {
  from: string;
  to: string;
  salesXof: number;
  collectedXof: number;
  salesCount: number;
  shops: ShopOverview[];
};

export type LabelledAmount = { label: string; valueXof: number; quantity: number };

export type ReportSummary = {
  from: string;
  to: string;
  salesXof: number;
  collectedXof: number;
  costXof: number;
  grossMarginXof: number;
  expensesXof: number;
  estimatedProfitXof: number;
  outstandingCreditXof: number;
  salesCount: number;
  costWarning: boolean;
  bestSeller: { label: string; quantity: number; valueXof: number } | null;
  byDay: LabelledAmount[];
  byShop: LabelledAmount[];
  byOperator: LabelledAmount[];
  byPaymentMode: LabelledAmount[];
  topProducts: LabelledAmount[];
};

export type CashClosing = {
  id: string;
  shopId: string;
  shopName: string;
  number: string;
  operatorName: string;
  closedBy: string | null;
  openedAt: string;
  closedAt: string | null;
  openingFloatXof: number;
  expectedCashXof: number;
  countedCashXof: number;
  differenceXof: number;
  differenceReason: string | null;
};

export type Advance = {
  id: string;
  shopId: string;
  shopName: string;
  customerName: string;
  customerPhone: string | null;
  saleNumber: string;
  isReserved: boolean;
  originalXof: number;
  balanceXof: number;
  dueAt: string;
  status: number;
};

export type StockRow = {
  variantId: string;
  sku: string;
  productName: string;
  size: string | null;
  color: string | null;
  onHand: number;
  reserved: number;
  available: number;
  threshold: number;
};

export type Shop = {
  id: string;
  name: string;
  city: string | null;
  isActive: boolean;
  devices: number;
  lastSeenAt: string | null;
};

export type Device = {
  id: string;
  name: string;
  createdAt: string;
  lastSeenAt: string | null;
  revoked: boolean;
  /** Version du logiciel en service, telle que le terminal la déclare à chaque synchronisation. */
  appVersion: string | null;
  appVersionSince: string | null;
  /** Version téléchargée, qui s'installera à la prochaine fermeture de l'application. */
  pendingVersion: string | null;
  updateError: string | null;
};

export type Category = { id: string; name: string; isActive: boolean };

export type Product = {
  id: string;
  categoryId: string;
  name: string;
  brand: string | null;
  description: string | null;
  subCategory: string | null;
  gender: string | null;
  season: string | null;
  type: number;
  isActive: boolean;
  /** null : article du catalogue global. Renseigné : exclusif à cette boutique. */
  shopId: string | null;
};

export type Variant = {
  id: string;
  productId: string;
  sku: string;
  barcode: string | null;
  size: string | null;
  color: string | null;
  material: string | null;
  supplier: string | null;
  costXof: number;
  priceXof: number;
  promotionalPriceXof: number | null;
  promotionStartsAt: string | null;
  promotionEndsAt: string | null;
  lowStockThreshold: number;
  isActive: boolean;
};

export type Catalog = { categories: Category[]; products: Product[]; variants: Variant[] };

export type OrderLine = {
  variantId: string;
  sku: string;
  description: string;
  quantity: number;
  unitPriceXof: number;
};

/** `status` : 0 en cours, 1 traitée (une vente existe), 2 livrée, 3 annulée. */
export type Order = {
  id: string;
  shopId: string;
  shopName: string;
  number: string;
  customerName: string;
  phone: string;
  note: string | null;
  channel: number;
  status: number;
  totalXof: number;
  saleId: string | null;
  createdAt: string;
  processedAt: string | null;
  deliveredAt: string | null;
  cancelReason: string | null;
  lines: OrderLine[];
};
