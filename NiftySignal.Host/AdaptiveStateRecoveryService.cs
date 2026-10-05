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
    IReadOnlyList<string> Tokens);

public sealed class AdaptiveStateRecoveryService(
    AdaptiveSourceTickReader tickReader,
    AdaptiveObserverPersistence persistence,
    AdaptiveWeak2ObservationService observations,
    ILogger<AdaptiveStateRecoveryService> logger)
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

        var raw = await tickReader.ReadRawSessionAsync(source, context.Session.TradeDate, tokens, throughUtc, ct);
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<ObserverTokenTick>(raw.Count);
        var marketOpenUtc = new DateTimeOffset(
            context.Session.TradeDate.ToDateTime(new TimeOnly(9, 15)),
            TimeSpan.FromHours(5.5)).ToUniversalTime();

        foreach (var item in raw)
        {
            var normalized = normalizer.Process(item.Token, item.Tick);
            if (normalized is { } tick && tick.AvailableAt >= marketOpenUtc && tick.AvailableAt <= throughUtc)
            {
                clean.Add(new ObserverTokenTick(item.Token, tick));
            }
        }

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
        DateTimeOffset? lastAvailable = null;
        long? lastTickId = null;
        foreach (var item in clean)
        {
            var packages = engine.Process(item.Token, item.Tick);
            lastAvailable = item.Tick.AvailableAt;
            lastTickId = item.Tick.Id;

            foreach (var package in packages)
            {
                var result = await persistence.PersistOrVerifyAsync(
                    observer, context.Session, package, DateTimeOffset.UtcNow, ct);
                await observations.ProcessPackageAsync(source, observer, context, package, ct);
                if (result.VerifiedExisting)
                {
                    reconciled++;
                }
            }
        }

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
            tokens);
    }
}
