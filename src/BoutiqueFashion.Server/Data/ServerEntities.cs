using System.ComponentModel.DataAnnotations;
using BoutiqueFashion.Domain;

namespace BoutiqueFashion.Server.Data;

/// <summary>
/// Modèle serveur. Volontairement distinct des entités du terminal plutôt que partagé avec elles.
///
/// Un terminal ne connaît qu'une boutique : ajouter <c>ShopId</c> partout dans le domaine y aurait
/// introduit une colonne toujours nulle et un concept sans objet. Ici au contraire tout est scopé
/// par boutique, les numéros de document ne sont uniques que par boutique, et le stock est une
/// table à part. Les deux modèles vont continuer de diverger — commandes du site vitrine,
/// agrégations multi-boutique — et les <see cref="BoutiqueFashion.Contracts"/> font le pont.
/// </summary>
public abstract class ServerEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Donnée dont le terminal est destinataire. <c>Seq</c> est le curseur de descente :
/// strictement croissant, il permet de ne redemander que ce qui a changé.</summary>
public abstract class SyncedDownEntity : ServerEntity
{
    public long Seq { get; set; }
}

public sealed class Shop : ServerEntity
{
    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    [MaxLength(120)] public string? City { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(30)] public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Compte de pilotage : la propriétaire, et plus tard qui elle voudra. Distinct des
/// terminaux, qui s'authentifient par jeton d'appareil et n'ont accès qu'à la synchronisation.</summary>
public sealed class User : ServerEntity
{
    /// <summary>Toujours en minuscules : « Awa » et « awa » doivent désigner le même compte,
    /// sans quoi deux comptes voisins finiraient par coexister sans qu'on le voie.</summary>
    [MaxLength(60)] public string Username { get; set; } = string.Empty;
    [MaxLength(200)] public string PasswordHash { get; set; } = string.Empty;
    [MaxLength(120)] public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedAttempts { get; set; }
    /// <summary>Verrouillage temporaire après échecs répétés : sans lui, un mot de passe faible
    /// tombe en quelques heures d'essais automatisés.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
}

public sealed class UserSession : ServerEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    [MaxLength(64)] public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Abonnement d'un navigateur aux notifications. L'endpoint est fourni par le service de push
/// du navigateur ; les deux clés servent au chiffrement, que nous n'utilisons pas — voir
/// WebPushSender pour la raison.
/// </summary>
public sealed class PushSubscription : ServerEntity
{
    public Guid UserId { get; set; }
    [MaxLength(500)] public string Endpoint { get; set; } = string.Empty;
    [MaxLength(200)] public string P256dh { get; set; } = string.Empty;
    [MaxLength(200)] public string Auth { get; set; } = string.Empty;
    [MaxLength(200)] public string? Label { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Réglages d'alerte. Une seule ligne : la propriétaire est seule à les recevoir.</summary>
public sealed class NotificationSettings
{
    public int Id { get; set; } = 1;
    /// <summary>Numéro WhatsApp au format international, sans « + » ni espaces.</summary>
    [MaxLength(30)] public string? WhatsAppNumber { get; set; }
    public bool OnCashOpened { get; set; } = true;
    public bool OnCashClosed { get; set; } = true;
    /// <summary>Un écart de caisse est la seule alerte qui mérite de réveiller quelqu'un.</summary>
    public bool OnCashVariance { get; set; } = true;
    public bool OnNewOrder { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Device : ServerEntity
{
    public Guid ShopId { get; set; }
    public Shop? Shop { get; set; }
    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    /// <summary>Empreinte SHA-256 du jeton. Le jeton lui-même n'est montré qu'une fois, à
    /// l'appairage : une base volée ne doit pas donner accès aux boutiques.</summary>
    [MaxLength(64)] public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Version en service sur ce terminal, telle qu'il la déclare à chaque cycle de
    /// synchronisation. Sans elle, « est-ce que la mise à jour s'est installée ? » est une
    /// question sans réponse depuis Abidjan.</summary>
    [MaxLength(40)] public string? AppVersion { get; set; }
    public DateTimeOffset? AppVersionSince { get; set; }

    /// <summary>Version téléchargée qui s'installera à la prochaine fermeture de l'application.</summary>
    [MaxLength(40)] public string? PendingVersion { get; set; }

    /// <summary>Dernier échec de mise à jour. Renseigné, il vaut mieux ne pas promouvoir la
    /// version aux autres boutiques.</summary>
    [MaxLength(400)] public string? UpdateError { get; set; }
}

/// <summary>Code d'appairage à usage unique et à durée limitée. Court, parce qu'il se recopie à
/// la main sur un terminal tactile.</summary>
public sealed class EnrollmentCode : ServerEntity
{
    [MaxLength(20)] public string Code { get; set; } = string.Empty;
    public Guid ShopId { get; set; }
    public Shop? Shop { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByDeviceId { get; set; }
}

/// <summary>Registre d'idempotence du push : la clé est l'identifiant de la ligne d'outbox du
/// terminal. Rejouer un lot devient sans effet, ce qui autorise à réessayer sans réfléchir.</summary>
public sealed class ProcessedEvent
{
    public Guid Id { get; set; }
    public Guid ShopId { get; set; }
    [MaxLength(60)] public string EntityType { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

// --- Référentiel : autorité serveur, descend vers les terminaux ------------

public sealed class Category : SyncedDownEntity
{
    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class Product : SyncedDownEntity
{
    public Guid CategoryId { get; set; }
    /// <summary>Portée de l'article. <c>null</c> : catalogue global, présent dans toutes les
    /// boutiques. Renseigné : exclusif à cette boutique — une pièce qu'on ne vend qu'à Marcory
    /// n'a rien à faire sur la caisse de Yopougon.</summary>
    public Guid? ShopId { get; set; }
    [MaxLength(180)] public string Name { get; set; } = string.Empty;
    [MaxLength(120)] public string? Brand { get; set; }
    [MaxLength(500)] public string? Description { get; set; }
    [MaxLength(120)] public string? SubCategory { get; set; }
    [MaxLength(60)] public string? Gender { get; set; }
    [MaxLength(60)] public string? Season { get; set; }
    public ProductType Type { get; set; } = ProductType.Clothing;
    public bool IsActive { get; set; } = true;
}

public sealed class Variant : SyncedDownEntity
{
    public Guid ProductId { get; set; }
    [MaxLength(80)] public string Sku { get; set; } = string.Empty;
    [MaxLength(80)] public string? Barcode { get; set; }
    [MaxLength(40)] public string? Size { get; set; }
    [MaxLength(60)] public string? Color { get; set; }
    [MaxLength(60)] public string? Material { get; set; }
    [MaxLength(160)] public string? Supplier { get; set; }
    public long CostXof { get; set; }
    public long PriceXof { get; set; }
    public long? PromotionalPriceXof { get; set; }
    public DateTimeOffset? PromotionStartsAt { get; set; }
    public DateTimeOffset? PromotionEndsAt { get; set; }
    public decimal LowStockThreshold { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ShopSetting : SyncedDownEntity
{
    public Guid ShopId { get; set; }
    [MaxLength(120)] public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>Le stock, lui, est par boutique — c'est ici, et seulement ici, que le multi-boutique
/// prend corps. Reconstitué à partir des mouvements reçus, jamais renvoyé au terminal, qui reste
/// maître de son propre inventaire.</summary>
public sealed class ShopStock
{
    public Guid ShopId { get; set; }
    public Guid VariantId { get; set; }
    public decimal QuantityOnHand { get; set; }
    public decimal QuantityReserved { get; set; }
    /// <summary>Dernier coût unitaire vu en réception. Ce n'est pas le coût moyen pondéré, que
    /// seul le terminal calcule : le nommer ainsi laisserait croire à une précision qu'on n'a pas.
    /// Suffisant pour une valorisation indicative à distance.</summary>
    public long LastUnitCostXof { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// --- Faits remontés des terminaux -----------------------------------------

public sealed class Customer : ServerEntity
{
    [MaxLength(160)] public string Name { get; set; } = string.Empty;
    [MaxLength(30)] public string? Phone { get; set; }
    [MaxLength(30)] public string? SecondaryPhone { get; set; }
    [MaxLength(30)] public string? Gender { get; set; }
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    [MaxLength(500)] public string? Preferences { get; set; }
    [MaxLength(60)] public string? PreferredChannel { get; set; }
    public long CreditLimitXof { get; set; }
    public bool MarketingConsent { get; set; }
    public bool IsArchived { get; set; }
    /// <summary>Arbitre la fusion : les clients sont la seule donnée modifiable des deux côtés.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Sale : ServerEntity
{
    public Guid ShopId { get; set; }
    [MaxLength(40)] public string Number { get; set; } = string.Empty;
    [MaxLength(64)] public string IdempotencyKey { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public Guid? CashSessionId { get; set; }
    [MaxLength(80)] public string SellerName { get; set; } = string.Empty;
    public long SubtotalXof { get; set; }
    public long DiscountXof { get; set; }
    public long TotalXof { get; set; }
    public long ChangeXof { get; set; }
    public SaleStatus Status { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public List<SaleLine> Lines { get; set; } = [];
    public List<SalePayment> Payments { get; set; } = [];
}

public sealed class SaleLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SaleId { get; set; }
    public Guid VariantId { get; set; }
    [MaxLength(80)] public string Sku { get; set; } = string.Empty;
    [MaxLength(200)] public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public long UnitPriceXof { get; set; }
    public long FrozenUnitCostXof { get; set; }
    public long DiscountXof { get; set; }
    public long LineTotalXof { get; set; }
}

public sealed class SalePayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SaleId { get; set; }
    public PaymentMode Mode { get; set; }
    public long AmountXof { get; set; }
    [MaxLength(120)] public string? ExternalReference { get; set; }
    public bool IsReversal { get; set; }
}

public sealed class Credit : ServerEntity
{
    public Guid ShopId { get; set; }
    public Guid SaleId { get; set; }
    public Guid CustomerId { get; set; }
    public long OriginalAmountXof { get; set; }
    public long BalanceXof { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public CreditStatus Status { get; set; }
}

public sealed class CreditPayment : ServerEntity
{
    public Guid ShopId { get; set; }
    public Guid CreditId { get; set; }
    [MaxLength(40)] public string Number { get; set; } = string.Empty;
    public long AmountXof { get; set; }
    public PaymentMode Mode { get; set; }
    public bool IsReversal { get; set; }
    public Guid? ReversesPaymentId { get; set; }
    [MaxLength(80)] public string Actor { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class CashSession : ServerEntity
{
    public Guid ShopId { get; set; }
    [MaxLength(40)] public string Number { get; set; } = string.Empty;
    [MaxLength(80)] public string OperatorName { get; set; } = string.Empty;
    [MaxLength(80)] public string? ClosedBy { get; set; }
    public long OpeningFloatXof { get; set; }
    public long? ExpectedCashXof { get; set; }
    public long? CountedCashXof { get; set; }
    public long? DifferenceXof { get; set; }
    [MaxLength(300)] public string? DifferenceReason { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public bool IsClosed { get; set; }
}

public sealed class CashMovement : ServerEntity
{
    public Guid ShopId { get; set; }
    public Guid CashSessionId { get; set; }
    public CashMovementDirection Direction { get; set; }
    public long AmountXof { get; set; }
    [MaxLength(250)] public string Reason { get; set; } = string.Empty;
    [MaxLength(80)] public string Actor { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class Expense : ServerEntity
{
    public Guid ShopId { get; set; }
    [MaxLength(100)] public string Category { get; set; } = string.Empty;
    [MaxLength(300)] public string Description { get; set; } = string.Empty;
    public long AmountXof { get; set; }
    public PaymentMode Mode { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class StockMovement : ServerEntity
{
    public Guid ShopId { get; set; }
    public Guid VariantId { get; set; }
    public StockMovementType Type { get; set; }
    public decimal QuantityDelta { get; set; }
    public long UnitCostXof { get; set; }
    [MaxLength(250)] public string Reason { get; set; } = string.Empty;
    [MaxLength(80)] public string SourceType { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
    [MaxLength(80)] public string Actor { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

// --- Commandes du site vitrine --------------------------------------------

/// <summary>
/// Demande passée depuis le site vitrine ou reçue par un autre canal. Ce n'est pas une vente :
/// rien n'est encaissé, rien ne sort du stock. Elle le devient quand une caisse crée la vente
/// correspondante — c'est cette vente qui fait foi, jamais le seul changement d'état.
///
/// La boutique est choisie par la cliente au moment de commander : inventer une règle
/// d'affectation automatique produirait surtout des commandes envoyées au mauvais endroit.
/// </summary>
public sealed class Order : ServerEntity
{
    public Guid ShopId { get; set; }
    [MaxLength(40)] public string Number { get; set; } = string.Empty;
    [MaxLength(160)] public string CustomerName { get; set; } = string.Empty;
    [MaxLength(30)] public string Phone { get; set; } = string.Empty;
    [MaxLength(500)] public string? Note { get; set; }
    public OrderChannel Channel { get; set; } = OrderChannel.Vitrine;
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public long TotalXof { get; set; }
    /// <summary>Renseigné à la création de la vente en caisse. Sa présence est ce qui distingue
    /// une commande réellement traitée d'une case cochée.</summary>
    public Guid? SaleId { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    [MaxLength(300)] public string? CancelReason { get; set; }
    /// <summary>Curseur de descente : la commande voyage vers la caisse de sa boutique.</summary>
    public long Seq { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

public sealed class OrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid VariantId { get; set; }
    [MaxLength(80)] public string Sku { get; set; } = string.Empty;
    [MaxLength(200)] public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    /// <summary>Prix figé au moment de la commande : une cliente ne doit pas découvrir en
    /// boutique que l'article a augmenté depuis qu'elle l'a réservé.</summary>
    public long UnitPriceXof { get; set; }
}

// ---------------------------------------------------------------------------
// Publication logicielle (lot 5). Voir docs/lot5-mises-a-jour-a-distance.md.
// ---------------------------------------------------------------------------

/// <summary>
/// Un fichier de mise à jour tel que « vpk pack » l'a produit : paquet complet ou delta. Les
/// champs reprennent exactement ceux que Velopack attend dans releases.{canal}.json — le serveur
/// ne réinvente pas ce format, il le stocke et le refiltre.
/// </summary>
public sealed class ReleaseAsset : ServerEntity
{
    [MaxLength(60)] public string PackageId { get; set; } = string.Empty;
    [MaxLength(40)] public string Version { get; set; } = string.Empty;
    /// <summary>« win » pour les terminaux Windows. Velopack demande releases.{Channel}.json.</summary>
    [MaxLength(20)] public string Channel { get; set; } = "win";
    /// <summary>« Full » ou « Delta ».</summary>
    [MaxLength(10)] public string Type { get; set; } = "Full";
    [MaxLength(200)] public string FileName { get; set; } = string.Empty;
    [MaxLength(64)] public string Sha1 { get; set; } = string.Empty;
    [MaxLength(64)] public string? Sha256 { get; set; }
    public long Size { get; set; }
    public string? NotesMarkdown { get; set; }
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Version retirée : les terminaux qui ne l'ont pas encore prise ne la prendront
    /// plus. On ne peut pas rappeler ce qui est déjà installé — pour cela, on republie.</summary>
    public bool IsWithdrawn { get; set; }
}

/// <summary>
/// Qui reçoit quelle version. C'est ici, et nulle part ailleurs, que vit l'échelonnement : le
/// terminal ne connaît aucune règle, il demande seulement « qu'est-ce qu'il y a pour moi ».
/// </summary>
public sealed class ReleaseTarget : ServerEntity
{
    [MaxLength(40)] public string Version { get; set; } = string.Empty;
    [MaxLength(20)] public string Channel { get; set; } = "win";
    /// <summary>Null = toutes les boutiques. Renseigné = cette boutique seulement.</summary>
    public Guid? ShopId { get; set; }
    public Shop? Shop { get; set; }
}
