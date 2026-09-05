using BoutiqueFashion.Domain;

namespace BoutiqueFashion.Contracts;

/// <summary>
/// Protocole de synchronisation entre un terminal et le serveur.
///
/// Deux principes le gouvernent :
///
/// 1. <b>Autorité par nature de donnée.</b> Ce qui est constaté à la caisse — ventes, paiements,
///    mouvements de stock et d'espèces — remonte et n'est jamais réécrit par le serveur : ce sont
///    des faits, pas des états. Ce qui décrit la boutique — produits, prix, paramètres — descend
///    du serveur. Il n'y a donc presque rien à arbitrer.
///
/// 2. <b>Charges utiles explicites.</b> Les entités du domaine ne sont pas sérialisées telles
///    quelles. Outre le versionnement, cela évite d'expédier au serveur des champs qui n'ont rien
///    à y faire — <see cref="CashSession.OperatorPinHash"/> en premier lieu.
/// </summary>
public static class SyncEntityTypes
{
    public const string Sale = "Sale";
    public const string CashSessionOpened = "CashSessionOpened";
    public const string CashSessionClosed = "CashSessionClosed";
    public const string CashMovement = "CashMovement";
    public const string Expense = "Expense";
    public const string CreditPayment = "CreditPayment";
    public const string Customer = "Customer";
    public const string StockMovement = "StockMovement";
    public const string ProductDraft = "ProductDraft";
    /// <summary>Changement d'état d'une commande décidé en caisse.</summary>
    public const string OrderStatus = "OrderStatus";
}

/// <summary>Une ligne d'outbox prête à partir. <paramref name="Id"/> est la clé d'idempotence :
/// le serveur ignore un identifiant déjà vu, ce qui rend tout rejeu sans effet.</summary>
public sealed record SyncEvent(Guid Id, string EntityType, Guid EntityId, DateTimeOffset OccurredAt, string PayloadJson);

public sealed record SyncPushRequest(IReadOnlyList<SyncEvent> Events);

/// <summary>Les refus sont nominatifs : un événement mal formé ne doit pas bloquer la file
/// derrière lui, sinon un seul enregistrement corrompu gèlerait la boutique pour toujours.</summary>
public sealed record SyncPushResponse(IReadOnlyList<Guid> AcceptedIds, IReadOnlyList<SyncRejection> Rejected);

public sealed record SyncRejection(Guid Id, string Reason);

public sealed record SyncPullResponse(
    long Cursor,
    IReadOnlyList<CategoryDto> Categories,
    IReadOnlyList<ProductDto> Products,
    IReadOnlyList<VariantDto> Variants,
    IReadOnlyList<SettingDto> Settings,
    bool HasMore,
    /// Articles qui ne concernent plus cette boutique : leur portée a été restreinte à une autre.
    /// Sans cette liste, le terminal garderait à jamais une copie devenue invisible côté serveur,
    /// puisque le filtre de descente cesse justement de la lui envoyer.
    IReadOnlyList<Guid>? RetiredProductIds = null,
    /// Commandes de cette boutique, dans les deux sens : elles descendent avec leur état
    /// courant, et la caisse renvoie le sien quand elle encaisse ou livre.
    IReadOnlyList<OrderDto>? Orders = null);

// --- Descendant du serveur -------------------------------------------------

public sealed record CategoryDto(Guid Id, string Name, bool IsActive);

/// <summary><paramref name="ShopId"/> : <c>null</c> pour un article du catalogue global, présent
/// dans toutes les boutiques ; renseigné pour un article exclusif à l'une d'elles.</summary>
public sealed record ProductDto(
    Guid Id, Guid CategoryId, string Name, string? Brand, string? Description,
    string? SubCategory, string? Gender, string? Season, ProductType Type, bool IsActive,
    Guid? ShopId = null);

