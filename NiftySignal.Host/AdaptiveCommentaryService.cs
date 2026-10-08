using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver.Commentary;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Host;

/// <summary>
/// Whether commentary processing may queue Telegram jobs. Catch-up/rebuild work reconstructs events and the checkpoint only; it must never
/// create a notification backlog, so only the bar the live observer has just completed may enqueue.
/// </summary>
public enum CommentaryNotificationMode
{
    /// <summary>Restart/first-deployment catch-up, rebuild and replay: persist events and the checkpoint, create no notification job.</summary>
    Suppress,
    /// <summary>Live forward processing: a NEWLY inserted eligible event on the final (just completed) bar of the call may enqueue; earlier bars of the same call are catch-up and never do.</summary>
    EnqueueFinalBar,
}

/// <summary>
/// Runs the pure <see cref="CommentaryEvaluator"/> over completed bars in strict BarSeq order and persists lifecycle events plus the
/// per-session checkpoint atomically (08-Oct plan sections 56-57). Silent bars (NoMaterialEvent, unchanged continuation) still advance the
/// checkpoint. Replaying a bar can never duplicate an event (unique <c>EventIdentity</c>; an existing row is verified, never overwritten).
/// Failures are logged and never thrown into the core observer: commentary is observational only.
/// A replay mismatch (a stored event that the recomputation disagrees with) never mutates history: processing stops at that bar, a durable
/// <see cref="AdaptiveProjectionHealthRow"/> is written, and later calls do nothing until the marker is reconciled.
/// </summary>
public sealed class AdaptiveCommentaryService(
    AdaptiveCommentaryFrameLoader frames,
    ILogger<AdaptiveCommentaryService> logger)
{
    int _mismatches;
    int _failures;

    public int MismatchCount => Volatile.Read(ref _mismatches);
    public int FailureCount => Volatile.Read(ref _failures);

    /// <summary>Evaluates every completed persisted bar after the checkpoint up to <paramref name="throughBarSeq"/>. Returns the number of bars evaluated.</summary>
    public async Task<int> ProcessThroughAsync(
        AdaptiveObserverDbContext db, AdaptiveSessionStateRow session, int throughBarSeq, CommentaryNotificationMode mode, CancellationToken ct,
        int? stopBeforeBarSeq = null)
    {
        var evaluated = 0;
        // A bar whose sidecar integrity is unknown (and every later bar) is not evaluated now: commentary only consumes verified inputs,
        // and evaluating it later with the verified row is deterministic. Bars before the cap are catch-up, so nothing is enqueued for them.
        var lastBar = stopBeforeBarSeq is { } cap ? Math.Min(throughBarSeq, cap - 1) : throughBarSeq;
        try
        {
            var version = CommentaryEvaluator.Version;
            if (await db.ProjectionHealth.AsNoTracking().AnyAsync(x => x.SessionId == session.Id
                    && x.Component == AdaptiveProjectionComponents.Commentary && x.Version == version, ct))
            {
                logger.LogDebug("Adaptive commentary is stopped by a durable replay-mismatch marker (session={SessionId}); not processing.", session.Id);
                return 0;
            }

            var runtime = await db.CommentaryRuntime.SingleOrDefaultAsync(x => x.SessionId == session.Id && x.CommentaryVersion == version, ct);
            var state = runtime is null ? CommentaryState.Initial : ToState(runtime);
            for (var seq = state.LastEvaluatedBarSeq + 1; seq <= lastBar; seq++)
            {
                var frame = await frames.LoadAsync(db, session, seq, ct);
                if (frame is null) break;                       // bar not persisted (yet): stop; the next call resumes here

                var step = CommentaryEvaluator.Evaluate(state, frame);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                long? lastEventId = runtime?.CurrentEventId;
                var enqueuedAtBar = (int?)null;
                if (step.Event is { } e)
                {
                    var persisted = await PersistOrVerifyEventAsync(db, e, lastEventId, ct);
                    if (persisted.Mismatch is { } mismatch)
                    {
                        await transaction.RollbackAsync(ct);
                        await StopOnMismatchAsync(db, session.Id, version, seq, mismatch, ct);
                        return evaluated;
                    }

                    lastEventId = persisted.Id;
                    // Only a newly inserted event may queue a notification: replaying/verifying existing events never re-queues, and catch-up
                    // (mode Suppress, or any bar before the final bar of a live call) never queues, so a restart cannot create a backlog.
                    if (persisted.Inserted && mode == CommentaryNotificationMode.EnqueueFinalBar && seq == throughBarSeq
                        && await TryEnqueueNotificationAsync(db, e, persisted.Id, ct)) enqueuedAtBar = e.BarSeq;
                }

                runtime = Upsert(db, runtime, session.Id, step.State, lastEventId);
                if (enqueuedAtBar is { } queued) runtime.LastTelegramBarSeq = queued;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                state = step.State;
                evaluated++;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            logger.LogError(ex, "Adaptive commentary evaluation failed (core observer unaffected): session={SessionId}, through bar {Bar}", session.Id, throughBarSeq);
            try { db.ChangeTracker.Clear(); } catch (Exception) { /* the context itself is unusable */ }
        }

        return evaluated;
    }

    /// <summary>
    /// Catches the checkpoint up to the latest persisted completed bar (restart / mid-session deployment / rebuild). Always a non-notifying
    /// backfill: history is reconstructed and persisted, but no Telegram job is created for it.
    /// </summary>
    public Task<int> ProcessAllCompletedAsync(AdaptiveObserverDbContext db, AdaptiveSessionStateRow session, CancellationToken ct) =>
        ProcessAllCompletedAsync(db, session, stopBeforeBarSeq: null, ct);

    /// <summary>As above, never evaluating at or past <paramref name="stopBeforeBarSeq"/> (a bar whose sidecar integrity is unknown).</summary>
    public async Task<int> ProcessAllCompletedAsync(AdaptiveObserverDbContext db, AdaptiveSessionStateRow session, int? stopBeforeBarSeq, CancellationToken ct)
    {
        var last = await db.FutureBars.AsNoTracking().Where(x => x.SessionId == session.Id).Select(x => (int?)x.BarSeq).MaxAsync(ct) ?? 0;
        return last == 0 ? 0 : await ProcessThroughAsync(db, session, last, CommentaryNotificationMode.Suppress, ct, stopBeforeBarSeq);
    }

    async Task StopOnMismatchAsync(AdaptiveObserverDbContext db, long sessionId, string version, int barSeq, string detail, CancellationToken ct)
    {
        Interlocked.Increment(ref _mismatches);
        db.ChangeTracker.Clear();                                // discard the rolled-back unit of work; nothing of it may be saved
        db.ProjectionHealth.Add(new AdaptiveProjectionHealthRow
        {
            SessionId = sessionId, Component = AdaptiveProjectionComponents.Commentary, Version = version, BarSeq = barSeq,
            Detail = detail.Length > 512 ? detail[..512] : detail, DetectedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        // Defense in depth: jobs for events at or after the mismatch are downstream of an unverified state transition and must never be sent.
        // Already-Sent jobs cannot be recalled; Suppressed is terminal and is never revived by a later reconciliation.
        var suppressed = await AdaptiveCommentaryOutbox.SuppressPendingFromBarAsync(db, sessionId, version, barSeq,
            $"Suppressed: commentary projection degraded upstream at bar {barSeq}.", ct);
        logger.LogError("Adaptive commentary STOPPED at bar {Bar} (session={SessionId}, version={Version}); the checkpoint was not advanced and history was not modified. {Detail} ({Suppressed} downstream pending notification(s) suppressed)",
            barSeq, sessionId, version, detail, suppressed);
    }

    /// <summary>Queues at most one Telegram job per event, applying the same-event cooldown (V1: every eligible class bypasses it). Never sends.</summary>
    static async Task<bool> TryEnqueueNotificationAsync(AdaptiveObserverDbContext db, CommentaryEvent e, long eventId, CancellationToken ct)
    {
        if (!e.ShouldNotifyTelegram) return false;
        var last = await (from job in db.CommentaryNotificationJobs
                          join ev in db.CommentaryEvents on job.EventId equals ev.Id
                          where ev.SessionId == e.SessionId
                          orderby ev.BarSeq descending, ev.Id descending
                          select new LastCommentaryNotification(ev.EventType, ev.EventBias, ev.BarSeq)).FirstOrDefaultAsync(ct);
        if (!CommentaryNotificationPolicy.ShouldEnqueue(e, last)) return false;
        if (await db.CommentaryNotificationJobs.AnyAsync(x => x.EventId == eventId, ct)) return false;
        db.CommentaryNotificationJobs.Add(new AdaptiveCommentaryNotificationJobRow
        {
            EventId = eventId, Status = AdaptiveCommentaryNotificationStatus.Pending, CreatedAtUtc = e.OccurredAtUtc, NextAttemptUtc = e.OccurredAtUtc,
        });
        return true;
    }

    async Task<(long Id, bool Inserted, string? Mismatch)> PersistOrVerifyEventAsync(AdaptiveObserverDbContext db, CommentaryEvent e, long? previousEventId, CancellationToken ct)
    {
        var expected = MapEvent(e, previousEventId);
        var existing = await db.CommentaryEvents.SingleOrDefaultAsync(x => x.EventIdentity == expected.EventIdentity, ct);
        if (existing is null)
        {
            db.CommentaryEvents.Add(expected);
            await db.SaveChangesAsync(ct);                      // assigns the Id used as the next event's PreviousEventId
            return (expected.Id, true, null);
        }

        var difference = FirstDifference(existing, expected);
        if (difference is not null)
        {
            return (existing.Id, false, $"{expected.EventIdentity}: {difference}");
        }

        return (existing.Id, false, null);
    }

    public static AdaptiveCommentaryEventRow MapEvent(CommentaryEvent e, long? previousEventId) => new()
    {
        SessionId = e.SessionId,
        TradeDate = e.TradeDate,
        BarSeq = e.BarSeq,
        OccurredAtUtc = e.OccurredAtUtc,
        EventType = e.EventType,
        EventBias = e.EventBias,
        MarketRegime = e.MarketRegime,
        Lifecycle = e.Lifecycle,
        EvidenceAgreement = e.EvidenceAgreement,
        Severity = e.Severity,
        PreviousEventId = previousEventId,
        PreviousBias = e.PreviousBias,
        BiasChanged = e.BiasChanged,
        PrimaryEvidenceJson = AdaptiveCommentaryJson.Items(e.PrimaryEvidence),
        ConfirmationEvidenceJson = AdaptiveCommentaryJson.Items(e.Confirmations),
        ContradictionEvidenceJson = AdaptiveCommentaryJson.Items(e.Contradictions),
        DataQualityJson = AdaptiveCommentaryJson.Notes(e.DataQuality),
        RenderedCommentary = e.RenderedCommentary,
        ShouldNotifyTelegram = e.ShouldNotifyTelegram,
        NotificationReason = e.NotificationReason,
        CommentaryVersion = e.CommentaryVersion,
        EventIdentity = e.Identity,
        CreatedAtUtc = e.OccurredAtUtc,        // deterministic (the bar boundary), so a replayed row is byte-identical
    };

    static string? FirstDifference(AdaptiveCommentaryEventRow actual, AdaptiveCommentaryEventRow expected)
    {
        foreach (var property in typeof(AdaptiveCommentaryEventRow).GetProperties())
        {
            if (property.Name is nameof(AdaptiveCommentaryEventRow.Id)) continue;
            var a = property.GetValue(actual); var b = property.GetValue(expected);
            // JSON columns round-trip through jsonb, which may normalise whitespace: compare them structurally.
            if (property.Name.EndsWith("Json", StringComparison.Ordinal) && a is string sa && b is string sb
                && System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(sa), System.Text.Json.Nodes.JsonNode.Parse(sb))) continue;
            if (!Equals(a, b)) return $"{property.Name}: persisted={a ?? "null"}, recomputed={b ?? "null"}";
        }

        return null;
    }

    static AdaptiveCommentaryRuntimeRow Upsert(AdaptiveObserverDbContext db, AdaptiveCommentaryRuntimeRow? row, long sessionId, CommentaryState state, long? lastEventId)
    {
        if (row is null)
        {
            row = new AdaptiveCommentaryRuntimeRow { SessionId = sessionId, CommentaryVersion = CommentaryEvaluator.Version };
            db.CommentaryRuntime.Add(row);
        }

        row.LastEvaluatedBarSeq = state.LastEvaluatedBarSeq;
        row.CurrentEventId = lastEventId;
        row.CurrentEventType = state.Active?.Type;
        row.CurrentBias = state.CurrentBias;
        row.CurrentRegime = state.Regime;
        row.CurrentLifecycle = state.Active?.Phase;
        row.ActiveBias = state.Active?.Bias;
        row.ActiveStartedBarSeq = state.Active?.StartedBarSeq;
        row.ActiveAgreement = state.Active?.Agreement;
        row.ActiveSupportingFamilies = state.Active is null ? null : string.Join(',', state.Active.SupportingFamilies.Select(x => x.ToString()));
        row.ActivePhase = state.Active?.Phase;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;   // operational timestamp only; not part of any event
        row.CommentaryVersion = CommentaryEvaluator.Version;
        return row;
    }

    static CommentaryState ToState(AdaptiveCommentaryRuntimeRow row)
    {
        ActiveCommentaryEvent? active = row.CurrentEventType is { } type && row.ActiveBias is { } bias && row.ActiveStartedBarSeq is { } started
            && row.ActiveAgreement is { } agreement && row.ActivePhase is { } phase
            ? new ActiveCommentaryEvent(type, bias, started, agreement,
                (row.ActiveSupportingFamilies ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<EvidenceFamily>).ToArray(), phase)
            : null;
        return new CommentaryState(row.CurrentRegime, active, row.CurrentBias, row.LastEvaluatedBarSeq);
    }
}
