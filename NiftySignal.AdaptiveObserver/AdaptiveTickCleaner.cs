namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Exact live port of NiftyResearcher.Bars.TickCleaner.
/// Duplicate detection occurs in source/arrival order; kept rows are then ordered by causal
/// availability (max(received, exchange)) and Id.
/// </summary>
public static class AdaptiveTickCleaner
{
    public sealed record Result(
        IReadOnlyList<CleanObserverTick> Ticks,
        int RawRows,
        int DuplicatesDropped,
        int ClampedTimestamps);

    public static Result Clean(IEnumerable<ObserverRawTick> raw)
    {
        var kept = new List<CleanObserverTick>();
        var rawRows = 0;
        var duplicates = 0;
        var clamped = 0;
        ObserverRawTick? previous = null;

        foreach (var r in raw)
        {
            rawRows++;
            var duplicate = previous is { } p && SamePayload(p, r);
            previous = r;
            if (duplicate)
            {
                duplicates++;
                continue;
            }

            var availableAt = r.ReceivedAt;
            if (r.ReceivedAt < r.ExchangeTimestamp)
            {
                availableAt = r.ExchangeTimestamp;
                clamped++;
            }

            kept.Add(new CleanObserverTick(
                r.Id,
                r.ExchangeTimestamp,
                availableAt,
                r.Last,
                r.Bid,
                r.Ask,
                r.BidQty,
                r.AskQty,
                r.Volume,
                r.OpenInterest));
        }

        return new Result(
            kept.OrderBy(t => t.AvailableAt).ThenBy(t => t.Id).ToList(),
            rawRows,
            duplicates,
            clamped);
    }

    public static bool SamePayload(ObserverRawTick a, ObserverRawTick b) =>
        a.ExchangeTimestamp == b.ExchangeTimestamp
        && a.Last == b.Last
        && a.Bid == b.Bid
        && a.Ask == b.Ask
        && a.BidQty == b.BidQty
        && a.AskQty == b.AskQty
        && a.Volume == b.Volume
        && a.OpenInterest == b.OpenInterest;
}
