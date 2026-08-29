using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BoutiqueFashion.Tests;

public sealed class SaleIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boutique-tests-{Guid.NewGuid():N}");
    private ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection(); services.AddBoutiqueInfrastructure(root); provider = services.BuildServiceProvider();
        await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        await provider.GetRequiredService<IAuthorizationService>().ConfigurePinAsync("123456");
        await provider.GetRequiredService<ICashSessionService>().OpenAsync(10_000);
    }

    public Task DisposeAsync() { provider.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); return Task.CompletedTask; }

    [Fact]
    public async Task Sale_is_atomic_decrements_stock_and_is_idempotent()
    {
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Chemise", "Vêtements", "CHE-M-BLA", "1001", "M", "Blanc", 8_000, 15_000, 2, 1);
        var service = provider.GetRequiredService<ISaleService>();
        var draft = new SaleDraft("same-key", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 15_000)]);
        var first = await service.CreateAsync(draft); var second = await service.CreateAsync(draft);
        Assert.False(first.AlreadyExisted); Assert.True(second.AlreadyExisted); Assert.Equal(first.SaleId, second.SaleId);
        var refreshed = (await provider.GetRequiredService<ICatalogService>().SearchAsync("CHE-M-BLA")).Single(); Assert.Equal(1, refreshed.QuantityOnHand);
    }

    [Fact]
    public async Task Payment_sum_must_match_sale_total()
    {
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Sac", "Accessoires", "SAC-01", null, null, "Noir", 5_000, 10_000, 1, 0);
        var draft = new SaleDraft("bad-payment", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 9_000)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<ISaleService>().CreateAsync(draft));
        var refreshed = (await provider.GetRequiredService<ICatalogService>().SearchAsync("SAC-01")).Single(); Assert.Equal(1, refreshed.QuantityOnHand);
    }

    [Fact]
    public async Task Backup_is_created_and_valid()
    {
        var backup = provider.GetRequiredService<IBackupService>(); var path = await backup.CreateAsync(); Assert.True(File.Exists(path)); Assert.True(await backup.VerifyAsync(path));
    }

    [Fact]
    public async Task Mixed_payment_and_inventory_count_are_persisted()
    {
        var variant=await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Robe","Vêtements","ROB-01",null,"M","Rouge",10_000,20_000,4,1);
        var result=await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("mixed",[new SaleLineDraft(variant.Id,1)],[new PaymentDraft(PaymentMode.Cash,5_000),new PaymentDraft(PaymentMode.Wave,15_000)]));
        Assert.Equal(20_000,result.TotalXof);
        await provider.GetRequiredService<IInventoryService>().ApplyCountAsync([new InventoryCount(variant.Id,7)],"Comptage test","123456");
        Assert.Equal(7,(await provider.GetRequiredService<ICatalogService>().SearchAsync("ROB-01")).Single().QuantityOnHand);
    }

    [Fact]
    public async Task Credit_payment_updates_balance()
    {
        var customer=await provider.GetRequiredService<ICustomerService>().CreateAsync("Awa","70000000",100_000);
        var variant=await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Sac crédit","Accessoires","SAC-CR",null,null,"Noir",10_000,30_000,2,0);
        await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("credit",[new SaleLineDraft(variant.Id,1)],[new PaymentDraft(PaymentMode.Credit,30_000)],customer.Id,ManagerPin:"123456",CreditDueAt:DateTimeOffset.Now.AddDays(30)));
        var credit=(await provider.GetRequiredService<ICreditService>().ListAsync()).Single();
        var payment=await provider.GetRequiredService<ICreditService>().PayAsync(credit.Id,10_000,PaymentMode.Wave,"WAVE-1");
        Assert.Equal(20_000,payment.NewBalanceXof);
    }
}
