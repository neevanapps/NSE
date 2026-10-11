namespace NiftySignal.Trading.Core.Risk;

public enum DailySlEnforcement
{
    /// <summary>The daily SL is observed and reported only. The only mode ResearchV1 accepts.</summary>
    None = 0,

    /// <summary>Future: block new entries of the variant once the daily SL is breached.</summary>
    BlockEntries = 1,

    /// <summary>Future: block new entries and liquidate open positions through the normal exit path.</summary>
    BlockEntriesAndLiquidate = 2,
}

/// <summary>
/// A named, versioned, hashed risk profile (contract section A.14.1).
/// <b>ResearchV1</b> (the only startable profile): no maximum trade count, the daily SL is observed and reported only (no entry lockout, no automatic
/// liquidation), peak premium exposure is recorded but not limited, per-trade SL remains an independent exit rule of the variant. Integrity gates,
/// quantity / lot validation, duplicate-order prevention and the session window always apply (they are not profile options).
/// <b>RealOrder</b>: carries the optional future enforcement settings (trade cap, daily-SL enforcement, exposure limit) so they are configurable and
/// unit-testable through <see cref="RiskPolicy"/>, but it is UNSTARTABLE: <c>TradingEngine</c> refuses it and no broker execution exists.
/// No monetary value here is approved.
/// </summary>
public sealed record RiskProfile
{
    public const string ResearchV1Name = "ResearchV1";
    public const string RealOrderName = "RealOrder";

    public string Name { get; init; } = ResearchV1Name;

    public string Version { get; init; } = "1";

    /// <summary>Maximum entries per variant per day; null = no cap. Must be null in ResearchV1.</summary>
    public int? MaxTradesPerDay { get; init; }

    public DailySlEnforcement DailySlEnforcement { get; init; } = DailySlEnforcement.None;

    /// <summary>Maximum committed premium (open positions at average entry + working entry orders at limit) per variant; null = unlimited. Must be null in ResearchV1, where the peak is only recorded.</summary>
    public decimal? MaxPeakPremiumExposure { get; init; }

    public static RiskProfile ResearchV1() => new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Name is not (ResearchV1Name or RealOrderName))
        {
            errors.Add($"Risk profile '{Name}' is not known.");
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            errors.Add("Risk profile needs a Version.");
        }

        if (!Enum.IsDefined(DailySlEnforcement))
        {
            errors.Add("DailySlEnforcement is not defined.");
        }

        if (MaxTradesPerDay is < 1)
        {
            errors.Add("MaxTradesPerDay must be at least 1 when set.");
        }

        if (MaxPeakPremiumExposure is <= 0m)
        {
            errors.Add("MaxPeakPremiumExposure must be positive when set.");
        }

        if (Name == ResearchV1Name && (MaxTradesPerDay is not null || DailySlEnforcement != DailySlEnforcement.None || MaxPeakPremiumExposure is not null))
        {
            errors.Add("ResearchV1 enforces no trade cap, no daily-SL lockout / liquidation and no exposure limit; those settings belong to the RealOrder profile.");
        }

        return errors;
    }

    /// <summary>Reasons this profile cannot run in the engine today (empty = startable).</summary>
    public IReadOnlyList<string> StartBlockers()
        => Name == RealOrderName
            ? new[] { "The RealOrder risk profile is not startable: no broker execution exists and real orders remain disabled." }
            : Array.Empty<string>();
}

/// <summary>What the risk policy needs to know about one variant at the moment of a new-entry decision.</summary>
public sealed record RiskState(int TradesToday, decimal DailyPnl, bool DailySlConfigured, bool DailySlBreached, decimal CommittedPremium, decimal NewPremium);

public sealed record RiskDecision(bool Allowed, string? ReasonCode, bool LiquidateOpenPositions)
{
    public static RiskDecision Allow { get; } = new(true, null, false);
}

/// <summary>
/// Pure risk policy. With ResearchV1 it ALWAYS allows and never liquidates, whatever the state (that is the non-enforcement guarantee). The enforcement
/// branches exist for the future RealOrder profile and are exercised by unit tests only.
/// </summary>
public static class RiskPolicy
{
    public static RiskDecision CheckEntry(RiskProfile profile, RiskState state)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        if (profile.Name == RiskProfile.ResearchV1Name)
        {
            return RiskDecision.Allow;
        }

        if (profile.MaxTradesPerDay is { } cap && state.TradesToday >= cap)
        {
            return new RiskDecision(false, "RiskMaxTrades", false);
        }

        if (profile.DailySlEnforcement != DailySlEnforcement.None && state.DailySlConfigured && state.DailySlBreached)
        {
            return new RiskDecision(false, "RiskDailySlBreached", profile.DailySlEnforcement == DailySlEnforcement.BlockEntriesAndLiquidate);
        }

        if (profile.MaxPeakPremiumExposure is { } limit && state.CommittedPremium + state.NewPremium > limit)
        {
            return new RiskDecision(false, "RiskExposureLimit", false);
        }

        return RiskDecision.Allow;
    }

    /// <summary>Whether open positions must be liquidated because of the daily SL. Never for ResearchV1.</summary>
    public static bool ShouldLiquidate(RiskProfile profile, RiskState state)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        return profile.Name != RiskProfile.ResearchV1Name
            && profile.DailySlEnforcement == DailySlEnforcement.BlockEntriesAndLiquidate
            && state.DailySlConfigured
            && state.DailySlBreached;
    }
}
