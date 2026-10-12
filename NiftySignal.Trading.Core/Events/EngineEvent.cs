using NiftySignal.Trading.Core.Time;

namespace NiftySignal.Trading.Core.Events;

/// <summary>
/// Kind of an input event. The numeric value is the within-group processing rank (contract section 4.3): in one
/// availability group the spot is applied first, then option quotes, then the future's quotes, and only then are the
/// bars that the group completed announced. This mirrors AdaptiveObserverEngine.ProcessAvailabilityGroup
/// (spot, options by source Id, future by source Id, bar packages returned afterwards).
/// </summary>
public enum EventKind
{
    /// <summary>Operator command (kill / reset). Ranks before everything else at the same instant, so a kill at T applies before the data of T.</summary>
    Control = -2,

    /// <summary>Global feed-health transition. Ranks after Control and before data events of the same instant.</summary>
    FeedHealth = -1,

    SpotQuote = 0,
    OptionQuote = 1,
    FutureQuote = 2,
    BarCompleted = 3,
}

/// <summary>Identity used for idempotent re-delivery: (kind, source id). Quote ids are source tick Ids, bar ids are BarSeq.</summary>
public readonly record struct EventIdentity(EventKind Kind, long SourceId);

/// <summary>
/// Immutable input event envelope. <see cref="AvailableAt"/> is the causal time the system could first know the event
/// (existing definition: max(ReceivedAt, ExchangeTimestamp)), always UTC. Wall-clock processing time is not part of the
/// envelope and must never influence a decision.
/// </summary>
public abstract record EngineEvent
{
    protected EngineEvent(DateTimeOffset availableAt, EventKind kind, long sourceId, string token)
    {
        Utc.Require(availableAt, nameof(availableAt));
        ArgumentNullException.ThrowIfNull(token);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        AvailableAt = availableAt;
        Kind = kind;
        SourceId = sourceId;
        Token = token;
    }

    public DateTimeOffset AvailableAt { get; }

    public EventKind Kind { get; }

    /// <summary>Source tick Id for quotes, BarSeq for completed bars. Orders events of the same kind within one availability group.</summary>
    public long SourceId { get; }

    /// <summary>Instrument token (quotes) or the bar stream's instrument token (bars). Final ordering tie-break only.</summary>
    public string Token { get; }

    public EventIdentity Identity => new(Kind, SourceId);
}

/// <summary>A top-of-book quote update. Missing sides are null; the engine never fills them from LTP.</summary>
public sealed record QuoteEvent : EngineEvent
{
    public QuoteEvent(DateTimeOffset availableAt, EventKind kind, long sourceTickId, string token, decimal? bid, decimal? ask, decimal? last = null, long? bidQty = null, long? askQty = null)
        : base(availableAt, kind, sourceTickId, token)
    {
        if (kind is not (EventKind.SpotQuote or EventKind.OptionQuote or EventKind.FutureQuote))
        {
            throw new ArgumentException("A quote event must have a quote kind.", nameof(kind));
        }

        Bid = bid;
        Ask = ask;
        Last = last;
        BidQty = bidQty;
        AskQty = askQty;
    }

    public decimal? Bid { get; }

    public decimal? Ask { get; }

    public decimal? Last { get; }

    /// <summary>Displayed level-1 bid quantity in units, when the source provides it (null = unknown; never inferred).</summary>
    public long? BidQty { get; }

    /// <summary>Displayed level-1 ask quantity in units, when the source provides it (null = unknown; never inferred).</summary>
    public long? AskQty { get; }
}

/// <summary>
/// Identity, quality and readiness evidence of a completed bar (contract section A.16.3). All members are value types or strings so that the
/// record has value equality (re-delivery of an identical bar is recognised). <see cref="Ready"/> is computed by the translator from the
/// adaptive readiness policy and the frozen session; the core never recomputes it.
/// </summary>
/// <param name="StartAvailableAt">Availability of the first tick of the bar (the bar spans [start, end] in availability time).</param>
/// <param name="EndAvailableAt">Availability of the closing tick; equals the event's AvailableAt.</param>
/// <param name="Volume">Volume accumulated in the bar (equals the session base bar volume for a valid bar).</param>
/// <param name="TradeUpdates">Number of trade updates in the bar.</param>
/// <param name="IsValid">The bar passes <c>AdaptiveReadinessPolicy.IsValid</c>.</param>
/// <param name="ConsecutiveValidBars">Length of the valid suffix ending at this bar, capped at <paramref name="RequiredValidBars"/>.</param>
/// <param name="RequiredValidBars">The policy's required number of consecutive valid bars.</param>
/// <param name="SignalStarted">The bar ended at or after the session's frozen signal start (09:30, or the frozen observation start of a late-start session).</param>
/// <param name="Ready"><c>ConsecutiveValidBars &gt;= RequiredValidBars &amp;&amp; SignalStarted</c> — trading eligibility from the adaptive side.</param>
/// <param name="ReadinessSpec">Version label of the readiness definition (policy and session model) that produced the flags.</param>
public sealed record BarInfo(
    DateTimeOffset StartAvailableAt,
    DateTimeOffset EndAvailableAt,
    long Volume,
    int TradeUpdates,
    bool IsValid,
    int ConsecutiveValidBars,
    int RequiredValidBars,
    bool SignalStarted,
    bool Ready,
    string ReadinessSpec);