/// <summary>Sans quantité : le stock appartient à la boutique et remonte, il ne descend pas.</summary>
public sealed record VariantDto(
    Guid Id, Guid ProductId, string Sku, string? Barcode, string? Size, string? Color,
    string? Material, string? Supplier, long CostXof, long PriceXof,
    long? PromotionalPriceXof, DateTimeOffset? PromotionStartsAt, DateTimeOffset? PromotionEndsAt,
    decimal LowStockThreshold, bool IsActive);

public sealed record SettingDto(string Key, string Value);

public sealed record OrderLineDto(Guid VariantId, string Sku, string Description, decimal Quantity, long UnitPriceXof);

public sealed record OrderDto(
    Guid Id, string Number, string CustomerName, string Phone, string? Note,
    OrderChannel Channel, OrderStatus Status, long TotalXof, Guid? SaleId,
    DateTimeOffset PlacedAt, IReadOnlyList<OrderLineDto> Lines);

/// <summary>Ce que la caisse renvoie : l'état, et la vente qui le justifie. Sans cet
/// identifiant, « traitée » ne serait qu'une case cochée.</summary>
public sealed record OrderStatusPayload(Guid Id, OrderStatus Status, Guid? SaleId, DateTimeOffset ChangedAt);

// --- Montant du terminal ---------------------------------------------------

public sealed record SaleLineDto(Guid VariantId, string Sku, string Description, decimal Quantity, long UnitPriceXof, long FrozenUnitCostXof, long DiscountXof, long LineTotalXof);

public sealed record PaymentDto(Guid Id, PaymentMode Mode, long AmountXof, string? ExternalReference, bool IsReversal);

public sealed record SalePayload(
    Guid Id, string Number, string IdempotencyKey, Guid? CustomerId, Guid? CashSessionId,
    string SellerName, long SubtotalXof, long DiscountXof, long TotalXof, long ChangeXof,
    SaleStatus Status, DateTimeOffset CreatedAt,
    IReadOnlyList<SaleLineDto> Lines, IReadOnlyList<PaymentDto> Payments,
    CreditPayload? Credit);

public sealed record CreditPayload(Guid Id, Guid CustomerId, long OriginalAmountXof, long BalanceXof, DateTimeOffset DueAt, CreditStatus Status);

/// <summary>Ouverture de vacation. Le condensé du code de vacation reste sur le terminal :
/// il n'a aucun usage à distance, et sa fuite n'apporterait rien de bon.</summary>
public sealed record CashSessionOpenedPayload(Guid Id, string Number, string OperatorName, long OpeningFloatXof, DateTimeOffset OpenedAt);

public sealed record CashSessionClosedPayload(
    Guid Id, string Number, string OperatorName, string? ClosedBy,
    long OpeningFloatXof, long ExpectedCashXof, long CountedCashXof, long DifferenceXof,
    string? DifferenceReason, DateTimeOffset OpenedAt, DateTimeOffset ClosedAt);

public sealed record CashMovementPayload(Guid Id, Guid CashSessionId, CashMovementDirection Direction, long AmountXof, string Reason, string Actor, DateTimeOffset CreatedAt);

public sealed record ExpensePayload(Guid Id, string Category, string Description, long AmountXof, PaymentMode Mode, DateTimeOffset CreatedAt);

public sealed record CreditPaymentPayload(Guid Id, Guid CustomerCreditId, string Number, long AmountXof, PaymentMode Mode, bool IsReversal, Guid? ReversesPaymentId, string Actor, DateTimeOffset CreatedAt);

public sealed record CustomerPayload(Guid Id, string Name, string? Phone, string? SecondaryPhone, string? Gender, string? Address, string? Notes, string? Preferences, string? PreferredChannel, long CreditLimitXof, bool MarketingConsent, bool IsArchived, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record StockMovementPayload(Guid Id, Guid VariantId, StockMovementType Type, decimal QuantityDelta, long UnitCostXof, string Reason, string SourceType, Guid? SourceId, string Actor, DateTimeOffset CreatedAt);

// --- Appairage d'un terminal ----------------------------------------------

public sealed record EnrollRequest(string Code, string DeviceName);

public sealed record EnrollResponse(Guid ShopId, string ShopName, Guid DeviceId, string DeviceToken);
