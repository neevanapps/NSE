namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Exact-volume futures builder. A crossing source update is split so every complete bar contains
/// exactly the configured threshold. Price/quote/OI values of each split chunk remain the observed
/// source-update values, matching NiftyResearcher.
/// </summary>
public sealed class ExactAdaptiveFuturesBarBuilder
{
    readonly AdaptiveSessionDefinition _session;
    readonly List<ExactAdaptiveBar> _bars = [];

    bool _started;
    DateTimeOffset _intervalStart;
    CleanObserverTick _latest;
    long? _previousOi;

    long _volume;
    int _tradeUpdates;
    long _firstSourceTickId;
    long _lastSourceTickId;
    long _strictBuy;
    long _strictSell;
    long _strictUnknown;
    long _enrichedBuy;
    long _enrichedSell;
    long _enrichedUnknown;
    double _open;
    double _high;
    double _low;
    double _close;
    double _priceVolume;

    public ExactAdaptiveFuturesBarBuilder(AdaptiveSessionDefinition session)
    {
        if (session.BaseBarVolume <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(session), "Base bar volume must be positive.");
        }

        _session = session;
    }

    public IReadOnlyList<ExactAdaptiveBar> Bars => _bars;
    public long PartialVolume => _volume;
    public DateTimeOffset? PartialBarStartedAtUtc => _started ? _intervalStart : null;
    public int SplitUpdates { get; private set; }
    public int MaxChunksPerUpdate { get; private set; }

    /// <summary>Processes one enriched source tick and returns bars completed by this tick.</summary>
    public IReadOnlyList<ExactAdaptiveBar> Add(EnrichedObserverTick e)
    {
        var newlyCompleted = new List<ExactAdaptiveBar>();
        var tick = e.Tick;
        _latest = tick;

        if (!_started)
        {
            _started = true;
            _intervalStart = tick.AvailableAt;
            _previousOi = tick.OpenInterest;
            return newlyCompleted;
        }

        if (e.TradeVolume <= 0)
        {
            return newlyCompleted;
        }

        var remaining = e.TradeVolume;
        var chunks = 0;
        while (remaining > 0)
        {
            var capacity = _session.BaseBarVolume - _volume;
            var chunk = Math.Min(capacity, remaining);
            AddChunk(e, chunk);
            remaining -= chunk;
            chunks++;

            if (_volume == _session.BaseBarVolume)
            {
                newlyCompleted.Add(CloseBar(tick, true));
            }
        }

        if (chunks > 1)
        {
            SplitUpdates++;
        }

        MaxChunksPerUpdate = Math.Max(MaxChunksPerUpdate, chunks);
        return newlyCompleted;
    }

    public ExactAdaptiveBar? FlushPartial()
    {
        if (!_started || _volume <= 0)
        {
            return null;
        }

        return CloseBar(_latest, false);
    }

    void AddChunk(EnrichedObserverTick e, long chunk)
    {
        if (chunk <= 0)
        {
            return;
        }

        var price = e.Tick.Last;
        if (_tradeUpdates == 0)
        {
            _open = price;
            _high = price;
            _low = price;
            _firstSourceTickId = e.Tick.Id;
        }
        else
        {
            _high = Math.Max(_high, price);
            _low = Math.Min(_low, price);
        }

        _close = price;
        _lastSourceTickId = e.Tick.Id;
        _volume += chunk;
        _tradeUpdates++;
        _priceVolume += price * chunk;

        AddSide(e.StrictSide, chunk, ref _strictBuy, ref _strictSell, ref _strictUnknown);
        AddSide(e.EnrichedSide, chunk, ref _enrichedBuy, ref _enrichedSell, ref _enrichedUnknown);
    }

    ExactAdaptiveBar CloseBar(CleanObserverTick closing, bool isComplete)
    {
        var twoSided = closing.HasTwoSidedQuote;
        var depth = closing.BidQty + closing.AskQty;

        var bar = new ExactAdaptiveBar
        {
            TradeDate = _session.TradeDate,
            BarSeq = _bars.Count + 1,
            Symbol = _session.FutureSymbol,
            IsComplete = isComplete,
            StartAvailableAtUtc = _intervalStart,
            EndAvailableAtUtc = closing.AvailableAt,
            Open = _open,
            High = _high,
            Low = _low,
            Close = _close,
            Vwap = _priceVolume / _volume,
            Volume = _volume,
            TradeUpdates = _tradeUpdates,
            FirstSourceTickId = _firstSourceTickId,
            LastSourceTickId = _lastSourceTickId,
            StrictBuyVolume = _strictBuy,
            StrictSellVolume = _strictSell,
            StrictUnknownVolume = _strictUnknown,
            EnrichedBuyVolume = _enrichedBuy,
            EnrichedSellVolume = _enrichedSell,
            EnrichedUnknownVolume = _enrichedUnknown,
            OiOpen = _previousOi,
            OiClose = closing.OpenInterest,
            Bid = twoSided ? closing.Bid : null,
            Ask = twoSided ? closing.Ask : null,
            BidQty = twoSided ? closing.BidQty : null,
            AskQty = twoSided ? closing.AskQty : null,
            Spread = twoSided ? closing.Ask - closing.Bid : null,
            BookImbalance = twoSided && depth > 0
                ? (double)(closing.BidQty - closing.AskQty) / depth
                : null,
            Microprice = twoSided && depth > 0
                ? (closing.Bid * closing.AskQty + closing.Ask * closing.BidQty) / depth
                : null,
        };

        _bars.Add(bar);
        _previousOi = closing.OpenInterest;
        _intervalStart = closing.AvailableAt;
        Reset();
        return bar;
    }

    static void AddSide(ObserverTradeSide side, long volume, ref long buy, ref long sell, ref long unknown)
    {
        switch (side)
        {
            case ObserverTradeSide.Buy:
                buy += volume;
                break;
            case ObserverTradeSide.Sell:
                sell += volume;
                break;
            default:
                unknown += volume;
                break;
        }
    }

    void Reset()
    {
        _volume = 0;
        _tradeUpdates = 0;
        _firstSourceTickId = 0;
        _lastSourceTickId = 0;
        _strictBuy = 0;
        _strictSell = 0;
        _strictUnknown = 0;
        _enrichedBuy = 0;
        _enrichedSell = 0;
        _enrichedUnknown = 0;
        _open = 0d;
        _high = 0d;
        _low = 0d;
        _close = 0d;
        _priceVolume = 0d;
    }
}
