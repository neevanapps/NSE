using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver.Commentary;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Host;

/// <summary>
/// Runs the pure <see cref="CommentaryEvaluator"/> over completed bars in strict BarSeq order and persists lifecycle events plus the
/// per-session checkpoint atomically (08-Oct plan sections 56-57). Silent bars (NoMaterialEvent, unchanged continuation) still advance the
/// checkpoint. Replaying a bar can never duplicate an event (unique <c>EventIdentity</c>; an existing row is verified, never overwritten).
/// Failures are logged and never thrown into the core observer: commentary is observational only.
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
    public async Task<int> ProcessThroughAsync(AdaptiveObserverDbContext db, AdaptiveSessionStateRow session, int throughBarSeq, CancellationToken ct)
    {
        var evaluated = 0;
        try
        {
            var runtime = await db.CommentaryRuntime.SingleOrDefaultAsync(x => x.SessionId == session.Id, ct);
            var state = runtime is null ? CommentaryState.Initial : ToState(runtime);
            for (var seq = state.LastEvaluatedBarSeq + 1; seq <= throughBarSeq; seq++)
            {
                var frame = await frames.LoadAsync(db, session, seq, ct);
                if (frame is null) break;                       // bar not persisted (yet): stop; the next call resumes here

                var step = CommentaryEvaluator.Evaluate(state, frame);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                long? lastEventId = runtime?.CurrentEventId;
                if (step.Event is { } e)
                {
                    lastEventId = await PersistOrVerifyEventAsync(db, e, lastEventId, ct);
                }

                runtime = Upsert(db, runtime, session.Id, step.State, lastEventId);
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

    /// <summary>Catches the checkpoint up to the latest persisted completed bar (restart / mid-session deployment).</summary>
    public async Task<int> ProcessAllCompletedAsync(AdaptiveObserverDbContext db, AdaptiveSessionStateRow session, CancellationToken ct)
    {
        var last = await db.FutureBars.AsNoTracking().Where(x => x.SessionId == session.Id).Select(x => (int?)x.BarSeq).MaxAsync(ct) ?? 0;
        return last == 0 ? 0 : await ProcessThroughAsync(db, session, last, ct);
    }

    async Task<long?> PersistOrVerifyEventAsync(AdaptiveObserverDbContext db, CommentaryEvent e, long? previousEventId, CancellationToken ct)
    {
        var expected = MapEvent(e, previousEventId);
        var existing = await db.CommentaryEvents.SingleOrDefaultAsync(x => x.EventIdentity == expected.EventIdentity, ct);
        if (existing is null)
        {
            db.CommentaryEvents.Add(expected);
            await db.SaveChangesAsync(ct);                      // assigns the Id used as the next event's PreviousEventId
            return expected.Id;
        }

        var difference = FirstDifference(existing, expected);
        if (difference is not null)
        {
            Interlocked.Increment(ref _mismatches);
            logger.LogError("Adaptive commentary event mismatch on replay (stored row left untouched): {Identity}, {Difference}", expected.EventIdentity, difference);
        }

        return existing.Id;
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
