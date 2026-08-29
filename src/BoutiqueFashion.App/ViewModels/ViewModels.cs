using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

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

public partial class DashboardViewModel(IReportService reports) : ObservableObject, ILoadable
{
    [ObservableProperty] private DashboardSummary summary = new(0, 0, 0, 0, 0, 0);
    public async Task LoadAsync() { var now = DateTimeOffset.Now; Summary = await reports.DashboardAsync(new DateTimeOffset(now.Date, now.Offset), new DateTimeOffset(now.Date.AddDays(1), now.Offset)); }
}

public partial class CartLineViewModel(ProductVariant variant) : ObservableObject
{
    public ProductVariant Variant { get; } = variant; public string Label => string.Join(" - ", new[] { Variant.Product?.Name, Variant.Color, Variant.Size }.Where(x => !string.IsNullOrWhiteSpace(x))); public long UnitPriceXof => Variant.PriceXof;
    [ObservableProperty] private decimal quantity = 1; public long TotalXof => decimal.ToInt64(Quantity * UnitPriceXof);
    partial void OnQuantityChanged(decimal value) => OnPropertyChanged(nameof(TotalXof));
}

public partial class PaymentLineViewModel : ObservableObject { public IReadOnlyList<PaymentMode> Modes { get; }=Enum.GetValues<PaymentMode>(); [ObservableProperty]private PaymentMode mode=PaymentMode.Cash;[ObservableProperty]private long amountXof;[ObservableProperty]private string reference=string.Empty; }

