using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Metrics;

namespace NiftySignal.PaperEngineV2.Core.Config;

/// <summary>The immutable identity of a strategy configuration: id, version and the SHA-256 of its canonical JSON. An open position keeps the identity it was opened under.</summary>
public sealed record StrategyIdentity(string StrategyId, int Version, string Hash)
{
    public override string ToString() => $"{StrategyId}@{Version}#{Hash[..12]}";
}

/// <summary>Parsed (not compiled) condition tree. <see cref="Leaves"/> is the number of leaf conditions below it.</summary>
public abstract record ConditionSpec;

public sealed record GroupSpec(bool IsAnd, IReadOnlyList<ConditionSpec> Items) : ConditionSpec;

public sealed record ThresholdSpec(MetricSpec Metric, SummaryWindowId Window, Comparison Cmp, double A, double B) : ConditionSpec;

public sealed record MetricPairSpec(MetricSpec MetricA, SummaryWindowId WindowA, Comparison Cmp, MetricSpec MetricB, SummaryWindowId WindowB) : ConditionSpec;

/// <summary>
/// Count node "atLeast/1": true when at least <see cref="Min"/> of the bounded list of leaf conditions are True. <see cref="OnUnknown"/> is explicit; the only supported policy is
/// <see cref="AtLeastUnknownPolicy.Unknown"/>: if ANY listed condition is Unknown the result is Unknown (never a partial count). Deterministic, no scripts.
/// </summary>
public sealed record AtLeastSpec(int Min, AtLeastUnknownPolicy OnUnknown, IReadOnlyList<ConditionSpec> Items) : ConditionSpec;

public enum AtLeastUnknownPolicy { Unknown }

public sealed record CrossSpec(CrossDirection Direction, MetricSpec Metric, SummaryWindowId Window, double? ConstantLevel, MetricSpec? LevelMetric, SummaryWindowId LevelWindow) : ConditionSpec;

public enum OnOppositeEntrySignal { Exit }

public enum FillQuoteRule { FirstAfterDecision, AsOfEligibleBarEnd }

public enum FillMode { AllOrNone }

public enum LiquidationPolicy { None, ExitOpenPositions }

public enum DistanceKind { Points, Percent }

/// <summary>An optional bar-evaluated exit threshold (disabled when absent). Never assumed executable at the threshold price.</summary>
public sealed record ThresholdExit(DistanceKind Kind, double Value);

public sealed record EntryFilters(TimeOnly EntryFrom, TimeOnly EntryBefore, bool RequireFreshVix);

public sealed record PremiumRange(double Min, double Max)
{
    /// <summary>The tie-break target (Rs 125 for the frozen 100-150 range): the midpoint of the configured ask-premium range.</summary>
    public double Target => (Min + Max) / 2d;
}

public sealed record ExecutionPolicy(
    FillQuoteRule FillQuote, double EntrySlippage, double ExitSlippage, int LatencyMs, int MaxQuoteAgeMs, int EntryOrderLifetimeMs, int MaxProcessingLagMs,
    double? MaxSpreadAbsolute, double? MaxSpreadFraction, FillMode FillMode);

public sealed record RiskPolicy(double MaxDailyLoss, int MaxDailyTrades, LiquidationPolicy Liquidation);

public sealed record OptionalExits(ThresholdExit? StopLoss, ThresholdExit? Target, ThresholdExit? Trailing, int? MaxHoldBars)
{
    public static readonly OptionalExits None = new(null, null, null, null);

    public bool Any => StopLoss is not null || Target is not null || Trailing is not null || MaxHoldBars is not null;
}

/// <summary>A fully validated strategy configuration. Buy-only by construction: there is no field that can express selling or writing an option.</summary>
public sealed record StrategyConfig(
    StrategyIdentity Identity, bool Enabled, string SourceModel,
    GroupSpec? BuyCall, GroupSpec? BuyPut, GroupSpec? ExitConditions, OnOppositeEntrySignal OnOppositeEntrySignal,
    EntryFilters Filters, PremiumRange Premium, ExecutionPolicy Execution, int Lots, double VirtualCapital, int MaxConcurrent, RiskPolicy Risk,
    OptionalExits Exits, string FeeSchedule, string CanonicalJson, string? ExitMechanism = null);

/// <summary>One problem found while validating a configuration document. <see cref="Path"/> is a JSON-pointer-like location.</summary>
public sealed record ConfigError(string Code, string Path, string Message)
{
    public override string ToString() => $"{Code} at {Path}: {Message}";
}

public sealed record ParseResult(StrategyConfig? Config, IReadOnlyList<ConfigError> Errors)
{
    public bool IsValid => Config is not null && Errors.Count == 0;
}
