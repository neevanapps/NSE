using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Trading.Core.Quotes;

/// <summary>Latest top-of-book state of one token as of its <see cref="AvailableAt"/>. Quantities are displayed level-1 sizes in units, null when unknown.</summary>
public sealed record QuoteSnapshot(string Token, decimal? Bid, decimal? Ask, DateTimeOffset AvailableAt, long SourceId, long? BidQty = null, long? AskQty = null)
{
    /// <summary>Executable on both sides: bid &gt; 0, ask &gt; 0, ask &gt;= bid (locked allowed, crossed not). Never inferred from LTP.</summary>
    public bool IsExecutable => Bid is > 0m && Ask is > 0m && Ask >= Bid;

    public decimal? Spread => IsExecutable ? Ask - Bid : null;

    public TimeSpan Age(DateTimeOffset now) => now - AvailableAt;

    public static QuoteSnapshot From(QuoteEvent quote)
        => new(quote.Token, quote.Bid, quote.Ask, quote.AvailableAt, quote.SourceId, quote.BidQty, quote.AskQty);
}

/// <summary>
/// Latest quote per token. A quote that is not two-sided replaces (clears) the previous executable quote rather than leaving it in
/// place, matching the adaptive pipeline: the previous quote is not carried forward.
/// </summary>
public sealed class QuoteBook
{
    private readonly Dictionary<string, QuoteSnapshot> _latest = new(StringComparer.Ordinal);

    public void Update(QuoteEvent quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        _latest[quote.Token] = QuoteSnapshot.From(quote);
    }

    public QuoteSnapshot? Get(string token) => _latest.TryGetValue(token, out var q) ? q : null;
}

/// <summary>
/// Time-indexed quote history per token, so that execution can ask "the latest quote available at or before time T" instead of
/// "the latest quote in the book". Only quotes that have been delivered to the engine are in it, so a lookup can never return a quote
/// from after T. Pruned on append to the recent window plus the last older quote (bounded memory).
/// </summary>
public sealed class QuoteHistory
{
    private readonly Dictionary<string, List<QuoteSnapshot>> _byToken = new(StringComparer.Ordinal);
    private readonly TimeSpan _retention;

    public QuoteHistory(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        _retention = retention;
    }

    public void Append(QuoteEvent quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (!_byToken.TryGetValue(quote.Token, out var list))
        {
            _byToken[quote.Token] = list = new List<QuoteSnapshot>();
        }

        if (list.Count > 0 && quote.AvailableAt < list[^1].AvailableAt)
        {
            throw new InvalidOperationException($"Quote history for {quote.Token} must be appended in non-decreasing time.");
        }

        list.Add(QuoteSnapshot.From(quote));
        var cutoff = quote.AvailableAt - _retention;
        var firstKept = 0;
        while (firstKept + 1 < list.Count && list[firstKept + 1].AvailableAt < cutoff)
        {
            firstKept++;
        }

        if (firstKept > 0)
        {
            list.RemoveRange(0, firstKept);
        }
    }

    /// <summary>The latest delivered quote of <paramref name="token"/> with AvailableAt &lt;= <paramref name="time"/>, or null.</summary>
    public QuoteSnapshot? LatestAtOrBefore(string token, DateTimeOffset time)
    {
        if (!_byToken.TryGetValue(token, out var list))
        {
            return null;
        }

        var lo = 0;
        var hi = list.Count - 1;
        QuoteSnapshot? best = null;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid].AvailableAt <= time)
            {
                best = list[mid];
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    public int Count(string token) => _byToken.TryGetValue(token, out var list) ? list.Count : 0;
}
