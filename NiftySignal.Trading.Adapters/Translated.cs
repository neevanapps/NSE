using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Trading.Adapters;

/// <summary>Provenance of a translated quote: which recorded row it came from and how its availability was derived (contract section A.16.1).</summary>
public sealed record QuoteProvenance(
    string Token,
    long SourceId,
    DateTimeOffset ExchangeTimestamp,
    DateTimeOffset ReceivedAt,
    AvailabilityBasis Basis);

public sealed record TranslatedQuote(QuoteEvent Event, QuoteProvenance Provenance);

/// <summary>Straddle / directional residual figures of one residual variant at one bar boundary, together with the basket they are defined on.</summary>
public sealed record ResidualMetrics(
    string Variant,
    bool Available,
    string? UnavailableReason,
    double? CeResidualPct,
    double? PeResidualPct,
    double? DirectionalResidualPct,
    double? CommonResidualPct,
    double? StraddleResidualPct,
    double? AdjacentDirectionalResidualDelta,
    double? CeBase,
    double? PeBase,
    string BasketIdentity);

/// <summary>Per-side option band rolling figures (the side's own strict ratios; NOT a relative CE-vs-PE ratio).</summary>
public sealed record BandSideMetrics(
    string Side,
    bool Available,
    double? ContractRollingStrictDeltaRatioTotal,
    double? NotionalRollingStrictDeltaRatioTotal,
    int? WindowBars);

/// <summary>
/// Derived metrics captured at the bar's own availability boundary (contract section A.16.3). Every value comes from the adaptive engine's package of
/// that bar or from the adjacent previous bar's reading; nothing is read from a later bar or a final-session projection. Carried for comparison and
/// for future strategies; the CP5 core does not consume it.
/// </summary>
public sealed record BarMetrics(
    long BarSeq,
    string FutureToken,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume,
    double BarStrictDeltaRatioTotal,
    double? FuturesRollingReturnBps,
    double? FuturesRollingStrictDeltaRatioTotal,
    string AdaptiveState,
    IReadOnlyList<ResidualMetrics> Residuals,
    BandSideMetrics Call,
    BandSideMetrics Put,
    IReadOnlyList<string> KnownGaps);

public sealed record TranslatedBar(BarCompletedEvent Event, BarMetrics Metrics);

/// <summary>All events of one availability instant, in the engine's group order (spot, options, future, then bars).</summary>
public sealed record TranslatedGroup(DateTimeOffset AvailableAt, long Ordinal, IReadOnlyList<TranslatedQuote> Quotes, IReadOnlyList<TranslatedBar> Bars)
{
    public IReadOnlyList<EngineEvent> Events()
        => Quotes.Select(q => (EngineEvent)q.Event).Concat(Bars.Select(b => (EngineEvent)b.Event)).ToList();
}

/// <summary>Why a source row was refused before it could touch any state.</summary>
public enum RowRejection
{
    UnknownToken = 0,
    MissingTimestamp = 1,
    NonUtcTimestamp = 2,
    NegativeValue = 3,
    ConflictingDuplicate = 4,
    IdOutOfOrder = 5,
    LateAvailability = 6,
}

/// <summary>
/// A refused row. <see cref="Transport"/> is true for reasons that arise from delivery order rather than from the row's content
/// (<see cref="RowRejection.IdOutOfOrder"/>, <see cref="RowRejection.LateAvailability"/>); those are excluded from the economic-equivalence comparison
/// only as a classification — their effect (a missing quote) is still compared through the quote channel.
/// </summary>
public sealed record RowQuarantine(RowRejection Reason, string Token, long Id, DateTimeOffset ReceivedAt, DateTimeOffset ExchangeTimestamp, string Detail)
{
    public bool Transport => Reason is RowRejection.IdOutOfOrder or RowRejection.LateAvailability;
}

/// <summary>What happened to one offered row.</summary>
public enum RowDisposition
{
    /// <summary>Became (or will become) a translated quote.</summary>
    Accepted = 0,

    /// <summary>Exact re-delivery of a row already accepted (overlapping polls). Ignored; transport-only.</summary>
    Redelivered = 1,

    /// <summary>Identical payload to the previous row of the same token: dropped by the adaptive normaliser by design.</summary>
    PayloadDuplicate = 2,

    /// <summary>Availability outside the accepted window.</summary>
    OutsideWindow = 3,

    Quarantined = 4,
}

public sealed record TranslatorCounters(
    long Input,
    long Translated,
    long Redelivered,
    long PayloadDuplicates,
    long OutsideWindow,
    long Quarantined,
    IReadOnlyDictionary<string, long> QuarantinedByReason,
    long ClampedAvailability,
    long ExchangeEqualsReceived,
    long GroupsReleased,
    long BarsReleased,
    int MaxBufferedRows,
    int SeenIdCount)
{
    /// <summary>Every offered row is accounted for exactly once: <c>Input = Translated + Redelivered + PayloadDuplicates + OutsideWindow + Quarantined</c>.</summary>
    public bool Balanced => Input == Translated + Redelivered + PayloadDuplicates + OutsideWindow + Quarantined;
}
