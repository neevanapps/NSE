using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Domain.Entities;

/// <summary>
/// A single market update, broker-agnostic (plan section 4.3: nothing outside
/// NiftySignal.Ingestion should ever see a FlatTrade-shaped type). Not a formal FK to
/// <see cref="Instrument"/> -- ticks are bulk-inserted at high frequency and referenced
/// by <see cref="Token"/> directly per the plan's indexing guidance (section 3.2), not
/// joined through a relationship that would slow that path down.
/// </summary>
public sealed class Tick
{
    public MarketDataProvider Provider { get; set; } = MarketDataProvider.FlatTrade;

    public long Id { get; set; }

    public bool IsSnapshot { get; set; }
    public DateTimeOffset? LastTradeTimestamp { get; set; }

    public required string Token { get; set; }

    public required Exchange Exchange { get; set; }

    /// <summary>
    /// Provider update clock: FlatTrade exchange time, or Upstox message currentTs.
    /// Upstox last-trade time is preserved separately in LastTradeTimestamp. Adaptive
    /// readers also use receipt time to enforce causal availability.
    /// </summary>
    public required DateTimeOffset ExchangeTimestamp { get; set; }

    /// <summary>Local UTC receipt time, used for latency and adaptive causal availability.</summary>
    public required DateTimeOffset ReceivedAt { get; set; }

    public decimal LastPrice { get; set; }

    public long Volume { get; set; }

    public long? OpenInterest { get; set; }

    /// <summary>Null for a touchline-only update that carried no depth snapshot.</summary>
    public MarketDepth? Depth { get; set; }
}
