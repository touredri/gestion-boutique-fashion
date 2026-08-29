using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

public sealed class CatalogService(IDbContextFactory<BoutiqueDbContext> factory, IAuthorizationService authorization, AppPaths paths) : ICatalogService
{
    public async Task<IReadOnlyList<ProductVariant>> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var variants = db.ProductVariants.AsNoTracking().Include(x => x.Product).ThenInclude(x => x!.Category).Include(x => x.Product!.Images).Where(x => x.IsActive && x.Product!.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            variants = variants.Where(x => x.Sku.Contains(term) || (x.Barcode != null && x.Barcode.Contains(term)) || x.Product!.Name.Contains(term));
        }
        return await variants.OrderBy(x => x.Product!.Name).ThenBy(x => x.Size).Take(250).ToListAsync(cancellationToken);
    }

    public async Task<ProductVariant> CreateVariantAsync(string productName, string categoryName, string sku, string? barcode, string? size, string? color, long costXof, long priceXof, decimal initialQuantity, decimal alertThreshold, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productName) || string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("Le produit et le SKU sont obligatoires.");
        if (costXof < 0 || priceXof < 0) throw new ArgumentOutOfRangeException(nameof(priceXof));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.ProductVariants.AnyAsync(x => x.Sku == sku || (barcode != null && x.Barcode == barcode), cancellationToken))
            throw new InvalidOperationException("Ce SKU ou code-barres existe déjà.");
        var category = await db.Categories.SingleOrDefaultAsync(x => x.Name == categoryName, cancellationToken) ?? new Category { Name = categoryName.Trim() };
        if (category.Id == Guid.Empty) category.Id = Guid.NewGuid();
        var product = await db.Products.SingleOrDefaultAsync(x => x.Name == productName && x.CategoryId == category.Id, cancellationToken);
        if (product is null)
        {
            product = new Product { Name = productName.Trim(), Category = category, CategoryId = category.Id };
            db.Products.Add(product);
        }
        var variant = new ProductVariant { Product = product, ProductId = product.Id, Sku = sku.Trim(), Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim(), Size = size, Color = color, CostXof = costXof, PriceXof = priceXof, QuantityOnHand = initialQuantity, WeightedAverageCostXof = costXof, LowStockThreshold = alertThreshold };
        db.ProductVariants.Add(variant);
        if (initialQuantity != 0)
            db.StockMovements.Add(new StockMovement { Variant = variant, VariantId = variant.Id, Type = StockMovementType.Inventory, QuantityDelta = initialQuantity, UnitCostXof = costXof, Reason = "Stock initial", SourceType = "InitialInventory", Actor = "Responsable" });
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Créer variante", EntityType = nameof(ProductVariant), EntityId = variant.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return variant;
    }
    public async Task<ProductVariant> UpdateVariantAsync(ProductUpdate update, string managerPin, CancellationToken cancellationToken = default)
    {
        if (!await authorization.AuthorizeSensitiveActionAsync(managerPin, "Modifier produit", cancellationToken: cancellationToken)) throw new UnauthorizedAccessException("PIN responsable invalide.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken); await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var variant = await db.ProductVariants.Include(x => x.Product).ThenInclude(x => x!.Category).SingleOrDefaultAsync(x => x.Id == update.VariantId, cancellationToken) ?? throw new KeyNotFoundException("Variante introuvable.");
        if (await db.ProductVariants.AnyAsync(x => x.Id != variant.Id && (x.Sku == update.Sku || (update.Barcode != null && x.Barcode == update.Barcode)), cancellationToken)) throw new InvalidOperationException("SKU ou code-barres déjà utilisé.");
        var category = await db.Categories.SingleOrDefaultAsync(x => x.Name == update.Category, cancellationToken) ?? new Category { Name = update.Category };
        variant.Product!.Name = update.ProductName; variant.Product.Category = category; variant.Product.CategoryId = category.Id;
        variant.Sku = update.Sku; variant.Barcode = update.Barcode; variant.Size = update.Size; variant.Color = update.Color; variant.CostXof = update.CostXof; variant.PriceXof = update.PriceXof; variant.PromotionalPriceXof = update.PromotionalPriceXof; variant.PromotionStartsAt = update.PromotionStartsAt; variant.PromotionEndsAt = update.PromotionEndsAt; variant.LowStockThreshold = update.AlertThreshold; variant.IsActive = update.IsActive; variant.UpdatedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(update.PhotoPath) && File.Exists(update.PhotoPath)) { var extension = Path.GetExtension(update.PhotoPath); var destination = Path.Combine(paths.Assets, $"product-{variant.ProductId:N}-{Guid.NewGuid():N}{extension}"); File.Copy(update.PhotoPath, destination); db.ProductImages.Add(new ProductImage { ProductId = variant.ProductId, RelativePath = destination, IsPrimary = true }); }
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = update.IsActive ? "Modifier variante" : "Archiver variante", EntityType = nameof(ProductVariant), EntityId = variant.Id.ToString() });
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
        variant.QuantityOnHand += adjustment.QuantityDelta;
        variant.CostXof = decimal.ToInt64(decimal.Round(variant.WeightedAverageCostXof, 0));
        variant.UpdatedAt = DateTimeOffset.UtcNow;
        db.StockMovements.Add(new StockMovement { VariantId = variant.Id, Type = adjustment.Type, QuantityDelta = adjustment.QuantityDelta, UnitCostXof = adjustment.UnitCostXof, Reason = adjustment.Reason, SourceType = "Manual", Actor = adjustment.Actor });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class CustomerService(IDbContextFactory<BoutiqueDbContext> factory) : ICustomerService
{
    public async Task<IReadOnlyList<Customer>> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var customers = db.Customers.AsNoTracking().Where(x => !x.IsArchived);
        if (!string.IsNullOrWhiteSpace(query)) customers = customers.Where(x => x.Name.Contains(query) || (x.Phone != null && x.Phone.Contains(query)));
        return await customers.OrderBy(x => x.Name).Take(250).ToListAsync(cancellationToken);
    }

    public async Task<Customer> CreateAsync(string name, string? phone, long creditLimitXof, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Le nom est obligatoire.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(phone) && await db.Customers.AnyAsync(x => x.Phone == phone, cancellationToken)) throw new InvalidOperationException("Ce téléphone est déjà utilisé.");
        var customer = new Customer { Name = name.Trim(), Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(), CreditLimitXof = creditLimitXof };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);
        return customer;
    }
}

public sealed class ExpenseService(IDbContextFactory<BoutiqueDbContext> factory) : IExpenseService
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
}
