using NiftySignal.Domain.Enums;

namespace NiftySignal.Domain.Entities;

/// <summary>Immutable provider choice for one trading day; prevents source mixing on restart.</summary>
public sealed class MarketDataDay
{
    public DateOnly TradeDate { get; set; }
    public MarketDataProvider Provider { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
}
