namespace BoutiqueFashion.Domain;

public static class BusinessRules
{
    public const decimal SellerDiscountLimitPercent = 10m;
    public const int ReturnWindowDays = 7;

    public static long CalculateDiscount(long baseAmountXof, DiscountKind kind, decimal value)
    {
        if (baseAmountXof < 0 || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        var discount = kind switch
        {
            DiscountKind.None => 0,
            DiscountKind.Amount => decimal.ToInt64(decimal.Round(value, 0, MidpointRounding.AwayFromZero)),
            DiscountKind.Percentage => decimal.ToInt64(decimal.Round(baseAmountXof * value / 100m, 0, MidpointRounding.AwayFromZero)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (discount > baseAmountXof) throw new InvalidOperationException("La remise ne peut pas dépasser le montant.");
        return discount;
    }

    public static decimal NewWeightedAverageCost(decimal oldQuantity, decimal oldCost, decimal receivedQuantity, long receivedCost)
    {
        if (receivedQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(receivedQuantity));
        var positiveOldQuantity = Math.Max(0, oldQuantity);
        var totalQuantity = positiveOldQuantity + receivedQuantity;
        return decimal.Round(((positiveOldQuantity * oldCost) + (receivedQuantity * receivedCost)) / totalQuantity, 2);
    }
}