public partial class SaleViewModel(ICatalogService catalog, ICustomerService customers, ISaleService sales, ICashSessionService cash, IThermalPrinterService printerService,IAppSettingsService settings, IDocumentService documents) : ObservableObject, ILoadable
{
    public ObservableCollection<ProductVariant> Products { get; } = []; public ObservableCollection<CartLineViewModel> Cart { get; } = [];
    public ObservableCollection<Customer> Customers { get; } = [];
    public ObservableCollection<PaymentLineViewModel> Payments { get; }=[];
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = Enum.GetValues<PaymentMode>(); public IReadOnlyList<PrinterProfile> Printers { get; } = printerService.Discover();
    [ObservableProperty] private string search = string.Empty; [ObservableProperty] private PaymentMode selectedPaymentMode = PaymentMode.Cash; [ObservableProperty] private PrinterProfile? selectedPrinter;
    [ObservableProperty] private Customer? selectedCustomer; [ObservableProperty] private decimal discountPercent; [ObservableProperty] private string discountReason = string.Empty; [ObservableProperty] private string managerPin = string.Empty; [ObservableProperty] private string creditDueDate = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd");
    [ObservableProperty] private string status = "Prêt"; [ObservableProperty] private bool isBusy;
    [ObservableProperty]private string countedCash="";[ObservableProperty]private string cashDifferenceReason="";[ObservableProperty]private string openingFloat="0";
    public long TotalXof => Cart.Sum(x => x.TotalXof); public long PayableXof => TotalXof - BusinessRules.CalculateDiscount(TotalXof, DiscountPercent == 0 ? DiscountKind.None : DiscountKind.Percentage, DiscountPercent);
    partial void OnDiscountPercentChanged(decimal value) => OnPropertyChanged(nameof(PayableXof));
    public async Task LoadAsync() { var items = await catalog.SearchAsync(Search); Products.Clear(); foreach (var item in items) Products.Add(item); Customers.Clear(); foreach (var item in await customers.SearchAsync(null)) Customers.Add(item); if(SelectedPrinter is null){var saved=await settings.GetAsync("Printer.Selected");SelectedPrinter=saved is null?Printers.FirstOrDefault():JsonSerializer.Deserialize<PrinterProfile>(saved);} }
    [RelayCommand] private async Task SearchProducts() => await LoadAsync();
    [RelayCommand] private void Add(ProductVariant variant) { var existing = Cart.FirstOrDefault(x => x.Variant.Id == variant.Id); if (existing is null) { var line = new CartLineViewModel(variant); line.PropertyChanged += (_, _) => OnPropertyChanged(nameof(TotalXof)); Cart.Add(line); } else existing.Quantity++; OnPropertyChanged(nameof(TotalXof)); }
    [RelayCommand] private void Remove(CartLineViewModel line) { Cart.Remove(line); OnPropertyChanged(nameof(TotalXof)); }
    partial void OnSelectedPrinterChanged(PrinterProfile? value) { if (value is not null) _ = PersistPrinterAsync(value); }
    private async Task PersistPrinterAsync(PrinterProfile value) { try { await settings.SetAsync("Printer.Selected", JsonSerializer.Serialize(value), "Vendeur boutique"); } catch { } }
    [RelayCommand] private async Task OpenCash() { try { await cash.OpenAsync(long.Parse(OpeningFloat)); Status = "Caisse ouverte"; } catch (Exception e) { Status = e.Message; } }
    [RelayCommand]private void AddPayment(){var line=new PaymentLineViewModel{AmountXof=Math.Max(0,PayableXof-Payments.Sum(x=>x.AmountXof))};line.PropertyChanged+=(_,_)=>OnPropertyChanged(nameof(PaymentTotalXof));Payments.Add(line);OnPropertyChanged(nameof(PaymentTotalXof));}
    [RelayCommand]private void RemovePayment(PaymentLineViewModel line){Payments.Remove(line);OnPropertyChanged(nameof(PaymentTotalXof));}
    public long PaymentTotalXof=>Payments.Sum(x=>x.AmountXof);
    [RelayCommand]private async Task CloseCash(){try{var result=await cash.CloseAsync(long.Parse(CountedCash),CashDifferenceReason);Status=$"Caisse clôturée • Écart {result.DifferenceXof:N0} FCFA";}catch(Exception e){Status=e.Message;}}
    [RelayCommand] private async Task Complete()
    {
        if (Cart.Count == 0 || IsBusy) return; IsBusy = true;
        try
        {
            var paymentDrafts=Payments.Count==0?[new PaymentDraft(SelectedPaymentMode,PayableXof)]:Payments.Select(x=>new PaymentDraft(x.Mode,x.AmountXof,x.Reference)).ToArray();var hasCredit=paymentDrafts.Any(x=>x.Mode==PaymentMode.Credit);var key = Guid.NewGuid().ToString("N"); DateTimeOffset? creditDue = hasCredit ? DateTimeOffset.Parse(CreditDueDate) : null; var draft = new SaleDraft(key, Cart.Select(x => new SaleLineDraft(x.Variant.Id, x.Quantity)).ToArray(), paymentDrafts, SelectedCustomer?.Id, DiscountPercent == 0 ? DiscountKind.None : DiscountKind.Percentage, DiscountPercent, DiscountReason, ManagerPin, creditDue);
            var result = await sales.CreateAsync(draft); Status = $"Vente {result.Number} enregistrée";
            if (SelectedPrinter is not null)
            {
                try { var receipt = await documents.GetReceiptAsync(result.DocumentId, false); await printerService.PrintReceiptAsync(SelectedPrinter, receipt); await documents.MarkPrintedAsync(result.DocumentId); }
                catch (Exception e) { Status += $" • Impression: {e.Message}"; }
            }
            Cart.Clear();Payments.Clear(); DiscountPercent = 0; OnPropertyChanged(nameof(TotalXof)); OnPropertyChanged(nameof(PayableXof));OnPropertyChanged(nameof(PaymentTotalXof)); await LoadAsync();
        }
        catch (Exception e) { Status = e.Message; } finally { IsBusy = false; }
    }
}

