using Microsoft.EntityFrameworkCore;
using NiftySignal.Persistence;
using NiftySignal.Trading.Adapters;

namespace NiftySignal.Npt1M1.Data;

/// <summary>
/// Read-only access of the NPT1 M1 producer to the authoritative raw tick table. Its own implementation (the adaptive-v1 reader is not used or modified); the projection and the
/// windows are the same as the observer's reader so both streams see the very same rows: id order, scalar fields only, session day window from 09:14 IST to the next midnight.
/// </summary>
public sealed class Npt1TickReader
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static (DateTimeOffset Lower, DateTimeOffset Upper) DayWindow(DateOnly day)
    {
        var open = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), Ist).ToUniversalTime();
        var next = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), Ist).ToUniversalTime();
        return (open.AddMinutes(-1), next);
    }

    /// <summary>Rows of the tokens with an id above <paramref name="afterId"/> on the trade day, in id order, at most <paramref name="batchSize"/> of them.</summary>
    public async Task<IReadOnlyList<SourceTick>> ReadAfterIdAsync(NiftySignalDbContext db, IReadOnlyCollection<string> tokens, long afterId, DateOnly day, int batchSize, CancellationToken ct)
    {
        if (tokens.Count == 0)
        {
            return Array.Empty<SourceTick>();
        }

        var (lower, upper) = DayWindow(day);
        var tokenArray = tokens.Distinct(StringComparer.Ordinal).ToArray();
        var query = db.Ticks.AsNoTracking()
            .Where(t => t.Id > afterId && tokenArray.Contains(t.Token)
                && t.ExchangeTimestamp >= lower && t.ReceivedAt >= lower && t.ExchangeTimestamp < upper && t.ReceivedAt < upper)
            .OrderBy(t => t.Id)
            .Take(batchSize);
        return await ProjectAsync(query, ct);
    }

    /// <summary>All rows of the tokens on the trade day received through <paramref name="throughUtc"/> (both timestamps), in id order. For the session freeze (small window) and prior-session volumes.</summary>
    public async Task<IReadOnlyList<SourceTick>> ReadWindowAsync(NiftySignalDbContext db, IReadOnlyCollection<string> tokens, DateOnly day, DateTimeOffset throughUtc, CancellationToken ct)
    {
        if (tokens.Count == 0)
        {
            return Array.Empty<SourceTick>();
        }

        var (lower, _) = DayWindow(day);
        var tokenArray = tokens.Distinct(StringComparer.Ordinal).ToArray();
        var query = db.Ticks.AsNoTracking()
            .Where(t => tokenArray.Contains(t.Token) && t.ExchangeTimestamp >= lower && t.ReceivedAt >= lower && t.ExchangeTimestamp <= throughUtc && t.ReceivedAt <= throughUtc)
            .OrderBy(t => t.Id);
        return await ProjectAsync(query, ct);
    }

    private static async Task<IReadOnlyList<SourceTick>> ProjectAsync(IQueryable<NiftySignal.Domain.Entities.Tick> query, CancellationToken ct)
    {
        var rows = await query.Select(t => new
        {
            t.Token,
            t.Id,
            t.ExchangeTimestamp,
            t.ReceivedAt,
            t.LastPrice,
            t.Volume,
            t.OpenInterest,
            Bid = t.Depth == null ? 0m : t.Depth.Bid1Price,
            Ask = t.Depth == null ? 0m : t.Depth.Ask1Price,
            BidQty = t.Depth == null ? 0L : t.Depth.Bid1Qty,
            AskQty = t.Depth == null ? 0L : t.Depth.Ask1Qty,
        }).ToListAsync(ct);
        return rows.Select(t => new SourceTick(t.Token, t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice, t.Bid, t.Ask, t.BidQty, t.AskQty, t.Volume, t.OpenInterest)).ToList();
    }
}
