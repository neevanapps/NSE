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
    public long Id { get; set; }

    public required string Token { get; set; }

    public required Exchange Exchange { get; set; }

    /// <summary>
    /// Authoritative for all time-sensitive computation (plan section 3.3) -- never use
    /// <see cref="ReceivedAt"/> for anything except latency/ops diagnostics.
    /// </summary>
    public required DateTimeOffset ExchangeTimestamp { get; set; }

    /// <summary>Local receive time. Ops/latency diagnostics only -- see <see cref="ExchangeTimestamp"/>.</summary>
    public required DateTimeOffset ReceivedAt { get; set; }

    public decimal LastPrice { get; set; }

    public long Volume { get; set; }

    public long? OpenInterest { get; set; }

    /// <summary>Null for a touchline-only update that carried no depth snapshot.</summary>
    public MarketDepth? Depth { get; set; }
}
