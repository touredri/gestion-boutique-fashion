using BoutiqueFashion.Domain;
using Xunit;

namespace BoutiqueFashion.Domain.Tests;

public sealed class BusinessRulesTests
{
    [Fact] public void Percentage_discount_is_rounded_in_xof() => Assert.Equal(1_500, BusinessRules.CalculateDiscount(15_000, DiscountKind.Percentage, 10));
    [Fact] public void Discount_cannot_exceed_total() => Assert.Throws<InvalidOperationException>(() => BusinessRules.CalculateDiscount(1_000, DiscountKind.Amount, 1_001));
    [Fact] public void Weighted_average_cost_uses_received_stock() => Assert.Equal(1_500m, BusinessRules.NewWeightedAverageCost(10, 1_000, 10, 2_000));
    [Fact] public void Negative_old_stock_does_not_reduce_receipt_value() => Assert.Equal(2_000m, BusinessRules.NewWeightedAverageCost(-2, 1_000, 5, 2_000));

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly LoyaltyThresholds Thresholds = new();

    [Fact] public void Segment_debtor_takes_priority() => Assert.Equal(CustomerSegment.Debtor, BusinessRules.ComputeSegment(Now, Now.AddDays(-400), Now.AddDays(-1), 10, 1_000_000, 5_000, Thresholds));
    [Fact] public void Segment_new_within_threshold() => Assert.Equal(CustomerSegment.New, BusinessRules.ComputeSegment(Now, Now.AddDays(-10), null, 0, 0, 0, Thresholds));
    [Fact] public void Segment_inactive_without_recent_sale() => Assert.Equal(CustomerSegment.Inactive, BusinessRules.ComputeSegment(Now, Now.AddDays(-400), Now.AddDays(-120), 0, 0, 0, Thresholds));
    [Fact] public void Segment_vip_by_yearly_revenue() => Assert.Equal(CustomerSegment.Vip, BusinessRules.ComputeSegment(Now, Now.AddDays(-400), Now.AddDays(-1), 2, 600_000, 0, Thresholds));
    [Fact] public void Segment_loyal_by_purchase_count() => Assert.Equal(CustomerSegment.Loyal, BusinessRules.ComputeSegment(Now, Now.AddDays(-400), Now.AddDays(-1), 5, 100_000, 0, Thresholds));
    [Fact] public void Segment_active_by_default() => Assert.Equal(CustomerSegment.Active, BusinessRules.ComputeSegment(Now, Now.AddDays(-400), Now.AddDays(-1), 2, 100_000, 0, Thresholds));
}