public partial class CatalogViewModel(ICatalogService catalog) : ObservableObject, ILoadable
{
    public ObservableCollection<ProductVariant> Items { get; } = [];
    [ObservableProperty] private string productName = string.Empty; [ObservableProperty] private string category = "Vêtements"; [ObservableProperty] private string sku = string.Empty; [ObservableProperty] private string price = string.Empty; [ObservableProperty] private string cost = string.Empty; [ObservableProperty] private string status = string.Empty;
    [ObservableProperty]private ProductVariant?selected;[ObservableProperty]private string managerPin="";[ObservableProperty]private string promotionPrice="";[ObservableProperty]private string photoPath="";
    public async Task LoadAsync() { var rows = await catalog.SearchAsync(null); Items.Clear(); foreach (var row in rows) Items.Add(row); }
    partial void OnSelectedChanged(ProductVariant? value) => PhotoPath = value?.Product?.PrimaryImagePath ?? string.Empty;
    [RelayCommand] private async Task Create() { try { await catalog.CreateVariantAsync(ProductName, Category, Sku, null, null, null, long.Parse(Cost), long.Parse(Price), 0, 2); Status = "Produit ajouté"; ProductName = Sku = Price = Cost = string.Empty; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand]private async Task Update(){if(Selected is null)return;try{var u=new ProductUpdate(Selected.Id,string.IsNullOrWhiteSpace(ProductName)?Selected.Product!.Name:ProductName,Category,string.IsNullOrWhiteSpace(Sku)?Selected.Sku:Sku,Selected.Barcode,Selected.Size,Selected.Color,string.IsNullOrWhiteSpace(Cost)?Selected.CostXof:long.Parse(Cost),string.IsNullOrWhiteSpace(Price)?Selected.PriceXof:long.Parse(Price),string.IsNullOrWhiteSpace(PromotionPrice)?null:long.Parse(PromotionPrice),DateTimeOffset.Now,DateTimeOffset.Now.AddMonths(1),Selected.LowStockThreshold,PhotoPath,true);await catalog.UpdateVariantAsync(u,ManagerPin);Status="Produit modifié";PhotoPath=string.Empty;await LoadAsync();}catch(Exception e){Status=e.Message;}}
    [RelayCommand]private async Task Archive(){if(Selected is null)return;try{await catalog.UpdateVariantAsync(new ProductUpdate(Selected.Id,Selected.Product!.Name,Selected.Product.Category?.Name??Category,Selected.Sku,Selected.Barcode,Selected.Size,Selected.Color,Selected.CostXof,Selected.PriceXof,Selected.PromotionalPriceXof,Selected.PromotionStartsAt,Selected.PromotionEndsAt,Selected.LowStockThreshold,null,false),ManagerPin);Status="Produit archivé";await LoadAsync();}catch(Exception e){Status=e.Message;}}
}

public partial class StockViewModel(ICatalogService catalog, IStockService stock,IInventoryService inventory) : ObservableObject, ILoadable
{
    public ObservableCollection<ProductVariant> Items { get; } = []; [ObservableProperty] private ProductVariant? selected; [ObservableProperty] private string quantity = string.Empty; [ObservableProperty] private string reason = string.Empty; [ObservableProperty] private string managerPin = string.Empty; [ObservableProperty] private string status = string.Empty;
    public ObservableCollection<StockHistoryRow>History{get;}=[];[ObservableProperty]private string countedQuantity="";
    public async Task LoadAsync() { var rows = await catalog.SearchAsync(null); Items.Clear(); foreach (var row in rows) Items.Add(row);History.Clear();foreach(var h in await inventory.HistoryAsync(Selected?.Id))History.Add(h); }
    [RelayCommand] private async Task Receive() { if (Selected is null) return; try { await stock.AdjustAsync(new StockAdjustment(Selected.Id, decimal.Parse(Quantity), StockMovementType.Receipt, Selected.CostXof, Reason, "Responsable")); Status = "Réception enregistrée"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task Adjust() { if (Selected is null) return; try { await stock.AdjustAsync(new StockAdjustment(Selected.Id, decimal.Parse(Quantity), StockMovementType.Adjustment, Selected.CostXof, Reason, "Responsable"), ManagerPin); Status = "Ajustement enregistré"; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand]private async Task ApplyInventory(){if(Selected is null)return;try{await inventory.ApplyCountAsync([new InventoryCount(Selected.Id,decimal.Parse(CountedQuantity))],Reason,ManagerPin);Status="Inventaire validé";await LoadAsync();}catch(Exception e){Status=e.Message;}}
}

public partial class CustomersViewModel(ICustomerService customers,ICreditService credits) : ObservableObject, ILoadable
{
    public ObservableCollection<Customer> Items { get; } = []; [ObservableProperty] private string name = string.Empty; [ObservableProperty] private string phone = string.Empty; [ObservableProperty] private string creditLimit = "0"; [ObservableProperty] private string status = string.Empty;
    public ObservableCollection<CreditSummary> Credits{get;}=[];public IReadOnlyList<PaymentMode>PaymentModes{get;}=Enum.GetValues<PaymentMode>().Where(x=>x!=PaymentMode.Credit).ToArray();[ObservableProperty]private CreditSummary?selectedCredit;[ObservableProperty]private string paymentAmount="";[ObservableProperty]private PaymentMode paymentMode=PaymentMode.Cash;[ObservableProperty]private string paymentReference="";[ObservableProperty]private string reversalPaymentId="";[ObservableProperty]private string managerPin="";
    public async Task LoadAsync() { var rows = await customers.SearchAsync(null); Items.Clear(); foreach (var row in rows) Items.Add(row);Credits.Clear();foreach(var row in await credits.ListAsync())Credits.Add(row); }
    [RelayCommand] private async Task Create() { try { await customers.CreateAsync(Name, Phone, long.Parse(CreditLimit)); Status = "Client ajouté"; Name = Phone = string.Empty; await LoadAsync(); } catch (Exception e) { Status = e.Message; } }
    [RelayCommand]private async Task PayCredit(){if(SelectedCredit is null)return;try{var r=await credits.PayAsync(SelectedCredit.Id,long.Parse(PaymentAmount),PaymentMode,PaymentReference);Status=$"Reçu {r.Number} • Solde {r.NewBalanceXof:N0}";await LoadAsync();}catch(Exception e){Status=e.Message;}}
    [RelayCommand]private async Task ReverseCredit(){try{var r=await credits.ReverseAsync(Guid.Parse(ReversalPaymentId),"Erreur de saisie",ManagerPin);Status=$"Contre-écriture {r.Number}";await LoadAsync();}catch(Exception e){Status=e.Message;}}
}

public partial class ExpensesViewModel(IExpenseService expenses) : ObservableObject, ILoadable
{
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = Enum.GetValues<PaymentMode>(); [ObservableProperty] private string category = "Autres"; [ObservableProperty] private string description = string.Empty; [ObservableProperty] private string amount = string.Empty; [ObservableProperty] private PaymentMode selectedMode = PaymentMode.Cash; [ObservableProperty] private string status = string.Empty;
    public Task LoadAsync() => Task.CompletedTask;
    [RelayCommand] private async Task Create() { try { await expenses.CreateAsync(Category, Description, long.Parse(Amount), SelectedMode); Status = "Dépense enregistrée"; Description = Amount = string.Empty; } catch (Exception e) { Status = e.Message; } }
}

public partial class DocumentsViewModel(IDocumentService documents,IReturnService returns,IThermalPrinterService printers,IAppSettingsService settings):ObservableObject,ILoadable
{
    public ObservableCollection<DocumentListItem>Items{get;}=[];public IReadOnlyList<PaymentMode>PaymentModes{get;}=Enum.GetValues<PaymentMode>().Where(x=>x!=PaymentMode.Credit).ToArray();[ObservableProperty]private DocumentListItem?selectedDocument;[ObservableProperty]private string saleNumber="";[ObservableProperty]private string returnedSku="";[ObservableProperty]private string returnedQuantity="1";[ObservableProperty]private string replacementSku="";[ObservableProperty]private string replacementQuantity="1";[ObservableProperty]private string exchangePaymentAmount="0";[ObservableProperty]private PaymentMode exchangePaymentMode=PaymentMode.Cash;[ObservableProperty]private string exchangePaymentReference="";[ObservableProperty]private string reason="";[ObservableProperty]private string managerPin="";[ObservableProperty]private string proformaDescription="Article";[ObservableProperty]private string proformaTotal="0";[ObservableProperty]private string status="";private PrinterProfile?printer;
    public async Task LoadAsync(){Items.Clear();foreach(var x in await documents.ListAsync())Items.Add(x);var saved=await settings.GetAsync("Printer.Selected");printer=saved is null?printers.Discover().FirstOrDefault():JsonSerializer.Deserialize<PrinterProfile>(saved);}
    [RelayCommand]private async Task Duplicate(){if(SelectedDocument is null||printer is null)return;try{var receipt=await documents.GetReceiptAsync(SelectedDocument.Id,true);await printers.PrintReceiptAsync(printer,receipt);await documents.MarkPrintedAsync(SelectedDocument.Id);Status="Duplicata imprimé";await LoadAsync();}catch(Exception e){Status=e.Message;}}
    [RelayCommand]private async Task ReturnExchange(){try{var amount=long.Parse(ExchangePaymentAmount);IReadOnlyList<PaymentDraft>payments=amount>0?[new PaymentDraft(ExchangePaymentMode,amount,ExchangePaymentReference)]:[];var r=await returns.ReturnOrExchangeAsync(new ReturnRequest(SaleNumber,ReturnedSku,decimal.Parse(ReturnedQuantity),string.IsNullOrWhiteSpace(ReplacementSku)?null:ReplacementSku,decimal.Parse(ReplacementQuantity),payments,Reason,ManagerPin));Status=$"Avoir {r.CreditNoteNumber} • Différence {r.DifferenceXof:N0}";await LoadAsync();}catch(Exception e){Status=e.Message;}}
    [RelayCommand]private async Task CancelSale(){try{var r=await returns.CancelSaleAsync(SaleNumber,Reason,ManagerPin);Status=$"Vente annulée • {r.CreditNoteNumber}";await LoadAsync();}catch(Exception e){Status=e.Message;}}
    [RelayCommand]private async Task Proforma(){try{var total=long.Parse(ProformaTotal);var data=new ReceiptData("Ma Boutique",null,null,"",DateTimeOffset.Now,null,[new ReceiptItem(ProformaDescription,1,total,0,total)],total,0,total,[],"Proforma sans encaissement");var d=await documents.CreateProformaAsync(data);Status=$"Proforma {d.Number} créée";await LoadAsync();}catch(Exception e){Status=e.Message;}}
}

public partial class ReportsViewModel(IReportService reports) : ObservableObject, ILoadable
{
    public ObservableCollection<ReportRow> PaymentRows { get; } = []; [ObservableProperty] private DashboardSummary summary = new(0, 0, 0, 0, 0, 0);
    public async Task LoadAsync() { var to = DateTimeOffset.Now; var from = to.AddDays(-30); Summary = await reports.DashboardAsync(from, to); PaymentRows.Clear(); foreach (var row in await reports.SalesByPaymentModeAsync(from, to)) PaymentRows.Add(row); }
}

public partial class SettingsViewModel(IAuthorizationService authorization, IAppSettingsService settings, IBackupService backup, IThermalPrinterService printer) : ObservableObject, ILoadable
{
    public IReadOnlyList<PrinterProfile> Printers { get; } = printer.Discover(); [ObservableProperty] private PrinterProfile? selectedPrinter; [ObservableProperty] private string shopName = string.Empty; [ObservableProperty] private string pin = string.Empty; [ObservableProperty] private string status = string.Empty;
    [ObservableProperty]private string address="";[ObservableProperty]private string phone="";[ObservableProperty]private string email="";[ObservableProperty]private string taxId="";[ObservableProperty]private string slogan="";[ObservableProperty]private string footer="Merci de votre visite";[ObservableProperty]private string returnPolicy="Échange ou avoir sous 7 jours";[ObservableProperty]private string logoPath="";[ObservableProperty]private string stampPath="";[ObservableProperty]private string signaturePath="";
    public async Task LoadAsync() { ShopName = await settings.GetAsync("Shop.Name") ?? "Ma Boutique";Address=await settings.GetAsync("Shop.Address")??"";Phone=await settings.GetAsync("Shop.Phone")??"";Email=await settings.GetAsync("Shop.Email")??"";TaxId=await settings.GetAsync("Shop.TaxId")??"";Slogan=await settings.GetAsync("Shop.Slogan")??"";Footer=await settings.GetAsync("Shop.Footer")??Footer;ReturnPolicy=await settings.GetAsync("Shop.ReturnPolicy")??ReturnPolicy;LogoPath=await settings.GetAsync("Shop.Logo")??"";StampPath=await settings.GetAsync("Shop.Stamp")??"";SignaturePath=await settings.GetAsync("Shop.Signature")??"";var saved=await settings.GetAsync("Printer.Selected");SelectedPrinter=saved is null?Printers.FirstOrDefault():JsonSerializer.Deserialize<PrinterProfile>(saved); }
    [RelayCommand] private async Task Save() { try { if (!await authorization.IsConfiguredAsync()) await authorization.ConfigurePinAsync(Pin); else if (!await authorization.AuthorizeSensitiveActionAsync(Pin, "Modifier paramètres")) throw new UnauthorizedAccessException("PIN responsable invalide.");var values=new Dictionary<string,string>{{"Shop.Name",ShopName},{"Shop.Address",Address},{"Shop.Phone",Phone},{"Shop.Email",Email},{"Shop.TaxId",TaxId},{"Shop.Slogan",Slogan},{"Shop.Footer",Footer},{"Shop.ReturnPolicy",ReturnPolicy},{"Shop.Logo",LogoPath},{"Shop.Stamp",StampPath},{"Shop.Signature",SignaturePath}};foreach(var pair in values)await settings.SetAsync(pair.Key,pair.Value);if(SelectedPrinter is not null)await settings.SetAsync("Printer.Selected",JsonSerializer.Serialize(SelectedPrinter)); Status = "Paramètres enregistrés"; } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task Backup() { try { Status = $"Sauvegarde: {await backup.CreateAsync()}"; } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task TestPrinter() { if (SelectedPrinter is null) return; try { await printer.PrintTestAsync(SelectedPrinter); Status = "Test envoyé"; } catch (Exception e) { Status = e.Message; } }
}
