using BoutiqueFashion.Domain;

namespace BoutiqueFashion.Application;

public sealed record SaleLineDraft(Guid VariantId, decimal Quantity, DiscountKind DiscountKind = DiscountKind.None, decimal DiscountValue = 0);
public sealed record PaymentDraft(PaymentMode Mode, long AmountXof, string? Reference = null);
public sealed record SaleDraft(
    string IdempotencyKey,
    IReadOnlyList<SaleLineDraft> Lines,
    IReadOnlyList<PaymentDraft> Payments,
    Guid? CustomerId = null,
    DiscountKind DiscountKind = DiscountKind.None,
    decimal DiscountValue = 0,
    string? DiscountReason = null,
    string? ManagerPin = null,
    DateTimeOffset? CreditDueAt = null,
    string? NewCustomerName = null,
    string? NewCustomerPhone = null,
    // Avance « réservé jusqu'au solde » : la marchandise reste en boutique et n'est que réservée.
    // Sans ce drapeau, une vente à crédit sort le stock immédiatement.
    // (commentaire simple et non doc XML : une balise ici déclencherait CS1587, promu en erreur.)
    bool ReserveStock = false,
    // Commande du site vitrine que cette vente vient honorer. Renseignée, elle passe en
    // « traitée » dans la transaction même qui crée la vente : c'est ce qui garantit qu'un
    // état « traité » correspond toujours à un encaissement réel.
    Guid? OrderId = null);

public sealed record SaleResult(Guid SaleId, string Number, long TotalXof, Guid DocumentId, bool AlreadyExisted, bool HasNegativeStock, long ChangeXof = 0, Guid? InvoiceDocumentId = null);
public sealed record StockAdjustment(Guid VariantId, decimal QuantityDelta, StockMovementType Type, long UnitCostXof, string Reason, string Actor);
public sealed record DashboardSummary(long SalesXof, long CollectedXof, long GrossMarginXof, long ExpensesXof, long CreditBalanceXof, int LowStockCount, long EstimatedProfitXof = 0, bool CostWarning = false, int SalesCount = 0, BestSeller? BestSeller = null);
/// <summary>Article le mieux vendu sur la période, agrégé au niveau du produit et non de la
/// variante : « la robe Amina » parle, « ROBE-AMINA-M-ROUGE » beaucoup moins.</summary>
public sealed record BestSeller(string Label, decimal Quantity, long ValueXof);
public sealed record RecentSaleRow(string Number, string Time, string Customer, long TotalXof);
public sealed record PrinterProfile(string Name, PrinterConnectionKind ConnectionKind, string Address, PaperWidth PaperWidth, bool CutPaper = true);
public sealed record ReceiptItem(string Description, decimal Quantity, long UnitPriceXof, long DiscountXof, long TotalXof);
public sealed record TicketLine(string Text, bool Bold = false, bool DoubleHeight = false);
public sealed record ReceiptData(string ShopName, string? Address, string? Phone, string Number, DateTimeOffset IssuedAt, string? Customer, IReadOnlyList<ReceiptItem> Items, long SubtotalXof, long DiscountXof, long TotalXof, IReadOnlyList<PaymentDraft> Payments, string Footer, bool IsDuplicate = false, string? Email = null, string? TaxId = null, string? Slogan = null, string? LogoPath = null, string? StampPath = null, string? SignaturePath = null, string? ReturnPolicy = null, long ChangeXof = 0, DocumentStyle Style = DocumentStyle.Moderne, DocumentType Kind = DocumentType.Receipt);
public sealed record ImportRow(string Product, string Category, string? Brand, string Sku, string? Barcode, string? Size, string? Color, long CostXof, long PriceXof, decimal Quantity, decimal AlertThreshold);
public sealed record ImportIssue(int Line, string Message);
public sealed record ImportPreview(IReadOnlyList<ImportRow> Rows, IReadOnlyList<ImportIssue> Issues);
public sealed record ReportRow(string Label, long ValueXof, decimal Quantity = 0);
// IsReserved distingue les deux formes d'avance : marchandise mise de côté, ou déjà emportée.
public sealed record CreditSummary(Guid Id, string SaleNumber, string CustomerName, long OriginalXof, long BalanceXof, DateTimeOffset DueAt, CreditStatus Status, bool IsReserved = false, string? CustomerPhone = null);
public sealed record CreditPaymentRow(Guid Id, string Number, long AmountXof, PaymentMode Mode, DateTimeOffset Date, bool IsReversal, bool Reversed);
public sealed record CreditPaymentResult(Guid PaymentId, string Number, long NewBalanceXof, Guid DocumentId);
public sealed record ReturnRequest(string SaleNumber, string ReturnedSku, decimal ReturnedQuantity, string? ReplacementSku, decimal ReplacementQuantity, IReadOnlyList<PaymentDraft> DifferencePayments, string Reason, string ManagerPin, bool Restock = true);
public sealed record ReturnResult(Guid CreditNoteId, string CreditNoteNumber, long DifferenceXof);
public sealed record InventoryCount(Guid VariantId, decimal CountedQuantity);
public sealed record StockHistoryRow(DateTimeOffset Date, string Sku, string Product, StockMovementType Type, decimal Delta, string Reason, string Actor);
public sealed record StockAlertRow(string Sku, string Product, decimal Quantity, decimal Threshold, string Kind, string? RelatedSale);
public sealed record CashClosingRow(string Number, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, long ExpectedXof, long CountedXof, long DifferenceXof, string? Reason);
public sealed record DocumentListItem(Guid Id, string Number, DocumentType Type, DateTimeOffset IssuedAt, int PrintCount);
public sealed record CustomerRow(Guid Id, string Name, string? Phone, CustomerSegment Segment, long OutstandingXof);
public sealed record CustomerUpdateRequest(Guid Id, string Name, string? Phone, string? SecondaryPhone, string? Gender, string? Address, string? Notes, string? Preferences, string? PreferredChannel, bool MarketingConsent, long CreditLimitXof);
public sealed record CustomerHistorySale(string Number, DateTimeOffset Date, long TotalXof, SaleStatus Status);
public sealed record CustomerHistoryPayment(string Number, DateTimeOffset Date, long AmountXof, PaymentMode Mode, string SaleNumber);
public sealed record CustomerHistory(IReadOnlyList<CustomerHistorySale> Sales, IReadOnlyList<CustomerHistoryPayment> Payments);
public sealed record MatrixDraft(
    string ProductName,
    string CategoryName,
    string SkuPrefix,
    IReadOnlyList<string> Colors,
    IReadOnlyList<string> Sizes,
    long CostXof,
    long PriceXof,
    decimal InitialQuantity,
    decimal AlertThreshold,
    ProductType Type = ProductType.Clothing,
    string? Brand = null,
    string? SubCategory = null,
    string? Gender = null,
    string? Season = null,
    string? Material = null,
    string? Supplier = null,
    string? ManagerPin = null);
