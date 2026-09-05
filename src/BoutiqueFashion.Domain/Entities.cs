using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BoutiqueFashion.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class Category : Entity
{
    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public ICollection<Product> Products { get; set; } = [];
}

public sealed class Product : Entity
{
    [MaxLength(180)] public string Name { get; set; } = string.Empty;
    [MaxLength(120)] public string? Brand { get; set; }
    [MaxLength(500)] public string? Description { get; set; }
    [MaxLength(120)] public string? SubCategory { get; set; }
    [MaxLength(60)] public string? Gender { get; set; }
    [MaxLength(60)] public string? Season { get; set; }
    public ProductType Type { get; set; } = ProductType.Clothing;
    public Guid CategoryId { get; set; }
    public Category? Category { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<ProductVariant> Variants { get; set; } = [];
    public ICollection<ProductImage> Images { get; set; } = [];
    public string? PrimaryImagePath => Images?.FirstOrDefault(x => x.IsPrimary)?.RelativePath ?? Images?.FirstOrDefault()?.RelativePath;
}

public sealed class ProductImage : Entity
{
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid? VariantId { get; set; }
    public ProductVariant? Variant { get; set; }
    [MaxLength(500)] public string RelativePath { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
}

public sealed class ProductVariant : Entity
{
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    [MaxLength(80)] public string Sku { get; set; } = string.Empty;
    [MaxLength(80)] public string? Barcode { get; set; }
    [MaxLength(40)] public string? Size { get; set; }
    [MaxLength(60)] public string? Color { get; set; }
    [MaxLength(60)] public string? Material { get; set; }
    [MaxLength(120)] public string? Location { get; set; }
    [MaxLength(160)] public string? Supplier { get; set; }
    public long CostXof { get; set; }
    public long PriceXof { get; set; }
    public long? PromotionalPriceXof { get; set; }
    public DateTimeOffset? PromotionStartsAt { get; set; }
    public DateTimeOffset? PromotionEndsAt { get; set; }
    public decimal QuantityOnHand { get; set; }
    /// <summary>Quantité physiquement présente mais promise à une avance en cours : elle reste
    /// dans <see cref="QuantityOnHand"/> jusqu'à la remise, et n'est donc pas vendable.</summary>
    public decimal QuantityReserved { get; set; }
    public decimal WeightedAverageCostXof { get; set; }
    public decimal LowStockThreshold { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<StockMovement> StockMovements { get; set; } = [];
    public ICollection<ProductImage> Images { get; set; } = [];
    [NotMapped] public string? PrimaryImagePath => Images?.FirstOrDefault(x => x.IsPrimary)?.RelativePath ?? Images?.FirstOrDefault()?.RelativePath ?? Product?.PrimaryImagePath;
    [NotMapped] public long MarginXof => PriceXof - CostXof;
    /// <summary>Ce qu'un vendeur peut réellement mettre au panier : le réservé est déjà vendu.</summary>
    [NotMapped] public decimal QuantityAvailable => QuantityOnHand - QuantityReserved;
    [NotMapped] public bool IsOutOfStock => QuantityAvailable <= 0;
}

public sealed class StockMovement : Entity
{
    public Guid VariantId { get; set; }
    public ProductVariant? Variant { get; set; }
    public StockMovementType Type { get; set; }
    public decimal QuantityDelta { get; set; }
    public long UnitCostXof { get; set; }
    [MaxLength(250)] public string Reason { get; set; } = string.Empty;
    [MaxLength(80)] public string SourceType { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
    [MaxLength(80)] public string Actor { get; set; } = "Vendeur boutique";
}

public sealed class Customer : Entity
{
    [MaxLength(160)] public string Name { get; set; } = string.Empty;
    [MaxLength(30)] public string? Phone { get; set; }
    [MaxLength(30)] public string? SecondaryPhone { get; set; }
    [MaxLength(30)] public string? Gender { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    [MaxLength(500)] public string? Preferences { get; set; }
    [MaxLength(60)] public string? PreferredChannel { get; set; }
    public DateTimeOffset? ConsentDate { get; set; }
    public long CreditLimitXof { get; set; }
    public bool MarketingConsent { get; set; }
    public bool IsArchived { get; set; }
    public ICollection<Sale> Sales { get; set; } = [];
}

public sealed class Sale : Entity
{
    [MaxLength(64)] public string IdempotencyKey { get; set; } = string.Empty;
    [MaxLength(40)] public string Number { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public Guid? CashSessionId { get; set; }
    public CashSession? CashSession { get; set; }
    [MaxLength(80)] public string SellerName { get; set; } = "Vendeur boutique";
    public long SubtotalXof { get; set; }
    public long DiscountXof { get; set; }
    public long TotalXof { get; set; }
    public long ChangeXof { get; set; }
    public SaleStatus Status { get; set; } = SaleStatus.Completed;
    public ICollection<SaleLine> Lines { get; set; } = [];
    public ICollection<Payment> Payments { get; set; } = [];
}

public sealed class SaleLine : Entity
{
    public Guid SaleId { get; set; }
    public Sale? Sale { get; set; }
    public Guid VariantId { get; set; }
    public ProductVariant? Variant { get; set; }
    [MaxLength(200)] public string Description { get; set; } = string.Empty;
    [MaxLength(80)] public string Sku { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public long UnitPriceXof { get; set; }
    public long FrozenUnitCostXof { get; set; }
    public long DiscountXof { get; set; }
    public long LineTotalXof { get; set; }
}

public sealed class Payment : Entity
{
    public Guid SaleId { get; set; }
    public Sale? Sale { get; set; }
    public PaymentMode Mode { get; set; }
    public long AmountXof { get; set; }
    [MaxLength(120)] public string? ExternalReference { get; set; }
    public bool IsReversal { get; set; }
    public Guid? ReversesPaymentId { get; set; }
    [MaxLength(80)] public string Actor { get; set; } = "Vendeur boutique";
}

public sealed class CustomerCredit : Entity
{
    public Guid SaleId { get; set; }
    public Guid CustomerId { get; set; }
    public long OriginalAmountXof { get; set; }
    public long BalanceXof { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public CreditStatus Status { get; set; } = CreditStatus.Due;
    public ICollection<CreditPayment> Payments { get; set; } = [];
}

public sealed class CreditPayment : Entity
{
    public Guid CustomerCreditId { get; set; }
    public CustomerCredit? Credit { get; set; }
    [MaxLength(40)] public string Number { get; set; } = string.Empty;
    public long AmountXof { get; set; }
    public PaymentMode Mode { get; set; }
    public bool IsReversal { get; set; }
    public Guid? ReversesPaymentId { get; set; }
    [MaxLength(80)] public string Actor { get; set; } = string.Empty;
}

public sealed class CashSession : Entity
{
    [MaxLength(40)] public string Number { get; set; } = string.Empty;
    /// <summary>Personne qui tient la caisse pour cette vacation. Sert de <see cref="Sale.SellerName"/>
    /// à toutes les ventes de la session ; à défaut de nom saisi, le nom de la boutique.</summary>
    [MaxLength(80)] public string OperatorName { get; set; } = string.Empty;
    /// <summary>PIN de vacation, choisi à l'ouverture. Nul si la caisse a été ouverte sans code :
    /// la clôture n'est alors possible qu'avec le PIN gérant.</summary>
    [MaxLength(200)] public string? OperatorPinHash { get; set; }
    [MaxLength(80)] public string? ClosedBy { get; set; }
    public long OpeningFloatXof { get; set; }
    public long? CountedCashXof { get; set; }
    public long? ExpectedCashXof { get; set; }
    public long? DifferenceXof { get; set; }
    [MaxLength(300)] public string? DifferenceReason { get; set; }
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
    public CashSessionStatus Status { get; set; } = CashSessionStatus.Open;
    public ICollection<Sale> Sales { get; set; } = [];
}

/// <summary>
/// Espèces qui entrent ou sortent du tiroir sans être une vente ni une dépense : la patronne
/// emporte la recette, on va faire de la monnaie, on renfloue le fond de caisse.
///
/// Distinct d'<see cref="Expense"/> à dessein. Une dépense est un coût qui pèse sur le bénéfice ;
/// un prélèvement de recette n'en est pas un. Faute de cette distinction, la seule façon de
/// justifier un retrait était de le saisir en dépense, ce qui effaçait le bénéfice de la journée.
/// </summary>
public sealed class CashMovement : Entity
{
    public Guid CashSessionId { get; set; }
    public CashSession? CashSession { get; set; }
    public CashMovementDirection Direction { get; set; }
    public long AmountXof { get; set; }
    [MaxLength(250)] public string Reason { get; set; } = string.Empty;
    [MaxLength(80)] public string Actor { get; set; } = string.Empty;
}

public sealed class Expense : Entity
{
    [MaxLength(100)] public string Category { get; set; } = string.Empty;
    [MaxLength(300)] public string Description { get; set; } = string.Empty;
    public long AmountXof { get; set; }
    public PaymentMode Mode { get; set; }
    [MaxLength(500)] public string? ReceiptPath { get; set; }
}

public sealed class PurchaseOrder : Entity
{
    [MaxLength(160)] public string Supplier { get; set; } = string.Empty;
    [MaxLength(500)] public string? Note { get; set; }
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Open;
    public ICollection<PurchaseOrderLine> Lines { get; set; } = [];
}

public sealed class PurchaseOrderLine : Entity
{
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder? Order { get; set; }
    public Guid VariantId { get; set; }
    public ProductVariant? Variant { get; set; }
    public decimal ExpectedQuantity { get; set; }
    public decimal ReceivedQuantity { get; set; }
}

/// <summary>
/// Commande reçue du site vitrine, descendue sur la caisse de sa boutique.
///
/// Elle n'est pas une vente : rien n'est encaissé, rien ne sort du stock tant que le vendeur n'a
/// pas encaissé. <see cref="SaleId"/> est ce qui distingue une commande réellement traitée d'une
/// case cochée — il est renseigné dans la transaction même qui crée la vente.
/// </summary>
public sealed class Order : Entity
{
    [MaxLength(40)] public string Number { get; set; } = string.Empty;
    [MaxLength(160)] public string CustomerName { get; set; } = string.Empty;
    [MaxLength(30)] public string Phone { get; set; } = string.Empty;
    [MaxLength(500)] public string? Note { get; set; }
    public OrderChannel Channel { get; set; } = OrderChannel.Vitrine;
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public long TotalXof { get; set; }
    public Guid? SaleId { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public ICollection<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderLine : Entity
{
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }
    public Guid VariantId { get; set; }
    [MaxLength(80)] public string Sku { get; set; } = string.Empty;
    [MaxLength(200)] public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    /// <summary>Prix figé à la commande : la cliente a réservé à ce prix-là.</summary>
    public long UnitPriceXof { get; set; }
}

public sealed class DocumentSnapshot : Entity
{
    public Guid? SaleId { get; set; }
    public DocumentType Type { get; set; }
    [MaxLength(40)] public string Number { get; set; } = string.Empty;
    public string JsonPayload { get; set; } = string.Empty;
    public byte[]? PdfBytes { get; set; }
    public int PrintCount { get; set; }
}

public sealed class PrintJob : Entity
{
    [MaxLength(120)] public string IdempotencyKey { get; set; } = string.Empty;
    public Guid DocumentSnapshotId { get; set; }
    public PrintJobStatus Status { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
    public int Attempts { get; set; }
}

/// <summary>
/// File d'attente de synchronisation vers le serveur.
///
/// Écrite dans la même transaction que la donnée qu'elle décrit. C'est la seule façon de garantir
/// à la fois qu'une vente enregistrée finira par remonter, et qu'aucune ne remontera sans avoir
/// été enregistrée : une file alimentée après coup perdrait tout ce qui se produit entre le
/// commit et la panne.
///
/// La caisse n'attend jamais ces lignes : hors réseau la file grossit, et la boutique continue.
/// </summary>
public sealed class SyncOutboxEntry : Entity
{
    [MaxLength(60)] public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    /// <summary>Renseigné une fois l'événement acquitté par le serveur. Les lignes envoyées sont
    /// conservées : elles coûtent peu et racontent ce qui est réellement parti.</summary>
    public DateTimeOffset? SentAt { get; set; }
    [MaxLength(500)] public string? LastError { get; set; }
}

public sealed class AuditEntry : Entity
{
    [MaxLength(80)] public string Actor { get; set; } = string.Empty;
    [MaxLength(100)] public string Action { get; set; } = string.Empty;
    [MaxLength(100)] public string EntityType { get; set; } = string.Empty;
    [MaxLength(80)] public string EntityId { get; set; } = string.Empty;
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
}

public sealed class AppSetting : Entity
{
    [MaxLength(120)] public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class DocumentSequence : Entity
{
    public DocumentType Type { get; set; }
    [MaxLength(12)] public string Prefix { get; set; } = string.Empty;
    public int Year { get; set; }
    public long NextValue { get; set; } = 1;
}

