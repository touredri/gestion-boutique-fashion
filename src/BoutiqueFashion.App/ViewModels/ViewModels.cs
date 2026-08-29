using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoutiqueFashion.App.ViewModels;

public partial class ShellViewModel(DashboardViewModel dashboard, SaleViewModel sale, CatalogViewModel catalog, StockViewModel stock, CustomersViewModel customers, ExpensesViewModel expenses, DocumentsViewModel documents, ReportsViewModel reports, SettingsViewModel settings) : ObservableObject
{
    [ObservableProperty] private object currentPage = dashboard; [ObservableProperty] private string pageTitle = "Tableau de bord";
    public async Task InitializeAsync() { await dashboard.LoadAsync(); await sale.LoadAsync(); }
    [RelayCommand] private async Task Navigate(string target)
    {
        (object Page, string Title) next = target switch { "Sale" => (sale, "Vente / Caisse"), "Catalog" => (catalog, "Produits et variantes"), "Stock" => (stock, "Gestion du stock"), "Customers" => (customers, "Clients et crédits"), "Expenses" => (expenses, "Dépenses"), "Documents" => (documents, "Documents et opérations"), "Reports" => (reports, "Rapports"), "Settings" => (settings, "Paramètres"), _ => (dashboard, "Tableau de bord") };
        CurrentPage = next.Page; PageTitle = next.Title;
        if (CurrentPage is ILoadable loadable) await loadable.LoadAsync();
    }
}

public interface ILoadable { Task LoadAsync(); }

internal static class UiConfirm
{
    public static bool Ask(string message) => MessageBox.Show(message, "Confirmation requise", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

public partial class DashboardViewModel(IReportService reports) : ObservableObject, ILoadable
{
    [ObservableProperty] private DashboardSummary summary = new(0, 0, 0, 0, 0, 0);
    public async Task LoadAsync() { var now = DateTimeOffset.Now; Summary = await reports.DashboardAsync(new DateTimeOffset(now.Date, now.Offset), new DateTimeOffset(now.Date.AddDays(1), now.Offset)); }
}

public partial class CartLineViewModel(ProductVariant variant) : ObservableObject
{
    public ProductVariant Variant { get; } = variant;
    public string Label => string.Join(" - ", new[] { Variant.Product?.Name, Variant.Color, Variant.Size }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public IReadOnlyList<string> DiscountKindLabels { get; } = ["Aucune", "%", "FCFA"];
    public long UnitPriceXof
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return Variant.PromotionalPriceXof is not null && Variant.PromotionStartsAt <= now && Variant.PromotionEndsAt >= now ? Variant.PromotionalPriceXof.Value : Variant.PriceXof;
        }
    }
    [ObservableProperty] private decimal quantity = 1;
    [ObservableProperty] private string discountKindLabel = "Aucune";
    [ObservableProperty] private decimal discountValue;
    public DiscountKind EffectiveDiscountKind => DiscountKindLabel switch { "%" => DiscountKind.Percentage, "FCFA" => DiscountKind.Amount, _ => DiscountKind.None };
    public long GrossXof => decimal.ToInt64(Quantity * UnitPriceXof);
    public long DiscountAmountXof => GrossXof - TotalXof;
    public long TotalXof
    {
        get
        {
            try { return GrossXof - BusinessRules.CalculateDiscount(GrossXof, EffectiveDiscountKind, DiscountValue); }
            catch { return GrossXof; }
        }
    }
    partial void OnQuantityChanged(decimal value) { OnPropertyChanged(nameof(TotalXof)); OnPropertyChanged(nameof(GrossXof)); OnPropertyChanged(nameof(DiscountAmountXof)); }
    partial void OnDiscountKindLabelChanged(string value) { OnPropertyChanged(nameof(TotalXof)); OnPropertyChanged(nameof(DiscountAmountXof)); }
    partial void OnDiscountValueChanged(decimal value) { OnPropertyChanged(nameof(TotalXof)); OnPropertyChanged(nameof(DiscountAmountXof)); }
}

public partial class PaymentLineViewModel : ObservableObject { public IReadOnlyList<PaymentMode> Modes { get; } = Enum.GetValues<PaymentMode>(); [ObservableProperty] private PaymentMode mode = PaymentMode.Cash; [ObservableProperty] private long amountXof; [ObservableProperty] private string reference = string.Empty; }

public partial class SaleViewModel(ICatalogService catalog, ICustomerService customers, ISaleService sales, ICashSessionService cash, IThermalPrinterService printerService, IAppSettingsService settings, IDocumentService documents) : ObservableObject, ILoadable
{
    public ObservableCollection<ProductVariant> Products { get; } = [];
    public ObservableCollection<CartLineViewModel> Cart { get; } = [];
    public ObservableCollection<CustomerRow> Customers { get; } = [];
    public ObservableCollection<PaymentLineViewModel> Payments { get; } = [];
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = Enum.GetValues<PaymentMode>();
    public ObservableCollection<PrinterProfile> Printers { get; } = [];
    [ObservableProperty] private string search = string.Empty;
    [ObservableProperty] private PaymentMode selectedPaymentMode = PaymentMode.Cash;
    [ObservableProperty] private PrinterProfile? selectedPrinter;
    [ObservableProperty] private CustomerRow? selectedCustomer;
    [ObservableProperty] private string newCustomerName = string.Empty;
    [ObservableProperty] private string newCustomerPhone = string.Empty;
    [ObservableProperty] private decimal discountPercent;
    [ObservableProperty] private string discountReason = string.Empty;
    [ObservableProperty] private string managerPin = string.Empty;
    [ObservableProperty] private string creditDueDate = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd");
    [ObservableProperty] private string status = "Prêt";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string countedCash = "";
    [ObservableProperty] private string cashDifferenceReason = "";
    [ObservableProperty] private string openingFloat = "0";
    [ObservableProperty] private string customerSearch = string.Empty;
    partial void OnCustomerSearchChanged(string value) => _ = RefreshCustomersAsync();
    public long TotalXof => Cart.Sum(x => x.TotalXof);
    public long PayableXof => TotalXof - BusinessRules.CalculateDiscount(TotalXof, DiscountPercent == 0 ? DiscountKind.None : DiscountKind.Percentage, DiscountPercent);
    public long ChangePreview { get { var sum = Payments.Sum(x => x.AmountXof); return sum > PayableXof ? sum - PayableXof : 0; } }
    partial void OnDiscountPercentChanged(decimal value) { OnPropertyChanged(nameof(PayableXof)); OnPropertyChanged(nameof(ChangePreview)); }

