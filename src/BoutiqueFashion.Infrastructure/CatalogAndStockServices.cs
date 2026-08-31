using System.Text.Json;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

public sealed class CatalogService(IDbContextFactory<BoutiqueDbContext> factory, IAuthorizationService authorization, AppPaths paths) : ICatalogService
{
    public async Task<IReadOnlyList<ProductVariant>> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var variants = db.ProductVariants.AsNoTracking().Include(x => x.Product).ThenInclude(x => x!.Category).Include(x => x.Product!.Images).Include(x => x.Images).Where(x => x.IsActive && x.Product!.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            variants = variants.Where(x => x.Sku.Contains(term) || (x.Barcode != null && x.Barcode.Contains(term)) || x.Product!.Name.Contains(term));
        }
        return await variants.OrderBy(x => x.Product!.Name).ThenBy(x => x.Size).Take(250).ToListAsync(cancellationToken);
    }

    public async Task<ProductVariant> CreateVariantAsync(string productName, string categoryName, string sku, string? barcode, string? size, string? color, long costXof, long priceXof, decimal initialQuantity, decimal alertThreshold, CancellationToken cancellationToken = default, string? subCategory = null, string? gender = null, string? season = null, string? material = null, string? location = null, string? supplier = null, ProductType type = ProductType.Clothing, string? description = null, string? photoPath = null, string? managerPin = null)
    {
        if (string.IsNullOrWhiteSpace(productName) || string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("Le produit et le SKU sont obligatoires.");
        if (costXof < 0 || priceXof < 0) throw new ArgumentOutOfRangeException(nameof(priceXof));
        if (priceXof < costXof && (managerPin is null || !await authorization.AuthorizeSensitiveActionAsync(managerPin, "Prix de vente inférieur au coût", cancellationToken: cancellationToken)))
            throw new UnauthorizedAccessException("Prix inférieur au coût d'achat : PIN responsable requis.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.ProductVariants.AnyAsync(x => x.Sku == sku || (barcode != null && x.Barcode == barcode), cancellationToken))
            throw new InvalidOperationException("Ce SKU ou code-barres existe déjà.");
        var category = await db.Categories.SingleOrDefaultAsync(x => x.Name == categoryName, cancellationToken) ?? new Category { Name = categoryName.Trim() };
        if (category.Id == Guid.Empty) category.Id = Guid.NewGuid();
        var product = await db.Products.SingleOrDefaultAsync(x => x.Name == productName && x.CategoryId == category.Id, cancellationToken);
        if (product is null)
        {
            product = new Product { Name = productName.Trim(), Category = category, CategoryId = category.Id, SubCategory = subCategory, Gender = gender, Season = season, Type = type, Description = description };
            db.Products.Add(product);
        }
        else
        {
            product.SubCategory ??= subCategory; product.Gender ??= gender; product.Season ??= season; product.Type = type; product.Description ??= description;
        }
        var variant = new ProductVariant { Product = product, ProductId = product.Id, Sku = sku.Trim(), Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim(), Size = size, Color = color, Material = material, Location = location, Supplier = supplier, CostXof = costXof, PriceXof = priceXof, QuantityOnHand = initialQuantity, WeightedAverageCostXof = costXof, LowStockThreshold = alertThreshold };
        db.ProductVariants.Add(variant);
        if (!string.IsNullOrWhiteSpace(photoPath) && File.Exists(photoPath))
        {
            var extension = Path.GetExtension(photoPath);
            var destination = Path.Combine(paths.Assets, $"variant-{variant.Id:N}-{Guid.NewGuid():N}{extension}");
            File.Copy(photoPath, destination);
            db.ProductImages.Add(new ProductImage { ProductId = product.Id, VariantId = variant.Id, RelativePath = destination, IsPrimary = true });
        }
        if (initialQuantity != 0)
            db.StockMovements.Add(new StockMovement { Variant = variant, VariantId = variant.Id, Type = StockMovementType.Inventory, QuantityDelta = initialQuantity, UnitCostXof = costXof, Reason = "Stock initial", SourceType = "InitialInventory", Actor = "Responsable" });
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Créer variante", EntityType = nameof(ProductVariant), EntityId = variant.Id.ToString(), AfterJson = JsonSerializer.Serialize(new { sku, productName }) });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return variant;
    }

    public async Task<IReadOnlyList<ProductVariant>> CreateMatrixAsync(MatrixDraft draft, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(draft.ProductName) || string.IsNullOrWhiteSpace(draft.SkuPrefix)) throw new ArgumentException("Le produit et le préfixe SKU sont obligatoires.");
        if (draft.Colors.Count == 0 || draft.Sizes.Count == 0) throw new ArgumentException("La matrice requiert au moins une couleur et une taille.");
        if (draft.PriceXof < draft.CostXof && (draft.ManagerPin is null || !await authorization.AuthorizeSensitiveActionAsync(draft.ManagerPin, "Prix de vente inférieur au coût", cancellationToken: cancellationToken)))
            throw new UnauthorizedAccessException("Prix inférieur au coût d'achat : PIN responsable requis.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var category = await db.Categories.SingleOrDefaultAsync(x => x.Name == draft.CategoryName, cancellationToken) ?? new Category { Name = draft.CategoryName.Trim() };
        if (category.Id == Guid.Empty) category.Id = Guid.NewGuid();
        var product = await db.Products.SingleOrDefaultAsync(x => x.Name == draft.ProductName && x.CategoryId == category.Id, cancellationToken);
        if (product is null)
        {
            product = new Product { Name = draft.ProductName.Trim(), Brand = draft.Brand, Category = category, CategoryId = category.Id, SubCategory = draft.SubCategory, Gender = draft.Gender, Season = draft.Season, Type = draft.Type };
            db.Products.Add(product);
        }
        else
        {
            product.Type = draft.Type;
        }
        var created = new List<ProductVariant>();
        foreach (var color in draft.Colors)
        {
            foreach (var size in draft.Sizes)
            {
                var sku = $"{draft.SkuPrefix.Trim()}-{Code(color)}-{Code(size)}";
                if (created.Any(x => x.Sku == sku)) throw new InvalidOperationException($"SKU dupliqué dans la matrice : {sku}.");
                if (await db.ProductVariants.AnyAsync(x => x.Sku == sku, cancellationToken)) throw new InvalidOperationException($"Ce SKU existe déjà : {sku}.");
                var variant = new ProductVariant { Product = product, ProductId = product.Id, Sku = sku, Size = size, Color = color, Material = draft.Material, Supplier = draft.Supplier, CostXof = draft.CostXof, PriceXof = draft.PriceXof, QuantityOnHand = draft.InitialQuantity, WeightedAverageCostXof = draft.CostXof, LowStockThreshold = draft.AlertThreshold };
                db.ProductVariants.Add(variant);
                if (draft.InitialQuantity != 0)
                    db.StockMovements.Add(new StockMovement { Variant = variant, VariantId = variant.Id, Type = StockMovementType.Inventory, QuantityDelta = draft.InitialQuantity, UnitCostXof = draft.CostXof, Reason = "Stock initial matrice", SourceType = "InitialInventory", Actor = "Responsable" });
                created.Add(variant);
            }
        }
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Créer matrice", EntityType = nameof(ProductVariant), EntityId = product.Id.ToString(), AfterJson = JsonSerializer.Serialize(new { draft.ProductName, variantes = created.Select(x => x.Sku) }) });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    private static string Code(string value)
    {
        var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return cleaned.Length == 0 ? "X" : cleaned.Length <= 3 ? cleaned : cleaned[..3];
    }

    public async Task DeleteVariantAsync(Guid variantId, string managerPin, CancellationToken cancellationToken = default)
    {
        if (!await authorization.AuthorizeSensitiveActionAsync(managerPin, "Supprimer variante", cancellationToken: cancellationToken)) throw new UnauthorizedAccessException("PIN responsable invalide.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var variant = await db.ProductVariants.SingleOrDefaultAsync(x => x.Id == variantId, cancellationToken) ?? throw new KeyNotFoundException("Variante introuvable.");
        var hasHistory = await db.StockMovements.AnyAsync(x => x.VariantId == variantId, cancellationToken)
            || await db.SaleLines.AnyAsync(x => x.VariantId == variantId, cancellationToken);
        if (hasHistory) throw new InvalidOperationException("Suppression impossible : la variante a un historique. Utilisez « Archiver ».");
        db.ProductImages.RemoveRange(db.ProductImages.Where(x => x.VariantId == variantId));
        db.ProductVariants.Remove(variant);
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Supprimer variante", EntityType = nameof(ProductVariant), EntityId = variant.Id.ToString(), BeforeJson = JsonSerializer.Serialize(new { variant.Sku }) });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Categories.AsNoTracking().OrderBy(x => x.Name).Select(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<ProductVariant> UpdateVariantAsync(ProductUpdate update, string managerPin, CancellationToken cancellationToken = default)
    {
        if (!await authorization.AuthorizeSensitiveActionAsync(managerPin, "Modifier produit", cancellationToken: cancellationToken)) throw new UnauthorizedAccessException("PIN responsable invalide.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken); await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var variant = await db.ProductVariants.Include(x => x.Product).ThenInclude(x => x!.Category).SingleOrDefaultAsync(x => x.Id == update.VariantId, cancellationToken) ?? throw new KeyNotFoundException("Variante introuvable.");
        if (await db.ProductVariants.AnyAsync(x => x.Id != variant.Id && (x.Sku == update.Sku || (update.Barcode != null && x.Barcode == update.Barcode)), cancellationToken)) throw new InvalidOperationException("SKU ou code-barres déjà utilisé.");
        var before = JsonSerializer.Serialize(new { variant.Product!.Name, variant.Sku, variant.Barcode, variant.Size, variant.Color, variant.CostXof, variant.PriceXof, variant.IsActive, variant.Location, variant.Supplier });
        var category = await db.Categories.SingleOrDefaultAsync(x => x.Name == update.Category, cancellationToken) ?? new Category { Name = update.Category };
        variant.Product!.Name = update.ProductName; variant.Product.Category = category; variant.Product.CategoryId = category.Id;
        variant.Product.SubCategory = update.SubCategory; variant.Product.Gender = update.Gender; variant.Product.Season = update.Season; variant.Product.Type = update.Type;
        if (update.Description is not null) variant.Product.Description = update.Description;
        variant.Sku = update.Sku; variant.Barcode = update.Barcode; variant.Size = update.Size; variant.Color = update.Color; variant.Material = update.Material; variant.Location = update.Location; variant.Supplier = update.Supplier; variant.CostXof = update.CostXof; variant.PriceXof = update.PriceXof; variant.PromotionalPriceXof = update.PromotionalPriceXof; variant.PromotionStartsAt = update.PromotionStartsAt; variant.PromotionEndsAt = update.PromotionEndsAt; variant.LowStockThreshold = update.AlertThreshold; variant.IsActive = update.IsActive; variant.UpdatedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(update.PhotoPath) && File.Exists(update.PhotoPath)) { var extension = Path.GetExtension(update.PhotoPath); var destination = Path.Combine(paths.Assets, $"variant-{variant.Id:N}-{Guid.NewGuid():N}{extension}"); File.Copy(update.PhotoPath, destination); db.ProductImages.Add(new ProductImage { ProductId = variant.ProductId, VariantId = variant.Id, RelativePath = destination, IsPrimary = true }); }
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = update.IsActive ? "Modifier variante" : "Archiver variante", EntityType = nameof(ProductVariant), EntityId = variant.Id.ToString(), BeforeJson = before, AfterJson = JsonSerializer.Serialize(new { update.ProductName, update.Sku, update.Barcode, update.Size, update.Color, update.CostXof, update.PriceXof, update.IsActive, update.Location, update.Supplier }) });
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return variant;
    }
}

public sealed class StockService(IDbContextFactory<BoutiqueDbContext> factory, IAuthorizationService authorization) : IStockService
{
    public async Task AdjustAsync(StockAdjustment adjustment, string? managerPin = null, CancellationToken cancellationToken = default)
    {
        if (adjustment.QuantityDelta == 0) throw new InvalidOperationException("La quantité ne peut pas être nulle.");
        if (adjustment.Type is StockMovementType.Adjustment or StockMovementType.Damaged or StockMovementType.Lost &&
            (managerPin is null || !await authorization.AuthorizeSensitiveActionAsync(managerPin, "Ajustement de stock", adjustment.Actor, cancellationToken)))
            throw new UnauthorizedAccessException("PIN responsable requis.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var variant = await db.ProductVariants.SingleOrDefaultAsync(x => x.Id == adjustment.VariantId, cancellationToken) ?? throw new KeyNotFoundException("Variante introuvable.");
        if (adjustment.Type == StockMovementType.Receipt && adjustment.QuantityDelta > 0)
            variant.WeightedAverageCostXof = BusinessRules.NewWeightedAverageCost(variant.QuantityOnHand, variant.WeightedAverageCostXof, adjustment.QuantityDelta, adjustment.UnitCostXof);
        var before = variant.QuantityOnHand;
        variant.QuantityOnHand += adjustment.QuantityDelta;
        variant.CostXof = decimal.ToInt64(decimal.Round(variant.WeightedAverageCostXof, 0));
        variant.UpdatedAt = DateTimeOffset.UtcNow;
        db.StockMovements.Add(new StockMovement { VariantId = variant.Id, Type = adjustment.Type, QuantityDelta = adjustment.QuantityDelta, UnitCostXof = adjustment.UnitCostXof, Reason = adjustment.Reason, SourceType = "Manual", Actor = adjustment.Actor });
        db.AuditEntries.Add(new AuditEntry { Actor = adjustment.Actor, Action = $"Mouvement {adjustment.Type}", EntityType = nameof(ProductVariant), EntityId = variant.Id.ToString(), BeforeJson = JsonSerializer.Serialize(new { quantity = before }), AfterJson = JsonSerializer.Serialize(new { quantity = variant.QuantityOnHand, adjustment.Reason }) });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class CustomerService(IDbContextFactory<BoutiqueDbContext> factory, IAuthorizationService authorization) : ICustomerService
{
    public async Task<IReadOnlyList<CustomerRow>> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var customers = db.Customers.AsNoTracking().Where(x => !x.IsArchived);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            customers = customers.Where(x => x.Name.Contains(term) || (x.Phone != null && x.Phone.Contains(term)) || (x.SecondaryPhone != null && x.SecondaryPhone.Contains(term)));
        }
        var list = await customers.OrderBy(x => x.Name).Take(250).ToListAsync(cancellationToken);
        var ids = list.Select(x => x.Id).ToArray();
        var now = DateTimeOffset.UtcNow;
        var yearAgo = now.AddDays(-365);
        var saleStats = await db.Sales.AsNoTracking()
            .Where(s => s.CustomerId != null && ids.Contains(s.CustomerId.Value) && s.Status == SaleStatus.Completed)
            .GroupBy(s => s.CustomerId!.Value)
            .Select(g => new { Id = g.Key, Last = (DateTimeOffset?)g.Max(x => x.CreatedAt), CountYear = g.Count(x => x.CreatedAt >= yearAgo), RevenueYear = g.Where(x => x.CreatedAt >= yearAgo).Sum(x => x.TotalXof) })
            .ToListAsync(cancellationToken);
        var balances = await db.CustomerCredits.AsNoTracking()
            .Where(c => ids.Contains(c.CustomerId) && c.Status != CreditStatus.Paid && c.Status != CreditStatus.Cancelled)
            .GroupBy(c => c.CustomerId)
            .Select(g => new { Id = g.Key, Balance = g.Sum(x => x.BalanceXof) })
            .ToListAsync(cancellationToken);
        var thresholds = await ReadThresholdsAsync(db, cancellationToken);
        return list.Select(x =>
        {
            var stats = saleStats.FirstOrDefault(s => s.Id == x.Id);
            var balance = balances.FirstOrDefault(b => b.Id == x.Id)?.Balance ?? 0;
            var segment = BusinessRules.ComputeSegment(now, x.CreatedAt, stats?.Last, stats?.CountYear ?? 0, stats?.RevenueYear ?? 0, balance, thresholds);
            return new CustomerRow(x.Id, x.Name, x.Phone, segment, balance);
        }).ToArray();
    }

    public async Task<Customer?> GetAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, cancellationToken);
    }

    public async Task<Customer> CreateAsync(string name, string? phone, long creditLimitXof, CancellationToken cancellationToken = default, string? gender = null, string? preferences = null, string? channel = null, bool marketingConsent = false)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Le nom est obligatoire.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(phone) && await db.Customers.AnyAsync(x => x.Phone == phone, cancellationToken)) throw new InvalidOperationException("Ce téléphone est déjà utilisé.");
        var customer = new Customer { Name = name.Trim(), Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(), CreditLimitXof = creditLimitXof, Gender = gender, Preferences = preferences, PreferredChannel = channel, MarketingConsent = marketingConsent, ConsentDate = marketingConsent ? DateTimeOffset.UtcNow : null };
        db.Customers.Add(customer);
        db.AuditEntries.Add(new AuditEntry { Actor = "Vendeur boutique", Action = "Créer client", EntityType = nameof(Customer), EntityId = customer.Id.ToString(), AfterJson = JsonSerializer.Serialize(new { name, phone }) });
        await db.SaveChangesAsync(cancellationToken);
        return customer;
    }

    public async Task<Customer> UpdateAsync(CustomerUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == request.Id, cancellationToken) ?? throw new KeyNotFoundException("Client introuvable.");
        if (!string.IsNullOrWhiteSpace(request.Phone) && await db.Customers.AnyAsync(x => x.Id != request.Id && x.Phone == request.Phone, cancellationToken)) throw new InvalidOperationException("Ce téléphone est déjà utilisé.");
        var before = JsonSerializer.Serialize(new { customer.Name, customer.Phone, customer.Preferences, customer.MarketingConsent });
        customer.Name = request.Name.Trim();
        customer.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        customer.SecondaryPhone = string.IsNullOrWhiteSpace(request.SecondaryPhone) ? null : request.SecondaryPhone.Trim();
        customer.Gender = request.Gender;
        customer.Address = request.Address;
        customer.Notes = request.Notes;
        customer.Preferences = request.Preferences;
        customer.PreferredChannel = request.PreferredChannel;
        if (request.MarketingConsent && !customer.MarketingConsent) customer.ConsentDate = DateTimeOffset.UtcNow;
        if (!request.MarketingConsent) customer.ConsentDate = null;
        customer.MarketingConsent = request.MarketingConsent;
        customer.CreditLimitXof = request.CreditLimitXof;
        customer.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntry { Actor = "Vendeur boutique", Action = "Compléter client", EntityType = nameof(Customer), EntityId = customer.Id.ToString(), BeforeJson = before, AfterJson = JsonSerializer.Serialize(new { customer.Name, customer.Phone, customer.Preferences, customer.MarketingConsent }) });
        await db.SaveChangesAsync(cancellationToken);
        return customer;
    }

    public async Task ArchiveAsync(Guid customerId, string managerPin, CancellationToken cancellationToken = default)
    {
        if (!await authorization.AuthorizeSensitiveActionAsync(managerPin, "Archiver client", cancellationToken: cancellationToken)) throw new UnauthorizedAccessException("PIN responsable invalide.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == customerId, cancellationToken) ?? throw new KeyNotFoundException("Client introuvable.");
        var outstanding = await db.CustomerCredits.Where(c => c.CustomerId == customerId && c.Status != CreditStatus.Paid && c.Status != CreditStatus.Cancelled).SumAsync(c => c.BalanceXof, cancellationToken);
        if (outstanding > 0) throw new InvalidOperationException("Archivage impossible : le client a un solde de crédit en cours.");
        customer.IsArchived = true; customer.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Archiver client", EntityType = nameof(Customer), EntityId = customer.Id.ToString(), BeforeJson = JsonSerializer.Serialize(new { customer.Name, customer.Phone }) });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid customerId, string managerPin, CancellationToken cancellationToken = default)
    {
        if (!await authorization.AuthorizeSensitiveActionAsync(managerPin, "Supprimer client", cancellationToken: cancellationToken)) throw new UnauthorizedAccessException("PIN responsable invalide.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == customerId, cancellationToken) ?? throw new KeyNotFoundException("Client introuvable.");
        var hasHistory = await db.Sales.AnyAsync(s => s.CustomerId == customerId, cancellationToken) || await db.CustomerCredits.AnyAsync(c => c.CustomerId == customerId, cancellationToken);
        if (hasHistory) throw new InvalidOperationException("Suppression impossible : le client a un historique. Utilisez « Archiver ».");
        db.Customers.Remove(customer);
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Supprimer client", EntityType = nameof(Customer), EntityId = customer.Id.ToString(), BeforeJson = JsonSerializer.Serialize(new { customer.Name, customer.Phone }) });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CustomerHistory> HistoryAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var saleRows = await db.Sales.AsNoTracking().Where(s => s.CustomerId == customerId)
            .Select(s => new CustomerHistorySale(s.Number, s.CreatedAt, s.TotalXof, s.Status)).ToListAsync(cancellationToken);
        var sales = saleRows.OrderByDescending(s => s.Date).Take(100).ToArray();
        var paymentRows = await (from p in db.CreditPayments
                                 join c in db.CustomerCredits on p.CustomerCreditId equals c.Id
                                 join s in db.Sales on c.SaleId equals s.Id
                                 where c.CustomerId == customerId
                                 select new CustomerHistoryPayment(p.Number, p.CreatedAt, p.AmountXof, p.Mode, s.Number)).ToListAsync(cancellationToken);
        var payments = paymentRows.OrderByDescending(p => p.Date).Take(100).ToArray();
        return new CustomerHistory(sales, payments);
    }

    internal static async Task<LoyaltyThresholds> ReadThresholdsAsync(BoutiqueDbContext db, CancellationToken cancellationToken)
    {
        async Task<long> Long(string key, long fallback) => long.TryParse(await db.AppSettings.Where(x => x.Key == key).Select(x => x.Value).SingleOrDefaultAsync(cancellationToken), out var v) ? v : fallback;
        async Task<int> Int(string key, int fallback) => int.TryParse(await db.AppSettings.Where(x => x.Key == key).Select(x => x.Value).SingleOrDefaultAsync(cancellationToken), out var v) ? v : fallback;
        return new LoyaltyThresholds(
            await Long("Loyalty.VipRevenueXof", 500_000),
            await Int("Loyalty.LoyalPurchases", 5),
            await Int("Loyalty.InactiveDays", 90),
            await Int("Loyalty.NewDays", 30));
    }
}

public sealed class ExpenseService(IDbContextFactory<BoutiqueDbContext> factory, IAuthorizationService authorization) : IExpenseService
{
    public async Task<Expense> CreateAsync(string category, string description, long amountXof, PaymentMode mode, CancellationToken cancellationToken = default)
    {
        if (amountXof <= 0) throw new ArgumentOutOfRangeException(nameof(amountXof));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var expense = new Expense { Category = category.Trim(), Description = description.Trim(), AmountXof = amountXof, Mode = mode };
        db.Expenses.Add(expense);
        db.AuditEntries.Add(new AuditEntry { Actor = "Vendeur boutique", Action = "Créer dépense", EntityType = nameof(Expense), EntityId = expense.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return expense;
    }

    public async Task DeleteAsync(Guid expenseId, string managerPin, CancellationToken cancellationToken = default)
    {
        if (!await authorization.AuthorizeSensitiveActionAsync(managerPin, "Supprimer dépense", cancellationToken: cancellationToken)) throw new UnauthorizedAccessException("PIN responsable invalide.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var expense = await db.Expenses.SingleOrDefaultAsync(x => x.Id == expenseId, cancellationToken) ?? throw new KeyNotFoundException("Dépense introuvable.");
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Supprimer dépense", EntityType = nameof(Expense), EntityId = expense.Id.ToString(), BeforeJson = JsonSerializer.Serialize(new { expense.Category, expense.AmountXof }) });
        db.Expenses.Remove(expense);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Expense>> ListRecentAsync(int count = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Expenses.AsNoTracking().ToListAsync(cancellationToken);
        return rows.OrderByDescending(x => x.CreatedAt).Take(count).ToArray();
    }
}
