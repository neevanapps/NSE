using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, per-strike time-cadence experiment (docs/Price_Based_Findings.md). Deliberately the
/// simplest possible version, per the user's own explicit "clean and slow" instruction -- exports
/// raw data only, no signal/crossover logic yet. For ONE trading day, builds a fixed 15-second
/// cadence (configurable) DIRECTLY on each PUT option's own ticks -- no future/underlying price
/// involved anywhere in this class, matching the user's explicit "we don't touch futures for our
/// time based simulation" instruction. Every strike in the nearest-expiry PUT chain gets its own
/// independent cadence; a bucket with no real tick for that strike is left as a gap (null), never
/// fabricated -- same "no invented reading" discipline every other bar/engine in this project
/// follows.
/// </summary>
public static class PutCadenceExporter
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketClose = new(15, 30);

    // 2026-09-23: AvgPrice/TickCount added alongside the original last-price-at-or-before Price
    // column, per the user's explicit request -- AvgPrice is the mean of every real tick whose
    // OWN timestamp falls strictly inside this bucket (not a point lookup like Price), TickCount is
    // how many such ticks there were. A bucket with zero ticks gets AvgPrice=null/TickCount=0 --
    // Price can still carry forward from an earlier bucket (last real print), but AvgPrice never
    // fabricates a value for a bucket with no ticks of its own.
    //
    // Fast/Slow (2026-09-23): plain SMA of the AvgPrice column itself, over the trailing
    // fastBars/slowBars REAL (non-null) AvgPrice readings for this strike -- a bucket with no ticks
    // is skipped for window purposes, same "null leaves the window untouched" convention
    // PriceCrossoverEngine already uses, not treated as a zero and not fabricated. Null until
    // slowBars real readings have been observed for this strike (no fabricated warm-up value,
    // same discipline throughout this project). Purely causal -- only uses this bucket's own and
    // earlier buckets' AvgPrice, never a later one.
    public sealed record CadenceRow(DateOnly AsOfDate, int BucketIndex, DateTimeOffset BucketEnd, decimal StrikePrice, string Token, decimal? Price, decimal? AvgPrice, int TickCount, decimal? Fast, decimal? Slow);

    public sealed record ExportResult(int StrikeCount, int BucketCount, int RowCount, List<CadenceRow> Rows);

    /// <param name="strikeFilter">Null (default) exports every strike in the nearest-expiry PUT chain; when set, only that one strike is exported.</param>
    /// <param name="fastBars">Trailing REAL-AvgPrice-reading count for the Fast SMA column. Default 8 = 2 minutes at the default 15-second interval.</param>
    /// <param name="slowBars">Trailing REAL-AvgPrice-reading count for the Slow SMA column. Default 40 = 10 minutes at the default 15-second interval.</param>
    public static async Task<ExportResult> BuildAsync(
        NiftySignalDbContext source, DateOnly asOfDate, CancellationToken cancellationToken, int intervalSeconds = 15, decimal? strikeFilter = null,
        int fastBars = 8, int slowBars = 40)
    {
        // Audit finding F62 discipline (NIFTY-filtered, deterministic ordering) already standard
        // everywhere else in this project.
        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.OptionType == OptionType.Put && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .OrderBy(i => i.StrikePrice)
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return new ExportResult(0, 0, 0, []);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var puts = allOptions.Where(o => o.ExpiryDate == nearestExpiry
            && (strikeFilter is null || o.StrikePrice == strikeFilter)).ToList();

        var dayStart = new DateTimeOffset(asOfDate.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();
        var bucketCount = (int)Math.Ceiling((dayEnd - dayStart).TotalSeconds / intervalSeconds);

        var rows = new List<CadenceRow>(puts.Count * bucketCount);
        foreach (var put in puts)
        {
            var series = await OptionPriceSeries.LoadAsync(source, put.Token, dayStart, dayEnd, cancellationToken);
            var allPrices = series.AllPrices; // sorted by timestamp, per OptionPriceSeries's own contract
            var tickIndex = 0;
            // Rolling window of the trailing REAL (non-null) AvgPrice readings for THIS strike --
            // a bucket with no ticks (avgPrice null) leaves this window untouched, same convention
            // PriceCrossoverEngine already uses for a missing bar.
            var window = new List<decimal>(slowBars);

            for (var b = 0; b < bucketCount; b++)
            {
                var bucketStart = dayStart + TimeSpan.FromSeconds(b * (double)intervalSeconds);
                var bucketEnd = dayStart + TimeSpan.FromSeconds((b + 1) * (double)intervalSeconds);

                var sum = 0m;
                var count = 0;
                while (tickIndex < allPrices.Count && allPrices[tickIndex].Timestamp <= bucketEnd)
                {
                    if (allPrices[tickIndex].Timestamp > bucketStart)
                    {
                        sum += allPrices[tickIndex].Price;
                        count++;
                    }
                    tickIndex++;
                }

                var lastPrice = series.PriceAtOrBefore(bucketEnd);
                var avgPrice = count > 0 ? sum / count : (decimal?)null;

                decimal? fast = null, slow = null;
                if (avgPrice is { } ap)
                {
                    window.Add(ap);
                    if (window.Count > slowBars)
                    {
                        window.RemoveAt(0);
                    }
                    if (window.Count >= fastBars)
                    {
                        fast = window.Skip(window.Count - fastBars).Average();
                    }
                    if (window.Count >= slowBars)
                    {
                        slow = window.Average();
                    }
                }

                rows.Add(new CadenceRow(asOfDate, b, bucketEnd, put.StrikePrice!.Value, put.Token, lastPrice, avgPrice, count, fast, slow));
            }
        }

        return new ExportResult(puts.Count, bucketCount, rows.Count, rows);
    }

    public static void WriteCsv(ExportResult result, string path, TimeSpan istOffset)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("AsOfDate,BucketIndex,TimeIST,Strike,Token,Price,AvgPrice,TickCount,Fast,Slow");
        foreach (var r in result.Rows)
        {
            var ist = r.BucketEnd.ToOffset(istOffset);
            writer.WriteLine($"{r.AsOfDate:yyyy-MM-dd},{r.BucketIndex},{ist:HH:mm:ss},{r.StrikePrice},{r.Token},{(r.Price?.ToString() ?? "")},{(r.AvgPrice?.ToString() ?? "")},{r.TickCount},{(r.Fast?.ToString() ?? "")},{(r.Slow?.ToString() ?? "")}");
        }
    }
}
