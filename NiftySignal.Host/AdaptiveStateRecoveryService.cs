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
    IReadOnlyList<string>? OptionDepthTokens = null);

public sealed class AdaptiveStateRecoveryService(
    AdaptiveSourceTickReader tickReader,
    AdaptiveObserverPersistence persistence,
    AdaptiveWeak2ObservationService observations,
    ILogger<AdaptiveStateRecoveryService> logger,
    AdaptiveSupplementalPersistence? supplemental = null,
    AdaptiveSessionSupplementalService? sessionSupplemental = null,
    FuturesDepth5Gateway? depth5 = null)
{
    public async Task<AdaptiveRecoveryResult> RecoverAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        AdaptiveObserverSessionContext context,
        DateTimeOffset throughUtc,
        CancellationToken ct)
    {
        var runtime = await observer.Runtime.SingleAsync(x => x.SessionId == context.Session.Id, ct);
        runtime.RuntimeStatus = AdaptiveRuntimeStatus.Rebuilding;
        runtime.LastRecoveryStartedUtc = DateTimeOffset.UtcNow;
        runtime.LastError = null;
        await observer.SaveChangesAsync(ct);

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

        IReadOnlyList<(string Token, ObserverRawTick Tick)> raw;
        using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.read"))
            raw = await tickReader.ReadRawSessionAsync(source, context.Session.TradeDate, tokens, throughUtc, ct, includeBeyondThrough: true);
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<ObserverTokenTick>(raw.Count);
        var pending = new List<ObserverTokenTick>();
        var marketOpenUtc = new DateTimeOffset(
            context.Session.TradeDate.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();

        using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.normalize"))
        foreach (var item in raw)
        {
            var normalized = normalizer.Process(item.Token, item.Tick);
            if (normalized is { } tick && tick.AvailableAt >= (context.Session.ObservationStartUtc ?? marketOpenUtc))
            {
                // Normalize the full persisted Id prefix, retaining rows newer than the stable
                // cutoff. Advancing the Id cursor while dropping these rows loses them forever.
                if (tick.AvailableAt <= throughUtc) clean.Add(new ObserverTokenTick(item.Token, tick));
                else pending.Add(new ObserverTokenTick(item.Token, tick));
            }
        }

        using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.sort"))
        clean.Sort(static (a, b) =>
        {
            var byTime = a.Tick.AvailableAt.CompareTo(b.Tick.AvailableAt);
            return byTime != 0 ? byTime : a.Tick.Id.CompareTo(b.Tick.Id);
        });

        // Futures Top-5 depth (flag-gated, default OFF): the same feed code as live polling, derived from the same stored rows of the future token.
        FuturesDepth5Feed? depthFeed = null;
        if (depth5 is { Enabled: true })
        {
            using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.depth5"))
                depthFeed = await depth5.RecoverFeedAsync(source, context.Session.TradeDate, context.Definition.FutureToken,
                    context.Session.ObservationStartUtc ?? marketOpenUtc, ct);
        }

        // Options Top-5 depth (S5; flag-gated, default OFF): bounded-memory lazy source over the frozen option universe's tokens only.
        OptionsDepth5Source? optionsSource = null;
        var optionTokens = context.Options.Select(x => x.Token).Distinct(StringComparer.Ordinal).ToArray();
        if (depth5 is { OptionsEnabled: true } && optionTokens.Length > 0)
            optionsSource = depth5.CreateOptionsSource(source, context.Session.TradeDate, optionTokens, context.Session.ObservationStartUtc ?? marketOpenUtc);

        var engine = new AdaptiveObserverEngine(
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
            trackFuturesDepth5: depthFeed is not null,
            trackOptionsDepth5: optionsSource is not null);
        var anchorStored = !AdaptiveFixedClockV1.IsFixedClock(context.Session.ModelVersion) || context.ResidualAnchor is not null;

        var reconciled = 0;
        var replayTriggers = new HashSet<int>();
        var unverifiedSidecars = new SortedSet<int>();
        DateTimeOffset? lastAvailable = null;
        long? lastTickId = null;
        var replayStart = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var group in clean.GroupBy(x=>x.Tick.AvailableAt))
        {
            var items=group.Select(x=>(x.Token,x.Tick)).ToArray();
            IReadOnlyList<AdaptiveCompletedBarPackage> packages;
            // Every depth event up to and including this boundary is delivered before the group, exactly as live polling does.
            if (depthFeed is not null) foreach (var depthEvent in depthFeed.DrainThrough(group.Key)) engine.ObserveDepth(depthEvent);
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
            }
        }

        if (supplemental is not null)
            logger.LogInformation("Per-strike verification replay finished for session {SessionId}: bars replayed through verification in {ElapsedMs} ms; per-strike retries pending={StrikePending}, close-ATM retries pending={CloseAtmPending} (bars not yet attested stay withheld from readers).",
                context.Session.Id, (long)System.Diagnostics.Stopwatch.GetElapsedTime(replayStart).TotalMilliseconds, supplemental.PendingStrikeRetryCount, supplemental.PendingCloseAtmRetryCount);

        // The options feed's cursor must equal the last stored option row (rows after the stable cutoff stay pending for live polling, exactly like the Level-1 pending rows).
        if (optionsSource is not null) await optionsSource.FinishAsync(ct);

        var rebuiltLastSeq = engine.FutureBars.Where(x => x.IsComplete).Select(x => x.BarSeq).DefaultIfEmpty(0).Max();
        if (await observer.FutureBars.AsNoTracking().AnyAsync(x => x.SessionId == context.Session.Id && x.BarSeq > rebuiltLastSeq, ct))
            throw new InvalidOperationException("Persisted adaptive bars exceed reconstructed source history. Persisted history will not be overwritten.");

        var persistedTriggers = await observer.Weak2Observations.AsNoTracking()
            .Where(x => x.SessionId == context.Session.Id).Select(x => x.TriggerBarSeq).ToListAsync(ct);
        if (!replayTriggers.SetEquals(persistedTriggers))
            throw new InvalidOperationException("Adaptive observation restart trigger set differs from reconstructed source. Persisted history will not be overwritten.");
        using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.verify"))
            await observations.VerifyCompletedAsync(source, observer, context, ct);

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
        await observer.SaveChangesAsync(ct);

        var lastRawId = raw.Count == 0 ? 0L : raw.Max(x => x.Tick.Id);
        logger.LogInformation(
            "Adaptive observer recovery complete for {TradeDate}: raw={RawCount}, clean={CleanCount}, completedBars={Bars}, verified={Verified}, partial={Partial}",
            context.Session.TradeDate, raw.Count, clean.Count, runtime.LastCompletedBarSeq, reconciled, runtime.CurrentPartialBarVolume);

        return new AdaptiveRecoveryResult(
            engine,
            normalizer,
            lastRawId,
            lastAvailable,
            lastTickId,
            reconciled,
            tokens,
            pending,
            unverifiedSidecars,
            depthFeed,
            optionsSource?.Feed,
            optionsSource is null ? null : optionTokens);
    }
}
