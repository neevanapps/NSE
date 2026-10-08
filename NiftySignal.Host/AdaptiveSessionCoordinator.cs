using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

public sealed record AdaptiveObserverSessionContext(
    AdaptiveSessionStateRow Session,
    AdaptiveSessionDefinition Definition,
    DateOnly WeeklyOptionExpiry,
    IReadOnlyList<ObserverOptionInstrument> Options,
    OptionResidualAnchor? ResidualAnchor,
    DateTimeOffset SignalStartUtc,
    // Frozen supplemental spot instrument (section 71.3); null = not yet resolved by this process or unresolved (Basis unavailable). Never feeds core calculations.
    string? SpotToken = null);

/// <summary>
/// Creates exactly one immutable daily adaptive configuration after persisted source data has
/// causally advanced through 09:30 IST. If the row already exists (restart), its values are loaded
/// unchanged; model/history changes never recalculate today's threshold.
/// </summary>
public sealed class AdaptiveSessionCoordinator(
    AdaptiveSourceTickReader tickReader,
    IOptionsMonitor<PricingOptions> pricing,
    ILogger<AdaptiveSessionCoordinator> logger)
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly SelectionTime = new(9, 30);
    const double ResidualQuoteMaxAgeSeconds = 5d;

    public async Task<AdaptiveObserverSessionContext?> TryGetOrCreateAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        DateOnly day,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var existing = await observer.Sessions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TradeDate == day && x.ModelVersion == OpeningVolumeProjectionV1.ModelVersion, ct);

        if (existing is not null)
        {
            return await LoadContextAsync(source, observer, existing, ct);
        }

        var cutoffUtc = ToUtc(day, SelectionTime);
        if (nowUtc < cutoffUtc)
        {
            return null;
        }

        var future = await source.Instruments
            .AsNoTracking()
            .Where(i => i.AsOfDate == day
                && i.Underlying == "NIFTY"
                && i.InstrumentType == InstrumentType.Future)
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefaultAsync(ct);
        if (future is null)
        {
            logger.LogInformation("Adaptive observer: NIFTY future is not resolved yet for {TradeDate}", day);
            return null;
        }

        var optionRows = await source.Instruments
            .AsNoTracking()
            .Where(i => i.AsOfDate == day
                && i.Underlying == "NIFTY"
                && i.InstrumentType == InstrumentType.Option
                && i.ExpiryDate != null
                && i.StrikePrice != null)
            .OrderBy(i => i.ExpiryDate)
            .ThenBy(i => i.StrikePrice)
            .ToListAsync(ct);
        var weeklyExpiry = optionRows.Select(x => x.ExpiryDate!.Value).Distinct().OrderBy(x => x).FirstOrDefault();
        if (weeklyExpiry == default)
        {
            logger.LogInformation("Adaptive observer: weekly NIFTY options are not resolved yet for {TradeDate}", day);
            return null;
        }

        var nearestOptions = optionRows.Where(x => x.ExpiryDate == weeklyExpiry).ToList();
        var optionDescriptors = nearestOptions.Select(ToDescriptor).ToList();
        var tokens = optionDescriptors.Select(x => x.Token).Append(future.Token).Distinct(StringComparer.Ordinal).ToArray();

        // Read through "now", not merely cutoff, so readiness can prove the persisted futures
        // stream actually crossed 09:30. Calculations below still use only <= cutoff data.
        var clean = await tickReader.ReadCleanSessionAsync(source, day, tokens, nowUtc, ct);
        var futureTicks = clean
            .Where(x => x.Token == future.Token)
            .Select(x => x.Tick)
            .OrderBy(x => x.AvailableAt)
            .ThenBy(x => x.Id)
            .ToList();

        if (!futureTicks.Any(x => x.AvailableAt >= cutoffUtc))
        {
            logger.LogDebug("Adaptive observer: waiting for persisted NIFTY future data through 09:30 for {TradeDate}", day);
            return null;
        }

        var openingEnricher = new AdaptiveTradeFlowEnricher();
        long openingVolume = 0;
        foreach (var tick in futureTicks)
        {
            var e = openingEnricher.Process(tick);
            if (tick.AvailableAt < cutoffUtc)
            {
                openingVolume += e.TradeVolume;
            }
        }

        var openUtc = ToUtc(day, MarketOpen);
        var coveredMinutes = AdaptiveOpeningCoverage.CoveredMinutes(futureTicks, openUtc, cutoffUtc);
        var useMedian = coveredMinutes != 15 || openingVolume <= 0;
        var validatedDiscoveryDays = OpeningVolumeProjectionV1.DiscoveryOutOfFold.Keys.ToArray();
        var priorOpeningVolumes = useMedian
            ? await observer.Sessions.AsNoTracking()
                .Where(x => x.TradeDate < day && x.ModelVersion == OpeningVolumeProjectionV1.ModelVersion
                    && !x.UsesMedianOpeningFallback && x.OpeningVolume > 0
                    && (x.OpeningCoverageMinutes == 15
                        || (x.IsHistoricalSeed && validatedDiscoveryDays.Contains(x.TradeDate))))
                .Select(x => x.OpeningVolume).ToListAsync(ct)
            : new List<long>();
        var estimatorInput = useMedian ? AdaptiveOpeningCoverage.Median(priorOpeningVolumes) : openingVolume;
        // Ignore incomplete opening fragments in fallback mode. First observed tick is a volume
        // baseline; cumulative volume collected while offline never becomes a synthetic huge bar.
        var observationStart = useMedian
            ? futureTicks.First(x => x.AvailableAt >= cutoffUtc).AvailableAt : openUtc;
        var predicted = OpeningVolumeProjectionV1.PredictFullDayVolume(estimatorInput);
        var baseVolume = OpeningVolumeProjectionV1.SelectBaseBarVolume(estimatorInput, future.LotSize);

        var priorSessionIds = await observer.Sessions
            .AsNoTracking()
            .Where(x => x.ModelVersion == OpeningVolumeProjectionV1.ModelVersion && x.TradeDate < day
                && !x.UsesMedianOpeningFallback && x.OpeningVolume > 0
                && (x.OpeningCoverageMinutes == 15
                    || (x.IsHistoricalSeed && validatedDiscoveryDays.Contains(x.TradeDate))))
            .Select(x => x.Id)
            .ToArrayAsync(ct);

        var priorRatios = priorSessionIds.Length == 0
            ? new List<double>()
            : await observer.RollingStates
                .AsNoTracking()
                .Where(x => priorSessionIds.Contains(x.SessionId))
                .Select(x => Math.Abs(x.StrictDeltaRatioTotal))
                .ToListAsync(ct);

        double? strongThreshold = priorRatios.Count > 0
            ? AdaptiveWeak2Classifier.ComputeStrongThreshold(priorRatios)
            : null;

        var future0930Tick = futureTicks
            .Where(x => x.AvailableAt <= cutoffUtc)
            .OrderByDescending(x => x.AvailableAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();

        var riskFreeRate = pricing.CurrentValue.RiskFreeRate;
        OptionResidualAnchor? anchor = null;
        if (future0930Tick.Id != 0
            && (cutoffUtc - future0930Tick.AvailableAt).TotalSeconds <= ResidualQuoteMaxAgeSeconds)
        {
            var quotes = BuildLatestQuotesAt(clean, optionDescriptors, cutoffUtc);
            anchor = OptionResidualModel.BuildAnchor(
                cutoffUtc,
                weeklyExpiry,
                future0930Tick.Last,
                optionDescriptors,
                quotes,
                riskFreeRate,
                ResidualQuoteMaxAgeSeconds);
        }

        var build = BuildIdentity.Current();
        var row = new AdaptiveSessionStateRow
        {
            TradeDate = day,
            ModelVersion = OpeningVolumeProjectionV1.ModelVersion,
            SourceBranch = build.SourceBranch,
            SourceCommitSha = build.CommitSha,
            BuildUtc = build.BuildUtc ?? nowUtc,
            IsHistoricalSeed = false,
            FutureToken = future.Token,
            FutureSymbol = future.TradingSymbol,
            FutureExpiry = future.ExpiryDate ?? day,
            LotSize = future.LotSize,
            RiskFreeRate = riskFreeRate,
            OptionUniverseJson = JsonSerializer.Serialize(optionDescriptors),
            OpeningWindowStartUtc = ToUtc(day, MarketOpen),
            OpeningWindowEndUtc = cutoffUtc,
            OpeningVolume = openingVolume,
            EstimatorInputOpeningVolume = estimatorInput,
            UsesMedianOpeningFallback = useMedian,
            OpeningCoverageMinutes = coveredMinutes,
            MedianOpeningSampleCount = useMedian ? priorOpeningVolumes.Count : null,
            ObservationStartUtc = observationStart,
            EstimatorName = useMedian ? "historical-opening-median-v1" : nameof(OpeningVolumeProjectionV1),
            EstimatorIntercept = OpeningVolumeProjectionV1.Intercept,
            EstimatorSlope = OpeningVolumeProjectionV1.Slope,
            TargetBarsPerDay = OpeningVolumeProjectionV1.TargetBarsPerDay,
            RoundingLots = OpeningVolumeProjectionV1.RoundingLots,
            EstimatedFullDayVolume = predicted,
            BaseBarVolume = baseVolume,
            RollingWindowBars = 10,
            RollingWindowVolume = baseVolume * 10,
            StrongQuantile = AdaptiveWeak2Classifier.StrongQuantile,
            StrongThreshold = strongThreshold,
            StrongThresholdPriorStateCount = priorRatios.Count,
            WeeklyOptionExpiry = weeklyExpiry,
            Future0930 = anchor?.Future0930,
            SyntheticWeeklyUnderlying0930 = anchor?.SyntheticUnderlying0930,
            ResidualCenterStrike = anchor?.CenterStrike,
            ResidualAnchorCompletedAtUtc = anchor is null ? null : nowUtc,
            CreatedAtUtc = nowUtc,
        };

        await using var transaction = await observer.Database.BeginTransactionAsync(ct);
        observer.Sessions.Add(row);
        await observer.SaveChangesAsync(ct);

        if (anchor is not null)
        {
            foreach (var component in anchor.Calls.Concat(anchor.Puts))
            {
                observer.ResidualAnchorComponents.Add(new AdaptiveResidualAnchorComponentRow
                {
                    SessionId = row.Id,
                    Side = component.Instrument.OptionType,
                    Strike = component.Instrument.Strike,
                    Token = component.Instrument.Token,
                    TradingSymbol = component.Instrument.TradingSymbol,
                    LotSize = component.Instrument.LotSize,
                    IsCenterStrike = component.IsCenterStrike,
                    Price0930 = component.Price0930,
                    ImpliedVolatility0930 = component.ImpliedVolatility0930,
                    QuoteTimestampUtc = component.QuoteTimestampUtc,
                    QuoteAgeSeconds = component.QuoteAgeSeconds,
                });
            }
        }

        observer.Runtime.Add(new AdaptiveObserverRuntimeRow
        {
            SessionId = row.Id,
            RuntimeStatus = AdaptiveRuntimeStatus.Calibrating,
            LastHeartbeatUtc = nowUtc,
        });

        await observer.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Adaptive observer session frozen for {TradeDate}: opening={OpeningVolume}, predicted={Predicted:F0}, base={BaseVolume}, rolling={RollingVolume}, strong={StrongThreshold}, residualAnchor={HasAnchor}, source={Branch}@{Sha}",
            day, openingVolume, predicted, baseVolume, baseVolume * 10, strongThreshold, anchor is not null, row.SourceBranch, row.SourceCommitSha);

        return new AdaptiveObserverSessionContext(
            row,
            new AdaptiveSessionDefinition(day, future.Token, future.TradingSymbol, future.ExpiryDate ?? day, future.LotSize, baseVolume),
            weeklyExpiry,
            optionDescriptors,
            anchor,
            useMedian ? observationStart : cutoffUtc);
    }

    async Task<AdaptiveObserverSessionContext> LoadContextAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        AdaptiveSessionStateRow row,
        CancellationToken ct)
    {
        if (row.OpeningVolume <= 0 && !row.UsesMedianOpeningFallback)
            throw new InvalidOperationException($"Adaptive session {row.Id} has an invalid zero opening estimate; it cannot be silently refrozen. Review and rebuild this session explicitly.");
        var expiry = row.WeeklyOptionExpiry
            ?? throw new InvalidOperationException($"Adaptive session {row.Id} has no weekly option expiry.");

        // Persisted session owns instrument identity, strike, expiry and lot size across source refreshes.
        if (string.IsNullOrWhiteSpace(row.OptionUniverseJson))
            throw new InvalidOperationException($"Adaptive session {row.Id} predates the frozen option universe; cannot safely infer its original descriptors.");
        var options = JsonSerializer.Deserialize<List<ObserverOptionInstrument>>(row.OptionUniverseJson)
            ?? throw new InvalidOperationException($"Adaptive session {row.Id} has an invalid frozen option universe.");
        if (options.Select(x => x.Token).Distinct(StringComparer.Ordinal).Count() != options.Count
            || options.Any(x => x.ExpiryDate != expiry || x.LotSize <= 0 || x.Strike <= 0))
            throw new InvalidOperationException($"Adaptive session {row.Id} has inconsistent frozen option descriptors.");

        OptionResidualAnchor? anchor = null;
        if (row.Future0930.HasValue && row.SyntheticWeeklyUnderlying0930.HasValue && row.ResidualCenterStrike.HasValue)
        {
            var components = await observer.ResidualAnchorComponents
                .AsNoTracking()
                .Where(x => x.SessionId == row.Id)
                .OrderBy(x => x.Side)
                .ThenBy(x => x.Strike)
                .ToListAsync(ct);

            if (components.Count > 0)
            {
                var calls = components.Where(x => x.Side == OptionType.Call).Select(ToAnchorComponent).ToList();
                var puts = components.Where(x => x.Side == OptionType.Put).Select(ToAnchorComponent).ToList();
                anchor = new OptionResidualAnchor(
                    row.OpeningWindowEndUtc,
                    expiry,
                    row.Future0930.Value,
                    row.SyntheticWeeklyUnderlying0930.Value,
                    row.ResidualCenterStrike.Value,
                    calls,
                    puts);
            }
        }

        return new AdaptiveObserverSessionContext(
            row,
            new AdaptiveSessionDefinition(
                row.TradeDate, row.FutureToken, row.FutureSymbol, row.FutureExpiry, row.LotSize, row.BaseBarVolume, row.RollingWindowBars),
            expiry,
            options,
            anchor,
            row.UsesMedianOpeningFallback ? row.ObservationStartUtc
                ?? throw new InvalidOperationException("Median fallback session has no frozen observation start.")
                : row.OpeningWindowEndUtc);
    }

    static ObserverOptionInstrument ToDescriptor(NiftySignal.Domain.Entities.Instrument i) =>
        new(i.Token, i.TradingSymbol, i.OptionType, (double)i.StrikePrice!.Value, i.ExpiryDate!.Value, i.LotSize);

    static OptionResidualAnchorComponent ToAnchorComponent(AdaptiveResidualAnchorComponentRow x) =>
        new(
            new ObserverOptionInstrument(x.Token, x.TradingSymbol, x.Side, x.Strike, default, x.LotSize),
            x.Price0930,
            x.ImpliedVolatility0930,
            x.QuoteTimestampUtc,
            x.QuoteAgeSeconds,
            x.IsCenterStrike);

    static IReadOnlyDictionary<string, OptionQuoteSnapshot> BuildLatestQuotesAt(
        IReadOnlyList<ObserverTokenTick> clean,
        IReadOnlyList<ObserverOptionInstrument> options,
        DateTimeOffset cutoff)
    {
        var wanted = options.Select(x => x.Token).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, OptionQuoteSnapshot>(StringComparer.Ordinal);
        foreach (var item in clean)
        {
            if (!wanted.Contains(item.Token) || item.Tick.AvailableAt > cutoff)
            {
                continue;
            }

            var t = item.Tick;
            result[item.Token] = new OptionQuoteSnapshot(
                item.Token, t.AvailableAt, t.Last, t.Bid, t.Ask, t.OpenInterest);
        }

        return result;
    }

    static DateTimeOffset ToUtc(DateOnly day, TimeOnly time) =>
        new DateTimeOffset(day.ToDateTime(time), IstOffset).ToUniversalTime();
}
