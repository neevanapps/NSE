using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Trading.Core.Candles;

/// <summary>Candle boundaries. V1 supports one clock: the adaptive futures bars (a candle covers the quotes ordered between two consecutive BarCompleted events).</summary>
public enum CandleClock
{
    AdaptiveFuturesBars = 0,
}

/// <summary>
/// Which price of a candle an average is taken over. Always an explicit choice made by the variant; there is no implied default.
/// All prices come from executable (two-sided) quotes observed inside the candle window only.
/// </summary>
public enum CandlePriceSource
{
    /// <summary>Ask of the last executable quote in the window.</summary>
    AskClose = 0,

    /// <summary>Bid of the last executable quote in the window.</summary>
    BidClose = 1,

    /// <summary>(bid + ask) / 2 of the last executable quote in the window.</summary>
    MidClose = 2,

    /// <summary>(highest mid + lowest mid + MidClose) / 3 over the executable quotes in the window.</summary>
    TypicalPrice = 3,
}

public enum CandleInvalidReason
{
    None = 0,
    NoObservations = 1,
    TooFewObservations = 2,
    StaleClose = 3,
}

/// <summary>Versioned candle construction settings (hashed). All explicit; none is a research tuning default.</summary>
public sealed record CandleConfig
{
    public CandleClock Clock { get; init; } = CandleClock.AdaptiveFuturesBars;

    /// <summary>Executable quotes required inside the window for the candle to be valid. 1 = at least one.</summary>
    public int MinObservationsPerCandle { get; init; } = 1;

    /// <summary>When set, the last executable quote must be no more than this long before the bar end; null = not enforced (the in-window requirement still applies).</summary>
    public TimeSpan? MaxCloseAge { get; init; }

    /// <summary>Bars of history retained per token. Structural; must be at least the largest N used by any variant.</summary>
    public int HistoryDepth { get; init; } = 64;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(Clock))
        {
            errors.Add("Candle clock is not defined.");
        }

        if (MinObservationsPerCandle < 1)
        {
            errors.Add("MinObservationsPerCandle must be at least 1.");
        }

        if (MaxCloseAge is { } age && age <= TimeSpan.Zero)
        {
            errors.Add("MaxCloseAge must be positive when set.");
        }

        if (HistoryDepth < 1)
        {
            errors.Add("HistoryDepth must be at least 1.");
        }

        return errors;
    }
}