public sealed record ProductUpdate(Guid VariantId, string ProductName, string Category, string Sku, string? Barcode, string? Size, string? Color, long CostXof, long PriceXof, long? PromotionalPriceXof, DateTimeOffset? PromotionStartsAt, DateTimeOffset? PromotionEndsAt, decimal AlertThreshold, string? PhotoPath, bool IsActive, string? SubCategory = null, string? Gender = null, string? Season = null, string? Material = null, string? Location = null, string? Supplier = null, ProductType Type = ProductType.Clothing, string? Description = null);

public sealed record PurchaseLineDraft(Guid VariantId, decimal ExpectedQuantity);
public sealed record PurchaseOrderRow(Guid OrderId, Guid LineId, string Supplier, string Sku, string ProductName, decimal Expected, decimal Received);
public interface IPurchaseService
{
    Task<Guid> CreateOrderAsync(string supplier, IReadOnlyList<PurchaseLineDraft> lines, string? note = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PurchaseOrderRow>> ListOpenAsync(CancellationToken cancellationToken = default);
    Task ReceiveAsync(Guid orderLineId, decimal receivedQuantity, long unitCostXof = 0, string actor = "Responsable", CancellationToken cancellationToken = default);
}

public interface ISaleService
{
    Task<SaleResult> CreateAsync(SaleDraft draft, CancellationToken cancellationToken = default);
}

public interface IStockService
{
    Task AdjustAsync(StockAdjustment adjustment, string? managerPin = null, CancellationToken cancellationToken = default);
}

public interface IAuthorizationService
{
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);
    Task ConfigurePinAsync(string pin, CancellationToken cancellationToken = default);
    Task ChangePinAsync(string oldPin, string newPin, CancellationToken cancellationToken = default);
    Task<bool> AuthorizeSensitiveActionAsync(string pin, string action, string actor = "Responsable", CancellationToken cancellationToken = default);
}

