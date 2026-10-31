using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.MarketSummary;
using NiftySignal.Persistence;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// India VIX for the Futures Summary. The VIX is read from the SAME raw tick table as every other instrument (instrument master row of type Vix, token 26017 - never a hard-coded or guessed symbol), through
/// one scoped range query that cannot see an observation received or exchanged after the target boundary. It does not touch the futures order-flow calculations, adds no subscription and persists nothing.
/// </summary>
public sealed class VixSummaryService(IDbContextFactory<NiftySignalDbContext> sourceFactory)
{
    /// <summary>Why the VIX is unavailable, when it is (null = values were computed, individual points may still be stale / missing).</summary>
    public sealed record Result(IReadOnlyList<VixWindowSummary>? Windows, string? UnavailableReason);

    public async Task<Result> LoadAsync(DateOnly tradeDate, IReadOnlyList<WindowInstant> instants, CancellationToken ct = default)
    {
        var starts = instants.Where(i => i.Start is not null).Select(i => i.Start!.Value).ToList();
        var ends = instants.Where(i => i.End is not null).Select(i => i.End!.Value).ToList();
        if (starts.Count == 0 || ends.Count == 0) return new(null, "bar boundaries unavailable");

        await using var db = await sourceFactory.CreateDbContextAsync(ct);
        var tokens = await db.Instruments.AsNoTracking().Where(i => i.AsOfDate == tradeDate && i.InstrumentType == InstrumentType.Vix).Select(i => i.Token).Distinct().ToListAsync(ct);
        if (tokens.Count != 1) return new(null, tokens.Count == 0 ? "no India VIX instrument in the instrument master" : "India VIX instrument is not unique");
        var token = tokens[0];
        var lo = starts.Min() - VixSummaryCalculator.FreshnessLimit;
        var hi = ends.Max();
        var rows = await db.Ticks.AsNoTracking()
            .Where(t => t.Token == token && t.LastPrice > 0m && t.ExchangeTimestamp >= lo && t.ExchangeTimestamp <= hi && t.ReceivedAt <= hi)
            .Select(t => new { t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice, t.Id }).ToListAsync(ct);
        var observations = rows
            .Select(t => (At: t.ReceivedAt < t.ExchangeTimestamp ? t.ExchangeTimestamp : t.ReceivedAt, t.Id, Value: (double)t.LastPrice))
            .OrderBy(x => x.At).ThenBy(x => x.Id)
            .Select(x => new VixObservation(x.At, x.Value)).ToList();
        return new(VixSummaryCalculator.Compute(observations, instants, VixSummaryCalculator.FreshnessLimit), null);
    }
}
