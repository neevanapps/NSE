namespace NiftySignal.Trading.Core.Risk;

/// <summary>
/// Entry limits of ONE scope (a variant, a strategy family or the whole account). Every limit is optional (null = not enforced). All are ENTRY gates:
/// crossing one blocks new entries in the scope and never liquidates or otherwise touches existing positions. Counts are per IST trading day.
/// <list type="bullet">
/// <item><b>MaxTradesPerDay</b>: a trade counts when an entry FILLS and creates a position (not an intent, a waiting opportunity or a rejection).</item>
/// <item><b>MaxDailyLoss</b> (positive rupees): realized NET P&amp;L of the scope today &lt;= -MaxDailyLoss. Realized net = gross proceeds of exits already filled (a TP1 leg counts at once) minus the average-entry cost of the quantity sold, minus ALL costs incurred so far; unrealized P&amp;L of open quantity is never included.</item>
/// <item><b>MaxConsecutiveLosses</b>: the streak of consecutively closed trades with total net P&amp;L &lt; 0 (TP1 leg + final leg - all costs) reaches the limit. A trade closing with net &gt;= 0 resets the streak; TP1 alone changes nothing.</item>
/// </list>
/// </summary>
public sealed record ScopeLimits
{
    public int? MaxTradesPerDay { get; init; }

    public decimal? MaxDailyLoss { get; init; }

    public int? MaxConsecutiveLosses { get; init; }

    public bool IsEmpty => MaxTradesPerDay is null && MaxDailyLoss is null && MaxConsecutiveLosses is null;

    public IReadOnlyList<string> Validate(string scope)
    {
        var errors = new List<string>();
        if (MaxTradesPerDay is < 1)
        {
            errors.Add($"{scope}: MaxTradesPerDay must be at least 1 when set.");
        }

        if (MaxDailyLoss is <= 0m)
        {
            errors.Add($"{scope}: MaxDailyLoss must be positive when set.");
        }

        if (MaxConsecutiveLosses is < 1)
        {
            errors.Add($"{scope}: MaxConsecutiveLosses must be at least 1 when set.");
        }

        return errors;
    }
}

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

    /// <summary>The startable paper-trading profile: the scoped limits below are ENFORCED as entry gates. Paper only: no broker path exists.</summary>
    public const string PaperLiveV1Name = "PaperLiveV1";

    public string Name { get; init; } = ResearchV1Name;

    public string Version { get; init; } = "1";

    /// <summary>Maximum entries per variant per day; null = no cap. Must be null in ResearchV1.</summary>
    public int? MaxTradesPerDay { get; init; }

    public DailySlEnforcement DailySlEnforcement { get; init; } = DailySlEnforcement.None;

    /// <summary>Maximum committed premium (open positions at average entry + working entry orders at limit) per variant; null = unlimited. Must be null in ResearchV1, where the peak is only recorded.</summary>
    public decimal? MaxPeakPremiumExposure { get; init; }

    /// <summary>Account-wide limits (PaperLiveV1). Null = none.</summary>
    public ScopeLimits? Global { get; init; }

    /// <summary>
    /// The ONE shared capital pool (rupees of committed premium): open positions at average entry price x open quantity plus entry orders still working, summed over
    /// ALL variants and strategies. A new entry is blocked when committed + its own premium would exceed it. Required for PaperLiveV1 (no approved value exists:
    /// it is supplied by configuration, never defaulted). Costs are not part of committed premium.
    /// </summary>
    public decimal? GlobalCapital { get; init; }

    /// <summary>Strategy-family limits keyed by StrategyId (PaperLiveV1). X1, X2 and X3 contribute collectively to family X; this never limits position capacity.</summary>
    public IReadOnlyDictionary<string, ScopeLimits> Families { get; init; } = NoFamilies;

    private static readonly IReadOnlyDictionary<string, ScopeLimits> NoFamilies = new Dictionary<string, ScopeLimits>();

    public static RiskProfile ResearchV1() => new();

    /// <summary>A PaperLiveV1 profile with the mandatory shared capital. Everything else is optional.</summary>
    public static RiskProfile PaperLiveV1(decimal globalCapital, ScopeLimits? global = null, IReadOnlyDictionary<string, ScopeLimits>? families = null)
        => new() { Name = PaperLiveV1Name, Version = "1", GlobalCapital = globalCapital, Global = global, Families = families ?? new Dictionary<string, ScopeLimits>() };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Name is not (ResearchV1Name or RealOrderName or PaperLiveV1Name))
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

        var scoped = Global is not null || GlobalCapital is not null || Families.Count > 0;
        if (Name != PaperLiveV1Name && scoped)
        {
            errors.Add($"{Name}: scoped limits (Global, GlobalCapital, Families) belong to the PaperLiveV1 profile.");
        }

        if (Name == PaperLiveV1Name)
        {
            if (GlobalCapital is not > 0m)
            {
                errors.Add("PaperLiveV1 needs a positive GlobalCapital (the one shared capital pool): no value is assumed.");
            }

            if (MaxTradesPerDay is not null || DailySlEnforcement != DailySlEnforcement.None || MaxPeakPremiumExposure is not null)
            {
                errors.Add("PaperLiveV1 takes its limits from Global / Families / the variants' Limits; the RealOrder-style per-variant fields must stay unset.");
            }

            if (Global is not null)
            {
                errors.AddRange(Global.Validate("Global"));
                if (Global.MaxConsecutiveLosses is not null)
                {
                    errors.Add("Global.MaxConsecutiveLosses is not enforced by PaperLiveV1 (only variant and family streaks are): it must stay unset rather than be silently ignored.");
                }
            }

            foreach (var (strategyId, limits) in Families)
            {
                if (!Strategies.VariantKey.IsValidPart(strategyId))
                {
                    errors.Add($"Family limits key '{strategyId}' is not a valid StrategyId.");
                }

                errors.AddRange((limits ?? new ScopeLimits()).Validate("Family " + strategyId));
            }
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
        if (profile.Name != RiskProfile.RealOrderName)
        {
            return RiskDecision.Allow;      // ResearchV1 never enforces; PaperLiveV1 is enforced by the scoped evaluator (ScopedRisk), not by this legacy per-variant policy
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
