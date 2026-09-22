using Microsoft.EntityFrameworkCore;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// One option instrument's own open-interest history for one day -- a LEVEL (position count),
/// not a flow, same "snapshot at or before timestamp T" query shape as <see cref="OptionQuoteSeries"/>
/// uses for mid price. Deliberately separate from that class rather than folding OI into it: the
/// two are read together but serve different metrics (Phase 1 metric 3's OI delta / buildup
/// quadrant vs. metric 1's IV), and keeping them separate keeps each class's binary-search series
/// small and single-purpose.
/// </summary>
public sealed class OptionOiSeries
{
    readonly List<(DateTimeOffset Timestamp, long OpenInterest)> _series;

    /// <summary>The last raw tick timestamp already incorporated into <see cref="_series"/> (whether or not that tick itself carried OI) -- lets <see cref="LoadAsync(OptionOiSeries?,NiftySignalDbContext,string,DateTimeOffset,DateTimeOffset,CancellationToken)"/> resume scanning strictly after this point instead of re-querying from <c>dayStart</c> again. Defaults to <c>dayStart</c> for a series built with no prior ticks scanned yet.</summary>
    readonly DateTimeOffset _scannedThrough;

    OptionOiSeries(List<(DateTimeOffset Timestamp, long OpenInterest)> series, DateTimeOffset scannedThrough)
    {
        _series = series;
        _scannedThrough = scannedThrough;
    }

    /// <summary>Diagnostic only (Phase G perf-check, docs/LIVE_PARITY_PLAN.md) -- how many OI-bearing rows this load pulled, so the CLI's read-only reload-cost measurement can report real row counts, not just wall-clock time.</summary>
    public int RowCountForDiagnostics => _series.Count;

    public static Task<OptionOiSeries> LoadAsync(
        NiftySignalDbContext source, string token, DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken cancellationToken) =>
        LoadAsync(existing: null, source, token, dayStart, dayEnd, cancellationToken);

    /// <summary>
    /// Phase G perf fix (docs/LIVE_PARITY_PLAN.md): incremental variant used by <see cref="LiveOptionSeriesCache"/>.
    /// When <paramref name="existing"/> is null this is byte-identical to the original from-scratch
    /// <see cref="LoadAsync(NiftySignalDbContext,string,DateTimeOffset,DateTimeOffset,CancellationToken)"/>
    /// (every existing caller -- the offline populator, every test, every replay harness -- keeps calling
    /// that overload and sees no behavior change at all). When <paramref name="existing"/> is supplied, only
    /// ticks strictly after its own <see cref="_scannedThrough"/> are queried and appended, turning a
    /// per-poll O(day-so-far) rescan into an O(new-ticks-since-last-poll) one -- the same "scope the query
    /// to only what's new" technique <c>LiveOptionDepthPopulator</c>'s own <c>scanStart</c> logic already
    /// established for its per-bar accumulator, applied here to a growing forward-fill series instead.
    /// Concretely measured to matter: <c>perf-check</c> (docs/LIVE_PARITY_PLAN.md Phase G section) showed
    /// the full from-scratch reload alone taking multiple seconds per token by late in a trading day --
    /// enough, combined across the ~10-40 touched tokens, to threaten the whole 10s poll budget.
    /// </summary>
    public static async Task<OptionOiSeries> LoadAsync(
        OptionOiSeries? existing, NiftySignalDbContext source, string token, DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken cancellationToken)
    {
        if (existing is not null && existing._scannedThrough >= dayEnd)
        {
            // Nothing new could exist in [dayStart, dayEnd] that isn't already scanned -- skip the
            // DB round trip entirely rather than issuing a query guaranteed to return zero rows.
            return existing;
        }

        var queryStart = existing?._scannedThrough ?? dayStart;
        var strictlyAfter = existing is not null;

        var rows = await source.Ticks
            .AsNoTracking()
            .Where(t => t.Token == token && (strictlyAfter ? t.ExchangeTimestamp > queryStart : t.ExchangeTimestamp >= queryStart) && t.ExchangeTimestamp <= dayEnd)
            .OrderBy(t => t.ExchangeTimestamp)
            .Select(t => new { t.ExchangeTimestamp, t.OpenInterest })
            .ToListAsync(cancellationToken);

        var series = existing is not null ? new List<(DateTimeOffset, long)>(existing._series) : new List<(DateTimeOffset, long)>(rows.Count);
        var scannedThrough = existing?._scannedThrough ?? dayStart;
        foreach (var row in rows)
        {
            if (row.OpenInterest is { } oi)
            {
                series.Add((row.ExchangeTimestamp, oi));
            }

            scannedThrough = row.ExchangeTimestamp;
        }

        // If no rows came back at all (e.g. this token is quiet for a stretch), _scannedThrough must
        // still advance to dayEnd -- otherwise the NEXT poll would needlessly re-query the exact same
        // now-known-empty [queryStart, dayEnd] range every single time instead of only the truly new tail.
        if (rows.Count == 0)
        {
            scannedThrough = dayEnd;
        }

        return new OptionOiSeries(series, scannedThrough);
    }

    /// <summary>The most recent real OI reading at or before <paramref name="timestamp"/> -- null if this contract never reported OI before that point.</summary>
    public long? OiAtOrBefore(DateTimeOffset timestamp)
    {
        if (_series.Count == 0)
        {
            return null;
        }

        var lo = 0;
        var hi = _series.Count - 1;
        var result = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_series[mid].Timestamp <= timestamp)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result >= 0 ? _series[result].OpenInterest : null;
    }
}
