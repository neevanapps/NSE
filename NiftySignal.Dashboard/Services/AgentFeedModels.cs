using System.Text.Json;
using System.Text.Json.Serialization;

namespace NiftySignal.Dashboard.Services;

// Wire contract "agent-v1" of the read-only agent feed (GET /api/agent/snapshot and the agent-feed JSONL files).
// RAW values only: MicroDev is NOT inverted (the Dashboard's "(Inv)" is a display convention), Delta / Vega are NOT scaled x100.
// A value the Dashboard data contract does not have, or that is missing for a bar, is JSON null - never 0.

public sealed record AgentClock(string Model, string Kind, long? BaseBarVolume, int? RollingWindowBars, string Description);

public sealed record AgentMeta(
    string Schema,
    string BuildSha,
    string? SessionSource,
    AgentClock? Clock,
    string? TradeDate,
    string? Expiry,
    double? SelectedStrike,
    double? CenterStrike,
    int? LatestCompletedBarSeq,
    int BarsReturned,
    bool DataOk,
    IReadOnlyList<string> DataIssues,
    IReadOnlyList<string> Notes);

public sealed record AgentFutures(
    double? Price,
    double? PriceDelta,
    double? RollDeltaPx,
    long? Volume,
    long? StrictDelta,
    long? EnrichedDelta,
    double? MicroDevRaw,
    long? Ofi,
    long? RollOiDelta,
    double? BasisDelta,
    double? RollEfficiency,
    string? State,
    string? Evolution);

/// <summary>One side (CE or PE) of the bar's fixed strike. Greeks are the Black-Scholes values stored by the observer: Delta is unit-less, vega is per 1.00 vol point, IV is in IV points.</summary>
public sealed record AgentOptionSide(
    string? Token,
    string? TradingSymbol,
    bool RowAvailable,
    double? Price,
    double? PriceStart,
    double? PriceChange,
    double? Bid,
    double? Ask,
    double? QuoteAgeSec,
    double? Delta,
    double? DeltaChange,
    double? VegaPer1Vol,
    double? VegaChangePer1Vol,
    double? IvPoints,
    double? IvChangePoints,
    long? StrictDelta,
    long? EnrichedDelta,
    long? TotalQuantity,
    long? OiChange,
    double? MicroDevRaw,
    long? Ofi,
    long? GrossOfi,
    double? ActivityPerSec);

/// <summary>A displayed cell as the Dashboard builds it: the raw value and the ratio in percentage points (numerator / denominator x 100). Both null when unavailable.</summary>
public sealed record AgentRatio(double? Raw, double? RatioPct);

public sealed record AgentSideRatios(AgentRatio Ce, AgentRatio Pe, AgentRatio Dir);

/// <summary>The Dashboard's Dir columns. Direction = CE - PE for every metric except OI, where it is PE - CE.</summary>
public sealed record AgentDir(
    AgentSideRatios Price,
    AgentSideRatios RollPrice,
    AgentSideRatios Strict,
    AgentSideRatios Enriched,
    AgentSideRatios Oi,
    AgentSideRatios MicroDevRaw,
    AgentSideRatios Ofi,
    AgentSideRatios Iv,
    AgentSideRatios Activity,
    AgentSideRatios Delta,
    AgentSideRatios VegaPer1Vol);

public sealed record AgentOptions(
    string? Expiry,
    double? Strike,
    double? CenterStrike,
    AgentOptionSide Ce,
    AgentOptionSide Pe,
    AgentDir Dir);

public sealed record AgentResidual(
    string Variant,
    bool Available,
    string? UnavailableReason,
    double? CeResPct,
    double? PeResPct,
    double? DirectionalResPct,
    double? StraddleResPct,
    string? Direction,
    string? Relation,
    double? QuoteAgeSec);

public sealed record AgentMarket(
    double? Spot,
    double? SpotAgeSec,
    double? Vix,
    double? VixChange,
    double? VixAgeSec);

public sealed record AgentBar(
    int BarSeq,
    string StartIst,
    string EndIst,
    bool IsCompleted,
    double? DurationSec,
    bool DataOk,
    IReadOnlyList<string> DataIssues,
    AgentFutures Futures,
    AgentOptions Options,
    AgentResidual Residual,
    AgentMarket Market);

public sealed record AgentSnapshotResponse(AgentMeta Meta, IReadOnlyList<AgentBar> Bars);

public static class AgentFeedJson
{
    public const string Schema = "agent-v1";

    /// <summary>camelCase, every property present (nulls are written as null), no indentation: one bar is exactly one JSONL line.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}
