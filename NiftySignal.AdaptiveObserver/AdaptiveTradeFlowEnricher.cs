namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Exact port of the research Phase1TickEnricher: cumulative-volume maxima define traded
/// quantity; strict side is quote-derived; enriched side falls back to the tick rule.
/// </summary>
public sealed class AdaptiveTradeFlowEnricher
{
    CleanObserverTick? _previous;
    long _maxCumVolume;
    int _tickDirection;

    public long FirstTickCumVolume { get; private set; }
    public long MaxCumVolume => _maxCumVolume;
    public int VolumeDecreases { get; private set; }
    public int NoQuoteTicks { get; private set; }
    public int OiUpdates { get; private set; }
    public int TradeUpdates { get; private set; }
    public long MaxTradeVolume { get; private set; }

    public EnrichedObserverTick Process(CleanObserverTick tick)
    {
        if (!tick.HasTwoSidedQuote)
        {
            NoQuoteTicks++;
        }

        if (_previous is not { } previous)
        {
            FirstTickCumVolume = tick.Volume;
            _maxCumVolume = tick.Volume;
            _previous = tick;
            return new EnrichedObserverTick(tick, 0, ObserverTradeSide.Unknown, ObserverTradeSide.Unknown);
        }

        if (tick.Last > previous.Last)
        {
            _tickDirection = 1;
        }
        else if (tick.Last < previous.Last)
        {
            _tickDirection = -1;
        }

        if (tick.Volume < previous.Volume)
        {
            VolumeDecreases++;
        }

        if (tick.OpenInterest != previous.OpenInterest)
        {
            OiUpdates++;
        }

        long traded = 0;
        if (tick.Volume > _maxCumVolume)
        {
            traded = tick.Volume - _maxCumVolume;
            _maxCumVolume = tick.Volume;
        }

        var strict = ObserverTradeSide.Unknown;
        var enriched = ObserverTradeSide.Unknown;
        if (traded > 0)
        {
            TradeUpdates++;
            MaxTradeVolume = Math.Max(MaxTradeVolume, traded);
            strict = ClassifyByQuote(tick, previous);
            enriched = strict;
            if (enriched == ObserverTradeSide.Unknown)
            {
                enriched = _tickDirection > 0
                    ? ObserverTradeSide.Buy
                    : _tickDirection < 0
                        ? ObserverTradeSide.Sell
                        : ObserverTradeSide.Unknown;
            }
        }

        _previous = tick;
        return new EnrichedObserverTick(tick, traded, strict, enriched);
    }

    static ObserverTradeSide ClassifyByQuote(CleanObserverTick tick, CleanObserverTick previous)
    {
        double bid;
        double ask;
        if (previous.HasTwoSidedQuote)
        {
            bid = previous.Bid;
            ask = previous.Ask;
        }
        else if (tick.HasTwoSidedQuote)
        {
            bid = tick.Bid;
            ask = tick.Ask;
        }
        else
        {
            return ObserverTradeSide.Unknown;
        }

        if (tick.Last >= ask)
        {
            return ObserverTradeSide.Buy;
        }

        if (tick.Last <= bid)
        {
            return ObserverTradeSide.Sell;
        }

        return ObserverTradeSide.Unknown;
    }
}