    public async Task LoadAsync()
    {
        var items = await catalog.SearchAsync(Search); Products.Clear(); foreach (var item in items) Products.Add(item);
        await RefreshCustomersAsync();
        Printers.Clear();
        foreach (var p in printerService.Discover()) Printers.Add(p);
        var tcp = await settings.GetAsync("Printer.Tcp");
        if (!string.IsNullOrWhiteSpace(tcp)) Printers.Add(new PrinterProfile($"Thermique réseau {tcp}", PrinterConnectionKind.TcpIp, tcp, PaperWidth.Mm80));
        if (SelectedPrinter is null) { var saved = await settings.GetAsync("Printer.Selected"); SelectedPrinter = saved is null ? Printers.FirstOrDefault() : JsonSerializer.Deserialize<PrinterProfile>(saved); }
    }

    private async Task RefreshCustomersAsync()
    {
        Customers.Clear();
        foreach (var item in await customers.SearchAsync(string.IsNullOrWhiteSpace(CustomerSearch) ? null : CustomerSearch)) Customers.Add(item);
    }

    [RelayCommand] private async Task FilterCustomers() => await RefreshCustomersAsync();
    [RelayCommand] private async Task SearchProducts() => await LoadAsync();
    [RelayCommand] private void Add(ProductVariant variant) { var existing = Cart.FirstOrDefault(x => x.Variant.Id == variant.Id); if (existing is null) { var line = new CartLineViewModel(variant); line.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(TotalXof)); OnPropertyChanged(nameof(ChangePreview)); }; Cart.Add(line); } else existing.Quantity++; OnPropertyChanged(nameof(TotalXof)); OnPropertyChanged(nameof(ChangePreview)); }
    [RelayCommand] private void Remove(CartLineViewModel line) { Cart.Remove(line); OnPropertyChanged(nameof(TotalXof)); OnPropertyChanged(nameof(ChangePreview)); }
    [RelayCommand] private void Increment(CartLineViewModel line) { line.Quantity++; }
    [RelayCommand] private void Decrement(CartLineViewModel line) { if (line.Quantity > 1) line.Quantity--; }
    [RelayCommand] private void QuickPay(PaymentMode mode) { Payments.Clear(); var line = new PaymentLineViewModel { Mode = mode, AmountXof = PayableXof }; line.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(PaymentTotalXof)); OnPropertyChanged(nameof(ChangePreview)); }; Payments.Add(line); OnPropertyChanged(nameof(PaymentTotalXof)); OnPropertyChanged(nameof(ChangePreview)); }
    partial void OnSelectedPrinterChanged(PrinterProfile? value) { if (value is not null) _ = PersistPrinterAsync(value); }
    private async Task PersistPrinterAsync(PrinterProfile value) { try { await settings.SetAsync("Printer.Selected", JsonSerializer.Serialize(value), "Vendeur boutique"); } catch { } }
    [RelayCommand] private async Task OpenCash() { try { await cash.OpenAsync(long.Parse(OpeningFloat)); Status = "Caisse ouverte"; } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private void AddPayment() { var line = new PaymentLineViewModel { AmountXof = Math.Max(0, PayableXof - Payments.Sum(x => x.AmountXof)) }; line.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(PaymentTotalXof)); OnPropertyChanged(nameof(ChangePreview)); }; Payments.Add(line); OnPropertyChanged(nameof(PaymentTotalXof)); OnPropertyChanged(nameof(ChangePreview)); }
    [RelayCommand] private void RemovePayment(PaymentLineViewModel line) { Payments.Remove(line); OnPropertyChanged(nameof(PaymentTotalXof)); OnPropertyChanged(nameof(ChangePreview)); }
    public long PaymentTotalXof => Payments.Sum(x => x.AmountXof);
    [RelayCommand] private async Task CloseCash() { try { var result = await cash.CloseAsync(long.Parse(CountedCash), CashDifferenceReason, ManagerPin); Status = $"Caisse clôturée • Écart {result.DifferenceXof:N0} FCFA"; } catch (Exception e) { Status = e.Message; } }

    [RelayCommand] private async Task Complete()
    {
        if (Cart.Count == 0 || IsBusy) return; IsBusy = true;
        try
        {
            var paymentDrafts = Payments.Count == 0 ? [new PaymentDraft(SelectedPaymentMode, PayableXof)] : Payments.Select(x => new PaymentDraft(x.Mode, x.AmountXof, x.Reference)).ToArray();
            var hasCredit = paymentDrafts.Any(x => x.Mode == PaymentMode.Credit);
            var key = Guid.NewGuid().ToString("N");
            DateTimeOffset? creditDue = hasCredit ? DateTimeOffset.Parse(CreditDueDate) : null;
            var draft = new SaleDraft(key, Cart.Select(x => new SaleLineDraft(x.Variant.Id, x.Quantity, x.EffectiveDiscountKind, x.DiscountValue)).ToArray(), paymentDrafts, SelectedCustomer?.Id, DiscountPercent == 0 ? DiscountKind.None : DiscountKind.Percentage, DiscountPercent, DiscountReason, ManagerPin, creditDue,
                SelectedCustomer is null ? NullIfEmpty(NewCustomerName) : null,
                SelectedCustomer is null ? NullIfEmpty(NewCustomerPhone) : null);
            var result = await sales.CreateAsync(draft);
            Status = $"Vente {result.Number} enregistrée";
            if (result.ChangeXof > 0) Status += $" • Monnaie à rendre : {result.ChangeXof:N0} FCFA";
            if (result.HasNegativeStock) Status += " • Alerte : stock négatif à régulariser";
            if (SelectedPrinter is not null)
            {
                try { var receipt = await documents.GetReceiptAsync(result.DocumentId, false); await printerService.PrintReceiptAsync(SelectedPrinter, receipt); await documents.MarkPrintedAsync(result.DocumentId); }
                catch (Exception e) { Status += $" • Impression: {e.Message}"; }
            }
            Cart.Clear(); Payments.Clear(); DiscountPercent = 0; NewCustomerName = string.Empty; NewCustomerPhone = string.Empty;
            OnPropertyChanged(nameof(TotalXof)); OnPropertyChanged(nameof(PayableXof)); OnPropertyChanged(nameof(PaymentTotalXof));
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; } finally { IsBusy = false; }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public partial class CatalogViewModel(ICatalogService catalog, IProductImportService import) : ObservableObject, ILoadable
{
    public ObservableCollection<ProductVariant> Items { get; } = [];
    public ObservableCollection<ImportIssue> ImportIssues { get; } = [];
    public IReadOnlyList<ProductType> ProductTypes { get; } = Enum.GetValues<ProductType>();
    private ImportPreview? importPreview;
    [ObservableProperty] private int importRowsCount;
    [ObservableProperty] private string productName = string.Empty; [ObservableProperty] private string category = "Vêtements"; [ObservableProperty] private string sku = string.Empty; [ObservableProperty] private string price = string.Empty; [ObservableProperty] private string cost = string.Empty; [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private string description = string.Empty; [ObservableProperty] private string barcode = string.Empty; [ObservableProperty] private string promoStart = string.Empty; [ObservableProperty] private string promoEnd = string.Empty;
    [ObservableProperty] private ProductType selectedType = ProductType.Clothing; [ObservableProperty] private string brand = string.Empty; [ObservableProperty] private string productNotice = string.Empty;
    [ObservableProperty] private string subCategory = string.Empty; [ObservableProperty] private string gender = string.Empty; [ObservableProperty] private string season = string.Empty;
    [ObservableProperty] private string material = string.Empty; [ObservableProperty] private string location = string.Empty; [ObservableProperty] private string supplier = string.Empty;
    [ObservableProperty] private string variantSize = string.Empty; [ObservableProperty] private string variantColor = string.Empty;
    [ObservableProperty] private string matrixPrefix = string.Empty; [ObservableProperty] private string matrixColors = string.Empty; [ObservableProperty] private string matrixSizes = string.Join(", ", BusinessRules.SizePresets(ProductType.Clothing)); [ObservableProperty] private string matrixQuantity = "0";
    [ObservableProperty] private ProductVariant? selected; [ObservableProperty] private string managerPin = ""; [ObservableProperty] private string promotionPrice = ""; [ObservableProperty] private string photoPath = "";
    public async Task LoadAsync() { var rows = await catalog.SearchAsync(null); Items.Clear(); foreach (var row in rows) Items.Add(row); }
    partial void OnSelectedTypeChanged(ProductType value) => MatrixSizes = string.Join(", ", BusinessRules.SizePresets(value));
    partial void OnProductNameChanged(string value) => _ = CheckExistingProductAsync(value);
    private async Task CheckExistingProductAsync(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) { ProductNotice = string.Empty; return; }
            var rows = await catalog.SearchAsync(name);
            var existing = rows.FirstOrDefault(x => string.Equals(x.Product?.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            ProductNotice = existing is null ? string.Empty : $"Un produit « {existing.Product!.Name } » existe déjà : ses variantes seront complétées, pas dupliquées.";
        }
        catch { ProductNotice = string.Empty; }
    }
    partial void OnSelectedChanged(ProductVariant? value)
    {
        PhotoPath = value?.PrimaryImagePath ?? string.Empty;
        if (value is not null)
        {
            ProductName = value.Product?.Name ?? string.Empty; Category = value.Product?.Category?.Name ?? category;
            SelectedType = value.Product?.Type ?? SelectedType; Brand = value.Product?.Brand ?? string.Empty;
            Description = value.Product?.Description ?? string.Empty; Barcode = value.Barcode ?? string.Empty;
            PromoStart = value.PromotionStartsAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? string.Empty;
            PromoEnd = value.PromotionEndsAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? string.Empty;
            SubCategory = value.Product?.SubCategory ?? string.Empty; Gender = value.Product?.Gender ?? string.Empty; Season = value.Product?.Season ?? string.Empty;
            Material = value.Material ?? string.Empty; Location = value.Location ?? string.Empty; Supplier = value.Supplier ?? string.Empty;
        }
    }
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [RelayCommand] private async Task Create() { try { await catalog.CreateVariantAsync(ProductName, Category, Sku, NullIfEmpty(Barcode), NullIfEmpty(VariantSize), NullIfEmpty(VariantColor), long.Parse(Cost), long.Parse(Price), 0, 2, default, NullIfEmpty(SubCategory), NullIfEmpty(Gender), NullIfEmpty(Season), NullIfEmpty(Material), NullIfEmpty(Location), NullIfEmpty(Supplier), SelectedType, NullIfEmpty(Description), NullIfEmpty(PhotoPath), NullIfEmpty(ManagerPin)); Status = "Produit ajouté"; ProductName = Sku = Price = Cost = string.Empty; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }

    [RelayCommand] private async Task CreateMatrix()
    {
        try
        {
            var colors = SplitList(MatrixColors); var sizes = SplitList(MatrixSizes);
            var created = await catalog.CreateMatrixAsync(new MatrixDraft(ProductName, Category, MatrixPrefix, colors, sizes, long.Parse(Cost), long.Parse(Price), decimal.Parse(MatrixQuantity), 2, SelectedType, NullIfEmpty(Brand), NullIfEmpty(SubCategory), NullIfEmpty(Gender), NullIfEmpty(Season), NullIfEmpty(Material), NullIfEmpty(Supplier), NullIfEmpty(ManagerPin)));
            Status = $"{created.Count} variantes uniques créées";
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }

    private static List<string> SplitList(string value) => value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    [RelayCommand] private async Task Update()
    {
        if (Selected is null) return;
        try
        {
            DateTimeOffset? promoStart = DateTimeOffset.TryParse(PromoStart, out var ps) ? ps : Selected.PromotionStartsAt;
            DateTimeOffset? promoEnd = DateTimeOffset.TryParse(PromoEnd, out var pe) ? pe : Selected.PromotionEndsAt;
            var u = new ProductUpdate(Selected.Id, string.IsNullOrWhiteSpace(ProductName) ? Selected.Product!.Name : ProductName, Category, string.IsNullOrWhiteSpace(Sku) ? Selected.Sku : Sku, NullIfEmpty(Barcode) ?? Selected.Barcode, Selected.Size, Selected.Color, string.IsNullOrWhiteSpace(Cost) ? Selected.CostXof : long.Parse(Cost), string.IsNullOrWhiteSpace(Price) ? Selected.PriceXof : long.Parse(Price), string.IsNullOrWhiteSpace(PromotionPrice) ? null : long.Parse(PromotionPrice), promoStart, promoEnd, Selected.LowStockThreshold, PhotoPath, true, NullIfEmpty(SubCategory), NullIfEmpty(Gender), NullIfEmpty(Season), NullIfEmpty(Material), NullIfEmpty(Location), NullIfEmpty(Supplier), SelectedType, NullIfEmpty(Description));
            await catalog.UpdateVariantAsync(u, ManagerPin); Status = "Produit modifié"; PhotoPath = string.Empty; await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task BrowseImportFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Fichier CSV d'import de produits", Filter = "CSV (*.csv)|*.csv", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        try
        {
            importPreview = await import.PreviewAsync(dialog.FileName);
            ImportIssues.Clear(); foreach (var issue in importPreview.Issues) ImportIssues.Add(issue);
            ImportRowsCount = importPreview.Rows.Count;
            Status = importPreview.Issues.Count == 0 ? $"{importPreview.Rows.Count} lignes prêtes à importer" : $"{importPreview.Issues.Count} problème(s) à corriger avant l'import";
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task RunImport()
    {
        if (importPreview is null || importPreview.Issues.Count > 0) { Status = "Corrigez les problèmes du fichier avant l'import."; return; }
        try
        {
            var count = await import.ImportAsync(importPreview);
            Status = $"{count} variantes importées";
            importPreview = null; ImportIssues.Clear(); ImportRowsCount = 0;
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task Archive() { if (Selected is null) return; if (!UiConfirm.Ask($"Archiver la variante {Selected.Sku} ? Elle restera dans l'historique mais ne pourra plus être vendue.")) return; try { await catalog.UpdateVariantAsync(new ProductUpdate(Selected.Id, Selected.Product!.Name, Selected.Product.Category?.Name ?? Category, Selected.Sku, Selected.Barcode, Selected.Size, Selected.Color, Selected.CostXof, Selected.PriceXof, Selected.PromotionalPriceXof, Selected.PromotionStartsAt, Selected.PromotionEndsAt, Selected.LowStockThreshold, null, false, Selected.Product.SubCategory, Selected.Product.Gender, Selected.Product.Season, Selected.Material, Selected.Location, Selected.Supplier, Selected.Product.Type), ManagerPin); Status = "Produit archivé"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
}

public partial class InventoryLineViewModel(ProductVariant variant) : ObservableObject
{
    public ProductVariant Variant { get; } = variant;
    public string Label => $"{Variant.Product?.Name} · {Variant.Sku}";
    public decimal CurrentQuantity => Variant.QuantityOnHand;
    [ObservableProperty] private decimal countedQuantity = variant.QuantityOnHand;
}

public partial class OrderDraftLine(string label, decimal expected) : ObservableObject
{
    public string Label { get; } = label;
    public decimal Expected { get; } = expected;
}

public partial class StockViewModel(ICatalogService catalog, IStockService stock, IInventoryService inventory, IReportService reports, IPurchaseService purchases) : ObservableObject, ILoadable
{
    public ObservableCollection<ProductVariant> Items { get; } = [];
    public ObservableCollection<StockHistoryRow> History { get; } = [];
    public ObservableCollection<StockAlertRow> Alerts { get; } = [];
    public ObservableCollection<InventoryLineViewModel> InventoryLines { get; } = [];
    public ObservableCollection<OrderDraftLine> OrderLines { get; } = [];
    public ObservableCollection<PurchaseOrderRow> OpenOrders { get; } = [];
    [ObservableProperty] private ProductVariant? selected; [ObservableProperty] private string quantity = string.Empty; [ObservableProperty] private string reason = string.Empty; [ObservableProperty] private string managerPin = string.Empty; [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private string countedQuantity = ""; [ObservableProperty] private string categoryFilter = string.Empty;
    [ObservableProperty] private string supplier = string.Empty; [ObservableProperty] private string orderExpected = "1";
    [ObservableProperty] private PurchaseOrderRow? selectedOpenLine; [ObservableProperty] private string receivedQuantity = "0"; [ObservableProperty] private string receivedCost = "";

    public async Task LoadAsync()
    {
        var rows = await catalog.SearchAsync(null); Items.Clear(); foreach (var row in rows) Items.Add(row);
        History.Clear(); foreach (var h in await inventory.HistoryAsync(Selected?.Id)) History.Add(h);
        Alerts.Clear(); foreach (var a in await reports.StockAlertsAsync()) Alerts.Add(a);
        OpenOrders.Clear(); foreach (var o in await purchases.ListOpenAsync()) OpenOrders.Add(o);
    }

    [RelayCommand] private async Task Receive() { if (Selected is null) return; try { await stock.AdjustAsync(new StockAdjustment(Selected.Id, decimal.Parse(Quantity), StockMovementType.Receipt, Selected.CostXof, Reason, "Responsable")); Status = "Réception enregistrée"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task Adjust() { if (Selected is null) return; try { await stock.AdjustAsync(new StockAdjustment(Selected.Id, decimal.Parse(Quantity), StockMovementType.Adjustment, Selected.CostXof, Reason, "Responsable"), ManagerPin); Status = "Ajustement enregistré"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task Damage() { if (Selected is null) return; try { await stock.AdjustAsync(new StockAdjustment(Selected.Id, -decimal.Parse(Quantity), StockMovementType.Damaged, Selected.CostXof, Reason, "Responsable"), ManagerPin); Status = "Article endommagé enregistré"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task Lose() { if (Selected is null) return; try { await stock.AdjustAsync(new StockAdjustment(Selected.Id, -decimal.Parse(Quantity), StockMovementType.Lost, Selected.CostXof, Reason, "Responsable"), ManagerPin); Status = "Perte enregistrée"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task ApplyInventory() { if (Selected is null) return; try { await inventory.ApplyCountAsync([new InventoryCount(Selected.Id, decimal.Parse(CountedQuantity))], Reason, ManagerPin); Status = "Inventaire validé"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }

    [RelayCommand] private async Task LoadFullInventory()
    {
        var rows = await catalog.SearchAsync(null);
        var filtered = string.IsNullOrWhiteSpace(CategoryFilter) ? rows : rows.Where(x => x.Product?.Category?.Name == CategoryFilter.Trim() || (x.Product?.Category?.Name ?? "").Contains(CategoryFilter.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        InventoryLines.Clear();
        foreach (var row in filtered) InventoryLines.Add(new InventoryLineViewModel(row));
        Status = $"{filtered.Count} articles chargés pour inventaire";
    }

    [RelayCommand] private async Task ApplyFullInventory()
    {
        try
        {
            var counts = InventoryLines.Where(x => x.CountedQuantity != x.CurrentQuantity).Select(x => new InventoryCount(x.Variant.Id, x.CountedQuantity)).ToArray();
            if (counts.Length == 0) { Status = "Aucun écart à valider."; return; }
            await inventory.ApplyCountAsync(counts, Reason, ManagerPin);
            Status = $"Inventaire validé : {counts.Length} écarts";
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private void AddToOrder()
    {
        if (Selected is null) return;
        OrderLines.Add(new OrderDraftLine($"{Selected.Product?.Name} · {Selected.Sku}", decimal.Parse(OrderExpected)));
    }

    [RelayCommand] private void RemoveOrderLine(OrderDraftLine line) => OrderLines.Remove(line);

    [RelayCommand] private async Task CreateOrder()
    {
        try
        {
            await purchases.CreateOrderAsync(Supplier, OrderLines.Select(x =>
            {
                var sku = x.Label.Split('·')[^1].Trim();
                var variant = Items.First(i => i.Sku == sku);
                return new PurchaseLineDraft(variant.Id, x.Expected);
            }).ToArray());
            Status = $"Commande fournisseur {Supplier} créée ({OrderLines.Count} lignes)";
            OrderLines.Clear(); Supplier = string.Empty;
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task ReceiveSelected()
    {
        if (SelectedOpenLine is null) return;
        try
        {
            var cost = long.TryParse(ReceivedCost, out var parsedCost) && parsedCost > 0 ? parsedCost : 0;
            await purchases.ReceiveAsync(SelectedOpenLine.LineId, decimal.Parse(ReceivedQuantity), cost);
            Status = $"Réception enregistrée : {ReceivedQuantity} × {SelectedOpenLine.Sku}";
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }
}

public partial class CustomersViewModel(ICustomerService customers, ICreditService credits) : ObservableObject, ILoadable
{
    public ObservableCollection<CustomerRow> Items { get; } = [];
    public ObservableCollection<CreditSummary> Credits { get; } = [];
    public ObservableCollection<CreditPaymentRow> CreditPayments { get; } = [];
    public ObservableCollection<CustomerHistorySale> HistorySales { get; } = [];
    public ObservableCollection<CustomerHistoryPayment> HistoryPayments { get; } = [];
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = Enum.GetValues<PaymentMode>().Where(x => x != PaymentMode.Credit).ToArray();

    [ObservableProperty] private string search = string.Empty;
    partial void OnSearchChanged(string value) => _ = LoadAsync();
    [ObservableProperty] private string name = string.Empty; [ObservableProperty] private string phone = string.Empty; [ObservableProperty] private string creditLimit = "0"; [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private string gender = string.Empty; [ObservableProperty] private string preferences = string.Empty; [ObservableProperty] private string channel = string.Empty; [ObservableProperty] private bool marketingConsent;
    [ObservableProperty] private CustomerRow? selectedCustomer;
    [ObservableProperty] private string editName = string.Empty; [ObservableProperty] private string editPhone = string.Empty; [ObservableProperty] private string editSecondaryPhone = string.Empty; [ObservableProperty] private string editGender = string.Empty; [ObservableProperty] private string editAddress = string.Empty; [ObservableProperty] private string editNotes = string.Empty; [ObservableProperty] private string editPreferences = string.Empty; [ObservableProperty] private string editChannel = string.Empty; [ObservableProperty] private bool editConsent; [ObservableProperty] private string editCreditLimit = "0";
    [ObservableProperty] private CreditSummary? selectedCredit; [ObservableProperty] private string paymentAmount = ""; [ObservableProperty] private PaymentMode paymentMode = PaymentMode.Cash; [ObservableProperty] private string paymentReference = ""; [ObservableProperty] private string managerPin = "";

    public async Task LoadAsync()
    {
        var rows = await customers.SearchAsync(Search); Items.Clear(); foreach (var row in rows) Items.Add(row);
        Credits.Clear(); foreach (var row in await credits.ListAsync()) Credits.Add(row);
    }

    [RelayCommand] private async Task SearchCustomers() => await LoadAsync();
    [RelayCommand] private async Task Create() { try { await customers.CreateAsync(Name, Phone, long.Parse(CreditLimit), default, NullIfEmpty(Gender), NullIfEmpty(Preferences), NullIfEmpty(Channel), MarketingConsent); Status = "Client ajouté"; Name = Phone = string.Empty; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }

    partial void OnSelectedCustomerChanged(CustomerRow? value)
    {
        HistorySales.Clear(); HistoryPayments.Clear();
        if (value is null) return;
        _ = LoadCustomerDetailsAsync(value.Id);
    }

    private async Task LoadCustomerDetailsAsync(Guid id)
    {
        try
        {
            var history = await customers.HistoryAsync(id);
            HistorySales.Clear(); foreach (var s in history.Sales) HistorySales.Add(s);
            HistoryPayments.Clear(); foreach (var p in history.Payments) HistoryPayments.Add(p);
            var customer = await customers.GetAsync(id);
            if (customer is not null)
            {
                EditName = customer.Name; EditPhone = customer.Phone ?? string.Empty; EditSecondaryPhone = customer.SecondaryPhone ?? string.Empty; EditGender = customer.Gender ?? string.Empty; EditAddress = customer.Address ?? string.Empty; EditNotes = customer.Notes ?? string.Empty; EditPreferences = customer.Preferences ?? string.Empty; EditChannel = customer.PreferredChannel ?? string.Empty; EditConsent = customer.MarketingConsent; EditCreditLimit = customer.CreditLimitXof.ToString();
            }
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task SaveCustomer()
    {
        if (SelectedCustomer is null) return;
        try
        {
            await customers.UpdateAsync(new CustomerUpdateRequest(SelectedCustomer.Id, EditName, NullIfEmpty(EditPhone), NullIfEmpty(EditSecondaryPhone), NullIfEmpty(EditGender), NullIfEmpty(EditAddress), NullIfEmpty(EditNotes), NullIfEmpty(EditPreferences), NullIfEmpty(EditChannel), EditConsent, long.Parse(EditCreditLimit));
            Status = "Fiche client complétée";
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }

    partial void OnSelectedCreditChanged(CreditSummary? value)
    {
        CreditPayments.Clear();
        if (value is null) return;
        _ = LoadCreditPaymentsAsync(value.Id);
    }

    private async Task LoadCreditPaymentsAsync(Guid creditId)
    {
        try { foreach (var p in await credits.ListPaymentsAsync(creditId)) CreditPayments.Add(p); }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task PayCredit() { if (SelectedCredit is null) return; try { var r = await credits.PayAsync(SelectedCredit.Id, long.Parse(PaymentAmount), PaymentMode, PaymentReference); Status = $"Reçu {r.Number} • Solde {r.NewBalanceXof:N0}"; await LoadAsync(); OnSelectedCreditChanged(SelectedCredit); } catch (Exception e) { Status = e.Message; } }

    [RelayCommand] private async Task ReverseCredit(CreditPaymentRow row)
    {
        if (!UiConfirm.Ask($"Contre-passer le versement {row.Number} de {row.AmountXof:N0} FCFA ? Une contre-écriture tracée sera créée.")) return;
        try { var r = await credits.ReverseAsync(row.Id, "Erreur de saisie", ManagerPin); Status = $"Contre-écriture {r.Number}"; await LoadAsync(); OnSelectedCreditChanged(SelectedCredit); } catch (Exception e) { Status = e.Message; }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public partial class ExpensesViewModel(IExpenseService expenses) : ObservableObject, ILoadable
{
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = Enum.GetValues<PaymentMode>();
    [ObservableProperty] private string category = "Autres"; [ObservableProperty] private string description = string.Empty; [ObservableProperty] private string amount = string.Empty; [ObservableProperty] private PaymentMode selectedMode = PaymentMode.Cash; [ObservableProperty] private string status = string.Empty;
    public Task LoadAsync() => Task.CompletedTask;
    [RelayCommand] private async Task Create() { try { await expenses.CreateAsync(Category, Description, long.Parse(Amount), SelectedMode); Status = "Dépense enregistrée"; Description = Amount = string.Empty; } catch (Exception e) { Status = e.Message; } }
}

public partial class DocumentsViewModel(IDocumentService documents, IReturnService returns, IThermalPrinterService printers, IAppSettingsService settings, IA4DocumentService a4) : ObservableObject, ILoadable
{
    public ObservableCollection<DocumentListItem> Items { get; } = [];
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = Enum.GetValues<PaymentMode>().Where(x => x != PaymentMode.Credit).ToArray();
    [ObservableProperty] private string search = string.Empty;
    partial void OnSearchChanged(string value) => _ = LoadAsync();
    [ObservableProperty] private DocumentListItem? selectedDocument; [ObservableProperty] private string saleNumber = ""; [ObservableProperty] private string returnedSku = ""; [ObservableProperty] private string returnedQuantity = "1"; [ObservableProperty] private string replacementSku = ""; [ObservableProperty] private string replacementQuantity = "1"; [ObservableProperty] private string exchangePaymentAmount = "0"; [ObservableProperty] private PaymentMode exchangePaymentMode = PaymentMode.Cash; [ObservableProperty] private string exchangePaymentReference = ""; [ObservableProperty] private string reason = ""; [ObservableProperty] private string managerPin = ""; [ObservableProperty] private bool restock = true; [ObservableProperty] private string proformaDescription = "Article"; [ObservableProperty] private string proformaTotal = "0"; [ObservableProperty] private string status = "";
    private PrinterProfile? printer;

    public async Task LoadAsync() { Items.Clear(); foreach (var x in await documents.ListAsync(Search)) Items.Add(x); var saved = await settings.GetAsync("Printer.Selected"); printer = saved is null ? printers.Discover().FirstOrDefault() : JsonSerializer.Deserialize<PrinterProfile>(saved); }
    [RelayCommand] private async Task Refresh() => await LoadAsync();
    [RelayCommand] private async Task Duplicate() { if (SelectedDocument is null || printer is null) return; try { var receipt = await documents.GetReceiptAsync(SelectedDocument.Id, true); await printers.PrintReceiptAsync(printer, receipt); await documents.MarkPrintedAsync(SelectedDocument.Id); Status = "Duplicata imprimé"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task ExportPdf() { if (SelectedDocument is null) return; try { var receipt = await documents.GetReceiptAsync(SelectedDocument.Id, SelectedDocument.PrintCount > 0); var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"{SelectedDocument.Number}.pdf", Filter = "Document PDF (*.pdf)|*.pdf" }; if (dialog.ShowDialog() != true) return; await File.WriteAllBytesAsync(dialog.FileName, a4.CreateInvoicePdf(receipt)); Status = "PDF exporté"; } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task PrintA4() { if (SelectedDocument is null) return; try { var dialog = new System.Windows.Controls.PrintDialog(); if (dialog.ShowDialog() != true) return; var receipt = await documents.GetReceiptAsync(SelectedDocument.Id, SelectedDocument.PrintCount > 0); await a4.PrintInvoiceAsync(receipt, dialog.PrintQueue.FullName); await documents.MarkPrintedAsync(SelectedDocument.Id); Status = "Document envoyé à l’imprimante A4"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }

    [RelayCommand] private async Task ReturnExchange()
    {
        if (!UiConfirm.Ask($"Valider le retour/échange sur la vente {SaleNumber} ?")) return;
        try
        {
            var amount = long.Parse(ExchangePaymentAmount);
            IReadOnlyList<PaymentDraft> payments = amount > 0 ? [new PaymentDraft(ExchangePaymentMode, amount, ExchangePaymentReference)] : [];
            var r = await returns.ReturnOrExchangeAsync(new ReturnRequest(SaleNumber, ReturnedSku, decimal.Parse(ReturnedQuantity), string.IsNullOrWhiteSpace(ReplacementSku) ? null : ReplacementSku, decimal.Parse(ReplacementQuantity), payments, Reason, ManagerPin, Restock));
            Status = $"Bon de retour {r.CreditNoteNumber} • Différence {r.DifferenceXof:N0}";
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task CancelSale()
    {
        if (!UiConfirm.Ask($"Annuler toute la vente {SaleNumber} ? Les articles seront réintégrés et les paiements contre-passés.")) return;
        try { var r = await returns.CancelSaleAsync(SaleNumber, Reason, ManagerPin); Status = $"Vente annulée • {r.CreditNoteNumber}"; await LoadAsync(); } catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task Proforma() { try { var total = long.Parse(ProformaTotal); var data = new ReceiptData("Ma Boutique", null, null, "", DateTimeOffset.Now, null, [new ReceiptItem(ProformaDescription, 1, total, 0, total)], total, 0, total, [], "Proforma sans encaissement"); var d = await documents.CreateProformaAsync(data); Status = $"Proforma {d.Number} créée"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
}

public partial class ReportsViewModel(IReportService reports) : ObservableObject, ILoadable
{
    public ObservableCollection<ReportRow> Rows { get; } = [];
    public ObservableCollection<CashClosingRow> Closings { get; } = [];
    [ObservableProperty] private DashboardSummary summary = new(0, 0, 0, 0, 0, 0);
    [ObservableProperty] private string fromDate = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd");
    [ObservableProperty] private string toDate = DateTime.Today.ToString("yyyy-MM-dd");
    [ObservableProperty] private string selectedReportKind = "Ventes par jour";
    [ObservableProperty] private string status = string.Empty;
    public IReadOnlyList<string> ReportKinds { get; } = ["Ventes par jour", "Modes de paiement", "Ventes par vendeur", "Top produits", "Articles sans vente", "Valeur du stock", "Écarts d'inventaire", "Remises et corrections", "Rotation & dormants"];

    public async Task LoadAsync()
    {
        try
        {
            var from = new DateTimeOffset(DateTime.Parse(FromDate), TimeSpan.Zero);
            var to = new DateTimeOffset(DateTime.Parse(ToDate).AddDays(1), TimeSpan.Zero);
            Summary = await reports.DashboardAsync(from, to);
            Rows.Clear();
            var rows = SelectedReportKind switch
            {
                "Modes de paiement" => await reports.SalesByPaymentModeAsync(from, to),
                "Ventes par vendeur" => await reports.SalesBySellerAsync(from, to),
                "Top produits" => await reports.TopProductsAsync(from, to),
                "Articles sans vente" => await reports.NoSalesProductsAsync(from, to),
                "Valeur du stock" => await reports.StockValueByCategoryAsync(),
                "Écarts d'inventaire" => await reports.InventoryVarianceAsync(from, to),
                "Remises et corrections" => await reports.CorrectionsAsync(from, to),
                "Rotation & dormants" => await reports.RotationAsync(from, to),
                _ => await reports.SalesByDayAsync(from, to)
            };
            foreach (var row in rows) Rows.Add(row);
            Closings.Clear();
            foreach (var closing in await reports.CashClosingsAsync(from, to)) Closings.Add(closing);
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task Refresh() => await LoadAsync();

    [RelayCommand] private async Task SetPeriod(string kind)
    {
        var today = DateTime.Today;
        var (from, to) = kind switch
        {
            "today" => (today, today),
            "7" => (today.AddDays(-6), today),
            "month" => (new DateTime(today.Year, today.Month, 1), today),
            _ => (today.AddDays(-30), today)
        };
        FromDate = from.ToString("yyyy-MM-dd");
        ToDate = to.ToString("yyyy-MM-dd");
        await LoadAsync();
    }

    [RelayCommand] private async Task ExportCsv()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"rapport-{SelectedReportKind}.csv", Filter = "CSV (*.csv)|*.csv" };
            if (dialog.ShowDialog() != true) return;
            var lines = new List<string> { "Libellé;Montant FCFA;Quantité" };
            lines.AddRange(Rows.Select(r => $"\"{r.Label.Replace("\"", "\"\"")}\";{r.ValueXof};{r.Quantity}"));
            await File.WriteAllLinesAsync(dialog.FileName, lines, System.Text.Encoding.UTF8);
            Status = "Export CSV enregistré";
        }
        catch (Exception e) { Status = e.Message; }
    }
}

public partial class SettingsViewModel(IAuthorizationService authorization, IAppSettingsService settings, IBackupService backup, IThermalPrinterService printer, IDocumentService documents, IA4DocumentService a4) : ObservableObject, ILoadable
{
    public ObservableCollection<PrinterProfile> Printers { get; } = [];
    public IReadOnlyList<DocumentType> DocumentTypes { get; } = Enum.GetValues<DocumentType>();
    public IReadOnlyList<string> Styles { get; } = ["Classique", "Moderne", "Minimal"];
    [ObservableProperty] private string selectedStyle = "Moderne";
    [ObservableProperty] private string networkPrinter = "";
    [ObservableProperty] private PrinterProfile? selectedPrinter;
    [ObservableProperty] private string shopName = string.Empty; [ObservableProperty] private string pin = string.Empty; [ObservableProperty] private string newPin = string.Empty; [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private string address = ""; [ObservableProperty] private string phone = ""; [ObservableProperty] private string email = ""; [ObservableProperty] private string taxId = ""; [ObservableProperty] private string slogan = ""; [ObservableProperty] private string footer = "Merci de votre visite"; [ObservableProperty] private string returnPolicy = "Échange ou avoir sous 7 jours"; [ObservableProperty] private string logoPath = ""; [ObservableProperty] private string stampPath = ""; [ObservableProperty] private string signaturePath = "";
    [ObservableProperty] private string seqReceipt = "TIC"; [ObservableProperty] private string seqInvoice = "FAC"; [ObservableProperty] private string seqProforma = "PRO"; [ObservableProperty] private string seqDeposit = "DEP"; [ObservableProperty] private string seqCreditPayment = "REC"; [ObservableProperty] private string seqBalance = "SOL"; [ObservableProperty] private string seqCreditNote = "AVO"; [ObservableProperty] private string seqReturnNote = "RET";
    [ObservableProperty] private string varianceTolerance = "0"; [ObservableProperty] private string vipRevenue = "500000"; [ObservableProperty] private string loyalPurchases = "5"; [ObservableProperty] private string inactiveDays = "90"; [ObservableProperty] private string newDays = "30";
    [ObservableProperty] private DocumentType selectedDocType = DocumentType.Invoice;
    [ObservableProperty] private bool flagLogo = true; [ObservableProperty] private bool flagSlogan = true; [ObservableProperty] private bool flagStamp = true; [ObservableProperty] private bool flagSignature = true;

    public async Task LoadAsync()
    {
        ShopName = await settings.GetAsync("Shop.Name") ?? "Ma Boutique"; Address = await settings.GetAsync("Shop.Address") ?? ""; Phone = await settings.GetAsync("Shop.Phone") ?? ""; Email = await settings.GetAsync("Shop.Email") ?? ""; TaxId = await settings.GetAsync("Shop.TaxId") ?? ""; Slogan = await settings.GetAsync("Shop.Slogan") ?? ""; Footer = await settings.GetAsync("Shop.Footer") ?? Footer; ReturnPolicy = await settings.GetAsync("Shop.ReturnPolicy") ?? ReturnPolicy; LogoPath = await settings.GetAsync("Shop.Logo") ?? ""; StampPath = await settings.GetAsync("Shop.Stamp") ?? ""; SignaturePath = await settings.GetAsync("Shop.Signature") ?? "";
        SeqReceipt = await settings.GetAsync($"Seq.{DocumentType.Receipt}") ?? SeqReceipt; SeqInvoice = await settings.GetAsync($"Seq.{DocumentType.Invoice}") ?? SeqInvoice; SeqProforma = await settings.GetAsync($"Seq.{DocumentType.Proforma}") ?? SeqProforma; SeqDeposit = await settings.GetAsync($"Seq.{DocumentType.DepositReceipt}") ?? SeqDeposit; SeqCreditPayment = await settings.GetAsync($"Seq.{DocumentType.CreditPaymentReceipt}") ?? SeqCreditPayment; SeqBalance = await settings.GetAsync($"Seq.{DocumentType.BalanceReceipt}") ?? SeqBalance; SeqCreditNote = await settings.GetAsync($"Seq.{DocumentType.CreditNote}") ?? SeqCreditNote; SeqReturnNote = await settings.GetAsync($"Seq.{DocumentType.ReturnNote}") ?? SeqReturnNote;
        VarianceTolerance = await settings.GetAsync("Cash.VarianceToleranceXof") ?? VarianceTolerance;
        VipRevenue = await settings.GetAsync("Loyalty.VipRevenueXof") ?? VipRevenue; LoyalPurchases = await settings.GetAsync("Loyalty.LoyalPurchases") ?? LoyalPurchases; InactiveDays = await settings.GetAsync("Loyalty.InactiveDays") ?? InactiveDays; NewDays = await settings.GetAsync("Loyalty.NewDays") ?? NewDays;
        await LoadPrintersAsync();
        var saved = await settings.GetAsync("Printer.Selected"); SelectedPrinter = saved is null ? Printers.FirstOrDefault() : JsonSerializer.Deserialize<PrinterProfile>(saved);
        NetworkPrinter = await settings.GetAsync("Printer.Tcp") ?? "";
        await LoadFlagsAsync();
    }

    private async Task LoadPrintersAsync()
    {
        Printers.Clear();
        foreach (var p in printer.Discover()) Printers.Add(p);
        var tcp = await settings.GetAsync("Printer.Tcp");
        if (!string.IsNullOrWhiteSpace(tcp)) Printers.Add(new PrinterProfile($"Thermique réseau {tcp}", PrinterConnectionKind.TcpIp, tcp, PaperWidth.Mm80));
    }

    [RelayCommand] private async Task AddNetworkPrinter()
    {
        try
        {
            var address = NetworkPrinter.Trim();
            var parts = address.Split(':');
            if (string.IsNullOrWhiteSpace(parts[0]) || (parts.Length > 1 && !int.TryParse(parts[1], out _))) throw new FormatException();
            await settings.SetAsync("Printer.Tcp", address);
            await LoadPrintersAsync();
            SelectedPrinter = Printers.FirstOrDefault(x => x.ConnectionKind == PrinterConnectionKind.TcpIp);
            Status = $"Imprimante externe {address} ajoutée";
        }
        catch { Status = "Adresse invalide. Format attendu : 192.168.1.50 ou 192.168.1.50:9100"; }
    }

    partial void OnSelectedDocTypeChanged(DocumentType value) => _ = LoadFlagsAsync();

    private async Task LoadFlagsAsync()
    {
        try
        {
            FlagLogo = (await settings.GetAsync($"Doc.{SelectedDocType}.Logo") ?? "1") == "1";
            FlagSlogan = (await settings.GetAsync($"Doc.{SelectedDocType}.Slogan") ?? "1") == "1";
            FlagStamp = (await settings.GetAsync($"Doc.{SelectedDocType}.Stamp") ?? "1") == "1";
            FlagSignature = (await settings.GetAsync($"Doc.{SelectedDocType}.Signature") ?? "1") == "1";
            var style = await settings.GetAsync($"Doc.{SelectedDocType}.Style") ?? "Moderne";
            SelectedStyle = Styles.Contains(style) ? style : "Moderne";
        }
        catch { }
    }

    [RelayCommand] private async Task Save()
    {
        try
        {
            if (!await authorization.IsConfiguredAsync()) await authorization.ConfigurePinAsync(Pin);
            else if (!await authorization.AuthorizeSensitiveActionAsync(Pin, "Modifier paramètres")) throw new UnauthorizedAccessException("PIN responsable invalide.");
            var values = new Dictionary<string, string>
            {
                {"Shop.Name", ShopName}, {"Shop.Address", Address}, {"Shop.Phone", Phone}, {"Shop.Email", Email}, {"Shop.TaxId", TaxId}, {"Shop.Slogan", Slogan}, {"Shop.Footer", Footer}, {"Shop.ReturnPolicy", ReturnPolicy}, {"Shop.Logo", LogoPath}, {"Shop.Stamp", StampPath}, {"Shop.Signature", SignaturePath},
                {$"Seq.{DocumentType.Receipt}", SeqReceipt}, {$"Seq.{DocumentType.Invoice}", SeqInvoice}, {$"Seq.{DocumentType.Proforma}", SeqProforma}, {$"Seq.{DocumentType.DepositReceipt}", SeqDeposit}, {$"Seq.{DocumentType.CreditPaymentReceipt}", SeqCreditPayment}, {$"Seq.{DocumentType.BalanceReceipt}", SeqBalance}, {$"Seq.{DocumentType.CreditNote}", SeqCreditNote}, {$"Seq.{DocumentType.ReturnNote}", SeqReturnNote},
                {"Cash.VarianceToleranceXof", VarianceTolerance}, {"Loyalty.VipRevenueXof", VipRevenue}, {"Loyalty.LoyalPurchases", LoyalPurchases}, {"Loyalty.InactiveDays", InactiveDays}, {"Loyalty.NewDays", NewDays},
                {$"Doc.{SelectedDocType}.Logo", FlagLogo ? "1" : "0"}, {$"Doc.{SelectedDocType}.Slogan", FlagSlogan ? "1" : "0"}, {$"Doc.{SelectedDocType}.Stamp", FlagStamp ? "1" : "0"}, {$"Doc.{SelectedDocType}.Signature", FlagSignature ? "1" : "0"}, {$"Doc.{SelectedDocType}.Style", SelectedStyle}
            };
            foreach (var pair in values) await settings.SetAsync(pair.Key, pair.Value);
            if (SelectedPrinter is not null) await settings.SetAsync("Printer.Selected", JsonSerializer.Serialize(SelectedPrinter));
            Status = "Paramètres enregistrés";
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task ChangePin()
    {
        try { await authorization.ChangePinAsync(Pin, NewPin); Status = "PIN modifié"; NewPin = string.Empty; }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task Backup() { try { Status = $"Sauvegarde: {await backup.CreateAsync()}"; } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task TestPrinter() { if (SelectedPrinter is null) return; try { await printer.PrintTestAsync(SelectedPrinter); Status = "Test envoyé"; } catch (Exception e) { Status = e.Message; } }

    [RelayCommand] private async Task PreviewTemplate()
    {
        try
        {
            var sample = await documents.BuildSampleAsync(SelectedDocType);
            var path = Path.Combine(Path.GetTempPath(), $"apercu-{SelectedDocType}.pdf");
            await File.WriteAllBytesAsync(path, a4.CreateInvoicePdf(sample));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Status = $"Aperçu du modèle {SelectedDocType} ouvert";
        }
        catch (Exception e) { Status = e.Message; }
    }
}
