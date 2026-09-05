using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Data;

public sealed class ServerDbContext(DbContextOptions<ServerDbContext> options) : DbContext(options)
{
    /// <summary>Séquence unique alimentant le curseur de descente. Un compteur central plutôt
    /// qu'un horodatage : deux écritures dans la même milliseconde donneraient le même curseur,
    /// et l'une des deux ne redescendrait jamais.</summary>
    public const string SyncSequence = "sync_seq";

    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<EnrollmentCode> EnrollmentCodes => Set<EnrollmentCode>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Variant> Variants => Set<Variant>();
    public DbSet<ShopSetting> ShopSettings => Set<ShopSetting>();
    public DbSet<ShopStock> ShopStocks => Set<ShopStock>();

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<Credit> Credits => Set<Credit>();
    public DbSet<CreditPayment> CreditPayments => Set<CreditPayment>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(SyncSequence).StartsAt(1).IncrementsBy(1);

        modelBuilder.Entity<User>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<UserSession>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<UserSession>().HasIndex(x => x.ExpiresAt);
        modelBuilder.Entity<PushSubscription>().HasIndex(x => x.Endpoint).IsUnique();
        modelBuilder.Entity<Device>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<Device>().HasIndex(x => x.ShopId);
        modelBuilder.Entity<EnrollmentCode>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<ProcessedEvent>().HasKey(x => x.Id);
        modelBuilder.Entity<ShopStock>().HasKey(x => new { x.ShopId, x.VariantId });

        // Chaque boutique a sa propre numérotation : deux terminaux produisent tous deux
        // « TIC-2026-0001 ». Une contrainte globale rejetterait le second pour toujours.
        modelBuilder.Entity<Sale>().HasIndex(x => new { x.ShopId, x.Number }).IsUnique();
        modelBuilder.Entity<Sale>().HasIndex(x => new { x.ShopId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<CashSession>().HasIndex(x => new { x.ShopId, x.Number }).IsUnique();
        modelBuilder.Entity<CreditPayment>().HasIndex(x => new { x.ShopId, x.Number }).IsUnique();

        modelBuilder.Entity<Sale>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Sale>().HasMany(x => x.Payments).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Sale>().HasIndex(x => new { x.ShopId, x.OccurredAt });
        modelBuilder.Entity<StockMovement>().HasIndex(x => new { x.ShopId, x.VariantId });
        modelBuilder.Entity<Expense>().HasIndex(x => new { x.ShopId, x.OccurredAt });
        modelBuilder.Entity<CashMovement>().HasIndex(x => x.CashSessionId);
        modelBuilder.Entity<Customer>().HasIndex(x => x.Phone).IsUnique().HasFilter("\"Phone\" IS NOT NULL");

        modelBuilder.Entity<Variant>().HasIndex(x => x.Sku).IsUnique();
        modelBuilder.Entity<ShopSetting>().HasIndex(x => new { x.ShopId, x.Key }).IsUnique();

        // Le curseur se lit par intervalle sur chaque table descendante.
        modelBuilder.Entity<Category>().HasIndex(x => x.Seq);
        modelBuilder.Entity<Product>().HasIndex(x => new { x.Seq, x.ShopId });
        modelBuilder.Entity<Variant>().HasIndex(x => x.Seq);
        modelBuilder.Entity<ShopSetting>().HasIndex(x => new { x.ShopId, x.Seq });
        modelBuilder.Entity<Order>().HasIndex(x => new { x.ShopId, x.Seq });
        modelBuilder.Entity<Order>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Order>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OrderLine>().Property(x => x.Quantity).HasPrecision(18, 3);

        modelBuilder.Entity<ShopStock>().Property(x => x.QuantityOnHand).HasPrecision(18, 3);
        modelBuilder.Entity<ShopStock>().Property(x => x.QuantityReserved).HasPrecision(18, 3);
        modelBuilder.Entity<SaleLine>().Property(x => x.Quantity).HasPrecision(18, 3);
        modelBuilder.Entity<StockMovement>().Property(x => x.QuantityDelta).HasPrecision(18, 3);
        modelBuilder.Entity<Variant>().Property(x => x.LowStockThreshold).HasPrecision(18, 3);
    }

    /// <summary>Prochaine valeur du curseur de descente.</summary>
    public async Task<long> NextSeqAsync(CancellationToken cancellationToken = default)
    {
        var values = await Database.SqlQueryRaw<long>($"SELECT nextval('{SyncSequence}') AS \"Value\"").ToListAsync(cancellationToken);
        return values[0];
    }
}
