using BoutiqueFashion.Domain;
using Xunit;

namespace BoutiqueFashion.Domain.Tests;

public sealed class BusinessRulesTests
{
    [Fact] public void Percentage_discount_is_rounded_in_xof() => Assert.Equal(1_500, BusinessRules.CalculateDiscount(15_000, DiscountKind.Percentage, 10));
    [Fact] public void Discount_cannot_exceed_total() => Assert.Throws<InvalidOperationException>(() => BusinessRules.CalculateDiscount(1_000, DiscountKind.Amount, 1_001));
    [Fact] public void Weighted_average_cost_uses_received_stock() => Assert.Equal(1_500m, BusinessRules.NewWeightedAverageCost(10, 1_000, 10, 2_000));
    [Fact] public void Negative_old_stock_does_not_reduce_receipt_value() => Assert.Equal(2_000m, BusinessRules.NewWeightedAverageCost(-2, 1_000, 5, 2_000));
}
