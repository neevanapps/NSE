using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.AdaptiveObserver;

namespace NiftySignal.Dashboard.Services;

/// <summary>A durable event that may justify an extra screenshot (commentary Confirmed / Flipped, NPT1 trade open / close).</summary>
public sealed record ScreenshotEventCandidate(string Id, DateTimeOffset AtUtc, string Kind);

public static class AdaptiveScreenshotSchedule
{
    /// <summary>Every fifth completed bar after the first one.</summary>
    public const int ScheduledEveryBars = 5;

    // Last completed bar whose events were already scanned, per session: events are evaluated once per newly completed bar (an event can only be mapped to a bar once that bar completed).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(long SessionId, DateTimeOffset CreatedAtUtc), int> EventScanned = new();

    /// <summary>Called under the worker's database lease. Reads committed state, never raw ticks.</summary>
    public static async Task EnqueueAsync(AdaptiveObserverDbContext db, DateOnly day, DateTimeOffset now, CancellationToken ct,
        Func<DateOnly, CancellationToken, Task<IReadOnlyList<ScreenshotEventCandidate>>>? ledgerEvents = null)
    {
        var fixedSessions = await db.Sessions.AsNoTracking().Where(x => x.TradeDate == day && !x.IsHistoricalSeed && x.ModelVersion == AdaptiveFixedClockV1.ModelVersion).ToListAsync(ct);
        foreach (var session in fixedSessions)
        {
            await EnqueueFixedClockAsync(db, session, day, now, ledgerEvents, ct);
        }

        // adaptive-v1 sessions keep their original schedule exactly: the fixed-clock rules are never applied to them.
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

    /// <summary>
    /// Fixed 13,650 clock: the FIRST completed bar, then every fifth completed bar after it (1, 6, 11, ...), completed bars only. In addition, a meaningful durable event
    /// (commentary Confirmed / Flipped, NPT1 trade open / close) schedules ONE capture of the first completed bar at or after the event, unless that bar already has any capture. Several
    /// events that map to the same boundary therefore share one screenshot, which carries the RECENT EVENTS strip.
    /// </summary>
    static async Task EnqueueFixedClockAsync(AdaptiveObserverDbContext db, AdaptiveSessionStateRow session, DateOnly day, DateTimeOffset now,
        Func<DateOnly, CancellationToken, Task<IReadOnlyList<ScreenshotEventCandidate>>>? ledgerEvents, CancellationToken ct)
    {
        var runtime = await db.Runtime.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session.Id, ct);
        if (runtime is null || (runtime.RuntimeStatus != AdaptiveRuntimeStatus.Live && runtime.RuntimeStatus != AdaptiveRuntimeStatus.Closed) || runtime.LastCompletedBarSeq < 1)
        {
            return;
        }

        var last = runtime.LastCompletedBarSeq;
        var jobs = await db.ScreenshotJobs.Where(x => x.SessionId == session.Id).ToListAsync(ct);
        var taken = jobs.Where(x => x.Kind != AdaptiveScreenshotKind.PreLiveTest).Select(x => x.TargetBarSeq).ToHashSet();
        var bars = await db.FutureBars.AsNoTracking().Where(x => x.SessionId == session.Id && x.BarSeq <= last)
            .OrderBy(x => x.BarSeq).Select(x => new { x.BarSeq, x.EndAvailableAtUtc }).ToListAsync(ct);
        if (bars.Count == 0)
        {
            return;
        }

        var initial = jobs.SingleOrDefault(x => x.Kind == AdaptiveScreenshotKind.Initialization);
        if (initial is null)
        {
            var first = bars[0];
            initial = New(session.Id, day, first.BarSeq, AdaptiveScreenshotKind.Initialization, first.EndAvailableAtUtc, now);
            db.ScreenshotJobs.Add(initial);
            taken.Add(first.BarSeq);
        }

        var endOf = bars.ToDictionary(b => b.BarSeq, b => b.EndAvailableAtUtc);
        for (var seq = initial.TargetBarSeq + ScheduledEveryBars; seq <= last; seq += ScheduledEveryBars)
        {
            if (taken.Contains(seq) || !endOf.TryGetValue(seq, out var end)) continue;
            db.ScreenshotJobs.Add(New(session.Id, day, seq, AdaptiveScreenshotKind.FiveBars, end, now));
            taken.Add(seq);
        }

        if (EventScanned.TryGetValue((session.Id, session.CreatedAtUtc), out var scanned) && scanned >= last)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var events = new List<ScreenshotEventCandidate>();
        var version = NiftySignal.AdaptiveObserver.Commentary.CommentaryEvaluator.Version;
        try
        {
            var commentary = await db.CommentaryEvents.AsNoTracking()
                .Where(x => x.SessionId == session.Id && x.CommentaryVersion == version && x.BarSeq <= last
                    && (x.Lifecycle == NiftySignal.AdaptiveObserver.Commentary.CommentaryLifecycle.Confirmed || x.Lifecycle == NiftySignal.AdaptiveObserver.Commentary.CommentaryLifecycle.Flipped))
                .ToListAsync(ct);
            events.AddRange(commentary.Select(e => new ScreenshotEventCandidate($"c:{e.Id}", e.OccurredAtUtc, "Commentary")));
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UndefinedTable) { }

        if (ledgerEvents is not null)
        {
            try { events.AddRange(await ledgerEvents(day, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* ledger unreadable: scheduled captures are unaffected */ }
        }

        foreach (var e in events.OrderBy(e => e.AtUtc).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            var target = bars.FirstOrDefault(b => b.EndAvailableAtUtc >= e.AtUtc);        // first completed bar at or after the event; none yet = wait for it
            if (target is null || taken.Contains(target.BarSeq)) continue;
            db.ScreenshotJobs.Add(New(session.Id, day, target.BarSeq, AdaptiveScreenshotKind.EventTriggered, e.AtUtc, now));
            taken.Add(target.BarSeq);
        }

        await db.SaveChangesAsync(ct);
        EventScanned[(session.Id, session.CreatedAtUtc)] = last;
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
