using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Sizing;

namespace NiftySignal.Trading.Core.Variants;

public enum CapacityBlockedPolicy
{
    /// <summary>A qualifying entry blocked by an occupied slot ends the opportunity (terminal Rejected).</summary>
    Reject = 0,

    /// <summary>The opportunity keeps waiting until the slot frees (and everything else still holds).</summary>
    Wait = 1,
}

public enum ReentryPolicy
{
    /// <summary>After an opportunity is consumed by a fill (or otherwise spent), the base strategy must become invalid and then valid again (a new episode) before another opportunity exists.</summary>
    NewEpisodeRequired = 0,
}

/// <summary>
/// One strategy variant: base strategy reference + entry condition + request. Owns its opportunities, capacity slot, orders (CP3) and
/// accounting; nothing here is shared between variants. Hashed as part of <see cref="Config.TradingConfig"/>.
/// </summary>
public sealed record VariantSpec
{
    public required string VariantId { get; init; }

    /// <summary>Key of the (pure) base strategy supplied to the engine.</summary>
    public required string StrategyId { get; init; }

    public required EntryConditionSpec EntryCondition { get; init; }

    /// <summary>Entry quantity rule. Default: fixed 650 units (confirmed requested simulation quantity), validated per contract against the lot size of the trade date; never silently rounded.</summary>
    public SizingSpec Sizing { get; init; } = new FixedQuantitySpec();

    /// <summary>Per-trade SL, TP1, final target (each optional / disabled by default).</summary>
    public ExitSpec Exit { get; init; } = new();

    /// <summary>Daily SL in rupees (positive = loss limit). ResearchV1 RECORDS a breach only: no entry lockout, no forced exit, no trade cap. Null = not tracked.</summary>
    public decimal? DailySl { get; init; }

    /// <summary>Null = condition-dependent default: Reject for Immediate, Wait for waiting conditions.</summary>
    public CapacityBlockedPolicy? OnCapacityBlocked { get; init; }

    public ReentryPolicy Reentry { get; init; } = ReentryPolicy.NewEpisodeRequired;

    /// <summary>Entry attempts allowed per opportunity; null = unlimited (attempts are not trades).</summary>
    public int? MaxAttemptsPerOpportunity { get; init; }

    /// <summary>Virtual account; null = the variant's own id (independent accounting for comparative research).</summary>
    public string? AccountId { get; init; }

    public CapacityBlockedPolicy ResolveCapacityPolicy()
        => OnCapacityBlocked ?? (EntryCondition is ImmediateEntrySpec ? CapacityBlockedPolicy.Reject : CapacityBlockedPolicy.Wait);

    public string ResolveAccountId() => AccountId ?? VariantId;

    public IReadOnlyList<string> Validate(int candleHistoryDepth)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(VariantId))
        {
            errors.Add("VariantId is required.");
        }

        if (string.IsNullOrWhiteSpace(StrategyId))
        {
            errors.Add($"Variant '{VariantId}': StrategyId is required.");
        }

        switch (Sizing)
        {
            case FixedQuantitySpec { Units: <= 0 }:
                errors.Add($"Variant '{VariantId}': fixed quantity must be positive.");
                break;
            case PremiumBudgetSpec { Rupees: <= 0m }:
                errors.Add($"Variant '{VariantId}': premium budget must be positive.");
                break;
            case PremiumBudgetSpec { MaxLots: < 1 }:
                errors.Add($"Variant '{VariantId}': MaxLots must be at least 1 when set.");
                break;
            case FixedQuantitySpec:
            case PremiumBudgetSpec:
                break;
            default:
                errors.Add($"Variant '{VariantId}': unknown sizing spec.");
                break;
        }

        errors.AddRange(Exit.Validate(VariantId));
        if (DailySl is <= 0m)
        {
            errors.Add($"Variant '{VariantId}': DailySl must be positive when set.");
        }

        if (MaxAttemptsPerOpportunity is <= 0)
        {
            errors.Add($"Variant '{VariantId}': MaxAttemptsPerOpportunity must be positive when set.");
        }

        if (OnCapacityBlocked is { } policy && !Enum.IsDefined(policy))
        {
            errors.Add($"Variant '{VariantId}': OnCapacityBlocked is not defined.");
        }

        if (EntryCondition is AskBelowCandleMaSpec ma)
        {
            if (ma.N < 1 || ma.N > candleHistoryDepth)
            {
                errors.Add($"Variant '{VariantId}': N must be between 1 and the candle HistoryDepth ({candleHistoryDepth}).");
            }

            if (!Enum.IsDefined(ma.PriceSource))
            {
                errors.Add($"Variant '{VariantId}': candle PriceSource is not defined.");
            }
        }

        return errors;
    }
}

/// <summary>Outcome of the pure base strategy for one completed bar: valid or not, and the option side it wants when valid.</summary>
public readonly record struct StrategyValidity(bool IsValid, OptionSide Side, string Reason);

/// <summary>
/// The base strategy: pure, stateless function of the bars it is shown. It decides only whether its setup is valid and on which side;
/// episodes, opportunities, contract selection, entry conditions and capacity belong to the engine. The same instance may serve
/// several variants, so it must hold no mutable state.
/// </summary>
public interface IBaseStrategy
{
    string StrategyId { get; }

    string Version { get; }

    StrategyValidity OnBar(BarCompletedEvent bar);
}
