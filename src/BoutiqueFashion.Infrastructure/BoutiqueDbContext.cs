using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BoutiqueFashion.Infrastructure;

public sealed class BoutiqueDbContext(DbContextOptions<BoutiqueDbContext> options) : DbContext(options)
{
    // Le provider SQLite d'EF Core ne traduit ni les comparaisons, ni les tris,
    // ni les agrégats sur DateTimeOffset ; stocker les ticks UTC (INTEGER) lève ces limites.
    private sealed class DateTimeOffsetToUtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));

    private sealed class NullableDateTimeOffsetToUtcTicksConverter() : ValueConverter<DateTimeOffset?, long?>(
        v => v.HasValue ? v.Value.UtcTicks : null,
        v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<NullableDateTimeOffsetToUtcTicksConverter>();
    }

    /// <summary>
    /// Tout mouvement de stock est mis en file de synchronisation, quelle que soit son origine.
    ///
    /// Interception plutôt qu'appel explicite dans chaque service : les mouvements naissent dans
    /// six endroits — vente, réception, ajustement, inventaire, retour, remise d'avance — et il
    /// suffirait d'en oublier un pour que le stock affiché à distance dérive silencieusement.
    /// C'est aussi la garantie que la ligne de file tombe dans la même transaction.
    /// </summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var added = ChangeTracker.Entries<StockMovement>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .ToList();
        foreach (var movement in added) Outbox.Enqueue(this, Contracts.SyncEntityTypes.StockMovement, movement.Id, Outbox.From(movement));
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CustomerCredit> CustomerCredits => Set<CustomerCredit>();
    public DbSet<CreditPayment> CreditPayments => Set<CreditPayment>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<DocumentSnapshot> DocumentSnapshots => Set<DocumentSnapshot>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<SyncOutboxEntry> SyncOutbox => Set<SyncOutboxEntry>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<ProductVariant>().HasIndex(x => x.Sku).IsUnique();
        modelBuilder.Entity<ProductVariant>().HasIndex(x => x.Barcode).IsUnique().HasFilter("Barcode IS NOT NULL");
        modelBuilder.Entity<Customer>().HasIndex(x => x.Phone).IsUnique().HasFilter("Phone IS NOT NULL");
        modelBuilder.Entity<Sale>().HasIndex(x => x.IdempotencyKey).IsUnique();
        modelBuilder.Entity<Sale>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<DocumentSnapshot>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<PrintJob>().HasIndex(x => x.IdempotencyKey).IsUnique();
        modelBuilder.Entity<AppSetting>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<DocumentSequence>().HasIndex(x => new { x.Type, x.Year }).IsUnique();
        modelBuilder.Entity<CashSession>().HasIndex(x => x.Status);
        modelBuilder.Entity<CashMovement>().HasIndex(x => x.CashSessionId);
        // L'agent de synchronisation ne lit que les lignes non envoyées, dans l'ordre d'écriture.
        modelBuilder.Entity<SyncOutboxEntry>().HasIndex(x => new { x.SentAt, x.CreatedAt });
        modelBuilder.Entity<Order>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Order>().HasIndex(x => x.Status);
        modelBuilder.Entity<Order>().HasMany(x => x.Lines).WithOne(x => x.Order).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OrderLine>().Property(x => x.Quantity).HasPrecision(18, 3);
        modelBuilder.Entity<CustomerCredit>().HasIndex(x => new { x.Status, x.DueAt });
        modelBuilder.Entity<StockMovement>().HasIndex(x => new { x.VariantId, x.CreatedAt });
        modelBuilder.Entity<Sale>().HasMany(x => x.Lines).WithOne(x => x.Sale).HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Sale>().HasMany(x => x.Payments).WithOne(x => x.Sale).HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductVariant>().Property(x => x.QuantityOnHand).HasPrecision(18, 3);
        modelBuilder.Entity<ProductVariant>().Property(x => x.WeightedAverageCostXof).HasPrecision(18, 2);
        modelBuilder.Entity<ProductVariant>().Property(x => x.LowStockThreshold).HasPrecision(18, 3);
        modelBuilder.Entity<StockMovement>().Property(x => x.QuantityDelta).HasPrecision(18, 3);
        modelBuilder.Entity<SaleLine>().Property(x => x.Quantity).HasPrecision(18, 3);
    }
}

