using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Trading.Core.Quotes;

/// <summary>Latest top-of-book state of one token as of its <see cref="AvailableAt"/>.</summary>
public sealed record QuoteSnapshot(string Token, decimal? Bid, decimal? Ask, DateTimeOffset AvailableAt, long SourceId)
{
    /// <summary>Executable on both sides: bid &gt; 0, ask &gt; 0, ask &gt;= bid (locked allowed, crossed not). Never inferred from LTP.</summary>
    public bool IsExecutable => Bid is > 0m && Ask is > 0m && Ask >= Bid;

    public decimal? Spread => IsExecutable ? Ask - Bid : null;

    public TimeSpan Age(DateTimeOffset now) => now - AvailableAt;
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
        _latest[quote.Token] = new QuoteSnapshot(quote.Token, quote.Bid, quote.Ask, quote.AvailableAt, quote.SourceId);
    }

    public QuoteSnapshot? Get(string token) => _latest.TryGetValue(token, out var q) ? q : null;
}
