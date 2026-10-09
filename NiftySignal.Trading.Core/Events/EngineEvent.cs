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
        if (kind is EventKind.BarCompleted)
        {
            throw new ArgumentException("A quote event cannot have kind BarCompleted.", nameof(kind));
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
/// A completed adaptive bar. <see cref="EngineEvent.AvailableAt"/> is the bar's closing-tick availability time, so the
/// event is ordered after every quote of the group that completed it.
/// </summary>
public sealed record BarCompletedEvent : EngineEvent
{
    public BarCompletedEvent(DateTimeOffset endAvailableAt, long barSeq, string token)
        : base(endAvailableAt, EventKind.BarCompleted, barSeq, token)
    {
    }

    public long BarSeq => SourceId;
}
