using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Infrastructure;
using BoutiqueFashion.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoutiqueFashion.App.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    /// <summary>
    /// Écrans réservés au gérant : quitter le mode doit en faire sortir immédiatement.
    /// Le vendeur ne garde que ce dont il a besoin au comptoir — tableau de bord, vente, produits
    /// et dépenses. Les rapports et les fiches clients en sortent : ils exposent marges, encours
    /// et historiques qui ne le concernent pas. Les dépenses y entrent, à l'inverse : c'est lui
    /// qui règle le taxi ou la recharge d'électricité dans la journée.
    /// </summary>
    private static readonly string[] ManagerOnlyPages = ["Stock", "Documents", "Reports", "Customers", "Settings"];

    private readonly DashboardViewModel dashboard; private readonly SaleViewModel sale; private readonly CatalogViewModel catalog;
    private readonly StockViewModel stock; private readonly CustomersViewModel customers; private readonly ExpensesViewModel expenses;
    private readonly DocumentsViewModel documents; private readonly ReportsViewModel reports; private readonly SettingsViewModel settings;
    private readonly CashViewModel cash; private readonly AdvancesViewModel advances;

    public ShellViewModel(ManagerSession session, DashboardViewModel dashboard, SaleViewModel sale, CatalogViewModel catalog, StockViewModel stock, CustomersViewModel customers, ExpensesViewModel expenses, DocumentsViewModel documents, ReportsViewModel reports, SettingsViewModel settings, CashViewModel cash, AdvancesViewModel advances)
    {
        Session = session;
        this.dashboard = dashboard; this.sale = sale; this.catalog = catalog; this.stock = stock; this.customers = customers;
        this.expenses = expenses; this.documents = documents; this.reports = reports; this.settings = settings; this.cash = cash; this.advances = advances;
        CurrentPage = dashboard;
        Session.PropertyChanged += OnSessionChanged;
    }

    public ManagerSession Session { get; }
    [ObservableProperty] private object currentPage; [ObservableProperty] private string pageTitle = "Tableau de bord";
    [ObservableProperty] private string currentTarget = "Dashboard";

    public async Task InitializeAsync()
    {
        AppNavigator.Go = Navigate;
        await Session.RefreshPinStateAsync();
        await dashboard.LoadAsync();
        await sale.LoadAsync();
    }

    private void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ManagerSession.IsManager) || Session.IsManager) return;
        if (ManagerOnlyPages.Contains(CurrentTarget)) _ = Navigate("Dashboard");
    }

    [RelayCommand] private async Task Navigate(string target)
    {
        // Une navigation forcée (lien direct, retour de verrouillage) ne doit pas ouvrir un écran gérant.
        if (ManagerOnlyPages.Contains(target) && !Session.IsManager) target = "Dashboard";
        (object Page, string Title) next = target switch { "Sale" => (sale, "Vente"), "Cash" => (cash, "Caisse"), "Advances" => (advances, "Avances et crédits"), "Catalog" => (catalog, "Produits et variantes"), "Stock" => (stock, "Gestion du stock"), "Customers" => (customers, "Clients et crédits"), "Expenses" => (expenses, "Dépenses"), "Documents" => (documents, "Documents et opérations"), "Reports" => (reports, "Rapports"), "Settings" => (settings, "Paramètres"), _ => (dashboard, "Tableau de bord") };
        CurrentPage = next.Page; PageTitle = next.Title; CurrentTarget = target;
        if (CurrentPage is ILoadable loadable) await loadable.LoadAsync();
    }
}

public interface ILoadable { Task LoadAsync(); }

