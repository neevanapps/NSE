using NiftySignal.Domain.Entities;

namespace NiftySignal.Dashboard.Services;

/// <summary>The latest pushed quote of ONE watched option token. Immutable, so a snapshot can be handed to the renderer without any locking.</summary>
/// <param name="BookAt">Availability of the last tick that carried market depth (the bid / ask): the basis of the quote age. Null until a depth-bearing tick arrived.</param>
public sealed record LiveOptionQuote(string Token, decimal? Bid, decimal? Ask, decimal? Ltp, DateTimeOffset? BookAt);

/// <summary>
/// In-memory quotes of the options held by the NPT1 paper strategies, fed by the EXISTING Host -> Dashboard SignalR tick channel (<see cref="Hubs.MarketDataHub.PushTick"/>); no polling and no
/// database read on a tick. The durable ledger decides WHICH tokens are held (<see cref="SetWatched"/>); a tick of any other token is ignored. Display only: nothing here is written anywhere,
/// and it never touches the strategies' durable MAE / MFE.
/// Thread safety follows <see cref="LiveDataService"/>: ticks arrive on SignalR threads while Blazor renders, so every access is under one lock and readers receive snapshot copies.
/// </summary>
public sealed class Npt1LiveQuotes
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _watched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveOptionQuote> _quotes = new(StringComparer.Ordinal);

    /// <summary>A watched token's quote changed (rare: only the held contracts raise it).</summary>
    public event Action? QuoteUpdated;

    /// <summary>A paper trade opened or closed on the Host (the existing PushTradesChanged invalidation): the durable set of open positions must be re-read.</summary>
    public event Action? TradesChanged;

    public void NotifyTradesChanged() => TradesChanged?.Invoke();

    /// <summary>Replaces the watched set with the tokens of the currently open positions; quotes of tokens no longer held are dropped (a closed position leaves no live state behind).</summary>
    public void SetWatched(IEnumerable<string> tokens)
    {
        lock (_lock)
        {
            _watched.Clear();
            foreach (var token in tokens.Where(t => !string.IsNullOrEmpty(t)))
            {
                _watched.Add(token);
            }

            foreach (var gone in _quotes.Keys.Where(k => !_watched.Contains(k)).ToList())
            {
                _quotes.Remove(gone);
            }
        }
    }

    /// <summary>A tick arrived from the Host. Returns true when it belonged to a watched token.</summary>
    public bool Apply(Tick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        lock (_lock)
        {
            if (!_watched.Contains(tick.Token))
            {
                return false;
            }

            _quotes.TryGetValue(tick.Token, out var previous);
            var available = tick.ReceivedAt > tick.ExchangeTimestamp ? tick.ReceivedAt : tick.ExchangeTimestamp;
            var ltp = tick.LastPrice > 0m ? tick.LastPrice : previous?.Ltp;
            if (tick.Depth is { } depth)
            {
                // Executable sides only: a non-positive side is "no bid / no ask", never zero, and a one-sided update clears the other side's old value.
                _quotes[tick.Token] = new LiveOptionQuote(tick.Token, depth.Bid1Price > 0m ? depth.Bid1Price : null, depth.Ask1Price > 0m ? depth.Ask1Price : null, ltp, available);
            }
            else
            {
                // A touchline-only tick carries no book: keep the last book (and its time, so the quote age keeps growing) and refresh only the last price.
                _quotes[tick.Token] = new LiveOptionQuote(tick.Token, previous?.Bid, previous?.Ask, ltp, previous?.BookAt);
            }
        }

        QuoteUpdated?.Invoke();
        return true;
    }

    /// <summary>Snapshot copy for rendering: the live dictionary is never exposed.</summary>
    public IReadOnlyDictionary<string, LiveOptionQuote> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, LiveOptionQuote>(_quotes, StringComparer.Ordinal);
        }
    }

    public IReadOnlyCollection<string> Watched
    {
        get
        {
            lock (_lock)
            {
                return _watched.ToArray();
            }
        }
    }
}
