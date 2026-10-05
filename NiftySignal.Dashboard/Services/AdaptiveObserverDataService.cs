using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Read-only adaptive observer projection. SignalR only invalidates this view; all values are read
/// from the committed adaptive database so Dashboard restart/reconnect cannot invent state.
/// </summary>
public sealed class AdaptiveObserverDataService(
    IDbContextFactory<AdaptiveObserverDbContext> dbFactory,
    ILogger<AdaptiveObserverDataService> logger)
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    long? _notifiedSessionId;
    int? _notifiedBarSeq;

    public event Action? Updated;

    public long? LastNotifiedSessionId => _notifiedSessionId;
    public int? LastNotifiedBarSeq => _notifiedBarSeq;

    public void NotifyStateChanged(long sessionId, int barSeq)
    {
        _notifiedSessionId = sessionId;
        _notifiedBarSeq = barSeq;
        Updated?.Invoke();
    }

    public async Task<AdaptiveObserverHeaderSnapshot?> LoadHeaderAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await FindTodaySessionAsync(db, ct);
        if (session is null)
        {
            return null;
        }

        var runtime = await db.Runtime.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id, ct);
        return runtime is null ? null : new AdaptiveObserverHeaderSnapshot(session, runtime);
    }

    public async Task<AdaptiveObserverSnapshot?> LoadSnapshotAsync(int requestedRows, CancellationToken ct = default)
    {
        var rowCount = requestedRows switch
        {
            <= 5 => 5,
            <= 10 => 10,
            _ => 15,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await FindTodaySessionAsync(db, ct);
        if (session is null)
        {
            return null;
        }

        var runtime = await db.Runtime.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id, ct);
        if (runtime is null)
        {
            return null;
        }

        var futureBars = await db.FutureBars.AsNoTracking()
            .Where(x => x.SessionId == session.Id)
            .OrderByDescending(x => x.BarSeq)
            .Take(rowCount)
            .ToListAsync(ct);

        var seqs = futureBars.Select(x => x.BarSeq).ToArray();
        var rolling = seqs.Length == 0
            ? new List<AdaptiveRollingStateRow>()
            : await db.RollingStates.AsNoTracking()
                .Where(x => x.SessionId == session.Id && seqs.Contains(x.EndBarSeq))
                .ToListAsync(ct);
        var rollingBySeq = rolling.ToDictionary(x => x.EndBarSeq);

        var futures = futureBars
            .Select(x => new AdaptiveFuturesGridRow(x, rollingBySeq.GetValueOrDefault(x.BarSeq)))
            .ToList();

        var optionRows = seqs.Length == 0
            ? new List<AdaptiveOptionBandBarRow>()
            : await db.OptionBandBars.AsNoTracking()
                .Where(x => x.SessionId == session.Id && seqs.Contains(x.BarSeq))
                .ToListAsync(ct);
        var callBySeq = optionRows.Where(x => x.Side == NiftySignal.Domain.Enums.OptionType.Call).ToDictionary(x => x.BarSeq);
        var putBySeq = optionRows.Where(x => x.Side == NiftySignal.Domain.Enums.OptionType.Put).ToDictionary(x => x.BarSeq);
        var options = futureBars.Select(x => new AdaptiveOptionPairGridRow(
                x.BarSeq,
                x.EndAvailableAtUtc,
                callBySeq.GetValueOrDefault(x.BarSeq),
                putBySeq.GetValueOrDefault(x.BarSeq)))
            .ToList();

        var residuals = seqs.Length == 0
            ? new List<AdaptiveOptionResidualBarRow>()
            : await db.OptionResidualBars.AsNoTracking()
                .Where(x => x.SessionId == session.Id && seqs.Contains(x.BarSeq))
                .OrderByDescending(x => x.BarSeq)
                .ThenBy(x => x.Variant)
                .ToListAsync(ct);

        var anchors = await db.ResidualAnchorComponents.AsNoTracking()
            .Where(x => x.SessionId == session.Id)
            .OrderBy(x => x.Side)
            .ThenBy(x => x.Strike)
            .ToListAsync(ct);

        return new AdaptiveObserverSnapshot(session, runtime, futures, options, residuals, anchors);
    }

    async Task<AdaptiveSessionStateRow?> FindTodaySessionAsync(AdaptiveObserverDbContext db, CancellationToken ct)
    {
        var todayIst = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IstOffset).DateTime);
        try
        {
            return await db.Sessions.AsNoTracking()
                .Where(x => x.TradeDate == todayIst && x.ModelVersion == OpeningVolumeProjectionV1.ModelVersion)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Adaptive dashboard read failed while locating today's session.");
            throw;
        }
    }
}
