using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Instruments;

namespace NiftySignal.Trading.Core.Sizing;

/// <summary>How a variant decides its entry quantity. Hashed with the variant.</summary>
public abstract record SizingSpec
{
    public abstract string Kind { get; }
}

/// <summary>
/// A fixed number of units. Default 650 (confirmed requested simulation quantity). It must be a whole number of lots of the selected
/// contract's lot size on the trade date; otherwise validation FAILS with a clear reason. It is never rounded or altered. No capital limit
/// is applied to fixed-quantity research.
/// </summary>
public sealed record FixedQuantitySpec : SizingSpec
{
    public int Units { get; init; } = 650;

    public override string Kind => "FixedQuantity";
}

/// <summary>
/// Size by premium budget: the largest whole number of lots such that <c>lots * lotSize * orderLimit</c> (plus the estimated entry costs
/// on that turnover when <see cref="IncludeEntryCosts"/>) does not exceed <see cref="Rupees"/>. The ORDER LIMIT (the qualifying ask) is
/// the valuation price, never a later fill price: a fill can only be at or below the limit, so the budget is not exceeded through
/// price. Rounds down only; a budget below one lot skips the entry (it can qualify later at a lower ask).
/// </summary>
public sealed record PremiumBudgetSpec : SizingSpec
{
    public required decimal Rupees { get; init; }

    /// <summary>True (default): entry costs at the limit-price turnover count against the budget.</summary>
    public bool IncludeEntryCosts { get; init; } = true;

    /// <summary>Optional cap on lots.</summary>
    public int? MaxLots { get; init; }

    public override string Kind => "PremiumBudget";
}

public enum SizingStatus
{
    Ok = 0,
    InvalidQuantity = 1,
    Unaffordable = 2,
}

public sealed record SizingResult(SizingStatus Status, int Quantity, int Lots, string Detail);

public static class Sizer
{
    public static SizingResult Size(SizingSpec spec, int lotSize, decimal limitPrice, CostModel? costs)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (lotSize <= 0)
        {
            return new SizingResult(SizingStatus.InvalidQuantity, 0, 0, FormattableString.Invariant($"lot size {lotSize} is not positive"));
        }

        switch (spec)
        {
            case FixedQuantitySpec fixedQuantity:
                if (fixedQuantity.Units <= 0 || fixedQuantity.Units % lotSize != 0)
                {
                    return new SizingResult(
                        SizingStatus.InvalidQuantity,
                        0,
                        0,
                        FormattableString.Invariant($"fixed quantity {fixedQuantity.Units} is not a positive whole number of lots (lot size {lotSize}); it is not rounded"));
                }

                return new SizingResult(SizingStatus.Ok, fixedQuantity.Units, fixedQuantity.Units / lotSize, "fixed");

            case PremiumBudgetSpec budget:
                if (limitPrice <= 0m)
                {
                    return new SizingResult(SizingStatus.Unaffordable, 0, 0, "limit price is not positive");
                }

                var perLot = lotSize * limitPrice;
                var lots = (int)Math.Min(int.MaxValue, Math.Floor(budget.Rupees / perLot));
                if (budget.MaxLots is { } cap)
                {
                    lots = Math.Min(lots, cap);
                }

                if (budget.IncludeEntryCosts)
                {
                    if (costs is null)
                    {
                        throw new InvalidOperationException("Premium-budget sizing with entry costs needs a configured cost schedule.");
                    }

                    while (lots > 0 && (lots * perLot) + costs.Compute(TradeSide.Buy, lots * perLot).Total > budget.Rupees)
                    {
                        lots--;
                    }
                }

                return lots < 1
                    ? new SizingResult(
                        SizingStatus.Unaffordable,
                        0,
                        0,
                        FormattableString.Invariant($"budget {budget.Rupees} cannot buy one lot of {lotSize} at limit {limitPrice}"))
                    : new SizingResult(SizingStatus.Ok, lots * lotSize, lots, FormattableString.Invariant($"budget {budget.Rupees}: {lots} lots at limit {limitPrice}"));

            default:
                throw new NotSupportedException($"Unknown sizing spec {spec.GetType().Name}.");
        }
    }

    /// <summary>Pre-run check: every fixed quantity must be a whole number of lots for every contract in the universe (per the instrument snapshot of the trade date). Returns clear reasons, one per violation.</summary>
    public static IReadOnlyList<string> ValidateFixedQuantities(string variantId, SizingSpec spec, IEnumerable<OptionContract> universe)
    {
        var errors = new List<string>();
        if (spec is not FixedQuantitySpec fixedQuantity)
        {
            return errors;
        }

        foreach (var contract in universe.OrderBy(c => c.Token, StringComparer.Ordinal))
        {
            if (fixedQuantity.Units <= 0 || contract.LotSize <= 0 || fixedQuantity.Units % contract.LotSize != 0)
            {
                errors.Add(FormattableString.Invariant($"Variant '{variantId}': fixed quantity {fixedQuantity.Units} is not a whole number of lots for {contract.Token} (lot size {contract.LotSize})."));
            }
        }

        return errors;
    }
}
