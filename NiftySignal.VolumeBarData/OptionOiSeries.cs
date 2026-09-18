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

    OptionOiSeries(List<(DateTimeOffset Timestamp, long OpenInterest)> series) => _series = series;

    public static async Task<OptionOiSeries> LoadAsync(
        NiftySignalDbContext source, string token, DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken cancellationToken)
    {
        var rows = await source.Ticks
            .AsNoTracking()
            .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd && t.OpenInterest != null)
            .OrderBy(t => t.ExchangeTimestamp)
            .Select(t => new { t.ExchangeTimestamp, t.OpenInterest })
            .ToListAsync(cancellationToken);

        var series = new List<(DateTimeOffset, long)>(rows.Count);
        foreach (var row in rows)
        {
            series.Add((row.ExchangeTimestamp, row.OpenInterest!.Value));
        }

        return new OptionOiSeries(series);
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
