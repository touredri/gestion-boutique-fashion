namespace BoutiqueFashion.Domain;

public static class Libelles
{
    public static string Text(ProductType value) => value switch
    {
        ProductType.Shoes => "Chaussures",
        ProductType.Accessories => "Accessoires",
        _ => "Vêtements"
    };

    public static string Text(PaymentMode value) => value switch
    {
        PaymentMode.Cash => "Espèces",
        PaymentMode.OrangeMoney => "Orange Money",
        PaymentMode.MoovMoney => "Moov Money",
        PaymentMode.Wave => "Wave",
        PaymentMode.Card => "Carte bancaire",
        PaymentMode.BankTransfer => "Virement",
        PaymentMode.Credit => "Crédit",
        _ => "Autre"
    };

    public static string Text(DocumentType value) => value switch
    {
        DocumentType.Receipt => "Ticket de caisse",
        DocumentType.Invoice => "Facture",
        DocumentType.Proforma => "Proforma",
        DocumentType.PaymentReceipt => "Reçu de paiement",
        DocumentType.DepositReceipt => "Reçu d'acompte",
        DocumentType.CreditPaymentReceipt => "Reçu de versement",
        DocumentType.BalanceReceipt => "Reçu de solde",
        DocumentType.CreditNote => "Avoir",
        DocumentType.ReturnNote => "Bon de retour",
        _ => value.ToString()
    };

    public static string Text(CustomerSegment value) => value switch
    {
        CustomerSegment.New => "Nouveau",
        CustomerSegment.Active => "Actif",
        CustomerSegment.Loyal => "Fidèle",
        CustomerSegment.Vip => "VIP",
        CustomerSegment.Inactive => "Inactif",
        CustomerSegment.Debtor => "Débiteur",
        _ => value.ToString()
    };

    public static string Text(StockMovementType value) => value switch
    {
        StockMovementType.Receipt => "Réception",
        StockMovementType.Sale => "Vente",
        StockMovementType.Return => "Retour",
        StockMovementType.Damaged => "Endommagé",
        StockMovementType.Lost => "Perdu",
        StockMovementType.Adjustment => "Ajustement",
        StockMovementType.Inventory => "Inventaire",
        StockMovementType.Reversal => "Contre-passation",
        StockMovementType.Reservation => "Mise de côté",
        StockMovementType.ReservationRelease => "Levée de réservation",
        _ => value.ToString()
    };

    public static string Text(CreditStatus value) => value switch
    {
        CreditStatus.Due => "À échoir",
        CreditStatus.PartiallyPaid => "Partiellement payé",
        CreditStatus.Paid => "Soldé",
        CreditStatus.Overdue => "En retard",
        CreditStatus.Disputed => "Litigieux",
        CreditStatus.Cancelled => "Annulé",
        _ => value.ToString()
    };

    public static string Text(CashMovementDirection value) => value switch
    {
        CashMovementDirection.In => "Entrée d'espèces",
        _ => "Sortie d'espèces"
    };

    public static string Text(OrderStatus value) => value switch
    {
        OrderStatus.Pending => "En cours",
        OrderStatus.Processed => "Traitée",
        OrderStatus.Delivered => "Livrée",
        _ => "Annulée"
    };

    public static string Text(OrderChannel value) => value switch
    {
        OrderChannel.Vitrine => "Site vitrine",
        OrderChannel.WhatsApp => "WhatsApp",
        _ => "Téléphone"
    };

    public static string Text(SaleStatus value) => value switch
    {
        SaleStatus.Completed => "Validée",
        SaleStatus.Cancelled => "Annulée",
        SaleStatus.Returned => "Retournée",
        SaleStatus.Reserved => "Réservée (avance)",
        _ => value.ToString()
    };

    public static string Text(object value) => value switch
    {
        ProductType v => Text(v),
        PaymentMode v => Text(v),
        DocumentType v => Text(v),
        CustomerSegment v => Text(v),
        StockMovementType v => Text(v),
        CreditStatus v => Text(v),
        SaleStatus v => Text(v),
        CashMovementDirection v => Text(v),
        OrderStatus v => Text(v),
        OrderChannel v => Text(v),
        null => string.Empty,
        _ => value.ToString() ?? string.Empty
    };
}
