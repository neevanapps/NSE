using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.AdaptiveObserver;

namespace NiftySignal.Dashboard.Services;

public static class AdaptiveScreenshotSchedule
{
    /// <summary>Called under the worker's database lease. Reads committed state, never raw ticks.</summary>
    public static async Task EnqueueAsync(AdaptiveObserverDbContext db, DateOnly day, DateTimeOffset now, CancellationToken ct)
    {
        var sessions = await db.Sessions.AsNoTracking().Where(x => x.TradeDate == day && !x.IsHistoricalSeed && x.ModelVersion == OpeningVolumeProjectionV1.ModelVersion).ToListAsync(ct);
        foreach (var session in sessions)
        {
            if (now < session.OpeningWindowEndUtc) continue;
            var runtime = await db.Runtime.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session.Id, ct);
            if (runtime is null || (runtime.RuntimeStatus != AdaptiveRuntimeStatus.Live && runtime.RuntimeStatus != AdaptiveRuntimeStatus.Closed)) continue;
            var jobs = await db.ScreenshotJobs.Where(x => x.SessionId == session.Id).ToListAsync(ct);
            var initial = jobs.SingleOrDefault(x => x.Kind == AdaptiveScreenshotKind.Initialization);
            if (initial is null)
            {
                var boundary = await db.FutureBars.AsNoTracking()
                    .Where(x => x.SessionId == session.Id && x.EndAvailableAtUtc <= session.OpeningWindowEndUtc)
                    .OrderByDescending(x => x.BarSeq).Select(x => (int?)x.BarSeq).FirstOrDefaultAsync(ct) ?? 0;
                initial = New(session.Id, day, boundary, AdaptiveScreenshotKind.Initialization, session.OpeningWindowEndUtc, now);
                db.ScreenshotJobs.Add(initial);
            }
            var existing = jobs.Where(x => x.Kind == AdaptiveScreenshotKind.FiveBars).Select(x => x.TargetBarSeq).ToHashSet();
            for (var seq = initial.TargetBarSeq + 5; seq <= runtime.LastCompletedBarSeq; seq += 5)
            {
                if (existing.Contains(seq)) continue;
                var bar = await db.FutureBars.AsNoTracking().SingleAsync(x => x.SessionId == session.Id && x.BarSeq == seq, ct);
                db.ScreenshotJobs.Add(New(session.Id, day, seq, AdaptiveScreenshotKind.FiveBars, bar.EndAvailableAtUtc, now));
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public static AdaptiveScreenshotJobRow New(long? sessionId, DateOnly day, int seq, AdaptiveScreenshotKind kind, DateTimeOffset trigger, DateTimeOffset now) =>
        new() { SessionId = sessionId, TradeDate = day, TargetBarSeq = seq, Kind = kind,
            TriggeredAtUtc = trigger, CreatedAtUtc = now, NextAttemptUtc = now };

    public static async Task RecoverInterruptedAsync(AdaptiveObserverDbContext db, CancellationToken ct)
    {
        foreach (var job in await db.ScreenshotJobs.Where(x => x.Status == AdaptiveScreenshotStatus.Capturing || x.Status == AdaptiveScreenshotStatus.Sending).ToListAsync(ct))
        {
            // Telegram has no sendDocument idempotency key. Never blindly resend an ambiguous upload.
            job.Status = job.Status == AdaptiveScreenshotStatus.Sending ? AdaptiveScreenshotStatus.DeliveryUncertain : AdaptiveScreenshotStatus.Pending;
            job.LastError = job.Status == AdaptiveScreenshotStatus.DeliveryUncertain ? "Interrupted upload; confirm delivery in Telegram before any manual retry." : "Interrupted capture; safe to retry.";
        }
        await db.SaveChangesAsync(ct);
    }
}