/// <summary>Photographie de la vacation en cours : ce que la caisse devrait contenir, et pourquoi.
/// Calculée à la demande — rien n'est stocké tant que la caisse n'est pas clôturée.</summary>
public sealed record CashDeskState(
    Guid Id, string Number, string OperatorName, DateTimeOffset OpenedAt, bool HasShiftPin,
    long OpeningFloatXof, long CashSalesXof, long CashCreditPaymentsXof, long CashExpensesXof,
    long ExpectedCashXof, int SalesCount, long TotalSalesXof,
    IReadOnlyList<ReportRow> CollectedByMode,
    long MovementsInXof = 0, long MovementsOutXof = 0,
    IReadOnlyList<CashMovementRow>? Movements = null);

public sealed record CashMovementRow(Guid Id, CashMovementDirection Direction, long AmountXof, string Reason, string Actor, DateTimeOffset At);

public interface ICashSessionService
{
    /// <summary>
    /// Espèces entrant ou sortant du tiroir hors vente et hors dépense. Le motif est obligatoire.
    /// Au-delà du plafond « Cash.MovementLimitXof », une sortie exige le code gérant : les petits
    /// mouvements du quotidien passent seuls, emporter la recette non.
    /// </summary>
    Task<CashMovement> RecordMovementAsync(CashMovementDirection direction, long amountXof, string reason, string? pin = null, CancellationToken cancellationToken = default);
    /// <param name="operatorName">Personne qui tient la caisse. Vide : le nom de la boutique.</param>
    /// <param name="operatorPin">PIN de vacation. Nul : seul le PIN gérant pourra clôturer.</param>
    Task<CashSession> OpenAsync(long openingFloatXof, string? operatorName = null, string? operatorPin = null, CancellationToken cancellationToken = default);
    Task<CashSession?> GetOpenAsync(CancellationToken cancellationToken = default);
    /// <param name="pin">PIN de vacation ou PIN gérant. Un écart hors tolérance exige le PIN gérant.</param>
    Task<CashSession> CloseAsync(long countedCashXof, string? differenceReason, string? pin = null, CancellationToken cancellationToken = default);
    /// <summary>Déverrouillage de l'espace vendeur après mise en veille.</summary>
    Task<bool> VerifyShiftPinAsync(string pin, CancellationToken cancellationToken = default);
    Task<CashDeskState?> GetStateAsync(CancellationToken cancellationToken = default);
}

public interface IThermalPrinterService
{
    IReadOnlyList<PrinterProfile> Discover();
    IReadOnlyList<TicketLine> Preview(ReceiptData receipt, PaperWidth paperWidth = PaperWidth.Mm80);
    Task<string> DiagnoseAsync(PrinterProfile printer, CancellationToken cancellationToken = default);
    Task PrintTestAsync(PrinterProfile printer, CancellationToken cancellationToken = default);
    Task PrintReceiptAsync(PrinterProfile printer, ReceiptData receipt, CancellationToken cancellationToken = default);
}

public interface IA4DocumentService
{
    byte[] CreateInvoicePdf(ReceiptData data);
    byte[] CreatePreviewImage(ReceiptData data);
    Task<string> PrintInvoiceAsync(ReceiptData data, string? printerName = null, CancellationToken cancellationToken = default);
}

public interface IPrintQueueService
{
    Task EnqueueAsync(string idempotencyKey, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);
}

public interface IBackupService
{
    Task<string> CreateAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(string path, CancellationToken cancellationToken = default);
    Task RestoreAsync(string path, string managerPin, CancellationToken cancellationToken = default);
}

public interface IProductImportService
{
    Task<ImportPreview> PreviewAsync(string csvPath, CancellationToken cancellationToken = default);
    Task<int> ImportAsync(ImportPreview preview, CancellationToken cancellationToken = default);
}

