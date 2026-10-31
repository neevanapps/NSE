using NiftySignal.AdaptiveObserver;
using NiftySignal.PaperEngineV2.Frames;

namespace NiftySignal.PaperEngineV2.Runtime;

/// <summary>
/// Latest top-of-book per option token, updated on the OBSERVER thread from the ticks it already processed (O(1) per tick) and copied once per completed bar. Single-threaded by construction (only the observer
/// calls it); the copy it returns is immutable. A quote that became available after the bar's end is never included (no look-ahead).
/// </summary>
public sealed class QuoteBook
{
    readonly Dictionary<string, QuoteInput> _latest = new(StringComparer.Ordinal);
    readonly HashSet<string> _tokens;

    public QuoteBook(IEnumerable<string> optionTokens) => _tokens = new HashSet<string>(optionTokens, StringComparer.Ordinal);

    public int Tracked => _latest.Count;

    public void Update(string token, in CleanObserverTick tick)
    {
        if (!_tokens.Contains(token)) return;
        _latest[token] = new QuoteInput(token, tick.Bid, tick.Ask, tick.BidQty, tick.AskQty, tick.AvailableAt, tick.Id);
    }

    public IReadOnlyList<QuoteInput> Snapshot(DateTimeOffset barEnd)
    {
        var list = new List<QuoteInput>(_latest.Count);
        foreach (var q in _latest.Values) if (q.AvailableAt <= barEnd) list.Add(q);
        return list;
    }
}
