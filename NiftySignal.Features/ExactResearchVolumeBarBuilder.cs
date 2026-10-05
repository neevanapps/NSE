using NiftySignal.Domain.Entities;
using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Features;

public enum ResearchAggressorSide
{
    Unknown = 0,
    Buy = 1,
    Sell = -1,
}

public sealed record ExactResearchVolumeBar(
    int BarIndex,
    DateTimeOffset StartTimestamp,
    DateTimeOffset EndTimestamp,
    decimal OpenPrice,
    decimal HighPrice,
    decimal LowPrice,
    decimal ClosePrice,
    long Volume,
    long StrictBuyVolume,
    long StrictSellVolume,
    long StrictUnknownVolume,
    long OiOpen,
    long OiClose,
    int SourceTradeUpdates)
{
    public long StrictDelta => StrictBuyVolume - StrictSellVolume;
    public double StrictDeltaRatioTotal => Volume > 0 ? (double)StrictDelta / Volume : 0d;
    public TimeSpan Duration => EndTimestamp - StartTimestamp;
}

/// <summary>
/// Exact split-volume builder used only by the new research observer. This intentionally
/// does NOT use the existing VolumeBarBuilder because that builder absorbs an entire
/// crossing tick into one bar and therefore produces threshold overshoot. The NiftyResearcher
/// weak2 work splits the cumulative-volume increment at the boundary; this class mirrors that
/// behavior so live observation and research use the same volume clock.
/// </summary>
public sealed class ExactResearchVolumeBarBuilder
{
    readonly long _threshold;
    readonly List<ExactResearchVolumeBar> _bars = [];

    Tick? _previous;
    long _maxCumVolume;
    DateTimeOffset _intervalStart;
    long _previousOi;
    long _volume;
    int _tradeUpdates;
    long _strictBuy;
    long _strictSell;
    long _strictUnknown;
    decimal _open;
    decimal _high;
    decimal _low;
    decimal _close;

    public ExactResearchVolumeBarBuilder(long threshold)
    {
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
    }

    public long Threshold => _threshold;
    public IReadOnlyList<ExactResearchVolumeBar> Bars => _bars;
    public long FirstTickCumVolume { get; private set; }
    public long MaxCumVolume => _maxCumVolume;

    public IReadOnlyList<ExactResearchVolumeBar> ApplyTick(Tick tick)
    {
        var completed = new List<ExactResearchVolumeBar>();

        if (_previous is null)
        {
            FirstTickCumVolume = tick.Volume;
            _maxCumVolume = tick.Volume;
            _intervalStart = AvailableAt(tick);
            _previousOi = tick.OpenInterest ?? 0;
            _previous = tick;
            return completed;
        }

        var previous = _previous;

        long traded = 0;
        if (tick.Volume > _maxCumVolume)
        {
            traded = tick.Volume - _maxCumVolume;
            _maxCumVolume = tick.Volume;
        }

        if (traded > 0)
        {
            var side = ClassifyByQuote(tick, previous);
            var remaining = traded;
            while (remaining > 0)
            {
                var capacity = _threshold - _volume;
                var chunk = Math.Min(capacity, remaining);
                AddChunk(tick, side, chunk);
                remaining -= chunk;

                if (_volume == _threshold)
                {
                    var bar = CloseBar(tick);
                    _bars.Add(bar);
                    completed.Add(bar);
                }
            }
        }

        _previous = tick;
        return completed;
    }

    static DateTimeOffset AvailableAt(Tick tick) =>
        tick.ReceivedAt > tick.ExchangeTimestamp ? tick.ReceivedAt : tick.ExchangeTimestamp;

    static bool HasTwoSidedQuote(MarketDepth? depth) =>
        depth is { Bid1Price: > 0, Ask1Price: > 0 } d && d.Ask1Price >= d.Bid1Price;

    ResearchAggressorSide ClassifyByQuote(Tick tick, Tick previous)
    {
        MarketDepth? depth = HasTwoSidedQuote(previous.Depth)
            ? previous.Depth
            : HasTwoSidedQuote(tick.Depth) ? tick.Depth : null;

        if (depth is null) return ResearchAggressorSide.Unknown;
        if (tick.LastPrice >= depth.Ask1Price) return ResearchAggressorSide.Buy;
        if (tick.LastPrice <= depth.Bid1Price) return ResearchAggressorSide.Sell;
        return ResearchAggressorSide.Unknown;
    }

    void AddChunk(Tick tick, ResearchAggressorSide side, long chunk)
    {
        if (_tradeUpdates == 0)
        {
            _open = tick.LastPrice;
            _high = tick.LastPrice;
            _low = tick.LastPrice;
        }
        else
        {
            _high = Math.Max(_high, tick.LastPrice);
            _low = Math.Min(_low, tick.LastPrice);
        }

        _close = tick.LastPrice;
        _volume += chunk;
        _tradeUpdates++;

        switch (side)
        {
            case ResearchAggressorSide.Buy: _strictBuy += chunk; break;
            case ResearchAggressorSide.Sell: _strictSell += chunk; break;
            default: _strictUnknown += chunk; break;
        }
    }

    ExactResearchVolumeBar CloseBar(Tick closing)
    {
        var closeOi = closing.OpenInterest ?? _previousOi;
        var bar = new ExactResearchVolumeBar(
            _bars.Count + 1,
            _intervalStart,
            AvailableAt(closing),
            _open,
            _high,
            _low,
            _close,
            _volume,
            _strictBuy,
            _strictSell,
            _strictUnknown,
            _previousOi,
            closeOi,
            _tradeUpdates);

        _previousOi = closeOi;
        _intervalStart = AvailableAt(closing);
        _volume = 0;
        _tradeUpdates = 0;
        _strictBuy = 0;
        _strictSell = 0;
        _strictUnknown = 0;
        _open = _high = _low = _close = 0m;
        return bar;
    }
}
