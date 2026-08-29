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
    DateTimeOffset? CreditDueAt = null);

public sealed record SaleResult(Guid SaleId, string Number, long TotalXof, Guid DocumentId, bool AlreadyExisted, bool HasNegativeStock);
public sealed record StockAdjustment(Guid VariantId, decimal QuantityDelta, StockMovementType Type, long UnitCostXof, string Reason, string Actor);
public sealed record DashboardSummary(long SalesXof, long CollectedXof, long GrossMarginXof, long ExpensesXof, long CreditBalanceXof, int LowStockCount);
public sealed record PrinterProfile(string Name, PrinterConnectionKind ConnectionKind, string Address, PaperWidth PaperWidth, bool CutPaper = true);
public sealed record ReceiptItem(string Description, decimal Quantity, long UnitPriceXof, long DiscountXof, long TotalXof);
public sealed record ReceiptData(string ShopName, string? Address, string? Phone, string Number, DateTimeOffset IssuedAt, string? Customer, IReadOnlyList<ReceiptItem> Items, long SubtotalXof, long DiscountXof, long TotalXof, IReadOnlyList<PaymentDraft> Payments, string Footer, bool IsDuplicate = false, string? Email = null, string? TaxId = null, string? Slogan = null, string? LogoPath = null, string? StampPath = null, string? SignaturePath = null, string? ReturnPolicy = null);
public sealed record ImportRow(string Product, string Category, string? Brand, string Sku, string? Barcode, string? Size, string? Color, long CostXof, long PriceXof, decimal Quantity, decimal AlertThreshold);
public sealed record ImportIssue(int Line, string Message);
public sealed record ImportPreview(IReadOnlyList<ImportRow> Rows, IReadOnlyList<ImportIssue> Issues);
public sealed record ReportRow(string Label, long ValueXof, decimal Quantity = 0);
public sealed record CreditSummary(Guid Id, string SaleNumber, string CustomerName, long OriginalXof, long BalanceXof, DateTimeOffset DueAt, CreditStatus Status);
public sealed record CreditPaymentResult(Guid PaymentId, string Number, long NewBalanceXof, Guid DocumentId);
public sealed record ReturnRequest(string SaleNumber, string ReturnedSku, decimal ReturnedQuantity, string? ReplacementSku, decimal ReplacementQuantity, IReadOnlyList<PaymentDraft> DifferencePayments, string Reason, string ManagerPin);
public sealed record ReturnResult(Guid CreditNoteId, string CreditNoteNumber, long DifferenceXof);
public sealed record InventoryCount(Guid VariantId, decimal CountedQuantity);
public sealed record StockHistoryRow(DateTimeOffset Date, string Sku, string Product, StockMovementType Type, decimal Delta, string Reason, string Actor);
public sealed record DocumentListItem(Guid Id, string Number, DocumentType Type, DateTimeOffset IssuedAt, int PrintCount);
public sealed record ProductUpdate(Guid VariantId, string ProductName, string Category, string Sku, string? Barcode, string? Size, string? Color, long CostXof, long PriceXof, long? PromotionalPriceXof, DateTimeOffset? PromotionStartsAt, DateTimeOffset? PromotionEndsAt, decimal AlertThreshold, string? PhotoPath, bool IsActive);

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
    Task<bool> AuthorizeSensitiveActionAsync(string pin, string action, string actor = "Responsable", CancellationToken cancellationToken = default);
}

public interface ICashSessionService
{
    Task<CashSession> OpenAsync(long openingFloatXof, CancellationToken cancellationToken = default);
    Task<CashSession?> GetOpenAsync(CancellationToken cancellationToken = default);
    Task<CashSession> CloseAsync(long countedCashXof, string? differenceReason, CancellationToken cancellationToken = default);
}

public interface IThermalPrinterService
{
    IReadOnlyList<PrinterProfile> Discover();
    Task PrintTestAsync(PrinterProfile printer, CancellationToken cancellationToken = default);
    Task PrintReceiptAsync(PrinterProfile printer, ReceiptData receipt, CancellationToken cancellationToken = default);
}

public interface IA4DocumentService
{
    byte[] CreateInvoicePdf(ReceiptData data);
    Task PrintInvoiceAsync(ReceiptData data, string? printerName = null, CancellationToken cancellationToken = default);
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
}

public interface ICatalogService
{
    Task<IReadOnlyList<ProductVariant>> SearchAsync(string? query, CancellationToken cancellationToken = default);
    Task<ProductVariant> CreateVariantAsync(string productName, string categoryName, string sku, string? barcode, string? size, string? color, long costXof, long priceXof, decimal initialQuantity, decimal alertThreshold, CancellationToken cancellationToken = default);
    Task<ProductVariant> UpdateVariantAsync(ProductUpdate update, string managerPin, CancellationToken cancellationToken = default);
}

public interface ICustomerService
{
    Task<IReadOnlyList<Customer>> SearchAsync(string? query, CancellationToken cancellationToken = default);
    Task<Customer> CreateAsync(string name, string? phone, long creditLimitXof, CancellationToken cancellationToken = default);
}

public interface IExpenseService
{
    Task<Expense> CreateAsync(string category, string description, long amountXof, PaymentMode mode, CancellationToken cancellationToken = default);
}

public interface IAppSettingsService
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, string actor = "Responsable", CancellationToken cancellationToken = default);
}

public interface ICreditService
{
    Task<IReadOnlyList<CreditSummary>> ListAsync(CancellationToken cancellationToken = default);
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
    Task<IReadOnlyList<DocumentListItem>> ListAsync(CancellationToken cancellationToken = default);
    Task<DocumentSnapshot> CreateProformaAsync(ReceiptData data, CancellationToken cancellationToken = default);
    Task<ReceiptData> GetReceiptAsync(Guid documentId, bool duplicate, CancellationToken cancellationToken = default);
    Task MarkPrintedAsync(Guid documentId, CancellationToken cancellationToken = default);
}