/// <summary>
/// The immutable, causal strategy input of ONE finalized bar of a bar-clocked stream (the NPT1 M1 stream): the exact metrics a strategy may use, produced by the stream's own
/// producer from the authoritative finalized bar and its verified sidecars. A strategy never queries a database, never recomputes an Adaptive metric and never sees anything that was not
/// known when the bar completed. Every metric is nullable-aware: <c>null</c> means UNAVAILABLE and is never treated as zero. The frame is journaled with the bar event (canonical input), so a
/// replay hands a strategy the very same values.
/// </summary>
/// <param name="AdaptiveSessionId">The stream's session identity (never the adaptive-v1 session id).</param>
/// <param name="MicroDevTimeWeighted">Futures micro price minus mid, time weighted over the bar (index points); null when the bar had no usable book seconds.</param>
/// <param name="MicroDevUsableSeconds">Seconds of the bar with a usable micro deviation (the weight of <paramref name="MicroDevTimeWeighted"/>); null when the metric was not produced.</param>
/// <param name="PeBandContractStrictDelta">Strict-classified buy minus sell CONTRACT quantity over the changing ATM+-2 PE basket for this bar; null when the band was unavailable.</param>
/// <param name="BandCenterStrike">Centre strike of the band chosen for this bar; null when the band was unavailable.</param>
/// <param name="BandExpiry">Expiry of the band contracts; null when the band was unavailable.</param>
/// <param name="BandUnavailableReason">Why the band was unavailable, when it was.</param>
/// <param name="CandidateStrikes">The strikes whose contracts may be chosen on this bar, as invariant text joined by '|' in ascending order, e.g. <c>23350|23400|23450|23500|23550</c>: the strikes of this bar's changing ATM+-2 band UNITED
/// with the strikes of the session's frozen 09:30 anchor basket. That union is exactly the set of contracts the frozen research evidence could choose among (its per-bar contract table lists those and no others); null when the band was unavailable.</param>
public sealed record AdaptiveStrategyFrame(
    string AdaptiveSessionId,
    DateOnly TradeDate,
    long BarSeq,
    DateTimeOffset StartAvailableAt,
    DateTimeOffset EndAvailableAt,
    bool Ready,
    bool IsValid,
    int ConsecutiveValidBars,
    string ModelVersion,
    string ReadinessSpec,
    string MicrostructureVersion,
    double? MicroDevTimeWeighted,
    double? MicroDevUsableSeconds,
    long? PeBandContractStrictDelta,
    double? BandCenterStrike,
    DateOnly? BandExpiry,
    string? BandUnavailableReason,
    string? CandidateStrikes = null);

/// <summary>
/// A completed adaptive bar. <see cref="EngineEvent.AvailableAt"/> is the bar's closing-tick availability time, so the
/// event is ordered after every quote of the group that completed it. <see cref="Info"/> carries readiness evidence; null means
/// "not supplied" (CP0–CP4 test harnesses), in which case the core applies no readiness gate to entries.
/// </summary>
public sealed record BarCompletedEvent : EngineEvent
{
    public BarCompletedEvent(DateTimeOffset endAvailableAt, long barSeq, string token, BarInfo? info = null, AdaptiveStrategyFrame? frame = null)
        : base(endAvailableAt, EventKind.BarCompleted, barSeq, token)
    {
        if (info is not null && info.EndAvailableAt != endAvailableAt)
        {
            throw new ArgumentException("BarInfo.EndAvailableAt must equal the bar event's AvailableAt.", nameof(info));
        }

        if (frame is not null && (frame.EndAvailableAt != endAvailableAt || frame.BarSeq != barSeq))
        {
            throw new ArgumentException("The strategy frame must describe exactly this bar (BarSeq and EndAvailableAt).", nameof(frame));
        }

        Info = info;
        Frame = frame;
    }

    public long BarSeq => SourceId;

    public BarInfo? Info { get; }

    /// <summary>The strategy input frame of a bar-clocked stream; null for streams that do not carry one.</summary>
    public AdaptiveStrategyFrame? Frame { get; }
}
