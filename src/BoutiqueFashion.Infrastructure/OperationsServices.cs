using System.Text.Json;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

public sealed class CreditService(IDbContextFactory<BoutiqueDbContext> factory, IAuthorizationService authorization) : ICreditService
{
    public async Task<IReadOnlyList<CreditSummary>> ListAsync(CancellationToken cancellationToken = default) { await using var db = await factory.CreateDbContextAsync(cancellationToken); var rows = await db.CustomerCredits.AsNoTracking().Join(db.Sales, c => c.SaleId, s => s.Id, (c,s) => new { Credit=c,Sale=s }).Join(db.Customers, x => x.Credit.CustomerId, c => c.Id, (x,c) => new { x.Credit,x.Sale,Customer=c }).OrderBy(x => x.Credit.DueAt).ToListAsync(cancellationToken); return rows.Select(x => new CreditSummary(x.Credit.Id,x.Sale.Number,x.Customer.Name,x.Credit.OriginalAmountXof,x.Credit.BalanceXof,x.Credit.DueAt,x.Credit.Status)).ToArray(); }
    public async Task<CreditPaymentResult> PayAsync(Guid creditId, long amountXof, PaymentMode mode, string? reference, CancellationToken cancellationToken = default)
    {
        if (amountXof <= 0 || mode == PaymentMode.Credit) throw new InvalidOperationException("Versement invalide.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var credit = await db.CustomerCredits.SingleOrDefaultAsync(x => x.Id == creditId,cancellationToken) ?? throw new KeyNotFoundException("Crédit introuvable.");
        if (amountXof > credit.BalanceXof) throw new InvalidOperationException("Le versement dépasse le solde.");
        var sale = await db.Sales.SingleAsync(x => x.Id == credit.SaleId, cancellationToken); var customer = await db.Customers.SingleAsync(x => x.Id == credit.CustomerId, cancellationToken);
        var number = $"REC-{DateTime.Now:yyyy}-{Guid.NewGuid():N}"[..19].ToUpperInvariant();
        var payment = new CreditPayment { CustomerCreditId=credit.Id,Number=number,AmountXof=amountXof,Mode=mode,Actor="Vendeur boutique" };
        credit.BalanceXof-=amountXof; credit.Status=credit.BalanceXof==0?CreditStatus.Paid:CreditStatus.PartiallyPaid; db.CreditPayments.Add(payment);
        var paymentDraft = new PaymentDraft(mode, amountXof, reference);
        var receipt = await DocumentReceiptFactory.CreateAsync(db, number, customer.Name, [new ReceiptItem($"Versement crédit {sale.Number}", 1, amountXof, 0, amountXof)], amountXof, 0, amountXof, [paymentDraft], $"Solde restant : {credit.BalanceXof:N0} FCFA", cancellationToken);
        var doc=new DocumentSnapshot{Type=DocumentType.CreditPaymentReceipt,Number=number,JsonPayload=JsonSerializer.Serialize(receipt)}; db.DocumentSnapshots.Add(doc);
        db.AuditEntries.Add(new AuditEntry { Actor="Vendeur boutique", Action="Versement crédit", EntityType=nameof(CustomerCredit), EntityId=credit.Id.ToString(), AfterJson=JsonSerializer.Serialize(new { amountXof, mode, reference, credit.BalanceXof }) });
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken); return new(payment.Id,number,credit.BalanceXof,doc.Id);
    }
    public async Task<CreditPaymentResult> ReverseAsync(Guid paymentId,string reason,string managerPin,CancellationToken cancellationToken=default)
    {
        if(string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Le motif est obligatoire.");
        if(!await authorization.AuthorizeSensitiveActionAsync(managerPin,"Contre-écriture crédit",cancellationToken:cancellationToken)) throw new UnauthorizedAccessException("PIN invalide.");
        await using var db=await factory.CreateDbContextAsync(cancellationToken); await using var tx=await db.Database.BeginTransactionAsync(cancellationToken);
        var original=await db.CreditPayments.SingleOrDefaultAsync(x=>x.Id==paymentId,cancellationToken)??throw new KeyNotFoundException("Versement introuvable.");
        if(original.IsReversal||await db.CreditPayments.AnyAsync(x=>x.ReversesPaymentId==paymentId,cancellationToken)) throw new InvalidOperationException("Versement déjà contre-passé.");
        var credit=await db.CustomerCredits.SingleAsync(x=>x.Id==original.CustomerCreditId,cancellationToken); var number=$"ANN-{DateTime.Now:yyyy}-{Guid.NewGuid():N}"[..19].ToUpperInvariant();
        var reversal=new CreditPayment{CustomerCreditId=credit.Id,Number=number,AmountXof=-original.AmountXof,Mode=original.Mode,IsReversal=true,ReversesPaymentId=original.Id,Actor="Responsable"};
        credit.BalanceXof+=original.AmountXof; credit.Status=credit.BalanceXof>=credit.OriginalAmountXof?CreditStatus.Due:CreditStatus.PartiallyPaid; db.CreditPayments.Add(reversal);
        var customer=await db.Customers.SingleAsync(x=>x.Id==credit.CustomerId,cancellationToken); var receipt=await DocumentReceiptFactory.CreateAsync(db,number,customer.Name,[new ReceiptItem("Contre-écriture de versement",1,-original.AmountXof,0,-original.AmountXof)],-original.AmountXof,0,-original.AmountXof,[new PaymentDraft(original.Mode,-original.AmountXof)],$"Motif : {reason}",cancellationToken);
        var doc=new DocumentSnapshot{Type=DocumentType.CreditNote,Number=number,JsonPayload=JsonSerializer.Serialize(receipt)};db.DocumentSnapshots.Add(doc);
        db.AuditEntries.Add(new AuditEntry{Actor="Responsable",Action="Contre-écriture crédit",EntityType=nameof(CreditPayment),EntityId=original.Id.ToString(),AfterJson=JsonSerializer.Serialize(new{reason,credit.BalanceXof})});
        await db.SaveChangesAsync(cancellationToken);await tx.CommitAsync(cancellationToken);return new(reversal.Id,number,credit.BalanceXof,doc.Id);
    }
}

public sealed class InventoryService(IDbContextFactory<BoutiqueDbContext> factory,IAuthorizationService authorization):IInventoryService
{
    public async Task ApplyCountAsync(IReadOnlyList<InventoryCount> counts,string reason,string managerPin,CancellationToken cancellationToken=default){if(!await authorization.AuthorizeSensitiveActionAsync(managerPin,"Valider inventaire",cancellationToken:cancellationToken))throw new UnauthorizedAccessException("PIN invalide.");await using var db=await factory.CreateDbContextAsync(cancellationToken);await using var tx=await db.Database.BeginTransactionAsync(cancellationToken);var ids=counts.Select(x=>x.VariantId).ToArray();var variants=await db.ProductVariants.Where(x=>ids.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,cancellationToken);foreach(var count in counts){var v=variants[count.VariantId];var delta=count.CountedQuantity-v.QuantityOnHand;if(delta==0)continue;v.QuantityOnHand=count.CountedQuantity;db.StockMovements.Add(new StockMovement{VariantId=v.Id,Type=StockMovementType.Inventory,QuantityDelta=delta,UnitCostXof=v.CostXof,Reason=reason,SourceType="Inventory",Actor="Responsable"});}await db.SaveChangesAsync(cancellationToken);await tx.CommitAsync(cancellationToken);}
    public async Task<IReadOnlyList<StockHistoryRow>> HistoryAsync(Guid? variantId=null,CancellationToken cancellationToken=default){await using var db=await factory.CreateDbContextAsync(cancellationToken);var q=db.StockMovements.AsNoTracking().Include(x=>x.Variant).ThenInclude(x=>x!.Product).AsQueryable();if(variantId!=null)q=q.Where(x=>x.VariantId==variantId);return await q.OrderByDescending(x=>x.CreatedAt).Take(1000).Select(x=>new StockHistoryRow(x.CreatedAt,x.Variant!.Sku,x.Variant.Product!.Name,x.Type,x.QuantityDelta,x.Reason,x.Actor)).ToListAsync(cancellationToken);}
}

public sealed class DocumentService(IDbContextFactory<BoutiqueDbContext> factory):IDocumentService
{
    public async Task<IReadOnlyList<DocumentListItem>> ListAsync(CancellationToken cancellationToken=default){await using var db=await factory.CreateDbContextAsync(cancellationToken);return await db.DocumentSnapshots.AsNoTracking().OrderByDescending(x=>x.CreatedAt).Select(x=>new DocumentListItem(x.Id,x.Number,x.Type,x.CreatedAt,x.PrintCount)).Take(500).ToListAsync(cancellationToken);}
    public async Task<DocumentSnapshot>CreateProformaAsync(ReceiptData data,CancellationToken cancellationToken=default){await using var db=await factory.CreateDbContextAsync(cancellationToken);var number=$"PRO-{DateTime.Now:yyyy}-{Guid.NewGuid():N}"[..19].ToUpperInvariant();var receipt=await DocumentReceiptFactory.CreateAsync(db,number,data.Customer,data.Items,data.SubtotalXof,data.DiscountXof,data.TotalXof,data.Payments,"PROFORMA · document sans encaissement",cancellationToken);var doc=new DocumentSnapshot{Type=DocumentType.Proforma,Number=number,JsonPayload=JsonSerializer.Serialize(receipt)};db.DocumentSnapshots.Add(doc);await db.SaveChangesAsync(cancellationToken);return doc;}
    public async Task<ReceiptData>GetReceiptAsync(Guid documentId,bool duplicate,CancellationToken cancellationToken=default){await using var db=await factory.CreateDbContextAsync(cancellationToken);var doc=await db.DocumentSnapshots.SingleAsync(x=>x.Id==documentId,cancellationToken);var receipt=JsonSerializer.Deserialize<ReceiptData>(doc.JsonPayload)??throw new InvalidDataException();return receipt with{IsDuplicate=duplicate||doc.PrintCount>0};}
    public async Task MarkPrintedAsync(Guid documentId,CancellationToken cancellationToken=default){await using var db=await factory.CreateDbContextAsync(cancellationToken);var doc=await db.DocumentSnapshots.SingleAsync(x=>x.Id==documentId,cancellationToken);doc.PrintCount++;await db.SaveChangesAsync(cancellationToken);}
}

public sealed class ReturnService(IDbContextFactory<BoutiqueDbContext> factory,IAuthorizationService authorization):IReturnService
{
    public async Task<ReturnResult> ReturnOrExchangeAsync(ReturnRequest request,CancellationToken cancellationToken=default)
    {
        if(string.IsNullOrWhiteSpace(request.Reason)) throw new InvalidOperationException("Le motif est obligatoire.");
        if(!await authorization.AuthorizeSensitiveActionAsync(request.ManagerPin,"Retour/échange",cancellationToken:cancellationToken))throw new UnauthorizedAccessException("PIN invalide.");
        await using var db=await factory.CreateDbContextAsync(cancellationToken);await using var tx=await db.Database.BeginTransactionAsync(cancellationToken);
        var sale=await db.Sales.Include(x=>x.Lines).Include(x=>x.Customer).SingleOrDefaultAsync(x=>x.Number==request.SaleNumber,cancellationToken)??throw new KeyNotFoundException("Vente introuvable.");
        if(sale.Status!=SaleStatus.Completed) throw new InvalidOperationException("Cette vente n’est plus retournable.");
        if(DateTimeOffset.UtcNow-sale.CreatedAt>TimeSpan.FromDays(BusinessRules.ReturnWindowDays))throw new InvalidOperationException("Délai de retour dépassé.");
        var line=sale.Lines.SingleOrDefault(x=>x.Sku==request.ReturnedSku)??throw new KeyNotFoundException("Article absent de la vente.");
        var alreadyReturned=await db.StockMovements.Where(x=>x.SourceId==sale.Id&&x.VariantId==line.VariantId&&x.Type==StockMovementType.Return).SumAsync(x=>x.QuantityDelta,cancellationToken);
        if(request.ReturnedQuantity<=0||alreadyReturned+request.ReturnedQuantity>line.Quantity)throw new InvalidOperationException("Quantité retournée invalide ou déjà retournée.");
        var returned=await db.ProductVariants.SingleAsync(x=>x.Id==line.VariantId,cancellationToken);returned.QuantityOnHand+=request.ReturnedQuantity;
        db.StockMovements.Add(new StockMovement{VariantId=returned.Id,Type=StockMovementType.Return,QuantityDelta=request.ReturnedQuantity,UnitCostXof=line.FrozenUnitCostXof,Reason=request.Reason,SourceType=nameof(Sale),SourceId=sale.Id,Actor="Responsable"});
        var returnValue=decimal.ToInt64(decimal.Round((decimal)line.LineTotalXof/line.Quantity*request.ReturnedQuantity,0,MidpointRounding.AwayFromZero));
        long replacementValue=0; var items=new List<ReceiptItem>{new($"Retour {line.Description}",-request.ReturnedQuantity,line.UnitPriceXof,0,-returnValue)};
        if(!string.IsNullOrWhiteSpace(request.ReplacementSku))
        {
            if(request.ReplacementQuantity<=0) throw new InvalidOperationException("Quantité de remplacement invalide.");
            var replacement=await db.ProductVariants.Include(x=>x.Product).SingleAsync(x=>x.Sku==request.ReplacementSku&&x.IsActive,cancellationToken);var now=DateTimeOffset.UtcNow;
            var replacementPrice=replacement.PromotionalPriceXof is not null&&replacement.PromotionStartsAt<=now&&replacement.PromotionEndsAt>=now?replacement.PromotionalPriceXof.Value:replacement.PriceXof;
            replacementValue=decimal.ToInt64(decimal.Round(request.ReplacementQuantity*replacementPrice,0,MidpointRounding.AwayFromZero));replacement.QuantityOnHand-=request.ReplacementQuantity;
            db.StockMovements.Add(new StockMovement{VariantId=replacement.Id,Type=StockMovementType.Sale,QuantityDelta=-request.ReplacementQuantity,UnitCostXof=decimal.ToInt64(decimal.Round(replacement.WeightedAverageCostXof,0)),Reason=$"Échange {sale.Number}",SourceType="Exchange",SourceId=sale.Id,Actor="Responsable"});
            items.Add(new ReceiptItem($"Remplacement {replacement.Product?.Name} {replacement.Sku}",request.ReplacementQuantity,replacementPrice,0,replacementValue));
        }
        var difference=replacementValue-returnValue;
        if(difference>0&&request.DifferencePayments.Sum(x=>x.AmountXof)!=difference)throw new InvalidOperationException("Le règlement de la différence est incorrect.");
        if(difference<=0&&request.DifferencePayments.Any(x=>x.AmountXof!=0))throw new InvalidOperationException("Aucun règlement ne doit être saisi pour un avoir.");
        foreach(var payment in request.DifferencePayments.Where(x=>x.AmountXof>0)) db.Payments.Add(new Payment{SaleId=sale.Id,Mode=payment.Mode,AmountXof=payment.AmountXof,ExternalReference=payment.Reference,Actor="Responsable"});
        var number=$"AVO-{DateTime.Now:yyyy}-{Guid.NewGuid():N}"[..19].ToUpperInvariant();
        var receipt=await DocumentReceiptFactory.CreateAsync(db,number,sale.Customer?.Name,items,replacementValue-returnValue,0,difference,request.DifferencePayments,$"Retour lié à {sale.Number} · {request.Reason}",cancellationToken);
        var doc=new DocumentSnapshot{SaleId=sale.Id,Type=DocumentType.CreditNote,Number=number,JsonPayload=JsonSerializer.Serialize(receipt)};db.DocumentSnapshots.Add(doc);
        db.AuditEntries.Add(new AuditEntry{Actor="Responsable",Action="Retour/échange",EntityType=nameof(Sale),EntityId=sale.Id.ToString(),AfterJson=JsonSerializer.Serialize(new{request.ReturnedSku,request.ReturnedQuantity,request.ReplacementSku,difference,request.Reason})});
        await db.SaveChangesAsync(cancellationToken);await tx.CommitAsync(cancellationToken);return new(doc.Id,number,difference);
    }
    public async Task<ReturnResult>CancelSaleAsync(string saleNumber,string reason,string managerPin,CancellationToken cancellationToken=default)
    {
        if(string.IsNullOrWhiteSpace(reason))throw new InvalidOperationException("Le motif est obligatoire.");if(!await authorization.AuthorizeSensitiveActionAsync(managerPin,"Annuler vente",cancellationToken:cancellationToken))throw new UnauthorizedAccessException("PIN invalide.");
        await using var db=await factory.CreateDbContextAsync(cancellationToken);await using var tx=await db.Database.BeginTransactionAsync(cancellationToken);var sale=await db.Sales.Include(x=>x.Lines).Include(x=>x.Payments).Include(x=>x.Customer).SingleOrDefaultAsync(x=>x.Number==saleNumber,cancellationToken)??throw new KeyNotFoundException("Vente introuvable.");if(sale.Status!=SaleStatus.Completed)throw new InvalidOperationException("Vente déjà corrigée.");
        foreach(var line in sale.Lines){var v=await db.ProductVariants.SingleAsync(x=>x.Id==line.VariantId,cancellationToken);v.QuantityOnHand+=line.Quantity;db.StockMovements.Add(new StockMovement{VariantId=v.Id,Type=StockMovementType.Reversal,QuantityDelta=line.Quantity,UnitCostXof=line.FrozenUnitCostXof,Reason=reason,SourceType=nameof(Sale),SourceId=sale.Id,Actor="Responsable"});}
        foreach(var p in sale.Payments.Where(x=>!x.IsReversal))db.Payments.Add(new Payment{SaleId=sale.Id,Mode=p.Mode,AmountXof=-p.AmountXof,IsReversal=true,ReversesPaymentId=p.Id,Actor="Responsable"});sale.Status=SaleStatus.Cancelled;
        var credit=await db.CustomerCredits.SingleOrDefaultAsync(x=>x.SaleId==sale.Id,cancellationToken);if(credit!=null){credit.Status=CreditStatus.Cancelled;credit.BalanceXof=0;}
        var number=$"AVO-{DateTime.Now:yyyy}-{Guid.NewGuid():N}"[..19].ToUpperInvariant();var receipt=await DocumentReceiptFactory.CreateAsync(db,number,sale.Customer?.Name,sale.Lines.Select(x=>new ReceiptItem($"Annulation {x.Description}",-x.Quantity,x.UnitPriceXof,0,-x.LineTotalXof)).ToArray(),-sale.TotalXof,0,-sale.TotalXof,sale.Payments.Where(x=>!x.IsReversal).Select(x=>new PaymentDraft(x.Mode,-x.AmountXof,x.ExternalReference)).ToArray(),$"Annulation de {sale.Number} · {reason}",cancellationToken);
        var doc=new DocumentSnapshot{SaleId=sale.Id,Type=DocumentType.CreditNote,Number=number,JsonPayload=JsonSerializer.Serialize(receipt)};db.DocumentSnapshots.Add(doc);db.AuditEntries.Add(new AuditEntry{Actor="Responsable",Action="Annuler vente",EntityType=nameof(Sale),EntityId=sale.Id.ToString(),AfterJson=JsonSerializer.Serialize(new{reason})});await db.SaveChangesAsync(cancellationToken);await tx.CommitAsync(cancellationToken);return new(doc.Id,number,-sale.TotalXof);
    }
}
