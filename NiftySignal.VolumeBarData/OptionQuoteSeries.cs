using Microsoft.EntityFrameworkCore;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// One option instrument's own real bid/ask-MIDPOINT history for one day -- deliberately
/// separate from <see cref="OptionPriceSeries"/>, which tracks LastPrice (the actual traded
/// price, correct for simulating a fill). This class exists for IV/synthetic-forward solving,
/// which needs the market's own quoted mid, not the last trade -- same "MidPrice" convention
/// `NiftySignal.BacktestData.CadencePopulator`'s own `MidPrice(Tick)` helper already uses
/// (bid1/ask1 average when both are quoted, LastPrice as a fallback, null only when neither is
/// available). Queried the same way as OptionPriceSeries: "what was this contract's own mid at or
/// before timestamp T," binary search, never fabricated.
/// </summary>
public sealed class OptionQuoteSeries
{
    readonly List<(DateTimeOffset Timestamp, decimal Mid)> _series;

    OptionQuoteSeries(List<(DateTimeOffset Timestamp, decimal Mid)> series) => _series = series;

    public static async Task<OptionQuoteSeries> LoadAsync(
        NiftySignalDbContext source, string token, DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken cancellationToken)
    {
        var rows = await source.Ticks
            .AsNoTracking()
            .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd)
            .OrderBy(t => t.ExchangeTimestamp)
            .Select(t => new { t.ExchangeTimestamp, t.LastPrice, t.Depth })
            .ToListAsync(cancellationToken);

        var series = new List<(DateTimeOffset, decimal)>(rows.Count);
        foreach (var row in rows)
        {
            var mid = MidPrice(row.LastPrice, row.Depth);
            if (mid is { } m)
            {
                series.Add((row.ExchangeTimestamp, m));
            }
        }

        return new OptionQuoteSeries(series);
    }

    /// <summary>Same convention as `NiftySignal.BacktestData.CadencePopulator`'s own `MidPrice(Tick)` helper -- bid1/ask1 average when both sides are quoted and positive, LastPrice as a fallback, null when neither is usable (never fabricated).</summary>
    static decimal? MidPrice(decimal lastPrice, NiftySignal.Domain.ValueObjects.MarketDepth? depth)
    {
        if (depth is { } d && d.Bid1Price > 0 && d.Ask1Price > 0)
        {
            return (d.Bid1Price + d.Ask1Price) / 2;
        }

        return lastPrice > 0 ? lastPrice : null;
    }

    /// <summary>The most recent real mid at or before <paramref name="timestamp"/> -- null if this contract had no usable quote at all before that point.</summary>
    public decimal? MidAtOrBefore(DateTimeOffset timestamp)
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

        return result >= 0 ? _series[result].Mid : null;
    }
}
