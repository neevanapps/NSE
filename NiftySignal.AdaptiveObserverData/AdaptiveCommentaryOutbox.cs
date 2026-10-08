using Microsoft.EntityFrameworkCore;

namespace NiftySignal.AdaptiveObserverData;

/// <summary>
/// Shared outbox maintenance used by both the Host and the Dashboard. <see cref="AdaptiveCommentaryNotificationStatus.Suppressed"/> is a
/// terminal state: a suppressed job is never delivered and nothing ever revives it, even after a manual reconciliation.
/// </summary>
public static class AdaptiveCommentaryOutbox
{
    /// <summary>Marks every unsent (Pending) job Suppressed. Returns the number suppressed. Sending / Sent / DeliveryUncertain are untouched.</summary>
    public static async Task<int> SuppressAllPendingAsync(AdaptiveObserverDbContext db, string reason, CancellationToken ct)
    {
        var jobs = await db.CommentaryNotificationJobs.Where(x => x.Status == AdaptiveCommentaryNotificationStatus.Pending).ToListAsync(ct);
        return await SuppressAsync(db, jobs, reason, ct);
    }

    /// <summary>Marks Pending jobs of events at or after <paramref name="fromBarSeq"/> (same session and commentary version) Suppressed.</summary>
    public static async Task<int> SuppressPendingFromBarAsync(
        AdaptiveObserverDbContext db, long sessionId, string commentaryVersion, int fromBarSeq, string reason, CancellationToken ct)
    {
        var jobs = await (from job in db.CommentaryNotificationJobs
                          join ev in db.CommentaryEvents on job.EventId equals ev.Id
                          where job.Status == AdaptiveCommentaryNotificationStatus.Pending && ev.SessionId == sessionId
                              && ev.CommentaryVersion == commentaryVersion && ev.BarSeq >= fromBarSeq
                          select job).ToListAsync(ct);
        return await SuppressAsync(db, jobs, reason, ct);
    }

    static async Task<int> SuppressAsync(AdaptiveObserverDbContext db, List<AdaptiveCommentaryNotificationJobRow> jobs, string reason, CancellationToken ct)
    {
        foreach (var job in jobs)
        {
            job.Status = AdaptiveCommentaryNotificationStatus.Suppressed;
            job.LastError = reason.Length > 512 ? reason[..512] : reason;
        }

        if (jobs.Count > 0) await db.SaveChangesAsync(ct);
        return jobs.Count;
    }
}
