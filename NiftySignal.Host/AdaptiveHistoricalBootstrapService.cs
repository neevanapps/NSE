using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

/// <summary>
/// One-time/idempotent bootstrap of prior futures sessions into the adaptive observer database.
/// It uses the same persisted raw Tick source and exact V1 futures semantics as live recovery.
/// No option or trading behavior is seeded here; its sole purpose is causal futures-state history
/// for the first live StrongThreshold and historical parity diagnostics.
/// </summary>
public sealed class AdaptiveHistoricalBootstrapService(
    AdaptiveSourceTickReader tickReader,
    ILogger<AdaptiveHistoricalBootstrapService> logger)
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly Open = new(9, 15);
    static readonly TimeOnly Cutoff = new(9, 30);
    static readonly TimeOnly Close = new(15, 35);

    public async Task EnsurePriorSessionsAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        DateOnly beforeDay,
        CancellationToken ct)
    {
        var candidateDays = await source.Instruments.AsNoTracking()
            .Where(x => x.Underlying == "NIFTY"
                && x.InstrumentType == InstrumentType.Future
                && x.AsOfDate < beforeDay
                && x.AsOfDate >= beforeDay.AddDays(-45))
            .Select(x => x.AsOfDate)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);

        if (candidateDays.Count == 0)
        {
            return;
        }

        var existing = await observer.Sessions.AsNoTracking()
            .Where(x => x.ModelVersion == OpeningVolumeProjectionV1.ModelVersion
                && x.TradeDate < beforeDay)
            .Select(x => x.TradeDate)
            .ToListAsync(ct);
        var existingSet = existing.ToHashSet();

        foreach (var day in candidateDays)
        {
            if (existingSet.Contains(day))
            {
                continue;
            }

            try
            {
                var seeded = await SeedOneAsync(source, observer, day, ct);
                if (seeded)
                {
                    existingSet.Add(day);
                }
            }
            catch (Exception ex)
            {
                // A partial/missing historical day is not allowed to break today's live observer.
                // It is skipped explicitly and logged; strong threshold uses only successfully
                // completed prior sessions.
                logger.LogWarning(ex, "Adaptive historical bootstrap skipped {TradeDate}.", day);
            }
        }
    }

    async Task<bool> SeedOneAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        DateOnly day,
        CancellationToken ct)
    {
        var future = await source.Instruments.AsNoTracking()
            .Where(x => x.AsOfDate == day
                && x.Underlying == "NIFTY"
                && x.InstrumentType == InstrumentType.Future)
            .OrderBy(x => x.ExpiryDate)
            .FirstOrDefaultAsync(ct);
        if (future is null)
        {
            return false;
        }

        var closeUtc = ToUtc(day, Close);
        var clean = await tickReader.ReadCleanSessionAsync(source, day, new[] { future.Token }, closeUtc, ct);
        var ticks = clean.Select(x => x.Tick).ToArray();
        if (ticks.Length < 2)
        {
            return false;
        }

        var cutoffUtc = ToUtc(day, Cutoff);
        var openingEnricher = new AdaptiveTradeFlowEnricher();
        long openingVolume = 0;
        foreach (var tick in ticks)
        {
            var e = openingEnricher.Process(tick);
            if (tick.AvailableAt < cutoffUtc)
            {
                openingVolume += e.TradeVolume;
            }
        }

        var hasDiscoverySpec = OpeningVolumeProjectionV1.DiscoveryOutOfFold.TryGetValue(day, out var discoverySpec);
        var baseVolume = hasDiscoverySpec
            ? discoverySpec!.AdaptiveBarVolume
            : OpeningVolumeProjectionV1.SelectBaseBarVolume(openingVolume, future.LotSize);

        if (hasDiscoverySpec && openingVolume != discoverySpec!.OpeningVolume0930)
        {
            logger.LogWarning(
                "Adaptive discovery parity mismatch {TradeDate}: raw opening volume {Actual} != frozen research {Expected}; day excluded from strong-threshold seed.",
                day, openingVolume, discoverySpec.OpeningVolume0930);
            return false;
        }

        var definition = new AdaptiveSessionDefinition(
            day,
            future.Token,
            future.TradingSymbol,
            future.ExpiryDate ?? day,
            future.LotSize,
            baseVolume);

        var enricher = new AdaptiveTradeFlowEnricher();
        var builder = new ExactAdaptiveFuturesBarBuilder(definition);
        foreach (var tick in ticks)
        {
            builder.Add(enricher.Process(tick));
        }

        var completeBars = builder.Bars.Where(x => x.IsComplete).ToArray();
        if (completeBars.Length < definition.RollingWindowBars)
        {
            logger.LogWarning(
                "Adaptive historical bootstrap {TradeDate}: only {Bars} complete bars; not seeding.",
                day, completeBars.Length);
            return false;
        }

        if (hasDiscoverySpec && completeBars.Length != discoverySpec!.ExpectedCompleteBars)
        {
            logger.LogWarning(
                "Adaptive discovery parity mismatch {TradeDate}: rebuilt complete bars {Actual} != frozen research {Expected}; day is excluded from strong-threshold seed.",
                day, completeBars.Length, discoverySpec.ExpectedCompleteBars);
            return false;
        }

        var flow = AdaptiveFlowEvolutionTracker.Build(completeBars);

        var priorSessionIds = await observer.Sessions.AsNoTracking()
            .Where(x => x.ModelVersion == OpeningVolumeProjectionV1.ModelVersion && x.TradeDate < day)
            .Select(x => x.Id)
            .ToArrayAsync(ct);
        var priorRatios = priorSessionIds.Length == 0
            ? new List<double>()
            : await observer.RollingStates.AsNoTracking()
                .Where(x => priorSessionIds.Contains(x.SessionId))
                .Select(x => Math.Abs(x.StrictDeltaRatioTotal))
                .ToListAsync(ct);

        double? strongThreshold = priorRatios.Count > 0
            ? AdaptiveWeak2Classifier.Quantile(priorRatios, AdaptiveWeak2Classifier.StrongQuantile)
            : null;
        if (strongThreshold.HasValue)
        {
            AdaptiveWeak2Classifier.Apply(flow, strongThreshold.Value);
        }

        var build = BuildIdentity.Current();
        await using var tx = await observer.Database.BeginTransactionAsync(ct);

        var session = new AdaptiveSessionStateRow
        {
            TradeDate = day,
            ModelVersion = OpeningVolumeProjectionV1.ModelVersion,
            SourceBranch = build.SourceBranch,
            SourceCommitSha = build.CommitSha,
            BuildUtc = build.BuildUtc ?? DateTimeOffset.UtcNow,
            IsHistoricalSeed = true,
            FutureToken = future.Token,
            FutureSymbol = future.TradingSymbol,
            FutureExpiry = future.ExpiryDate ?? day,
            LotSize = future.LotSize,
            OpeningWindowStartUtc = ToUtc(day, Open),
            OpeningWindowEndUtc = cutoffUtc,
            OpeningVolume = openingVolume,
            EstimatorName = hasDiscoverySpec ? "adaptive-v1-discovery-loocv" : nameof(OpeningVolumeProjectionV1),
            EstimatorIntercept = hasDiscoverySpec ? discoverySpec!.TrainingIntercept : OpeningVolumeProjectionV1.Intercept,
            EstimatorSlope = hasDiscoverySpec ? discoverySpec!.TrainingSlope : OpeningVolumeProjectionV1.Slope,
            TargetBarsPerDay = OpeningVolumeProjectionV1.TargetBarsPerDay,
            RoundingLots = OpeningVolumeProjectionV1.RoundingLots,
            EstimatedFullDayVolume = hasDiscoverySpec
                ? discoverySpec!.PredictedFullDayVolume
                : OpeningVolumeProjectionV1.PredictFullDayVolume(openingVolume),
            BaseBarVolume = baseVolume,
            RollingWindowBars = definition.RollingWindowBars,
            RollingWindowVolume = definition.RollingWindowVolume,
            StrongQuantile = AdaptiveWeak2Classifier.StrongQuantile,
            StrongThreshold = strongThreshold,
            StrongThresholdPriorStateCount = priorRatios.Count,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        observer.Sessions.Add(session);
        await observer.SaveChangesAsync(ct);

        observer.RollingStates.AddRange(flow
            .Where(x => x.Rolling is not null)
            .Select(x => MapRolling(session.Id, x)));

        observer.Runtime.Add(new AdaptiveObserverRuntimeRow
        {
            SessionId = session.Id,
            RuntimeStatus = AdaptiveRuntimeStatus.Closed,
            LastHeartbeatUtc = DateTimeOffset.UtcNow,
            LastCompletedBarSeq = completeBars[^1].BarSeq,
            CurrentPartialBarVolume = 0,
        });

        await observer.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Adaptive historical bootstrap seeded {TradeDate}: ticks={Ticks}, base={Base}, bars={Bars}, rolling={Rolling}, strong={Strong}",
            day, ticks.Length, baseVolume, completeBars.Length, flow.Count(x => x.Rolling is not null), strongThreshold);
        return true;
    }

    static AdaptiveRollingStateRow MapRolling(long sessionId, AdaptiveFlowState flow)
    {
        var s = flow.Rolling!;
        return new AdaptiveRollingStateRow
        {
            SessionId = sessionId,
            StartBarSeq = s.StartBarSeq,
            EndBarSeq = s.EndBarSeq,
            StartAvailableAtUtc = s.StartAvailableAtUtc,
            EndAvailableAtUtc = s.EndAvailableAtUtc,
            WindowBars = s.WindowBars,
            WindowVolume = s.WindowVolume,
            ElapsedSeconds = s.ElapsedSeconds,
            StartPrice = s.StartPrice,
            EndPrice = s.EndPrice,
            High = s.High,
            Low = s.Low,
            PriceDisplacement = s.PriceDisplacement,
            ReturnBps = s.ReturnBps,
            PathLength = s.PathLength,
            Efficiency = s.Efficiency,
            SignedEfficiency = s.SignedEfficiency,
            StrictBuyVolume = s.StrictBuyVolume,
            StrictSellVolume = s.StrictSellVolume,
            StrictUnknownVolume = s.StrictUnknownVolume,
            StrictDelta = s.StrictDelta,
            StrictQuoteCoverage = s.StrictQuoteCoverage,
            StrictDeltaRatioTotal = s.StrictDeltaRatioTotal,
            StrictDeltaRatioClassified = s.StrictDeltaRatioClassified,
            EnrichedBuyVolume = s.EnrichedBuyVolume,
            EnrichedSellVolume = s.EnrichedSellVolume,
            EnrichedUnknownVolume = s.EnrichedUnknownVolume,
            EnrichedDelta = s.EnrichedDelta,
            EnrichedDeltaRatio = s.EnrichedDeltaRatio,
            FallbackShare = s.FallbackShare,
            OiStart = s.OiStart,
            OiEnd = s.OiEnd,
            OiChange = s.OiChange,
            OiChangePct = s.OiChangePct,
            VolumePerSecond = s.VolumePerSecond,
            TradeUpdates = s.TradeUpdates,
            WindowRange = s.WindowRange,
            PriceDirection = s.PriceDirection,
            StrictDeltaDirection = s.StrictDeltaDirection,
            EnrichedDeltaDirection = s.EnrichedDeltaDirection,
            PriceStrictDeltaAgree = s.PriceStrictDeltaAgree,
            PriceEnrichedDeltaAgree = s.PriceEnrichedDeltaAgree,
            RollingStrictDeltaChange = flow.RollingStrictDeltaChange,
            RollingStrictAbsDeltaChange = flow.RollingStrictAbsDeltaChange,
            RollingEnrichedDeltaChange = flow.RollingEnrichedDeltaChange,
            RollingOiChangePctChange = flow.RollingOiChangePctChange,
            StrictDominanceEvolution = flow.StrictDominanceEvolution,
            IsStrong = flow.IsStrong,
            WeakeningSequence = flow.WeakeningSequence,
            StrongBaseBarSeq = flow.StrongBaseBarSeq,
            Weak1BarSeq = flow.Weak1BarSeq,
            State = flow.State,
        };
    }

    static DateTimeOffset ToUtc(DateOnly day, TimeOnly time) =>
        new DateTimeOffset(day.ToDateTime(time), IstOffset).ToUniversalTime();
}
