namespace NiftySignal.Trading.Core.Risk;

/// <summary>The per-day counters of one scope, all derived from fills and closed trades (never stored as an independent balance).</summary>
public readonly record struct ScopeCounters(int Trades, decimal RealizedNet, int ConsecutiveLosses);

/// <summary>Everything the scoped evaluator needs at the moment of a new-entry decision.</summary>
public sealed record ScopedRiskState(ScopeCounters Variant, ScopeCounters Family, ScopeCounters Global, decimal CommittedPremium, decimal NewPremium);

/// <summary>
/// Pure PaperLiveV1 entry gate over the scoped limits (variant: <see cref="Variants.VariantSpec.Limits"/>, family: <see cref="RiskProfile.Families"/>,
/// global: <see cref="RiskProfile.Global"/> and <see cref="RiskProfile.GlobalCapital"/>). Returns EVERY reason that holds, in stable order. Other
/// profiles never block here. It only gates new entries.
/// </summary>
public static class ScopedRisk
{
    public static IReadOnlyList<RejectionReason> Evaluate(RiskProfile profile, string strategyId, ScopeLimits? variantLimits, ScopedRiskState state)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        var reasons = new List<RejectionReason>();
        if (profile.Name != RiskProfile.PaperLiveV1Name)
        {
            return reasons;
        }

        profile.Families.TryGetValue(strategyId, out var family);

        if (profile.Global?.MaxTradesPerDay is { } globalTrades && state.Global.Trades >= globalTrades)
        {
            reasons.Add(RejectionReason.GlobalTradeLimit);
        }

        if (family?.MaxTradesPerDay is { } familyTrades && state.Family.Trades >= familyTrades)
        {
            reasons.Add(RejectionReason.FamilyTradeLimit);
        }

        if (variantLimits?.MaxTradesPerDay is { } variantTrades && state.Variant.Trades >= variantTrades)
        {
            reasons.Add(RejectionReason.VariantTradeLimit);
        }

        if (profile.Global?.MaxDailyLoss is { } globalLoss && state.Global.RealizedNet <= -globalLoss)
        {
            reasons.Add(RejectionReason.GlobalDailyLoss);
        }

        if (family?.MaxDailyLoss is { } familyLoss && state.Family.RealizedNet <= -familyLoss)
        {
            reasons.Add(RejectionReason.FamilyDailyLoss);
        }

        if (variantLimits?.MaxDailyLoss is { } variantLoss && state.Variant.RealizedNet <= -variantLoss)
        {
            reasons.Add(RejectionReason.VariantDailyLoss);
        }

        if (family?.MaxConsecutiveLosses is { } familyStreak && state.Family.ConsecutiveLosses >= familyStreak)
        {
            reasons.Add(RejectionReason.FamilyConsecutiveLoss);
        }

        if (variantLimits?.MaxConsecutiveLosses is { } variantStreak && state.Variant.ConsecutiveLosses >= variantStreak)
        {
            reasons.Add(RejectionReason.VariantConsecutiveLoss);
        }

        if (profile.GlobalCapital is { } capital && state.CommittedPremium + state.NewPremium > capital)
        {
            reasons.Add(RejectionReason.GlobalCapital);
        }

        return reasons.OrderBy(r => (int)r).ToList();
    }
}
