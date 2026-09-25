using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec. A sibling of <see cref="OptionPriceSeries"/> for
/// the parts of this spec that need more than LTP: bar building needs per-tick traded-qty delta
/// (for VWAP) and OI; the trade simulator's execution-fill layer needs bid/ask
/// (spec section 27-29). <see cref="OptionPriceSeries"/> itself is left untouched -- it is still
/// used by the unrelated, already-in-progress time-based cadence track.
/// </summary>
public sealed class OptionTickSeries
{
    // DEFERRED (audit finding F66) — exchange-time-only entries omit receipt availability and
    // carried depth has no field-refresh timestamp; frozen fills cannot establish quote freshness.
    // Preserve legacy records; the isolated research replay checks receipt timing. See docs/REVIEW_FINDINGS.md.
    public readonly record struct Entry(DateTimeOffset Timestamp, decimal LastPrice, long VolumeDelta, MarketDepth? Depth, long? OpenInterest);

    readonly List<Entry> _series;

    OptionTickSeries(List<Entry> series) => _series = series;

    public static async Task<OptionTickSeries> LoadAsync(
        NiftySignalDbContext source, string token, DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken cancellationToken)
    {
        var rows = await source.Ticks
            .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd && t.LastPrice > 0)
            .OrderBy(t => t.ExchangeTimestamp)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

        var entries = new List<Entry>(rows.Count);
        long? previousVolume = null;
        foreach (var row in rows)
        {
            var delta = previousVolume is { } prev ? Math.Max(0, row.Volume - prev) : 0;
            previousVolume = row.Volume;
            entries.Add(new Entry(row.ExchangeTimestamp, row.LastPrice, delta, row.Depth, row.OpenInterest));
        }

        return new OptionTickSeries(entries);
    }

    public IReadOnlyList<Entry> AllEntries => _series;

    /// <summary>The first real entry at or after <paramref name="timestamp"/> -- the "never fill before the signal" convention (spec section 26/30). Null if none exists (never fabricated).</summary>
    public Entry? EntryAtOrAfter(DateTimeOffset timestamp)
    {
        var lo = 0;
        var hi = _series.Count - 1;
        var result = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_series[mid].Timestamp >= timestamp)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return result >= 0 ? _series[result] : null;
    }

    /// <summary>The most recent real entry at or before <paramref name="timestamp"/> -- used for the forced end-of-day close (spec section 33), never a manufactured price. Null if none exists.</summary>
    public Entry? EntryAtOrBefore(DateTimeOffset timestamp)
    {
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

        return result >= 0 ? _series[result] : null;
    }
}