public interface IReportService
{
    Task<DashboardSummary> DashboardAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> SalesByPaymentModeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> SalesByDayAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> SalesBySellerAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> TopProductsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    /// <summary>Même classement, mais regroupé par produit plutôt que par variante.</summary>
    Task<IReadOnlyList<ReportRow>> TopProductsByProductAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> NoSalesProductsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> StockValueByCategoryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> InventoryVarianceAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> CorrectionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> RotationAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CashClosingRow>> CashClosingsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StockAlertRow>> StockAlertsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecentSaleRow>> RecentSalesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}

public interface ICatalogService
{
    Task<IReadOnlyList<ProductVariant>> SearchAsync(string? query, CancellationToken cancellationToken = default);
    Task<ProductVariant> CreateVariantAsync(string productName, string categoryName, string sku, string? barcode, string? size, string? color, long costXof, long priceXof, decimal initialQuantity, decimal alertThreshold, CancellationToken cancellationToken = default, string? subCategory = null, string? gender = null, string? season = null, string? material = null, string? location = null, string? supplier = null, ProductType type = ProductType.Clothing, string? description = null, string? photoPath = null, string? managerPin = null);
    Task<IReadOnlyList<ProductVariant>> CreateMatrixAsync(MatrixDraft draft, CancellationToken cancellationToken = default);
    Task<ProductVariant> UpdateVariantAsync(ProductUpdate update, string managerPin, CancellationToken cancellationToken = default);
    Task DeleteVariantAsync(Guid variantId, string managerPin, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Produit proposé par un vendeur, en attente de validation par le gérant.</summary>
public sealed record ProductDraft(
    Guid Id, string ProductName, string CategoryName, ProductType Type, string? Brand, string? Description,
    string? Gender, decimal InitialQuantity, long CostXof, long PriceXof,
    IReadOnlyList<ProductDraftLine> Lines, DateTimeOffset CreatedAt);

public sealed record ProductDraftLine(string? Size, string? Color, string? Material, string? PhotoPath, long? CostXof, long? PriceXof);

/// <summary>
/// Stocke les brouillons hors des tables produit : ils ne doivent ni être vendables, ni compter en stock,
/// et la base est créée par EnsureCreated (aucune migration ne pourrait ajouter une colonne ailleurs).
/// </summary>
public interface IProductDraftService
{
    Task<Guid> SubmitAsync(ProductDraft draft, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductDraft>> ListAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid draftId, CancellationToken cancellationToken = default);
}

public interface ICustomerService
{
    Task<IReadOnlyList<CustomerRow>> SearchAsync(string? query, CancellationToken cancellationToken = default);
    Task<Customer?> GetAsync(Guid customerId, CancellationToken cancellationToken = default);
    Task<Customer> CreateAsync(string name, string? phone, long creditLimitXof, CancellationToken cancellationToken = default, string? gender = null, string? preferences = null, string? channel = null, bool marketingConsent = false);
    Task<Customer> UpdateAsync(CustomerUpdateRequest request, CancellationToken cancellationToken = default);
    Task ArchiveAsync(Guid customerId, string managerPin, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid customerId, string managerPin, CancellationToken cancellationToken = default);
    Task<CustomerHistory> HistoryAsync(Guid customerId, CancellationToken cancellationToken = default);
}

public interface IExpenseService
{
    Task<Expense> CreateAsync(string category, string description, long amountXof, PaymentMode mode, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid expenseId, string managerPin, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Expense>> ListRecentAsync(int count = 20, CancellationToken cancellationToken = default);
}

/// <summary>Ce que la barre de titre doit pouvoir dire d'un coup d'œil : appairé ou non, combien
/// d'événements attendent, et si la dernière tentative a échoué.</summary>
public sealed record SyncState(
    bool IsEnrolled, string? ShopName, int PendingCount,
    DateTimeOffset? LastSuccessAt, string? LastError, bool IsRunning);

/// <summary>
/// Verdict sur la possibilité d'appliquer une mise à jour maintenant. <c>Reason</c> est destiné
/// au journal et à la remontée vers le téléphone, pas au vendeur : il n'a rien à décider.
/// </summary>
public sealed record UpdateReadiness(bool CanApply, string Reason);

/// <summary>État de mise à jour du terminal, tel qu'il remonte au serveur à chaque cycle.</summary>
public sealed record UpdateStatus(string? CurrentVersion, string? PendingVersion, string? LastError);

/// <summary>
/// Règles métier de la mise à jour, séparées de la mécanique Velopack pour être testables : la
/// question « a-t-on le droit d'installer maintenant ? » n'a rien à voir avec WPF.
/// </summary>
public interface IUpdateService
{
    /// <summary>Une vacation ouverte ou une file de synchronisation non vide interdisent
    /// d'installer. Prend une sauvegarde quand le verdict est positif — c'est le seul retour
    /// arrière possible sur les données.</summary>
    Task<UpdateReadiness> PrepareAsync(CancellationToken cancellationToken = default);
    Task<UpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task RecordAsync(string? currentVersion, string? pendingVersion, string? lastError, CancellationToken cancellationToken = default);
}

public interface ISyncService
{
    Task<SyncState> GetStateAsync(CancellationToken cancellationToken = default);
    /// <summary>Échange un code d'appairage contre un jeton d'appareil. Exige le réseau, une fois.</summary>
    Task<SyncState> EnrollAsync(string serverUrl, string code, string deviceName, CancellationToken cancellationToken = default);
    /// <summary>Un cycle complet : remontée des faits puis descente du référentiel. Ne lève
    /// jamais — hors réseau, la file grossit et la boutique continue.</summary>
    Task<SyncState> RunOnceAsync(CancellationToken cancellationToken = default);
    Task<SyncState> ForgetAsync(string managerPin, CancellationToken cancellationToken = default);
}

public interface IAppSettingsService
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, string actor = "Responsable", CancellationToken cancellationToken = default);
}

public interface ICreditService
{
    Task<IReadOnlyList<CreditSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CreditPaymentRow>> ListPaymentsAsync(Guid creditId, CancellationToken cancellationToken = default);
    Task<CreditPaymentResult> PayAsync(Guid creditId, long amountXof, PaymentMode mode, string? reference, CancellationToken cancellationToken = default);
    Task<CreditPaymentResult> ReverseAsync(Guid paymentId, string reason, string managerPin, CancellationToken cancellationToken = default);
}

public interface IReturnService
{
    Task<ReturnResult> ReturnOrExchangeAsync(ReturnRequest request, CancellationToken cancellationToken = default);
    Task<ReturnResult> CancelSaleAsync(string saleNumber, string reason, string managerPin, CancellationToken cancellationToken = default);
}

public interface IInventoryService
{
    Task ApplyCountAsync(IReadOnlyList<InventoryCount> counts, string reason, string managerPin, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StockHistoryRow>> HistoryAsync(Guid? variantId = null, CancellationToken cancellationToken = default);
}

public interface IDocumentService
{
    Task<IReadOnlyList<DocumentListItem>> ListAsync(string? query = null, CancellationToken cancellationToken = default);
    Task<DocumentSnapshot> CreateProformaAsync(ReceiptData data, CancellationToken cancellationToken = default);
    Task<ReceiptData> GetReceiptAsync(Guid documentId, bool duplicate, CancellationToken cancellationToken = default);
    Task<ReceiptData> BuildSampleAsync(DocumentType type, CancellationToken cancellationToken = default);
    Task MarkPrintedAsync(Guid documentId, CancellationToken cancellationToken = default);
}

public sealed record OrderLineRow(Guid VariantId, string Sku, string Description, decimal Quantity, long UnitPriceXof);

public sealed record OrderRow(
    Guid Id, string Number, string CustomerName, string Phone, string? Note,
    OrderChannel Channel, OrderStatus Status, long TotalXof, Guid? SaleId,
    DateTimeOffset PlacedAt, IReadOnlyList<OrderLineRow> Lines);

public interface IOrderService
{
    Task<IReadOnlyList<OrderRow>> ListAsync(bool includeClosed = false, CancellationToken cancellationToken = default);
    /// <summary>Marque la commande livrée. Seule une commande déjà encaissée peut l'être :
    /// on ne remet pas une marchandise qui n'a pas été payée.</summary>
    Task MarkDeliveredAsync(Guid orderId, CancellationToken cancellationToken = default);
}
