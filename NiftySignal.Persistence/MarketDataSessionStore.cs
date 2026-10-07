using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Persistence;

public static class MarketDataSessionStore
{
    public static async Task<MarketDataCredential?> LoadAsync(NiftySignalDbContext db, MarketDataProvider provider, DateTimeOffset now, CancellationToken ct)
    {
        if (provider == MarketDataProvider.FlatTrade)
        {
            var session = await db.FlatTradeSessions.FindAsync([FlatTradeSession.SingletonId], ct);
            return session is { Token: not null, ClientId: not null } && session.IsValidAt(now)
                ? new MarketDataCredential(session.Token, session.ClientId) : null;
        }
        if (provider == MarketDataProvider.Upstox)
        {
            var session = await db.UpstoxSessions.FindAsync([UpstoxSession.SingletonId], ct);
            return session is not null && session.IsValidAt(now) ? new MarketDataCredential(session.Token!, "") : null;
        }
        throw new InvalidOperationException("Unknown market-data provider.");
    }

    public static async Task EnsureDayAsync(NiftySignalDbContext db, DateOnly day, MarketDataProvider provider, DateTimeOffset now, CancellationToken ct)
    {
        if (!Enum.IsDefined(provider)) throw new InvalidOperationException("Unknown market-data provider.");
        var existing = await db.MarketDataDays.FindAsync([day], ct);
        if (existing is not null)
        {
            Validate(existing.Provider, provider);
            return;
        }
        // Adopt an existing legacy day only if every instrument and raw row agrees.
        var instrumentProviders = await db.Instruments.Where(x => x.AsOfDate == day).Select(x => x.Provider).Distinct().ToListAsync(ct);
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUniversalTime();
        var end = start.AddDays(1);
        var tickProviders = await db.Ticks.Where(x => x.ReceivedAt >= start && x.ReceivedAt < end).Select(x => x.Provider).Distinct().ToListAsync(ct);
        foreach (var value in instrumentProviders.Concat(tickProviders)) Validate(value, provider);
        var row = new MarketDataDay { TradeDate = day, Provider = provider, StartedAtUtc = now };
        db.MarketDataDays.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            existing = await db.MarketDataDays.AsNoTracking().SingleOrDefaultAsync(x => x.TradeDate == day, ct);
            if (existing is null) throw;
            Validate(existing.Provider, provider);
        }
    }

    static void Validate(MarketDataProvider existing, MarketDataProvider selected)
    {
        if (existing != selected)
            throw new InvalidOperationException($"This trading day is frozen to {existing}; switch to {selected} on a new trading day. Existing source history will not be mixed or rewritten.");
    }
}
