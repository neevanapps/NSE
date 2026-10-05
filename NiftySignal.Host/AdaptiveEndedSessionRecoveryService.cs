using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

/// <summary>Resume persisted ended watch sessions after a crash, including outside market hours.</summary>
public sealed class AdaptiveEndedSessionRecoveryService(
    AdaptiveSessionCoordinator coordinator,
    AdaptiveStateRecoveryService recovery,
    AdaptiveWeak2ObservationService observations,
    AdaptiveObserverPersistence persistence,
    ILogger<AdaptiveEndedSessionRecoveryService> logger)
{
    public async Task FinalizeEndedAsync(NiftySignalDbContext source,AdaptiveObserverDbContext db,
        DateOnly todayIst,bool includeToday,CancellationToken ct)
    {
        var ended = await (from session in db.Sessions
            join runtime in db.Runtime on session.Id equals runtime.SessionId
            where session.ModelVersion == OpeningVolumeProjectionV1.ModelVersion && !session.IsHistoricalSeed && runtime.RuntimeStatus != AdaptiveRuntimeStatus.Closed
                && (session.TradeDate < todayIst || (includeToday && session.TradeDate == todayIst))
            orderby session.TradeDate
            select session).ToListAsync(ct);
        foreach(var session in ended)
        {
            var close=new DateTimeOffset(session.TradeDate.ToDateTime(new TimeOnly(15,35)),TimeSpan.FromHours(5.5)).ToUniversalTime();
            try
            {
                var context=await coordinator.TryGetOrCreateAsync(source,db,session.TradeDate,close,ct)
                    ?? throw new InvalidOperationException($"Persisted ended session {session.Id} cannot be reconstructed.");
                await recovery.RecoverAsync(source,db,context,close,ct);
                await observations.FinalizeSessionAsync(source,db,context,ct);
                await persistence.SetRuntimeStatusAsync(db,session.Id,AdaptiveRuntimeStatus.Closed,DateTimeOffset.UtcNow,null,ct);
                logger.LogInformation("Recovered and closed adaptive watch session {SessionId} for {TradeDate}",session.Id,session.TradeDate);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex)
            {
                db.ChangeTracker.Clear(); // Do not flush entities left tracked by a failed replay.
                await persistence.SetRuntimeStatusAsync(db,session.Id,AdaptiveRuntimeStatus.Degraded,DateTimeOffset.UtcNow,ex.Message,ct);
                logger.LogError(ex,"Ended adaptive session {SessionId} failed reconstruction; retained for retry",session.Id);
            }
        }
    }
}
