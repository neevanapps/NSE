using Microsoft.EntityFrameworkCore;

namespace NiftySignal.Persistence;

/// <summary>
/// DEFERRED (audit finding F79) — intraday adaptive resumption needs explicit gap/window recovery.
/// V1 reconnects raw collection but fails closed for adaptive observations after a source outage.
/// A snapshot cannot reconstruct the trades/quotes lost during an outage. Legacy FlatTrade replay is unchanged.
/// </summary>
public static class UpstoxContinuityGuard
{
    public static async Task EnsureAsync(NiftySignalDbContext db, DateOnly day, IReadOnlyCollection<string> tokens, DateTimeOffset throughUtc, CancellationToken ct)
    {
        var upstox = tokens.Where(x => x.StartsWith("UP:", StringComparison.Ordinal)).ToArray();
        if (upstox.Length == 0) return;
        var open = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime();
        var dayStart = open.AddHours(-9.25);
        var snapshots = await db.Ticks.AsNoTracking()
            .Where(x => upstox.Contains(x.Token) && x.IsSnapshot && x.ReceivedAt >= dayStart && x.ReceivedAt <= throughUtc)
            .GroupBy(x => x.Token).Select(x => new
            {
                Token = x.Key,
                InMarket = x.Count(t => t.ReceivedAt >= open),
                HadPreOpen = x.Any(t => t.ReceivedAt < open),
            }).ToListAsync(ct);
        var gap = await db.DataGaps.AsNoTracking().AnyAsync(x => x.Reason.StartsWith("Upstox feed failure:")
            && x.StartedAt <= throughUtc && (x.EndedAt == null || x.EndedAt >= open), ct);
        // Completed pre-open reconnects precede adaptive observation and share one baseline.
        if (snapshots.Any(x => x.InMarket + (x.HadPreOpen ? 1 : 0) > 1) || gap)
            throw new InvalidOperationException("Upstox source continuity was interrupted. Raw collection can reconnect, but adaptive observations are paused for this day; missing trades are not reconstructed from a snapshot.");
    }
}
