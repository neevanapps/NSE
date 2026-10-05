using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

public sealed record ObserverTokenTick(string Token, CleanObserverTick Tick);

/// <summary>
/// Read-only adapter over the authoritative raw tick database. Full replay intentionally reads
/// source rows in Id order first (the order duplicate detection saw them), then the per-token
/// normalizer produces clean ticks which are globally ordered by causal availability.
/// </summary>
public sealed class AdaptiveSourceTickReader
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);

    public async Task<IReadOnlyList<(string Token, ObserverRawTick Tick)>> ReadRawSessionAsync(
        NiftySignalDbContext db,
        DateOnly day,
        IReadOnlyCollection<string> tokens,
        DateTimeOffset throughUtc,
        CancellationToken ct,
        bool includeBeyondThrough = false)
    {
        if (tokens.Count == 0)
        {
            return Array.Empty<(string, ObserverRawTick)>();
        }

        var startUtc = new DateTimeOffset(day.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var queryStart = startUtc.AddMinutes(-1);
        var upper = includeBeyondThrough
            ? new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), IstOffset).ToUniversalTime().AddTicks(-1)
            : throughUtc;
        var tokenArray = tokens.Distinct(StringComparer.Ordinal).ToArray();

        var rows = await db.Ticks
            .AsNoTracking()
            .Where(t => tokenArray.Contains(t.Token)
                && t.ExchangeTimestamp >= queryStart
                && t.ReceivedAt >= queryStart
                && t.ExchangeTimestamp <= upper
                && t.ReceivedAt <= upper)
            .OrderBy(t => t.Id)
            .ToListAsync(ct);

        return rows.Select(x => (x.Token, ToRaw(x))).ToList();
    }

    public async Task<IReadOnlyList<ObserverTokenTick>> ReadCleanSessionAsync(
        NiftySignalDbContext db,
        DateOnly day,
        IReadOnlyCollection<string> tokens,
        DateTimeOffset throughUtc,
        CancellationToken ct)
    {
        var raw = await ReadRawSessionAsync(db, day, tokens, throughUtc, ct);
        var startUtc = new DateTimeOffset(day.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<ObserverTokenTick>(raw.Count);

        foreach (var item in raw)
        {
            var normalized = normalizer.Process(item.Token, item.Tick);
            if (normalized is { } tick && tick.AvailableAt >= startUtc && tick.AvailableAt <= throughUtc)
            {
                clean.Add(new ObserverTokenTick(item.Token, tick));
            }
        }

        clean.Sort(static (a, b) =>
        {
            var byTime = a.Tick.AvailableAt.CompareTo(b.Tick.AvailableAt);
            return byTime != 0 ? byTime : a.Tick.Id.CompareTo(b.Tick.Id);
        });
        return clean;
    }

    public async Task<IReadOnlyList<(string Token, ObserverRawTick Tick)>> ReadRawAfterIdAsync(
        NiftySignalDbContext db,
        IReadOnlyCollection<string> tokens,
        long afterId,
        CancellationToken ct)
    {
        if (tokens.Count == 0)
        {
            return Array.Empty<(string, ObserverRawTick)>();
        }

        var tokenArray = tokens.Distinct(StringComparer.Ordinal).ToArray();
        var rows = await db.Ticks
            .AsNoTracking()
            .Where(t => t.Id > afterId && tokenArray.Contains(t.Token))
            .OrderBy(t => t.Id)
            .ToListAsync(ct);

        return rows.Select(x => (x.Token, ToRaw(x))).ToList();
    }

    public static ObserverRawTick ToRaw(NiftySignal.Domain.Entities.Tick tick)
    {
        var depth = tick.Depth;
        return new ObserverRawTick(
            tick.Id,
            tick.ExchangeTimestamp,
            tick.ReceivedAt,
            (double)tick.LastPrice,
            depth is null ? 0d : (double)depth.Bid1Price,
            depth is null ? 0d : (double)depth.Ask1Price,
            depth?.Bid1Qty ?? 0,
            depth?.Ask1Qty ?? 0,
            tick.Volume,
            tick.OpenInterest);
    }
}