internal static class UiConfirm
{
    public static bool Ask(string message) => MessageBox.Show(message, "Confirmation requise", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

internal static class UiFeedback
{
    public static void Success(string message) => MessageBox.Show(message, "Succès", MessageBoxButton.OK, MessageBoxImage.Information);
    public static void Error(string message) => MessageBox.Show(message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
}

internal static class AppNavigator
{
    public static Func<string, Task>? Go { get; set; }
}

public partial class DashboardViewModel(IReportService reports, ICashSessionService cash, ManagerSession session) : ObservableObject, ILoadable
{
    public ObservableCollection<RecentSaleRow> RecentSales { get; } = [];
    public ManagerSession Session => session;
    [ObservableProperty] private DashboardSummary summary = new(0, 0, 0, 0, 0, 0);
    [ObservableProperty] private string cashSessionState = "Caisse fermée";
    [ObservableProperty] private bool isCashOpen;
    [ObservableProperty] private string cashStatus = "";
    /// <summary>Le PIN vient de la session gérant : plus aucun écran ne le redemande.</summary>
    private string ManagerPin => session.Pin;

    // --- Bascule vers le mode gérant ---
    [ObservableProperty] private bool isPinDialogOpen;
    [ObservableProperty] private bool isPinCreation;
    [ObservableProperty] private string pinEntry = string.Empty;
    [ObservableProperty] private string pinConfirm = string.Empty;
    [ObservableProperty] private string pinDialogError = string.Empty;

    [RelayCommand]
    private async Task OpenManagerMode()
    {
        await session.RefreshPinStateAsync();
        // Premier lancement : aucun PIN en base, on le fait créer au lieu de le demander.
        IsPinCreation = !session.IsPinConfigured;
        PinEntry = PinConfirm = PinDialogError = string.Empty;
        IsPinDialogOpen = true;
    }

    [RelayCommand]
    private void CancelPinDialog()
    {
        IsPinDialogOpen = false;
        PinEntry = PinConfirm = PinDialogError = string.Empty;
    }

    [RelayCommand]
    private async Task SubmitPin()
    {
        try
        {
            if (IsPinCreation)
            {
                await session.CreatePinAsync(PinEntry, PinConfirm);
                CashStatus = "Code gérant créé. Notez-le : il n'est récupérable que par restauration d'une sauvegarde.";
            }
            else if (!await session.TryUnlockAsync(PinEntry))
            {
                PinDialogError = "Code gérant incorrect.";
                return;
            }
            IsPinDialogOpen = false;
            PinEntry = PinConfirm = PinDialogError = string.Empty;
        }
        catch (Exception e) { PinDialogError = e.Message; }
    }

    [RelayCommand] private void LeaveManagerMode() => session.Lock();

    public async Task LoadAsync()
    {
        var now = DateTimeOffset.Now;
        var from = new DateTimeOffset(now.Date, now.Offset);
        var to = new DateTimeOffset(now.Date.AddDays(1), now.Offset);
        Summary = await reports.DashboardAsync(from, to);
        RecentSales.Clear();
        foreach (var sale in await reports.RecentSalesAsync(from, to)) RecentSales.Add(sale);

        var openSession = await cash.GetOpenAsync();
        IsCashOpen = openSession is not null;
        // Qui tient la caisse compte autant que le fait qu'elle soit ouverte.
        CashSessionState = openSession is null
            ? "Caisse fermée · ouvrez-la pour commencer à encaisser"
            : $"Caisse ouverte · {openSession.Number} · {openSession.OperatorName}";
        CashStatus = string.Empty;
    }

    [RelayCommand] private async Task QuickSell() { if (AppNavigator.Go is not null) await AppNavigator.Go("Sale"); }
    [RelayCommand] private async Task QuickProducts() { if (AppNavigator.Go is not null) await AppNavigator.Go("Catalog"); }
    /// <summary>L'ouverture et la clôture vivent désormais dans l'écran Caisse : le tableau de
    /// bord n'en montre plus que l'état, et y renvoie.</summary>
    [RelayCommand] private async Task QuickCash() { if (AppNavigator.Go is not null) await AppNavigator.Go("Cash"); }
}

public partial class CartLineViewModel(ProductVariant variant) : ObservableObject
{
    public ProductVariant Variant { get; } = variant;
    public string Label => string.Join(" - ", new[] { Variant.Product?.Name, Variant.Color, Variant.Size }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public long UnitPriceXof
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return Variant.PromotionalPriceXof is not null && Variant.PromotionStartsAt <= now && Variant.PromotionEndsAt >= now ? Variant.PromotionalPriceXof.Value : Variant.PriceXof;
        }
    }
    [ObservableProperty] private decimal quantity = 1;
    /// <summary>Remise de ligne en FCFA : la remise en pourcentage n'existe qu'au niveau de la vente entière.</summary>
    [ObservableProperty] private decimal discountValue;
    public DiscountKind EffectiveDiscountKind => DiscountValue == 0 ? DiscountKind.None : DiscountKind.Amount;
    public bool HasDiscount => DiscountValue != 0;
    /// <summary>Texte du champ remise : vide à zéro, pour laisser apparaître l'invite « Remise ».
    /// Le signe moins est rendu à côté du champ, jamais dans le texte éditable — il casserait l'analyse et ferait sauter le curseur.</summary>
    public string DiscountText
    {
        get => DiscountValue == 0 ? string.Empty : DiscountValue.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            var parsed = decimal.TryParse((value ?? string.Empty).Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : 0;
            if (parsed != DiscountValue) DiscountValue = parsed;
        }
    }
    /// <summary>Au-delà du brut, CalculateDiscount lève et la remise est ignorée : il faut le montrer.</summary>
    public bool IsDiscountTooLarge => DiscountValue > GrossXof;
    public long GrossXof => decimal.ToInt64(Quantity * UnitPriceXof);
    public long TotalXof
    {
        get
        {
            try { return GrossXof - BusinessRules.CalculateDiscount(GrossXof, EffectiveDiscountKind, DiscountValue); }
            catch { return GrossXof; }
        }
    }
    partial void OnQuantityChanged(decimal value) { OnPropertyChanged(nameof(GrossXof)); RaiseLineTotals(); }
    partial void OnDiscountValueChanged(decimal value) => RaiseLineTotals();
    private void RaiseLineTotals()
    {
        OnPropertyChanged(nameof(TotalXof));
        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(IsDiscountTooLarge));
        OnPropertyChanged(nameof(DiscountText));
    }
}

public partial class PaymentLineViewModel : ObservableObject { public IReadOnlyList<PaymentMode> Modes { get; } = Enum.GetValues<PaymentMode>(); [ObservableProperty] private PaymentMode mode = PaymentMode.Cash; [ObservableProperty] private long amountXof; [ObservableProperty] private string reference = string.Empty; }

public partial class SaleViewModel(ICatalogService catalog, ICustomerService customers, ISaleService sales, ICashSessionService cash, IThermalPrinterService printerService, IAppSettingsService settings, IDocumentService documents, ManagerSession session) : ObservableObject, ILoadable
{
    public ObservableCollection<ProductVariant> Products { get; } = [];
    private readonly List<ProductVariant> masterProducts = [];
    public ObservableCollection<string> CategoryFilters { get; } = ["Tous"];
    public ObservableCollection<CartLineViewModel> Cart { get; } = [];
    public ObservableCollection<CustomerRow> Customers { get; } = [];
    public ObservableCollection<CustomerChoice> CustomerChoices { get; } = [];
    public ObservableCollection<PaymentLineViewModel> Payments { get; } = [];
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = Enum.GetValues<PaymentMode>();
    public ObservableCollection<PrinterProfile> Printers { get; } = [];
    [ObservableProperty] private string search = string.Empty;
    [ObservableProperty] private string selectedCategoryFilter = "Tous";
    partial void OnSelectedCategoryFilterChanged(string value) => ApplyProductFilter();
    [ObservableProperty] private string newCustomerName = string.Empty;
    [ObservableProperty] private string newCustomerPhone = string.Empty;
    [ObservableProperty] private PaymentMode selectedPaymentMode = PaymentMode.Cash;
    partial void OnSelectedPaymentModeChanged(PaymentMode value)
    {
        if (Payments.Count == 0 && PayableXof > 0)
        {
            var line = new PaymentLineViewModel { Mode = value, AmountXof = PayableXof };
            line.PropertyChanged += OnPaymentLineChanged;
            Payments.Add(line);
        }
        RaiseTotals();
    }
    [ObservableProperty] private bool printInvoice;
    [RelayCommand] private void ChooseReceipt() => PrintInvoice = false;
    [RelayCommand] private void ChooseInvoice() => PrintInvoice = true;
    [ObservableProperty] private PrinterProfile? selectedPrinter;
    [ObservableProperty] private CustomerRow? selectedCustomer;
    [ObservableProperty] private decimal discountPercent;
    [ObservableProperty] private string discountReason = string.Empty;
    /// <summary>Le PIN vient de la session gérant : plus aucun écran ne le redemande.</summary>
    private string ManagerPin => session.Pin;
    [ObservableProperty] private string creditDueDate = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd");
    [ObservableProperty] private string status = "Prêt";
    [ObservableProperty] private string cashSessionState = "Caisse fermée";
    [ObservableProperty] private bool isCashOpen;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteCommand))]
    private bool isBusy;
    [ObservableProperty] private string customerSearch = string.Empty;
    [ObservableProperty] private bool isCustomerPickerOpen;
    [ObservableProperty] private bool isCustomerDialogOpen;
    [ObservableProperty] private string customerDialogError = string.Empty;
    private bool suppressCustomerSearchReload;
    partial void OnCustomerSearchChanged(string value) { if (!suppressCustomerSearchReload) _ = RefreshCustomersAsync(); }
    public long TotalXof => Cart.Sum(x => x.TotalXof);
    public long PayableXof => TotalXof - BusinessRules.CalculateDiscount(TotalXof, DiscountPercent == 0 ? DiscountKind.None : DiscountKind.Percentage, DiscountPercent);
    public long ChangePreview { get { var sum = Payments.Sum(x => x.AmountXof); return sum > PayableXof ? sum - PayableXof : 0; } }
    public ManagerSession Session => session;
    /// <summary>Une part de la vente reste à devoir : c'est ce qui ouvre le choix entre emport
    /// immédiat et mise de côté.</summary>
    public bool HasCreditPortion => SelectedPaymentMode == PaymentMode.Credit || Payments.Any(x => x.Mode == PaymentMode.Credit);

    /// <summary>Avance « réservé jusqu'au solde » : la marchandise attend en boutique.</summary>
    [ObservableProperty] private bool reserveStock;
    partial void OnReserveStockChanged(bool value) => RaiseTotals();
    [RelayCommand] private void TakeAway() => ReserveStock = false;
    [RelayCommand] private void Reserve() => ReserveStock = true;

    /// <summary>Vente que le service refusera sans PIN valide. Croisé avec Session.IsManager dans la vue,
    /// car l'état de session change hors de ce ViewModel et ne serait pas notifié ici.
    /// L'avance réservée en est exclue : rien ne quitte la boutique, donc rien n'est exposé.</summary>
    public bool IsSensitiveSale => DiscountPercent != 0 || (HasCreditPortion && !ReserveStock);
    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(TotalXof));
        OnPropertyChanged(nameof(PayableXof));
        OnPropertyChanged(nameof(PaymentTotalXof));
        OnPropertyChanged(nameof(ChangePreview));
        OnPropertyChanged(nameof(HasCreditPortion));
        OnPropertyChanged(nameof(IsSensitiveSale));
        CompleteCommand.NotifyCanExecuteChanged();
    }
    private void OnPaymentLineChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RaiseTotals();
    partial void OnDiscountPercentChanged(decimal value) => RaiseTotals();
    [RelayCommand] private void SetCreditDue(string days) { if (int.TryParse(days, out var d)) CreditDueDate = DateTime.Today.AddDays(d).ToString("yyyy-MM-dd"); }

    public async Task LoadAsync()
    {
        var items = await catalog.SearchAsync(Search);
        masterProducts.Clear(); masterProducts.AddRange(items);
        ApplyProductFilter();
        if (CategoryFilters.Count <= 1) foreach (var c in await catalog.CategoriesAsync()) if (!CategoryFilters.Contains(c)) CategoryFilters.Add(c);
        await RefreshCustomersAsync();
        var openSession = await cash.GetOpenAsync();
        CashSessionState = openSession is null ? "Caisse fermée · ouvrez-la depuis l'écran Caisse" : $"Caisse ouverte · {openSession.OperatorName}";
        IsCashOpen = openSession is not null;
        Printers.Clear();
        foreach (var p in printerService.Discover()) Printers.Add(p);
        var tcp = await settings.GetAsync("Printer.Tcp");
        if (!string.IsNullOrWhiteSpace(tcp)) Printers.Add(new PrinterProfile($"Thermique réseau {tcp}", PrinterConnectionKind.TcpIp, tcp, PaperWidth.Mm80));
        if (SelectedPrinter is null) SelectedPrinter = await PrinterStore.LoadAsync(settings, printerService) ?? Printers.FirstOrDefault();
    }

    private async Task RefreshCustomersAsync(Guid? select = null)
    {
        Customers.Clear();
        foreach (var item in await customers.SearchAsync(string.IsNullOrWhiteSpace(CustomerSearch) ? null : CustomerSearch)) Customers.Add(item);
        RebuildCustomerChoices(select);
    }

    private void RebuildCustomerChoices(Guid? select = null)
    {
        var wanted = select is not null ? Customers.FirstOrDefault(x => x.Id == select) : SelectedCustomer;
        CustomerChoices.Clear();
        CustomerChoices.Add(CustomerChoice.WalkIn);
        foreach (var row in Customers) CustomerChoices.Add(new CustomerChoice(row));
        // Un client déjà choisi doit survivre à un filtre qui l'exclut, sinon taper une
        // recherche le désélectionnerait en silence au beau milieu d'une vente.
        if (wanted is not null && CustomerChoices.All(x => x.Row?.Id != wanted.Id))
            CustomerChoices.Insert(1, new CustomerChoice(wanted));
        SelectedCustomer = wanted;
    }

    /// <summary>Libellé du bouton de sélection : vente comptoir tant qu'aucun client n'est choisi.</summary>
    public string SelectedCustomerLabel => SelectedCustomer?.Name ?? "Client comptoir";

    partial void OnSelectedCustomerChanged(CustomerRow? value) => OnPropertyChanged(nameof(SelectedCustomerLabel));

    [RelayCommand]
    private void OpenCustomerPicker()
    {
        CustomerSearch = string.Empty;
        IsCustomerPickerOpen = true;
    }

    [RelayCommand] private void CloseCustomerPicker() => IsCustomerPickerOpen = false;

    [RelayCommand]
    private void PickCustomer(CustomerChoice? choice)
    {
        SelectedCustomer = choice is null || choice.IsWalkIn ? null : choice.Row;
        IsCustomerPickerOpen = false;
    }

    [RelayCommand]
    private void OpenCustomerDialog()
    {
        NewCustomerName = string.Empty; NewCustomerPhone = string.Empty; CustomerDialogError = string.Empty;
        IsCustomerPickerOpen = false;
        IsCustomerDialogOpen = true;
    }

    [RelayCommand]
    private void CancelCustomerDialog()
    {
        IsCustomerDialogOpen = false;
        NewCustomerName = string.Empty; NewCustomerPhone = string.Empty; CustomerDialogError = string.Empty;
    }

    [RelayCommand]
    private async Task SaveCustomer()
    {
        if (string.IsNullOrWhiteSpace(NewCustomerName)) { CustomerDialogError = "Le nom du client est obligatoire."; return; }
        try
        {
            var created = await customers.CreateAsync(NewCustomerName.Trim(), NullIfEmpty(NewCustomerPhone), 0);
            suppressCustomerSearchReload = true;
            CustomerSearch = string.Empty;
            suppressCustomerSearchReload = false;
            await RefreshCustomersAsync(created.Id);
            IsCustomerDialogOpen = false;
            NewCustomerName = string.Empty; NewCustomerPhone = string.Empty; CustomerDialogError = string.Empty;
            Status = $"Client {created.Name} créé et sélectionné";
        }
        catch (Exception e) { CustomerDialogError = e.Message; }
    }

    private void ApplyProductFilter()
    {
        Products.Clear();
        foreach (var item in masterProducts.Where(x => SelectedCategoryFilter == "Tous" || x.Product?.Category?.Name == SelectedCategoryFilter)) Products.Add(item);
    }

    [RelayCommand] private async Task FilterCustomers() => await RefreshCustomersAsync();
    [RelayCommand] private async Task SearchProducts() => await LoadAsync();
    private static bool CanAdd(ProductVariant variant) => variant is not null && !variant.IsOutOfStock;
    [RelayCommand(CanExecute = nameof(CanAdd))] private void Add(ProductVariant variant) { var existing = Cart.FirstOrDefault(x => x.Variant.Id == variant.Id); if (existing is null) { var line = new CartLineViewModel(variant); line.PropertyChanged += (_, _) => RaiseTotals(); Cart.Add(line); } else existing.Quantity++; RaiseTotals(); }
    [RelayCommand] private void Remove(CartLineViewModel line) { Cart.Remove(line); RaiseTotals(); }
    [RelayCommand] private void ClearCart() { Cart.Clear(); RaiseTotals(); }
    [RelayCommand] private void Increment(CartLineViewModel line) { line.Quantity++; }
    [RelayCommand] private void Decrement(CartLineViewModel line) { if (line.Quantity > 1) line.Quantity--; }
    partial void OnSelectedPrinterChanged(PrinterProfile? value) { if (value is not null) _ = PersistPrinterAsync(value); }
    private async Task PersistPrinterAsync(PrinterProfile value) { try { await PrinterStore.SaveAsync(settings, value); } catch { } }
    [RelayCommand] private void AddPayment() { var line = new PaymentLineViewModel { AmountXof = Math.Max(0, PayableXof - Payments.Sum(x => x.AmountXof)) }; line.PropertyChanged += OnPaymentLineChanged; Payments.Add(line); RaiseTotals(); }
    [RelayCommand] private void RemovePayment(PaymentLineViewModel line) { line.PropertyChanged -= OnPaymentLineChanged; Payments.Remove(line); RaiseTotals(); }
    public long PaymentTotalXof => Payments.Sum(x => x.AmountXof);

    private bool CanComplete() => !IsBusy && Cart.Count > 0;

    [RelayCommand(CanExecute = nameof(CanComplete))] private async Task Complete()
    {
        if (Cart.Count == 0 || IsBusy) return; IsBusy = true;
        try
        {
            var paymentDrafts = Payments.Count == 0 ? [new PaymentDraft(SelectedPaymentMode, PayableXof)] : Payments.Select(x => new PaymentDraft(x.Mode, x.AmountXof, x.Reference)).ToArray();
            var hasCredit = paymentDrafts.Any(x => x.Mode == PaymentMode.Credit);
            var key = Guid.NewGuid().ToString("N");
            DateTimeOffset? creditDue = hasCredit ? DateTimeOffset.Parse(CreditDueDate) : null;
            var reserving = hasCredit && ReserveStock;
            var draft = new SaleDraft(key, Cart.Select(x => new SaleLineDraft(x.Variant.Id, x.Quantity, x.EffectiveDiscountKind, x.DiscountValue)).ToArray(), paymentDrafts, SelectedCustomer?.Id, DiscountPercent == 0 ? DiscountKind.None : DiscountKind.Percentage, DiscountPercent, DiscountReason, ManagerPin, creditDue, null, null, reserving);
            var result = await sales.CreateAsync(draft);
            var documentLabel = PrintInvoice ? "Facture" : "Reçu";
            var message = reserving
                ? $"Avance {result.Number} enregistrée · articles mis de côté jusqu'au solde."
                : $"Vente {result.Number} enregistrée · {documentLabel} créé.";
            Status = reserving
                ? $"Avance {result.Number} enregistrée • articles réservés jusqu'au solde"
                : $"Vente {result.Number} enregistrée • {documentLabel} créé (visible dans Documents)";
            if (result.ChangeXof > 0) { Status += $" • Monnaie à rendre : {result.ChangeXof:N0} FCFA"; message += $"\nMonnaie à rendre : {result.ChangeXof:N0} FCFA."; }
            if (result.HasNegativeStock) { Status += " • Alerte : stock négatif à régulariser"; message += "\nAlerte : stock négatif à régulariser."; }
            if (SelectedPrinter is not null)
            {
                var documentId = PrintInvoice ? result.InvoiceDocumentId ?? result.DocumentId : result.DocumentId;
                try { var receipt = await documents.GetReceiptAsync(documentId, false); await printerService.PrintReceiptAsync(SelectedPrinter, receipt); await documents.MarkPrintedAsync(documentId); message += $"\n{documentLabel} imprimé."; }
                catch (Exception e) { Status += $" • Impression: {e.Message}"; message += $"\nImpression échouée : {e.Message}"; }
            }
            Cart.Clear(); Payments.Clear(); DiscountPercent = 0; SelectedPaymentMode = PaymentMode.Cash; PrintInvoice = false; NewCustomerName = string.Empty; NewCustomerPhone = string.Empty; SelectedCustomer = null; ReserveStock = false;
            RaiseTotals();
            UiFeedback.Success(message);
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; } finally { IsBusy = false; }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Entrée du sélecteur de client : vente comptoir ou client existant.</summary>
public sealed class CustomerChoice
{
    private CustomerChoice(string label, CustomerRow? row, bool isWalkIn)
    { Label = label; Row = row; IsWalkIn = isWalkIn; }

    public CustomerChoice(CustomerRow row) : this(row.Name, row, false) { }

    public static CustomerChoice WalkIn { get; } = new("Client comptoir (aucun)", null, true);

    public string Label { get; }
    public string? Phone => Row?.Phone;
    public CustomerRow? Row { get; }
    public bool IsWalkIn { get; }
}

public partial class CatalogViewModel(ICatalogService catalog, IProductImportService import, IProductDraftService drafts, ManagerSession session) : ObservableObject, ILoadable
{
    public ObservableCollection<ProductVariant> Items { get; } = [];
    public ObservableCollection<ImportIssue> ImportIssues { get; } = [];
    public ManagerSession Session => session;
    public ObservableCollection<string> SizeOptions { get; } = [];
    public IReadOnlyList<ProductType> ProductTypes { get; } = Enum.GetValues<ProductType>();
    public IReadOnlyList<string> GenderOptions { get; } = ["Femme", "Homme", "Enfant", "Mixte"];
    public IReadOnlyList<string> MaterialOptions { get; } = ["Coton", "Polyester", "Lin", "Soie", "Laine", "Denim", "Cuir", "Synthétique", "Autre"];
    private ImportPreview? importPreview;
    [ObservableProperty] private int importRowsCount;
    [ObservableProperty] private bool hasImportPreview;
    [ObservableProperty] private int selectedTab;
    [ObservableProperty] private bool hasSelection;
    public bool NoSelection => !HasSelection;
    [ObservableProperty] private string productName = string.Empty; [ObservableProperty] private string category = string.Empty; [ObservableProperty] private string price = string.Empty; [ObservableProperty] private string cost = string.Empty; [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private string description = string.Empty;
    [ObservableProperty] private ProductType selectedType = ProductType.Clothing; [ObservableProperty] private string brand = string.Empty; [ObservableProperty] private string productNotice = string.Empty;
    [ObservableProperty] private string subCategory = string.Empty; [ObservableProperty] private string gender = string.Empty;
    [ObservableProperty] private bool isEditing;
    public ObservableCollection<ProductDraft> Drafts { get; } = [];
    public string SaveButtonLabel => !session.IsManager ? "Envoyer pour validation" : IsEditing ? "Enregistrer" : "Ajouter";
    public string DraftsTabHeader => Drafts.Count == 0 ? "Brouillons en attente" : $"Brouillons en attente ({Drafts.Count})";
    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(SaveButtonLabel));
    public ObservableCollection<VariantRowViewModel> VariantRows { get; } = [];
    [ObservableProperty] private string matrixQuantity = "0";
    [ObservableProperty] private ProductVariant? selected;
    /// <summary>Le PIN vient de la session gérant : plus aucun écran ne le redemande.</summary>
    private string ManagerPin => session.Pin;
    public async Task LoadAsync()
    {
        var rows = await catalog.SearchAsync(null); Items.Clear(); foreach (var row in rows) Items.Add(row);
        RefreshSizeOptions();
        Drafts.Clear();
        foreach (var d in await drafts.ListAsync()) Drafts.Add(d);
        OnPropertyChanged(nameof(DraftsTabHeader));
        OnPropertyChanged(nameof(SaveButtonLabel));
    }

    private void RefreshSizeOptions()
    {
        SizeOptions.Clear();
        foreach (var s in BusinessRules.SizePresets(SelectedType)) SizeOptions.Add(s);
    }

    partial void OnSelectedTypeChanged(ProductType value) => RefreshSizeOptions();
    private string EffectiveCategory => string.IsNullOrWhiteSpace(Category) ? TypeCategory(SelectedType) : Category;
    private static string TypeCategory(ProductType type) => type switch
    {
        ProductType.Shoes => "Chaussures",
        ProductType.Accessories => "Accessoires",
        _ => "Vêtements",
    };
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
        HasSelection = value is not null;
        OnPropertyChanged(nameof(NoSelection));
        if (value is not null)
        {
            ProductName = value.Product?.Name ?? string.Empty; Category = value.Product?.Category?.Name ?? Category;
            SelectedType = value.Product?.Type ?? SelectedType; Brand = value.Product?.Brand ?? string.Empty;
            Description = value.Product?.Description ?? string.Empty;
            SubCategory = value.Product?.SubCategory ?? string.Empty; Gender = value.Product?.Gender ?? string.Empty;
        }
    }
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Ligne neuve dont aucun champ distinctif n'est renseigné : rien ne la différencie des autres.</summary>
    private static bool IsBlankVariantRow(VariantRowViewModel row) =>
        row.VariantId == Guid.Empty
        && string.IsNullOrWhiteSpace(row.Size) && string.IsNullOrWhiteSpace(row.Color)
        && string.IsNullOrWhiteSpace(row.Material) && string.IsNullOrWhiteSpace(row.PhotoPath)
        && string.IsNullOrWhiteSpace(row.Cost) && string.IsNullOrWhiteSpace(row.Price);

    /// <summary>Réduit un libellé à une base de SKU : majuscules, sans accents, séparée par des tirets.</summary>
    private static string SkuSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new System.Text.StringBuilder();
        foreach (var c in value.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToUpperInvariant(c));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }

    /// <summary>SKU dérivé du produit (+ taille et couleur), suffixé d'un compteur tant qu'il est déjà pris.</summary>
    private static string BuildSku(string productName, VariantRowViewModel row, HashSet<string> taken)
    {
        var parts = new[] { SkuSlug(productName), SkuSlug(row.Size), SkuSlug(row.Color) }.Where(x => x.Length > 0);
        var root = string.Join('-', parts);
        if (root.Length == 0) root = "ART";
        if (root.Length > 60) root = root[..60];
        var candidate = root;
        var suffix = 1;
        while (!taken.Add(candidate)) candidate = $"{root}-{++suffix}";
        return candidate;
    }

    [RelayCommand] private void AddVariantRow() => VariantRows.Add(new VariantRowViewModel());

    [RelayCommand] private void RemoveVariantRow(VariantRowViewModel row) => VariantRows.Remove(row);

    [RelayCommand] private async Task SaveVariants()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ProductName)) { Status = "Renseignez le nom du produit."; return; }
            if (!long.TryParse(Cost, out var cost)) { Status = "Renseignez le coût d'achat."; return; }
            if (!long.TryParse(Price, out var price)) { Status = "Renseignez le prix de vente."; return; }
            var rows = VariantRows.Where(r => !IsBlankVariantRow(r)).ToList();
            // Un produit sans déclinaison garde tout de même sa variante unique.
            if (rows.Count == 0 && VariantRows.Count > 0) rows.Add(VariantRows[0]);
            if (rows.Count == 0) { Status = "Ajoutez au moins une variante (« + Ajouter une variante »)."; return; }
            var skipped = VariantRows.Count - rows.Count;
            var initial = string.IsNullOrWhiteSpace(MatrixQuantity) ? 0 : decimal.Parse(MatrixQuantity);

            if (!session.IsManager)
            {
                await SubmitDraftAsync(rows, cost, price, initial);
                return;
            }

            var takenSkus = new HashSet<string>((await catalog.SearchAsync(null)).Select(x => x.Sku), StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows) if (!string.IsNullOrWhiteSpace(row.Sku)) takenSkus.Add(row.Sku.Trim());
            foreach (var row in rows)
            {
                // Le SKU n'est plus saisi : il est dérivé du nom du produit et rendu unique.
                var sku = string.IsNullOrWhiteSpace(row.Sku) ? BuildSku(ProductName, row, takenSkus) : row.Sku.Trim();
                var rowCost = long.TryParse(row.Cost, out var overrideCost) ? overrideCost : cost;
                var rowPrice = long.TryParse(row.Price, out var overridePrice) ? overridePrice : price;
                if (row.VariantId == Guid.Empty)
                {
                    await catalog.CreateVariantAsync(ProductName, EffectiveCategory, sku, NullIfEmpty(row.Barcode), NullIfEmpty(row.Size), NullIfEmpty(row.Color), rowCost, rowPrice, initial, 2, default, NullIfEmpty(SubCategory), NullIfEmpty(Gender), null, NullIfEmpty(row.Material), null, NullIfEmpty(row.Supplier), SelectedType, NullIfEmpty(Description), NullIfEmpty(row.PhotoPath), NullIfEmpty(ManagerPin));
                }
                else
                {
                    await catalog.UpdateVariantAsync(new ProductUpdate(row.VariantId, ProductName, EffectiveCategory, sku, NullIfEmpty(row.Barcode), NullIfEmpty(row.Size), NullIfEmpty(row.Color), rowCost, rowPrice, null, null, null, 2, NullIfEmpty(row.PhotoPath), true, NullIfEmpty(SubCategory), NullIfEmpty(Gender), null, NullIfEmpty(row.Material), null, NullIfEmpty(row.Supplier), SelectedType, NullIfEmpty(Description)), ManagerPin);
                }
            }
            Status = IsEditing ? "Modifications enregistrées" : $"{rows.Count} variante(s) ajoutée(s)";
            if (skipped > 0) Status += $" • {skipped} ligne(s) vide(s) ignorée(s)";
            UiFeedback.Success(Status);
            ResetForm();
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }

    private static long? OverrideOrNull(string value) => long.TryParse(value, out var v) ? v : null;

    private async Task SubmitDraftAsync(List<VariantRowViewModel> rows, long cost, long price, decimal initial)
    {
        var lines = rows.Select(r => new ProductDraftLine(NullIfEmpty(r.Size), NullIfEmpty(r.Color), NullIfEmpty(r.Material), NullIfEmpty(r.PhotoPath), OverrideOrNull(r.Cost), OverrideOrNull(r.Price))).ToArray();
        var draft = new ProductDraft(Guid.NewGuid(), ProductName.Trim(), EffectiveCategory, SelectedType, NullIfEmpty(Brand), NullIfEmpty(Description), NullIfEmpty(Gender), initial, cost, price, lines, DateTimeOffset.Now);
        await drafts.SubmitAsync(draft);
        Status = $"« {draft.ProductName} » envoyé au gérant pour validation ({lines.Length} variante(s)).";
        UiFeedback.Success(Status);
        ResetForm();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ApproveDraft(ProductDraft? draft)
    {
        if (draft is null) return;
        try
        {
            var taken = new HashSet<string>((await catalog.SearchAsync(null)).Select(x => x.Sku), StringComparer.OrdinalIgnoreCase);
            foreach (var line in draft.Lines)
            {
                var row = new VariantRowViewModel { Size = line.Size ?? "", Color = line.Color ?? "" };
                await catalog.CreateVariantAsync(draft.ProductName, draft.CategoryName, BuildSku(draft.ProductName, row, taken), null,
                    line.Size, line.Color, line.CostXof ?? draft.CostXof, line.PriceXof ?? draft.PriceXof, draft.InitialQuantity, 2, default,
                    null, draft.Gender, null, line.Material, null, null, draft.Type, draft.Description, line.PhotoPath, ManagerPin);
            }
            await drafts.DeleteAsync(draft.Id);
            Status = $"« {draft.ProductName} » validé et ajouté au catalogue.";
            UiFeedback.Success(Status);
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand]
    private async Task RejectDraft(ProductDraft? draft)
    {
        if (draft is null) return;
        if (!UiConfirm.Ask($"Refuser définitivement la proposition « {draft.ProductName} » ? Elle sera supprimée.")) return;
        try { await drafts.DeleteAsync(draft.Id); Status = $"Proposition « {draft.ProductName} » refusée."; await LoadAsync(); }
        catch (Exception e) { Status = e.Message; }
    }

    private void ResetForm()
    {
        IsEditing = false;
        VariantRows.Clear();
        VariantRows.Add(new VariantRowViewModel());
        ProductName = Description = Brand = Cost = Price = Category = SubCategory = string.Empty;
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
            HasImportPreview = true;
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
            importPreview = null; ImportIssues.Clear(); ImportRowsCount = 0; HasImportPreview = false;
            await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private void EditSelected()
    {
        if (Selected is null) return;
        ProductName = Selected.Product?.Name ?? string.Empty;
        Category = Selected.Product?.Category?.Name ?? Category;
        SelectedType = Selected.Product?.Type ?? SelectedType;
        Brand = Selected.Product?.Brand ?? string.Empty;
        Description = Selected.Product?.Description ?? string.Empty;
        SubCategory = Selected.Product?.SubCategory ?? string.Empty;
        Gender = Selected.Product?.Gender ?? string.Empty;
        Cost = Selected.CostXof.ToString();
        Price = Selected.PriceXof.ToString();
        VariantRows.Clear();
        foreach (var v in Items.Where(x => x.Product?.Id == Selected.Product?.Id))
            VariantRows.Add(new VariantRowViewModel
            {
                VariantId = v.Id, Sku = v.Sku, Barcode = v.Barcode ?? string.Empty, Size = v.Size ?? string.Empty,
                Color = v.Color ?? string.Empty, Material = v.Material ?? string.Empty, Supplier = v.Supplier ?? string.Empty,
                PhotoPath = v.PrimaryImagePath ?? string.Empty,
                // Vide tant que la variante suit le tarif du produit.
                Cost = v.CostXof == Selected.CostXof ? string.Empty : v.CostXof.ToString(),
                Price = v.PriceXof == Selected.PriceXof ? string.Empty : v.PriceXof.ToString(),
            });
        if (VariantRows.Count == 0) VariantRows.Add(new VariantRowViewModel());
        IsEditing = true;
        SelectedTab = 0;
    }

    [RelayCommand] private async Task DeleteSelected()
    {
        if (Selected is null) return;
        if (!UiConfirm.Ask($"Supprimer définitivement la variante {Selected.Sku} ? Impossible si elle a un historique (utilisez Archiver dans ce cas).")) return;
        try { await catalog.DeleteVariantAsync(Selected.Id, ManagerPin); Status = "Variante supprimée"; Selected = null; await LoadAsync(); }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task Archive() { if (Selected is null) return; if (!UiConfirm.Ask($"Archiver la variante {Selected.Sku} ? Elle restera dans l'historique mais ne pourra plus être vendue.")) return; try { await catalog.UpdateVariantAsync(new ProductUpdate(Selected.Id, Selected.Product!.Name, Selected.Product.Category?.Name ?? Category, Selected.Sku, Selected.Barcode, Selected.Size, Selected.Color, Selected.CostXof, Selected.PriceXof, Selected.PromotionalPriceXof, Selected.PromotionStartsAt, Selected.PromotionEndsAt, Selected.LowStockThreshold, null, false, Selected.Product.SubCategory, Selected.Product.Gender, Selected.Product.Season, Selected.Material, Selected.Location, Selected.Supplier, Selected.Product.Type), ManagerPin); Status = "Produit archivé"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
}

public partial class VariantRowViewModel : ObservableObject
{
    [ObservableProperty] private Guid variantId = Guid.Empty;
    [ObservableProperty] private string sku = string.Empty;
    [ObservableProperty] private string barcode = string.Empty;
    [ObservableProperty] private string size = string.Empty;
    [ObservableProperty] private string color = string.Empty;
    [ObservableProperty] private string material = string.Empty;
    [ObservableProperty] private string supplier = string.Empty;
    [ObservableProperty] private string photoPath = string.Empty;
    /// <summary>Vide = hérite du coût saisi au niveau du produit.</summary>
    [ObservableProperty] private string cost = string.Empty;
    /// <summary>Vide = hérite du prix saisi au niveau du produit.</summary>
    [ObservableProperty] private string price = string.Empty;
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

public partial class StockViewModel(ICatalogService catalog, IStockService stock, IInventoryService inventory, IReportService reports, IPurchaseService purchases, ManagerSession session) : ObservableObject, ILoadable
{
    public ObservableCollection<ProductVariant> Items { get; } = [];
    public ObservableCollection<StockHistoryRow> History { get; } = [];
    public ObservableCollection<StockAlertRow> Alerts { get; } = [];
    public ObservableCollection<InventoryLineViewModel> InventoryLines { get; } = [];
    public ObservableCollection<OrderDraftLine> OrderLines { get; } = [];
    public ObservableCollection<PurchaseOrderRow> OpenOrders { get; } = [];
    public ObservableCollection<string> Categories { get; } = [];
    [ObservableProperty] private ProductVariant? selected; [ObservableProperty] private string quantity = string.Empty; [ObservableProperty] private string reason = string.Empty; [ObservableProperty] private string status = string.Empty;
    /// <summary>Le PIN vient de la session gérant : plus aucun écran ne le redemande.</summary>
    private string ManagerPin => session.Pin;
    [ObservableProperty] private string countedQuantity = ""; [ObservableProperty] private string categoryFilter = string.Empty;
    [ObservableProperty] private string supplier = string.Empty; [ObservableProperty] private string orderExpected = "1";
    [ObservableProperty] private PurchaseOrderRow? selectedOpenLine; [ObservableProperty] private string receivedQuantity = "0"; [ObservableProperty] private string receivedCost = "";

    public async Task LoadAsync()
    {
        var rows = await catalog.SearchAsync(null); Items.Clear(); foreach (var row in rows) Items.Add(row);
        Categories.Clear(); foreach (var c in await catalog.CategoriesAsync()) Categories.Add(c);
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

public partial class CustomersViewModel(ICustomerService customers, ICreditService credits, ManagerSession session) : ObservableObject, ILoadable
{
    public ObservableCollection<CustomerRow> Items { get; } = [];
    public ObservableCollection<CreditSummary> Credits { get; } = [];
    public ObservableCollection<CreditPaymentRow> CreditPayments { get; } = [];
    public ObservableCollection<CustomerHistorySale> HistorySales { get; } = [];
    public ObservableCollection<CustomerHistoryPayment> HistoryPayments { get; } = [];
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = Enum.GetValues<PaymentMode>().Where(x => x != PaymentMode.Credit).ToArray();
    public IReadOnlyList<string> GenderOptions { get; } = ["Femme", "Homme", "Enfant", "Mixte"];
    public IReadOnlyList<string> ChannelOptions { get; } = ["Boutique", "WhatsApp", "Instagram", "Facebook", "Téléphone", "Autre"];

    [ObservableProperty] private string search = string.Empty;
    partial void OnSearchChanged(string value) => _ = LoadAsync();
    [ObservableProperty] private string name = string.Empty; [ObservableProperty] private string phone = string.Empty; [ObservableProperty] private string creditLimit = "0"; [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private string gender = string.Empty; [ObservableProperty] private string preferences = string.Empty; [ObservableProperty] private string channel = string.Empty; [ObservableProperty] private bool marketingConsent;
    [ObservableProperty] private CustomerRow? selectedCustomer;
    [ObservableProperty] private int customerTab;
    [ObservableProperty] private bool editExpanded;
    [ObservableProperty] private string editName = string.Empty; [ObservableProperty] private string editPhone = string.Empty; [ObservableProperty] private string editSecondaryPhone = string.Empty; [ObservableProperty] private string editGender = string.Empty; [ObservableProperty] private string editAddress = string.Empty; [ObservableProperty] private string editNotes = string.Empty; [ObservableProperty] private string editPreferences = string.Empty; [ObservableProperty] private string editChannel = string.Empty; [ObservableProperty] private bool editConsent; [ObservableProperty] private string editCreditLimit = "0";
    [ObservableProperty] private CreditSummary? selectedCredit; [ObservableProperty] private string paymentAmount = ""; [ObservableProperty] private PaymentMode paymentMode = PaymentMode.Cash; [ObservableProperty] private string paymentReference = "";
    /// <summary>Le PIN vient de la session gérant : plus aucun écran ne le redemande.</summary>
    private string ManagerPin => session.Pin;

    public async Task LoadAsync()
    {
        var rows = await customers.SearchAsync(Search); Items.Clear(); foreach (var row in rows) Items.Add(row);
        Credits.Clear(); foreach (var row in await credits.ListAsync()) Credits.Add(row);
    }

    [RelayCommand] private async Task SearchCustomers() => await LoadAsync();
    [RelayCommand] private async Task Create() { try { await customers.CreateAsync(Name, Phone, long.Parse(CreditLimit), default, NullIfEmpty(Gender), NullIfEmpty(Preferences), NullIfEmpty(Channel), MarketingConsent); Status = "Client ajouté"; UiFeedback.Success("Client ajouté."); Name = Phone = string.Empty; CreditLimit = "0"; MarketingConsent = false; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }

    [RelayCommand] private void EditCustomer() { if (SelectedCustomer is null) { Status = "Sélectionnez d'abord un client."; return; } CustomerTab = 0; EditExpanded = true; }

    [RelayCommand] private async Task ArchiveCustomer()
    {
        if (SelectedCustomer is null) { Status = "Sélectionnez d'abord un client."; return; }
        if (!UiConfirm.Ask($"Archiver le client {SelectedCustomer.Name} ? Il n'apparaîtra plus dans les listes.")) return;
        try { await customers.ArchiveAsync(SelectedCustomer.Id, ManagerPin); Status = "Client archivé"; UiFeedback.Success("Client archivé."); SelectedCustomer = null; await LoadAsync(); }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task DeleteCustomer()
    {
        if (SelectedCustomer is null) { Status = "Sélectionnez d'abord un client."; return; }
        if (!UiConfirm.Ask($"Supprimer définitivement le client {SelectedCustomer.Name} ? Impossible s'il a un historique (utilisez Archiver).")) return;
        try { await customers.DeleteAsync(SelectedCustomer.Id, ManagerPin); Status = "Client supprimé"; UiFeedback.Success("Client supprimé."); SelectedCustomer = null; await LoadAsync(); }
        catch (Exception e) { Status = e.Message; }
    }

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
            await customers.UpdateAsync(new CustomerUpdateRequest(SelectedCustomer.Id, EditName, NullIfEmpty(EditPhone), NullIfEmpty(EditSecondaryPhone), NullIfEmpty(EditGender), NullIfEmpty(EditAddress), NullIfEmpty(EditNotes), NullIfEmpty(EditPreferences), NullIfEmpty(EditChannel), EditConsent, long.Parse(EditCreditLimit)));
            Status = "Fiche client complétée";
            UiFeedback.Success("Fiche client enregistrée.");
            EditExpanded = false;
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

public partial class ExpensesViewModel(IExpenseService expenses, ManagerSession session) : ObservableObject, ILoadable
{
    public ObservableCollection<Expense> RecentExpenses { get; } = [];
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = Enum.GetValues<PaymentMode>();
    public IReadOnlyList<string> CategoryOptions { get; } = ["Loyer", "Salaires", "Transport", "Électricité", "Eau", "Internet", "Fournitures", "Marketing", "Maintenance", "Impôts", "Assurance", "Autres"];
    [ObservableProperty] private string category = "Autres"; [ObservableProperty] private string description = string.Empty; [ObservableProperty] private string amount = string.Empty; [ObservableProperty] private PaymentMode selectedMode = PaymentMode.Cash; [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private Expense? selectedExpense;
    /// <summary>Le PIN vient de la session gérant : plus aucun écran ne le redemande.</summary>
    private string ManagerPin => session.Pin;
    public async Task LoadAsync() { RecentExpenses.Clear(); foreach (var e in await expenses.ListRecentAsync(20)) RecentExpenses.Add(e); }
    [RelayCommand] private async Task Create() { try { await expenses.CreateAsync(Category, Description, long.Parse(Amount), SelectedMode); Status = "Dépense enregistrée"; UiFeedback.Success("Dépense enregistrée."); Description = Amount = string.Empty; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task DeleteExpense()
    {
        if (SelectedExpense is null) { Status = "Sélectionnez d'abord une dépense."; return; }
        if (!UiConfirm.Ask($"Supprimer la dépense « {SelectedExpense.Category} » de {SelectedExpense.AmountXof:N0} FCFA ?")) return;
        try { await expenses.DeleteAsync(SelectedExpense.Id, ManagerPin); Status = "Dépense supprimée"; UiFeedback.Success("Dépense supprimée."); SelectedExpense = null; await LoadAsync(); }
        catch (Exception e) { Status = e.Message; }
    }
}

public partial class DocumentsViewModel(IDocumentService documents, IReturnService returns, IThermalPrinterService printers, IAppSettingsService settings, IA4DocumentService a4, ManagerSession session) : ObservableObject, ILoadable
{
    public ObservableCollection<DocumentListItem> Items { get; } = [];
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = Enum.GetValues<PaymentMode>().Where(x => x != PaymentMode.Credit).ToArray();
    [ObservableProperty] private string search = string.Empty;
    partial void OnSearchChanged(string value) => _ = LoadAsync();
    [ObservableProperty] private DocumentListItem? selectedDocument; [ObservableProperty] private string saleNumber = ""; [ObservableProperty] private string returnedSku = ""; [ObservableProperty] private string returnedQuantity = "1"; [ObservableProperty] private string replacementSku = ""; [ObservableProperty] private string replacementQuantity = "1"; [ObservableProperty] private string exchangePaymentAmount = "0"; [ObservableProperty] private PaymentMode exchangePaymentMode = PaymentMode.Cash; [ObservableProperty] private string exchangePaymentReference = ""; [ObservableProperty] private string reason = ""; [ObservableProperty] private bool restock = true; [ObservableProperty] private string proformaDescription = "Article"; [ObservableProperty] private string proformaTotal = "0"; [ObservableProperty] private string status = "";
    /// <summary>Le PIN vient de la session gérant : plus aucun écran ne le redemande.</summary>
    private string ManagerPin => session.Pin;
    partial void OnSelectedDocumentChanged(DocumentListItem? value) => _ = RefreshPreviewAsync();
    public ObservableCollection<PrinterProfile> Printers { get; } = [];
    public ObservableCollection<string> A4Printers { get; } = [];
    [ObservableProperty] private PrinterProfile? selectedPrinter;
    [ObservableProperty] private string? selectedA4Printer;
    partial void OnSelectedPrinterChanged(PrinterProfile? value) { if (value is not null) _ = PrinterStore.SaveAsync(settings, value); }
    public ObservableCollection<string> PreviewLines { get; } = [];
    [ObservableProperty] private bool hasPreview;
    partial void OnHasPreviewChanged(bool value) => OnPropertyChanged(nameof(NoPreview));
    public bool NoPreview => !HasPreview;

    public async Task LoadAsync()
    {
        var keepId = SelectedDocument?.Id;
        Items.Clear(); foreach (var x in await documents.ListAsync(Search)) Items.Add(x);
        if (keepId is Guid id) SelectedDocument = Items.FirstOrDefault(x => x.Id == id);
        await LoadPrintersAsync();
    }

    private async Task LoadPrintersAsync()
    {
        if (Printers.Count == 0)
        {
            foreach (var p in printers.Discover()) Printers.Add(p);
            var tcp = await settings.GetAsync("Printer.Tcp");
            if (!string.IsNullOrWhiteSpace(tcp)) Printers.Add(new PrinterProfile($"Thermique réseau {tcp}", PrinterConnectionKind.TcpIp, tcp, PaperWidth.Mm80));
            SelectedPrinter = await PrinterStore.LoadAsync(settings, printers);
        }
        if (A4Printers.Count == 0)
        {
            try
            {
                using var server = new System.Printing.LocalPrintServer();
                foreach (var queue in server.GetPrintQueues()) A4Printers.Add(queue.FullName);
                SelectedA4Printer ??= server.DefaultPrintQueue?.FullName ?? A4Printers.FirstOrDefault();
            }
            catch { }
        }
    }

    private async Task RefreshPreviewAsync()
    {
        PreviewLines.Clear();
        if (SelectedDocument is null) { HasPreview = false; return; }
        try
        {
            var receipt = await documents.GetReceiptAsync(SelectedDocument.Id, false);
            PreviewLines.Add(receipt.ShopName);
            PreviewLines.Add($"{receipt.Number} · {receipt.IssuedAt:dd/MM/yyyy HH:mm}");
            if (!string.IsNullOrWhiteSpace(receipt.Customer)) PreviewLines.Add($"Client : {receipt.Customer}");
            PreviewLines.Add("──────────────────────────────");
            foreach (var item in receipt.Items) PreviewLines.Add($"{item.Quantity:0.###} × {item.Description} — {item.TotalXof:N0} FCFA");
            PreviewLines.Add("──────────────────────────────");
            if (receipt.DiscountXof > 0) PreviewLines.Add($"Remise : -{receipt.DiscountXof:N0} FCFA");
            PreviewLines.Add($"TOTAL : {receipt.TotalXof:N0} FCFA");
            foreach (var payment in receipt.Payments) PreviewLines.Add($"{Libelles.Text(payment.Mode)} : {payment.AmountXof:N0} FCFA");
            if (receipt.ChangeXof > 0) PreviewLines.Add($"Monnaie rendue : {receipt.ChangeXof:N0} FCFA");
            HasPreview = true;
        }
        catch (Exception e) { HasPreview = false; Status = e.Message; }
    }

    [RelayCommand] private async Task Refresh() => await LoadAsync();
    [RelayCommand] private async Task PreviewDocument()
    {
        if (SelectedDocument is null) { Status = "Sélectionnez d'abord un document."; return; }
        try
        {
            var paper = SelectedPrinter?.PaperWidth ?? PaperWidth.Mm80;
            var receipt = await documents.GetReceiptAsync(SelectedDocument.Id, SelectedDocument.PrintCount > 0);
            TicketPreviewWindow.Show(printers.Preview(receipt, paper), $"Aperçu ticket · {SelectedDocument.Number}", paper);
            Status = "Aperçu affiché";
        }
        catch (Exception e) { Status = e.Message; }
    }
    [RelayCommand] private async Task Duplicate() { if (SelectedDocument is null) { Status = "Sélectionnez d'abord un document."; return; } if (SelectedPrinter is null) { Status = "Sélectionnez d'abord une imprimante ticket."; return; } try { var receipt = await documents.GetReceiptAsync(SelectedDocument.Id, true); await printers.PrintReceiptAsync(SelectedPrinter, receipt); await documents.MarkPrintedAsync(SelectedDocument.Id); Status = "Duplicata imprimé"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task ExportPdf() { if (SelectedDocument is null) return; try { var receipt = await documents.GetReceiptAsync(SelectedDocument.Id, SelectedDocument.PrintCount > 0); var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"{SelectedDocument.Number}.pdf", Filter = "Document PDF (*.pdf)|*.pdf" }; if (dialog.ShowDialog() != true) return; await File.WriteAllBytesAsync(dialog.FileName, a4.CreateInvoicePdf(receipt)); Status = "PDF exporté"; } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task PrintA4() { if (SelectedDocument is null) return; try { var receipt = await documents.GetReceiptAsync(SelectedDocument.Id, SelectedDocument.PrintCount > 0); var path = await a4.PrintInvoiceAsync(receipt, SelectedA4Printer); await documents.MarkPrintedAsync(SelectedDocument.Id); Status = SelectedA4Printer is null ? $"Document envoyé à l'imprimante A4 par défaut · Copie : {path}" : $"Document envoyé à « {SelectedA4Printer} » · Copie : {path}"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }

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
    public IReadOnlyList<string> ReportKinds { get; } = ["Ventes par jour", "Modes de paiement", "Ventes par vendeur", "Top produits", "Top produits (toutes tailles)", "Articles sans vente", "Valeur du stock", "Écarts d'inventaire", "Remises et corrections", "Rotation & dormants"];

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
                // Cumule les variantes d'un même article : une robe déclinée en cinq tailles ne
                // remonterait jamais devant un article unique si on comptait par SKU.
                "Top produits (toutes tailles)" => await reports.TopProductsByProductAsync(from, to),
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

public partial class SettingsViewModel(IAuthorizationService authorization, IAppSettingsService settings, IBackupService backup, IThermalPrinterService printer, IDocumentService documents, IA4DocumentService a4, ManagerSession session) : ObservableObject, ILoadable
{
    public ObservableCollection<PrinterProfile> Printers { get; } = [];
    public ManagerSession Session => session;
    public IReadOnlyList<DocumentType> DocumentTypes { get; } = Enum.GetValues<DocumentType>();
    public IReadOnlyList<string> Styles { get; } = ["Classique", "Moderne", "Minimal"];
    public IReadOnlyList<string> RenderModes { get; } = ["ESC/POS brut (recommandé)", "Rendu Windows (si rien ne sort)"];
    public IReadOnlyList<int> SerialBauds { get; } = [9600, 19200, 38400, 57600, 115200];
    public IReadOnlyList<string> PaperWidthOptions { get; } = ["58 mm", "80 mm"];
    [ObservableProperty] private string selectedStyle = "Moderne";
    [ObservableProperty] private string networkPrinter = "";
    [ObservableProperty] private PrinterProfile? selectedPrinter;
    [ObservableProperty] private bool cutPaper = true;
    [ObservableProperty] private string selectedRenderMode = "ESC/POS brut (recommandé)";
    [ObservableProperty] private int serialBaud = 9600;
    [ObservableProperty] private string selectedPaperWidthOption = "80 mm";
    [ObservableProperty] private System.Windows.Media.ImageSource? previewImage;
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
        SelectedPrinter = await PrinterStore.LoadAsync(settings, printer) ?? Printers.FirstOrDefault();
        CutPaper = (await settings.GetAsync("Printer.CutPaper") ?? "1") == "1";
        SelectedRenderMode = (await settings.GetAsync("Printer.RenderMode") ?? "Raw") == "Gdi" ? RenderModes[1] : RenderModes[0];
        SerialBaud = int.TryParse(await settings.GetAsync("Printer.SerialBaud"), out var baud) && SerialBauds.Contains(baud) ? baud : 9600;
        SelectedPaperWidthOption = (await settings.GetAsync("Printer.PaperWidth") ?? "80") == "58" ? "58 mm" : "80 mm";
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

    partial void OnSelectedPrinterChanged(PrinterProfile? value) { if (value is not null) _ = PrinterStore.SaveAsync(settings, value); }

    partial void OnCutPaperChanged(bool value) { _ = PersistCutPaperAsync(value); }

    private async Task PersistCutPaperAsync(bool value)
    {
        try { await settings.SetAsync("Printer.CutPaper", value ? "1" : "0"); Status = value ? "Découpe du papier activée." : "Découpe du papier désactivée."; }
        catch (Exception e) { Status = e.Message; }
    }

    partial void OnSelectedRenderModeChanged(string value) => _ = PersistRenderModeAsync(value);

    private async Task PersistRenderModeAsync(string value)
    {
        try
        {
            var mode = value.StartsWith("Rendu", StringComparison.Ordinal) ? "Gdi" : "Raw";
            await settings.SetAsync("Printer.RenderMode", mode);
            Status = mode == "Gdi" ? "Mode Rendu Windows activé : lancez un ticket test." : "Mode ESC/POS brut activé.";
        }
        catch (Exception e) { Status = e.Message; }
    }

    partial void OnSerialBaudChanged(int value) { _ = PersistSerialBaudAsync(value); }

    private async Task PersistSerialBaudAsync(int value)
    {
        try { await settings.SetAsync("Printer.SerialBaud", value.ToString(CultureInfo.InvariantCulture)); Status = $"Vitesse série réglée à {value} bauds."; }
        catch (Exception e) { Status = e.Message; }
    }

    partial void OnSelectedPaperWidthOptionChanged(string value) { _ = PersistPaperWidthAsync(value); }

    private async Task PersistPaperWidthAsync(string value)
    {
        try
        {
            var width = value.StartsWith("58", StringComparison.Ordinal) ? "58" : "80";
            await settings.SetAsync("Printer.PaperWidth", width);
            Status = $"Largeur du rouleau réglée sur {width} mm.";
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task DiagnosePrinter()
    {
        if (SelectedPrinter is null) { Status = "Sélectionnez d'abord une imprimante."; return; }
        try
        {
            var report = await printer.DiagnoseAsync(SelectedPrinter);
            Status = "Diagnostic terminé.";
            System.Windows.MessageBox.Show(report, $"Diagnostic · {SelectedPrinter.Name}", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e) { Status = e.Message; }
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
        await RefreshPreviewAsync();
    }

    [RelayCommand] private async Task Save()
    {
        try
        {
            if (!await authorization.AuthorizeSensitiveActionAsync(session.Pin, "Modifier paramètres")) throw new UnauthorizedAccessException("Session gérant expirée : rebasculez en mode gérant.");
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
        try { await authorization.ChangePinAsync(Pin, NewPin); session.UpdatePin(NewPin); Status = "Code gérant modifié"; Pin = NewPin = string.Empty; }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task Backup() { try { Status = $"Sauvegarde: {await backup.CreateAsync()}"; } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task TestPrinter() { if (SelectedPrinter is null) { Status = "Sélectionnez d'abord une imprimante."; return; } try { await printer.PrintTestAsync(SelectedPrinter); Status = $"Ticket test envoyé vers « {SelectedPrinter.Name} »."; } catch (Exception e) { Status = $"Échec du test sur « {SelectedPrinter.Name} » : {e.Message}"; } }

    private async Task<ReceiptData> BuildSampleWithFlagsAsync()
    {
        var sample = await documents.BuildSampleAsync(SelectedDocType);
        var style = Enum.TryParse<DocumentStyle>(SelectedStyle, true, out var parsed) ? parsed : DocumentStyle.Moderne;
        return sample with
        {
            Style = style,
            LogoPath = FlagLogo ? sample.LogoPath : null,
            Slogan = FlagSlogan ? sample.Slogan : null,
            StampPath = FlagStamp ? sample.StampPath : null,
            SignaturePath = FlagSignature ? sample.SignaturePath : null
        };
    }

    private int previewToken;

    private async Task RefreshPreviewAsync()
    {
        try
        {
            var token = ++previewToken;
            var sample = await BuildSampleWithFlagsAsync();
            var png = await Task.Run(() => a4.CreatePreviewImage(sample));
            if (token != previewToken) return;
            var image = new BitmapImage();
            using var stream = new MemoryStream(png);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            PreviewImage = image;
        }
        catch { }
    }

    partial void OnSelectedStyleChanged(string value) => _ = RefreshPreviewAsync();
    partial void OnFlagLogoChanged(bool value) => _ = RefreshPreviewAsync();
    partial void OnFlagSloganChanged(bool value) => _ = RefreshPreviewAsync();
    partial void OnFlagStampChanged(bool value) => _ = RefreshPreviewAsync();
    partial void OnFlagSignatureChanged(bool value) => _ = RefreshPreviewAsync();

    [RelayCommand] private async Task PreviewTemplate()
    {
        try
        {
            var sample = await BuildSampleWithFlagsAsync();
            var path = Path.Combine(Path.GetTempPath(), $"apercu-{SelectedDocType}-{Guid.NewGuid():N}.pdf");
            await File.WriteAllBytesAsync(path, a4.CreateInvoicePdf(sample));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Status = $"Aperçu {SelectedStyle} · {SelectedDocType} ouvert";
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand] private async Task PreviewTicket()
    {
        try
        {
            var paper = SelectedPaperWidthOption.StartsWith("58", StringComparison.Ordinal) ? PaperWidth.Mm58 : PaperWidth.Mm80;
            var sample = await BuildSampleWithFlagsAsync();
            TicketPreviewWindow.Show(printer.Preview(sample, paper), $"Aperçu ticket · {SelectedStyle} · {Libelles.Text(SelectedDocType)}", paper);
            Status = $"Aperçu ticket {SelectedStyle} · {SelectedDocType} affiché";
        }
        catch (Exception e) { Status = e.Message; }
    }
}
