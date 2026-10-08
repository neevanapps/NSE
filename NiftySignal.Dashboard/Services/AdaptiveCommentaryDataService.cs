using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using Npgsql;

namespace NiftySignal.Dashboard.Services;

/// <summary>What the Live Market Commentary panel shows: the session's current regime/bias and its latest persisted lifecycle events.</summary>
public sealed record AdaptiveCommentaryPanelModel(
    long? SessionId,
    AdaptiveCommentaryRuntimeRow? Runtime,
    IReadOnlyList<AdaptiveCommentaryEventRow> Events,
    // Durable replay-mismatch markers: when present, commentary is stopped and must not be read as healthy.
    IReadOnlyList<AdaptiveProjectionHealthRow>? Degraded = null,
    // Degraded only: events at or before this bar are verified; later stored rows are quarantined (kept in PostgreSQL, never shown as current).
    int VerifiedThroughBar = 0,
    int QuarantinedCount = 0)
{
    public bool IsDegraded => Degraded is { Count: > 0 };
}

/// <summary>Read-only projection of the persisted commentary store. A missing commentary table means "nothing to show", never a failed Dashboard.</summary>
public sealed class AdaptiveCommentaryDataService(IDbContextFactory<AdaptiveObserverDbContext> dbFactory)
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public async Task<AdaptiveCommentaryPanelModel> LoadLatestAsync(int count, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IstOffset).DateTime);
            var session = await db.Sessions.AsNoTracking()
                .Where(x => x.TradeDate == today && x.ModelVersion == OpeningVolumeProjectionV1.ModelVersion)
                .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
            if (session is null) return new AdaptiveCommentaryPanelModel(null, null, []);

            var version = NiftySignal.AdaptiveObserver.Commentary.CommentaryEvaluator.Version;
            var runtime = await db.CommentaryRuntime.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session.Id && x.CommentaryVersion == version, ct);
            var degraded = await db.ProjectionHealth.AsNoTracking()
                .Where(x => x.SessionId == session.Id && x.Component == AdaptiveProjectionComponents.Commentary && x.Version == version)
                .OrderBy(x => x.BarSeq).ToListAsync(ct);
            var events = db.CommentaryEvents.AsNoTracking().Where(x => x.SessionId == session.Id && x.CommentaryVersion == version);
            var verifiedThrough = runtime?.LastEvaluatedBarSeq ?? 0;
            var quarantined = 0;
            if (degraded.Count > 0)
            {
                // Rows after the verified checkpoint sit downstream of an unverified state transition: keep them (forensics), never show them as current.
                quarantined = await events.CountAsync(x => x.BarSeq > verifiedThrough, ct);
                events = events.Where(x => x.BarSeq <= verifiedThrough);
            }

            var shown = await events.OrderByDescending(x => x.BarSeq).ThenByDescending(x => x.Id).Take(Math.Clamp(count, 1, 50)).ToListAsync(ct);
            return new AdaptiveCommentaryPanelModel(session.Id, runtime, shown, degraded, verifiedThrough, quarantined);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new AdaptiveCommentaryPanelModel(null, null, []);
        }
    }
}
