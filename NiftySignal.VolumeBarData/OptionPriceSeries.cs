using Microsoft.EntityFrameworkCore;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// One option instrument's own real tick-level price history for one day -- loaded once from raw
/// ticks (confirmed ~99.96% depth coverage on a sample strike during the 2026-09-17 feasibility
/// check, but this class only needs <see cref="Domain.Entities.Tick.LastPrice"/>, not depth), then
/// queried by "what was this contract's own price at or before timestamp T" -- the same "never
/// fabricate a price, use whatever's available" discipline
/// `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs`'s own price lookups already follow, just
/// tick-accurate here instead of rounded to the nearest 15s cadence.
/// </summary>
public sealed class OptionPriceSeries
{
    readonly List<(DateTimeOffset Timestamp, decimal Price)> _series;

    OptionPriceSeries(List<(DateTimeOffset Timestamp, decimal Price)> series) => _series = series;

    public static async Task<OptionPriceSeries> LoadAsync(
        NiftySignalDbContext source, string token, DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken cancellationToken)
    {
        var rows = await source.Ticks
            .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd && t.LastPrice > 0)
            .OrderBy(t => t.ExchangeTimestamp)
            .Select(t => new { t.ExchangeTimestamp, t.LastPrice })
            .ToListAsync(cancellationToken);

        return new OptionPriceSeries(rows.Select(r => (r.ExchangeTimestamp, r.LastPrice)).ToList());
    }

    /// <summary>The most recent real print at or before <paramref name="timestamp"/> -- null if this contract had no priced tick at all before that point (never fabricated).</summary>
    public decimal? PriceAtOrBefore(DateTimeOffset timestamp)
    {
        if (_series.Count == 0)
        {
            return null;
        }

        // Binary search for the last entry with Timestamp <= timestamp -- _series is already
        // timestamp-ordered from the query above.
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

        return result >= 0 ? _series[result].Price : null;
    }
}
