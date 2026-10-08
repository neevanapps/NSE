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
    IReadOnlyCollection<int> UnverifiedSidecarBars);

public sealed class AdaptiveStateRecoveryService(
    AdaptiveSourceTickReader tickReader,
    AdaptiveObserverPersistence persistence,
    AdaptiveWeak2ObservationService observations,
    ILogger<AdaptiveStateRecoveryService> logger,
    AdaptiveSupplementalPersistence? supplemental = null)
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

        var tokens = context.Options.Select(x => x.Token)
            .Append(context.Definition.FutureToken)
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

        var engine = new AdaptiveObserverEngine(
            context.Definition,
            context.WeeklyOptionExpiry,
            context.Session.RiskFreeRate,
            context.Session.StrongThreshold,
            context.SignalStartUtc,
            context.Options,
            context.ResidualAnchor);

        var reconciled = 0;
        var replayTriggers = new HashSet<int>();
        var unverifiedSidecars = new SortedSet<int>();
        DateTimeOffset? lastAvailable = null;
        long? lastTickId = null;
        foreach (var group in clean.GroupBy(x=>x.Tick.AvailableAt))
        {
            var items=group.Select(x=>(x.Token,x.Tick)).ToArray();
            IReadOnlyList<AdaptiveCompletedBarPackage> packages;
            using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.engine"))
                packages = engine.ProcessAvailabilityGroup(items);
            lastAvailable = group.Key;
            lastTickId = items.Max(x=>x.Tick.Id);

            foreach (var package in packages)
            {
                if (package.IsActionableWeak2) replayTriggers.Add(package.FutureBar.BarSeq);
                AdaptivePersistResult result;
                using (AdaptiveLiveTelemetry.Source.StartActivity("recovery.core"))
                    result = await persistence.PersistOrVerifyAsync(observer, context.Session, package, DateTimeOffset.UtcNow, ct);
                // Replay recomputes the supplemental metrics: verify rows that exist, insert the ones that do not (mid-session backfill).
                // A bar whose sidecar integrity cannot be established is remembered so commentary never evaluates at or past it.
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
            unverifiedSidecars);
    }
}
