using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

public sealed record AdaptiveRecoveryResult(
    AdaptiveObserverEngine Engine,
    AdaptiveIncrementalTickNormalizer Normalizer,
    long LastFetchedRawId,
    DateTimeOffset? LastProcessedAvailableAt,
    long? LastProcessedTickId,
    int ReconciledBars,
    IReadOnlyList<string> Tokens,
    IReadOnlyList<ObserverTokenTick> Pending,
    IReadOnlyCollection<int> UnverifiedSidecarBars,
    FuturesDepth5Feed? Depth5Feed = null,
    FuturesDepth5Feed? OptionsDepth5Feed = null,
    IReadOnlyList<string>? OptionDepthTokens = null,
    IReadOnlyCollection<int>? TriggerBars = null,
    string? CheckpointIdentity = null);

public sealed class AdaptiveStateRecoveryService(
    AdaptiveSourceTickReader tickReader,
    AdaptiveObserverPersistence persistence,
    AdaptiveWeak2ObservationService observations,
    ILogger<AdaptiveStateRecoveryService> logger,
    AdaptiveSupplementalPersistence? supplemental = null,
    AdaptiveSessionSupplementalService? sessionSupplemental = null,
    FuturesDepth5Gateway? depth5 = null,
    Microsoft.Extensions.Options.IOptions<AdaptiveRecoveryOptions>? recoveryOptions = null,
    AdaptiveCheckpointStore? checkpoints = null,
    AdaptiveDepthTickReader? depthReader = null)
{
    readonly AdaptiveRecoveryOptions _options = recoveryOptions?.Value ?? new AdaptiveRecoveryOptions();

    /// <summary>
    /// Deterministic recovery of a session: from the newest usable checkpoint, otherwise a replay of the whole day. With a <paramref name="timeLimit"/> (live starts) the attempt is cancelled cooperatively once it is exceeded and reported as
    /// <see cref="AdaptiveRecoveryTimeoutException"/>; the caller awaits it to completion, so a timed-out attempt never runs beside the next one.
    /// </summary>
    public async Task<AdaptiveRecoveryResult> RecoverAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        AdaptiveObserverSessionContext context,
        DateTimeOffset throughUtc,
        CancellationToken ct,
        TimeSpan? timeLimit = null)
    {
        var progress = new AdaptiveRecoveryProgress(observer, context.Session.Id, TimeSpan.FromMilliseconds(_options.ProgressIntervalMilliseconds));
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeLimit is { } limit)
        {
            if (limit <= TimeSpan.Zero) await limited.CancelAsync();
            else limited.CancelAfter(limit);
        }
        try
        {
            return await ReplayAsync(source, observer, context, throughUtc, progress, limited.Token);
        }
        catch (Exception ex) when (timeLimit is { } exceeded && limited.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogError("Adaptive recovery TIMED OUT for session {SessionId} ({Reached}); phase timings: {Timings}", context.Session.Id, progress.Describe(), progress.Timings());
            throw new AdaptiveRecoveryTimeoutException(exceeded, progress.Describe(), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Adaptive recovery failed for session {SessionId} ({Reached}); phase timings: {Timings}", context.Session.Id, progress.Describe(), progress.Timings());
            throw;
        }
    }

    async Task<AdaptiveRecoveryResult> ReplayAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        AdaptiveObserverSessionContext context,
        DateTimeOffset throughUtc,
        AdaptiveRecoveryProgress progress,
        CancellationToken ct)
    {
        var runtime = await observer.Runtime.SingleAsync(x => x.SessionId == context.Session.Id, ct);
        runtime.RuntimeStatus = AdaptiveRuntimeStatus.Rebuilding;
        runtime.LastRecoveryStartedUtc = DateTimeOffset.UtcNow;
        runtime.LastHeartbeatUtc = runtime.LastRecoveryStartedUtc.Value;
        runtime.LastRecoveryProgressUtc = runtime.LastRecoveryStartedUtc;
        runtime.RecoveryPhase = progress.Phase;
        runtime.RecoveryTicksProcessed = 0;
        runtime.RecoveryBarsReplayed = 0;
        runtime.RecoveryPersistedBarsAtStart = null;
        runtime.RecoveryReplayedThroughUtc = null;
        // LastError of a previous failed attempt stays visible until an attempt succeeds.
        await observer.SaveChangesAsync(ct);
        progress.PersistedBarsAtStart = await observer.FutureBars.AsNoTracking().CountAsync(x => x.SessionId == context.Session.Id, ct);
        await progress.EnterAsync("preparing", ct);

        // Frozen supplemental spot identity (resolved once per session, reused on every restart). Failure to freeze it never stops the core
        // observer: Basis is simply unavailable for this process.
        string? spotToken = context.SpotToken;
        if (sessionSupplemental is not null && spotToken is null)
        {
            try { spotToken = await sessionSupplemental.GetOrFreezeSpotTokenAsync(source, observer, context.Session, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Adaptive supplemental spot identity unavailable; basis disabled for this process (core observer unaffected).");
                observer.ChangeTracker.Clear();
            }
        }

        // The ATM rule is part of the session's frozen contract: a stamped session replays under the rule it was created with, an unstamped (older) one under the legacy rule. An unknown stamp throws.
        var atmRule = AtmCenterRules.FromVersion(context.Session.AtmRuleVersion);
        if (atmRule == AtmCenterRule.SpotV1 && string.IsNullOrWhiteSpace(spotToken))
        {
            logger.LogWarning("Adaptive session {SessionId} uses {Rule} but has no frozen spot instrument; the weekly ATM±2 band stays unavailable for this session (no fallback to another price).",
                context.Session.Id, AtmCenterRules.SpotV1Version);
        }

        var tokens = context.Options.Select(x => x.Token)
            .Append(context.Definition.FutureToken)
            .Concat(string.IsNullOrWhiteSpace(spotToken) ? [] : [spotToken!])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var marketOpenUtc = new DateTimeOffset(
            context.Session.TradeDate.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();
        var notBefore = context.Session.ObservationStartUtc ?? marketOpenUtc;
        var futuresDepth = depth5 is { Enabled: true };
        var optionTokens = context.Options.Select(x => x.Token).Distinct(StringComparer.Ordinal).ToArray();
        var optionsDepth = depth5 is { OptionsEnabled: true } && optionTokens.Length > 0;
        var window = TimeSpan.FromSeconds(_options.ReadWindowSeconds);

        // The newest usable checkpoint (same session, configuration and build; verified payload and persisted bars) replaces the replay up to its boundary. Without one the whole day is replayed.
        string? identity = null;
        (long Id, AdaptiveCheckpointState State)? restored = null;
        if (checkpoints is { Enabled: true })
        {
            identity = AdaptiveCheckpointStore.Identity(context, spotToken, futuresDepth, optionsDepth);
            await progress.EnterAsync("restoring-checkpoint", ct);
            restored = await checkpoints.FindAsync(observer, context.Session.Id, identity, throughUtc, ct);
        }

        var checkpoint = restored?.State;
        var reader = depthReader ?? new AdaptiveDepthTickReader();
        // Futures Top-5 depth (flag-gated, default OFF): the same feed code as live polling, derived from the same stored rows of the future token.
        IAdaptiveDepthSource? futuresSource = null;
        if (futuresDepth)
            futuresSource = checkpoint is null
                ? depth5!.CreateFuturesSource(source, context.Session.TradeDate, context.Definition.FutureToken, notBefore)
                : new AdaptiveCursorDepthSource(reader, source, [context.Definition.FutureToken], context.Session.TradeDate, notBefore, window, _options.LiveReadBatchSize,
                    checkpoint.Depth5Feed!, checkpoint.Depth5Position!);
        var depthFeed = futuresSource?.Feed;

        // Options Top-5 depth (S5; flag-gated, default OFF): bounded-memory lazy source over the frozen option universe's tokens only.
        IAdaptiveDepthSource? optionsSource = null;
        if (optionsDepth)
            optionsSource = checkpoint is null
                ? depth5!.CreateOptionsSource(source, context.Session.TradeDate, optionTokens, notBefore)
                : new AdaptiveCursorDepthSource(reader, source, optionTokens, context.Session.TradeDate, notBefore, window, _options.LiveReadBatchSize,
                    checkpoint.OptionsDepth5Feed!, checkpoint.OptionsDepth5Position!);

        // Bounded memory: the day is read in slices (or Id batches after a checkpoint) and handed to the engine as soon as its order is final.
        IAdaptiveTickStream stream = checkpoint is null
            ? new AdaptiveRecoveryTickStream(tickReader, source, context.Session.TradeDate, tokens, notBefore, throughUtc, window)
            : new AdaptiveCursorTickStream(tickReader, source, context.Session.TradeDate, tokens, notBefore, throughUtc, window, _options.LiveReadBatchSize,
                checkpoint.Normalizer, checkpoint.Position, checkpoint.Unprocessed);

        var engine = checkpoint?.Engine ?? new AdaptiveObserverEngine(
            context.Definition,
            context.WeeklyOptionExpiry,
            context.Session.RiskFreeRate,
            context.Session.StrongThreshold,
            context.SignalStartUtc,
            context.Options,
            context.ResidualAnchor,
            spotToken,
            projectStrikes: true,
            selfAnchorCutoffUtc: AdaptiveFixedClockV1.IsFixedClock(context.Session.ModelVersion) ? context.Session.OpeningWindowEndUtc : null,
            atmRule: atmRule,
            trackFuturesDepth5: futuresSource is not null,
            trackOptionsDepth5: optionsSource is not null);
        var anchorStored = !AdaptiveFixedClockV1.IsFixedClock(context.Session.ModelVersion) || context.ResidualAnchor is not null;

        var reconciled = 0;
        var replayTriggers = new HashSet<int>(checkpoint?.TriggerBars ?? []);
        var unverifiedSidecars = new SortedSet<int>();
        DateTimeOffset? lastAvailable = checkpoint?.LastProcessedAvailableAt;
        long? lastTickId = checkpoint?.LastProcessedTickId;
        if (checkpoint is not null)
        {
            reconciled = progress.BarsReplayed = engine.FutureBars.Count(x => x.IsComplete);
            progress.ReplayedThroughUtc = lastAvailable;
            logger.LogInformation("Adaptive recovery of session {SessionId} continues from checkpoint {Checkpoint}: {Bars} bars restored, processed through {Through:O}.",
                context.Session.Id, restored!.Value.Id, reconciled, lastAvailable);
        }

        checkpoints?.StartInterval();
        var replayStart = System.Diagnostics.Stopwatch.GetTimestamp();
        await progress.EnterAsync("replaying", ct);
        try
        {
            while (true)
            {
                IReadOnlyList<ObserverTokenTick>? batch;
                using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.read"))
                    batch = await stream.NextAsync(ct);
                if (batch is null) break;
                await progress.AdvancedAsync(ct);
                foreach (var group in batch.GroupBy(x=>x.Tick.AvailableAt))
                {
                    ct.ThrowIfCancellationRequested();
                    var items=group.Select(x=>(x.Token,x.Tick)).ToArray();
                    IReadOnlyList<AdaptiveCompletedBarPackage> packages;
                    // Every depth event up to and including this boundary is delivered before the group, exactly as live polling does.
                    if (futuresSource is not null)
                    {
                        await futuresSource.AdvanceToAsync(group.Key, ct);
                        foreach (var depthEvent in futuresSource.Feed.DrainThrough(group.Key)) engine.ObserveDepth(depthEvent);
                    }
                    if (optionsSource is not null)
                    {
                        await optionsSource.AdvanceToAsync(group.Key, ct);
                        foreach (var depthEvent in optionsSource.Feed.DrainThrough(group.Key)) engine.ObserveDepth(depthEvent);
                    }
                    using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.engine"))
                        packages = engine.ProcessAvailabilityGroup(items);
                    lastAvailable = group.Key;
                    lastTickId = items.Max(x=>x.Tick.Id);
                    if (!anchorStored && engine.SelfAnchorAttempted)
                    {
                        // The engine froze the 09:30 anchor on this boundary; it is stored before any bar that depends on it.
                        await persistence.PersistResidualAnchorOnceAsync(observer, context.Session.Id, engine.ResidualAnchor, DateTimeOffset.UtcNow, ct);
                        anchorStored = true;
                    }

                    foreach (var package in packages)
                    {
                        if (package.IsActionableWeak2) replayTriggers.Add(package.FutureBar.BarSeq);
                        AdaptivePersistResult result;
                        using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.core"))
                            result = await persistence.PersistOrVerifyAsync(observer, context.Session, package, DateTimeOffset.UtcNow, ct);
                        // Replay recomputes the supplemental metrics: verify rows that exist, insert the ones that do not (mid-session backfill).
                        // A bar whose sidecar integrity cannot be established is remembered so the paper path never receives it (or any later bar) as verified.
                        if (supplemental is not null)
                        {
                            using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.sidecars"))
                                if (!await supplemental.PersistAndVerifyAsync(observer, context.Session, package, ct))
                                    unverifiedSidecars.Add(package.FutureBar.BarSeq);
                        }
                        using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.observations"))
                            await observations.ProcessPackageAsync(source, observer, context, package, ct);
                        if (result.VerifiedExisting)
                        {
                            reconciled++;
                        }
                        progress.BarsReplayed++;
                        await progress.AdvancedAsync(ct);
                    }

                    progress.TicksProcessed += items.Length;
                    progress.ReplayedThroughUtc = group.Key;
                    await progress.AdvancedAsync(ct);
                }

                // Between two batches the state is at a consistent boundary. A checkpoint here lets a timed-out or interrupted attempt continue instead of starting again.
                // Never while a bar's sidecars are unsettled: a restore skips the replay that would verify them.
                if (identity is not null && unverifiedSidecars.Count == 0 && SupplementalIsSettled() && checkpoints!.IsDue())
                {
                    var (position, unreleased) = stream.Capture();
                    await checkpoints.TrySaveAsync(observer, identity, new AdaptiveCheckpointState
                    {
                        SessionId = context.Session.Id, Engine = engine, Normalizer = stream.Normalizer, Position = position, Unprocessed = unreleased,
                        LastProcessedAvailableAt = lastAvailable, LastProcessedTickId = lastTickId, TriggerBars = replayTriggers.ToList(),
                        Depth5Feed = futuresSource?.Feed, Depth5Position = futuresSource?.Capture(),
                        OptionsDepth5Feed = optionsSource?.Feed, OptionsDepth5Position = optionsSource?.Capture(),
                    }, ct, restored?.Id);
                }
            }

            await progress.EnterAsync("finishing-depth", ct);

            if (supplemental is not null)
                logger.LogInformation("Per-strike verification replay finished for session {SessionId}: bars replayed through verification in {ElapsedMs} ms; per-strike retries pending={StrikePending}, close-ATM retries pending={CloseAtmPending} (bars not yet attested stay withheld from readers).",
                    context.Session.Id, (long)System.Diagnostics.Stopwatch.GetElapsedTime(replayStart).TotalMilliseconds, supplemental.PendingStrikeRetryCount, supplemental.PendingCloseAtmRetryCount);

            // Each depth feed's cursor must equal the last stored row of its tokens (rows after the stable cutoff stay pending for live polling, exactly like the Level-1 pending rows).
            if (futuresSource is not null) await futuresSource.FinishAsync(ct);
            if (optionsSource is not null) await optionsSource.FinishAsync(ct);
            var outOfOrder = (futuresSource?.OutOfOrderRows ?? 0) + (optionsSource?.OutOfOrderRows ?? 0);
            if (outOfOrder > 0) logger.LogError("Adaptive recovery depth rows arrived out of per-token Id order beyond the read-ahead: {Count} row(s).", outOfOrder);
            await progress.EnterAsync("verifying", ct);

            var rebuiltLastSeq = engine.FutureBars.Where(x => x.IsComplete).Select(x => x.BarSeq).DefaultIfEmpty(0).Max();
            if (await observer.FutureBars.AsNoTracking().AnyAsync(x => x.SessionId == context.Session.Id && x.BarSeq > rebuiltLastSeq, ct))
                throw new InvalidOperationException("Persisted adaptive bars exceed reconstructed source history. Persisted history will not be overwritten.");

            var persistedTriggers = await observer.Weak2Observations.AsNoTracking()
                .Where(x => x.SessionId == context.Session.Id).Select(x => x.TriggerBarSeq).ToListAsync(ct);
            if (!replayTriggers.SetEquals(persistedTriggers))
                throw new InvalidOperationException("Adaptive observation restart trigger set differs from reconstructed source. Persisted history will not be overwritten.");
            using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.verify"))
                await observations.VerifyCompletedAsync(source, observer, context, ct);
        }
        catch (Exception ex) when (restored is not null && ex is not OperationCanceledException)
        {
            // An attempt that fails after a restore, in the replay or in the verification that follows it, must not be retried from the same state.
            logger.LogError(ex, "Adaptive recovery failed after restoring checkpoint {Checkpoint}; it and any newer one are discarded.", restored.Value.Id);
            try
            {
                observer.ChangeTracker.Clear();
                await checkpoints!.InvalidateFromAsync(observer, context.Session.Id, restored.Value.Id, CancellationToken.None);
            }
            catch (Exception cleanup)
            {
                logger.LogError(cleanup, "Could not discard adaptive checkpoint {Checkpoint}.", restored.Value.Id);
            }

            throw;
        }

        // The restored state has now been replayed and verified: the rows held back for a fallback are subject to retention again.
        if (identity is not null) await checkpoints!.PruneAsync(observer, context.Session.Id, identity, ct);

        runtime = await observer.Runtime.SingleAsync(x => x.SessionId == context.Session.Id, ct);
        runtime.RuntimeStatus = AdaptiveRuntimeStatus.Live;
        runtime.LastHeartbeatUtc = DateTimeOffset.UtcNow;
        runtime.LastProcessedSourceAvailableAtUtc = lastAvailable;
        runtime.LastProcessedSourceTickId = lastTickId;
        runtime.LastCompletedBarSeq = engine.FutureBars.Where(x => x.IsComplete).Select(x => x.BarSeq).DefaultIfEmpty(0).Max();
        runtime.CurrentPartialBarVolume = engine.PartialBar.AccumulatedVolume;
        runtime.CurrentPartialBarStartedAtUtc = engine.PartialBar.StartedAtUtc;
        runtime.LastRecoveryCompletedUtc = DateTimeOffset.UtcNow;
        runtime.LastRecoveryReconciledBars = reconciled;
        runtime.LastError = null;
        runtime.RecoveryFailureCount = 0;
        runtime.RecoveryPhase = "complete";
        runtime.LastRecoveryProgressUtc = runtime.LastRecoveryCompletedUtc;
        runtime.RecoveryTicksProcessed = progress.TicksProcessed;
        runtime.RecoveryBarsReplayed = progress.BarsReplayed;
        runtime.RecoveryPersistedBarsAtStart = progress.PersistedBarsAtStart;
        runtime.RecoveryReplayedThroughUtc = progress.ReplayedThroughUtc;
        await observer.SaveChangesAsync(ct);

        logger.LogInformation(
            "Adaptive observer recovery complete for {TradeDate}: raw={RawCount}, clean={CleanCount}, completedBars={Bars}, verified={Verified}, partial={Partial}, peakBufferedRows={Peak}, phase timings: {Timings}",
            context.Session.TradeDate, stream.RawRows, stream.CleanRows, runtime.LastCompletedBarSeq, reconciled, runtime.CurrentPartialBarVolume, stream.PeakBufferedRows, progress.Timings());

        return new AdaptiveRecoveryResult(
            engine,
            stream.Normalizer,
            stream.LastRawId,
            lastAvailable,
            lastTickId,
            reconciled,
            tokens,
            stream.Pending,
            unverifiedSidecars,
            depthFeed,
            optionsSource?.Feed,
            optionsSource is null ? null : optionTokens,
            replayTriggers,
            identity);

        bool SupplementalIsSettled() => supplemental is null
            || supplemental.PendingStrikeRetryCount + supplemental.PendingCloseAtmRetryCount + supplemental.PendingDepth5RetryCount + supplemental.PendingOptionsDepth5RetryCount == 0;
    }
}