/// <summary>
/// One completed candle of one option token for one adaptive bar. An invalid candle is kept (with its reason) so a missing bar is
/// distinguishable from a gap; it is never forward-filled and never counts toward an average.
/// </summary>
public sealed record Candle(
    string Token,
    long BarSeq,
    DateTimeOffset PublishedAt,
    bool IsValid,
    CandleInvalidReason InvalidReason,
    int Observations,
    decimal? LastBid,
    decimal? LastAsk,
    DateTimeOffset? LastQuoteAt,
    decimal? MidHigh,
    decimal? MidLow)
{
    public decimal? Price(CandlePriceSource source)
    {
        if (!IsValid)
        {
            return null;
        }

        var mid = (LastBid!.Value + LastAsk!.Value) / 2m;
        return source switch
        {
            CandlePriceSource.AskClose => LastAsk,
            CandlePriceSource.BidClose => LastBid,
            CandlePriceSource.MidClose => mid,
            CandlePriceSource.TypicalPrice => (MidHigh!.Value + MidLow!.Value + mid) / 3m,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }
}

public enum WindowStatus
{
    Ready = 0,
    NoBarsPublished = 1,
    InsufficientHistory = 2,
    InvalidCandleInWindow = 3,
}

public sealed record CandleWindow(WindowStatus Status, IReadOnlyList<Candle> Candles, long? FirstBarSeq, long? LastBarSeq, long? OffendingBarSeq);

/// <summary>
/// Read-only history of ONE token. A view is bound to its token; there is no way to build a view over a synthetic series, a strike
/// splice or a basket, so a condition can only average the selected contract's own published candles.
/// </summary>
public sealed class CandleHistoryView
{
    private readonly CandleStore _store;

    internal CandleHistoryView(CandleStore store, string token)
    {
        _store = store;
        Token = token;
    }

    public string Token { get; }

    public long? LatestBarSeq => _store.LatestBarSeq;

    public Candle? Get(long barSeq) => _store.TryGet(Token, barSeq);

    /// <summary>
    /// The last <paramref name="n"/> published bars as one window. V1 requires them to be CONSECUTIVE bars ending at the latest
    /// published bar, every one valid. A missing or invalid candle anywhere in the window makes it unusable; nothing is skipped or filled.
    /// </summary>
    public CandleWindow ConsecutiveWindow(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        if (_store.LatestBarSeq is not { } last)
        {
            return new CandleWindow(WindowStatus.NoBarsPublished, Array.Empty<Candle>(), null, null, null);
        }

        var first = last - n + 1;
        if (first < _store.FirstPublishedBarSeq || last - first + 1 > _store.Config.HistoryDepth)
        {
            return new CandleWindow(WindowStatus.InsufficientHistory, Array.Empty<Candle>(), first, last, null);
        }

        var candles = new List<Candle>(n);
        for (var seq = first; seq <= last; seq++)
        {
            var candle = Get(seq);
            if (candle is null && (_store.FirstSeqFor(Token) is not { } tokenFirst || seq < tokenFirst))
            {
                // The token had not been observed yet at that bar: not enough history, as opposed to a gap or invalid candle inside its history.
                return new CandleWindow(WindowStatus.InsufficientHistory, Array.Empty<Candle>(), first, last, seq);
            }

            if (candle is null || !candle.IsValid)
            {
                return new CandleWindow(WindowStatus.InvalidCandleInWindow, Array.Empty<Candle>(), first, last, seq);
            }

            candles.Add(candle);
        }

        return new CandleWindow(WindowStatus.Ready, candles, first, last, null);
    }
}

/// <summary>
/// Builds and publishes per-token candles from option quotes, on the adaptive bar clock. Quotes observed (in the engine's total
/// order) between BarCompleted(k-1) and BarCompleted(k) form candle k; a quote in the same availability group as bar k's closing tick
/// precedes the BarCompleted event and therefore belongs to candle k. A candle becomes visible to consumers only when
/// <see cref="PublishBar"/> runs for its BarCompleted event, so a quote processed earlier in the same group cannot see it.
/// </summary>
public sealed class CandleStore
{
    private sealed class Accumulator
    {
        public int Count;
        public decimal LastBid;
        public decimal LastAsk;
        public DateTimeOffset LastAt;
        public decimal MidHigh;
        public decimal MidLow;

        public void Reset() => Count = 0;
    }

    private readonly Dictionary<string, Accumulator> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<long, Candle>> _history = new(StringComparer.Ordinal);

    public CandleStore(CandleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid candle config: " + string.Join(" ", errors), nameof(config));
        }

        Config = config;
    }

    public CandleConfig Config { get; }

    public long? LatestBarSeq { get; private set; }

    public long FirstPublishedBarSeq { get; private set; }

    public CandleHistoryView ViewFor(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return new CandleHistoryView(this, token);
    }

    /// <summary>Records one option quote into the open candle of its token. Non-executable quotes are not observations and are never carried forward.</summary>
    public void Observe(QuoteEvent quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.Kind != EventKind.OptionQuote)
        {
            return;
        }

        if (!_open.TryGetValue(quote.Token, out var acc))
        {
            _open[quote.Token] = acc = new Accumulator();
        }

        if (quote.Bid is not > 0m || quote.Ask is not > 0m || quote.Ask < quote.Bid)
        {
            return;
        }

        var mid = (quote.Bid.Value + quote.Ask.Value) / 2m;
        if (acc.Count == 0)
        {
            acc.MidHigh = mid;
            acc.MidLow = mid;
        }
        else
        {
            acc.MidHigh = Math.Max(acc.MidHigh, mid);
            acc.MidLow = Math.Min(acc.MidLow, mid);
        }

        acc.Count++;
        acc.LastBid = quote.Bid.Value;
        acc.LastAsk = quote.Ask.Value;
        acc.LastAt = quote.AvailableAt;
    }

    /// <summary>Closes the open candle of every known token as bar <paramref name="barSeq"/> and publishes them. Bar sequence must strictly increase; skipped sequences stay absent (= invalid).</summary>
    public void PublishBar(long barSeq, DateTimeOffset at)
    {
        if (LatestBarSeq is { } latest && barSeq <= latest)
        {
            throw new InvalidOperationException($"Bar {barSeq} is not after the latest published bar {latest}.");
        }

        foreach (var token in _open.Keys.Order(StringComparer.Ordinal))
        {
            var acc = _open[token];
            var reason = CandleInvalidReason.None;
            if (acc.Count == 0)
            {
                reason = CandleInvalidReason.NoObservations;
            }
            else if (acc.Count < Config.MinObservationsPerCandle)
            {
                reason = CandleInvalidReason.TooFewObservations;
            }
            else if (Config.MaxCloseAge is { } maxAge && at - acc.LastAt > maxAge)
            {
                reason = CandleInvalidReason.StaleClose;
            }

            var valid = reason == CandleInvalidReason.None;
            var candle = acc.Count == 0
                ? new Candle(token, barSeq, at, false, reason, 0, null, null, null, null, null)
                : new Candle(token, barSeq, at, valid, reason, acc.Count, acc.LastBid, acc.LastAsk, acc.LastAt, acc.MidHigh, acc.MidLow);
            if (!_history.TryGetValue(token, out var series))
            {
                _history[token] = series = new SortedDictionary<long, Candle>();
            }

            series[barSeq] = candle;
            while (series.Count > Config.HistoryDepth)
            {
                series.Remove(series.Keys.First());
            }

            acc.Reset();
        }

        if (LatestBarSeq is null)
        {
            FirstPublishedBarSeq = barSeq;
        }

        LatestBarSeq = barSeq;
    }

    /// <summary>The candles published for bar <paramref name="barSeq"/> (one per known token, ordinal token order), as long as the bar is still inside the history depth. For run comparison.</summary>
    public IReadOnlyList<Candle> CandlesPublishedFor(long barSeq)
        => _history.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Value.TryGetValue(barSeq, out var candle) ? candle : null)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

    internal long? FirstSeqFor(string token)
        => _history.TryGetValue(token, out var series) && series.Count > 0 ? series.Keys.First() : null;

    internal Candle? TryGet(string token, long barSeq)
        => _history.TryGetValue(token, out var series) && series.TryGetValue(barSeq, out var candle) ? candle : null;
}
