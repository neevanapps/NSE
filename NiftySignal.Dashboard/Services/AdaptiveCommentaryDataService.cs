using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using Npgsql;

namespace NiftySignal.Dashboard.Services;

/// <summary>What the Live Market Commentary panel shows: the session's current regime/bias and its latest persisted lifecycle events.</summary>
public sealed record AdaptiveCommentaryPanelModel(
    long? SessionId,
    AdaptiveCommentaryRuntimeRow? Runtime,
    IReadOnlyList<AdaptiveCommentaryEventRow> Events);

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

            var runtime = await db.CommentaryRuntime.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session.Id, ct);
            var events = await db.CommentaryEvents.AsNoTracking().Where(x => x.SessionId == session.Id)
                .OrderByDescending(x => x.BarSeq).ThenByDescending(x => x.Id).Take(Math.Clamp(count, 1, 50)).ToListAsync(ct);
            return new AdaptiveCommentaryPanelModel(session.Id, runtime, events);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new AdaptiveCommentaryPanelModel(null, null, []);
        }
    }
}
