using NiftySignal.Domain.Configuration;

namespace NiftySignal.Rules;

/// <summary>
/// The full ruleset shape from plan section 7.2. Every persisted signal/trade records
/// <see cref="RulesetVersion"/> so results are always traceable to the config that
/// produced them (plan section 7.1).
///
/// Costs deliberately has no STT/charges field: the plan's prose (section 9) mentions "an
/// STT/charges approximation" alongside brokerage, but the JSON config shape in 7.2 only
/// defines BrokeragePerOrder and SlippageTicks. Rather than invent an STT rate that isn't
/// in the plan, PaperTradeSimulator applies brokerage only for now -- add an SttRate field
/// here (and thread it through the simulator) once a real figure is sourced.
/// </summary>
public sealed record RulesetConfig(
    string RulesetVersion,
    CapitalConfig Capital,
    SessionConfig Session,
    EntryConfig Entry,
    StrikeSelectionConfig StrikeSelection,
    ExitConfig Exit,
    CostsConfig Costs,
    RiskLimitsConfig RiskLimits,
    KillSwitchOptions KillSwitch);

/// <summary>
/// LotSize is the exchange-defined unit (65 for NIFTY) -- fixed, not a tuning knob.
/// LotsPerTrade (2026-09-07) is how many of those lots one position actually trades; it
/// exists separately because PartialBookFraction needs the traded quantity to be a multiple
/// large enough that "book 50%" lands on a whole number of lots. At 1 lot, 50% of 65 is 32.5
/// -- not a size the exchange would even accept, let alone a realistic paper-trade fill.
/// </summary>
public sealed record CapitalConfig(decimal Total, int LotSize, int MaxConcurrentPositions, int LotsPerTrade);

/// <summary>
/// Times are wall-clock IST. NoEntryBeforeMinutes counts from market open (09:15 IST,
/// <see cref="EntryRuleEvaluator.MarketOpen"/>), not from midnight.
/// </summary>
public sealed record SessionConfig(
    int NoEntryBeforeMinutes,
    TimeOnly NoEntryAfterTime,
    TimeOnly SquareOffTime,
    bool ExpiryDayEnabled,
    TimeOnly ExpiryDayNoEntryAfterTime);

/// <summary>
/// MinScoreSustainedCadences (2026-09-08, audit finding F12) is checked in addition to
/// MinScoreSustainedSeconds, not instead of it -- see ScoreSustainTracker.Observe's own doc
/// comment for why elapsed time alone isn't sufficient. At the 15s cadence, 3 matches a 45s
/// MinScoreSustainedSeconds under normal (no-gap) conditions; it's a separate config value
/// rather than derived from MinScoreSustainedSeconds/15 so the two can be tuned independently.
/// </summary>
public sealed record EntryConfig(
    double MinAbsScore,
    int MinScoreSustainedSeconds,
    int ReEntryGapSameDirectionMinutes,
    int MaxTradesPerDay,
    double MaxIvRankForEntry,
    int MinScoreSustainedCadences = 3);

public sealed record StrikeSelectionConfig(
    decimal MinPremium,
    decimal MaxPremium,
    double MaxSpreadPctOfMid,
    long MinOpenInterest);

public sealed record ExitConfig(
    double PartialBookAtProfitPct,
    double PartialBookFraction,
    double StopLossPct,
    bool TrailAfterPartialBook,
    bool ExitOnScoreFlip,
    double ExitOnScoreBelowAbs,
    int MaxHoldMinutes);

public sealed record CostsConfig(decimal BrokeragePerOrder, int SlippageTicks);

/// <summary>
/// <paramref name="MaxDailyProfitPct"/> (2026-09-05) is the upside counterpart to
/// <paramref name="MaxDailyLossPct"/>: once realised profit for the day clears it, new entries
/// stop. Both are percentages of <see cref="CapitalConfig.Total"/>.
/// </summary>
public sealed record RiskLimitsConfig(double MaxDailyLossPct, double MaxDailyProfitPct, int MaxConsecutiveLosses);
