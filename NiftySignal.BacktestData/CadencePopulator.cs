using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;
using NiftySignal.Persistence;
using NiftySignal.Pricing;

namespace NiftySignal.BacktestData;

public enum PopulationOutcome
{
    Populated,
    AlreadyPopulated,
    NoTradableData,
}

public sealed record PopulationResult(PopulationOutcome Outcome, int RowCount, int StrikeRowCount = 0, int BandRowCount = 0);

/// <summary>Read-only per-day reference data, bundled so BuildRow doesn't have to pass each piece separately.</summary>
sealed record DayContext(
    DateOnly AsOfDate,
    DateOnly NearestExpiry,
    DateTimeOffset ExpiryMoment,
    double RiskFreeRate,
    string FutureToken,
    List<decimal> DistinctStrikes,
    List<Instrument> NearestExpiryOptions,
    Dictionary<string, Tick> LatestOptionTick,
    List<ExpiryChain> TrackedChains);

/// <summary>
/// One tracked option chain (near-week or next-week) for <see cref="StrikeCadenceSnapshot"/>/
/// <see cref="StrikeBandCadenceSnapshot"/> purposes -- deliberately separate from
/// <see cref="DayContext.NearestExpiry"/>/<see cref="DayContext.DistinctStrikes"/> above, which
/// exist only for the synthetic-forward solve and stay near-week-only, unchanged, to avoid any
/// risk of altering that already-working calculation while adding the child-table population.
/// <see cref="Options"/> is this chain's own instrument list -- needed to resolve a strike/option
/// type back to a Token for this chain specifically, since the same strike price can exist in
/// both chains under different tokens.
/// </summary>
sealed record ExpiryChain(DateOnly ExpiryDate, List<decimal> DistinctStrikes, List<Instrument> Options);

/// <summary>
/// Builds one trading day's worth of <see cref="CadenceContext"/> rows purely from raw
/// historical ticks -- a single forward pass over the day's ticks in timestamp order,
/// mirroring the same "state updated per tick, snapshot taken per cadence" shape
/// <c>NiftySignal.Host.LiveFeatureEngine</c> already uses, but built fresh here (not reusing
/// LiveFeatureEngine itself, which lives in Host and is deliberately untouched while
/// backtesting hasn't found an edge yet).
///
/// Idempotent per <see cref="CadenceContext.AsOfDate"/>: a day that already has rows in the
/// destination is skipped entirely, never recomputed. The only way to redo a day is to delete
/// its rows first, deliberately, after a real calculation mistake has been found and fixed --
/// this class never repopulates automatically.
/// </summary>
public static class CadencePopulator
{
    public const string BacktestAnalysisDatabaseName = "niftysignal_backtest_analysis";

    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketClose = new(15, 30);
    static readonly TimeSpan CadenceLength = TimeSpan.FromSeconds(15);

    const int SyntheticForwardStrikeCount = 5;

    public static async Task<PopulationResult> PopulateDayAsync(
        NiftySignalDbContext source,
        BacktestAnalysisDbContext destination,
        DateOnly asOfDate,
        double riskFreeRate,
        CancellationToken cancellationToken)
    {
        if (await destination.CadenceContexts.AnyAsync(c => c.AsOfDate == asOfDate, cancellationToken))
        {
            return new PopulationResult(PopulationOutcome.AlreadyPopulated, 0);
        }

        var instruments = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate)
            .ToListAsync(cancellationToken);

        var spot = instruments.FirstOrDefault(i => i.InstrumentType == InstrumentType.Index);
        var future = instruments.FirstOrDefault(i => i.InstrumentType == InstrumentType.Future);
        var vix = instruments.FirstOrDefault(i => i.InstrumentType == InstrumentType.Vix);
        var options = instruments
            .Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate is not null)
            .ToList();

        // Can't build a meaningful day without Spot, Future, and at least one tracked option --
        // same fundamental requirement LiveFeatureEngine.ComputeCadence already has for Spot/
        // Future, extended here to "no options" since DTE/ATM-strike fields need a real chain.
        if (spot is null || future is null || options.Count == 0)
        {
            return new PopulationResult(PopulationOutcome.NoTradableData, 0);
        }

        var nearestExpiry = options.Select(o => o.ExpiryDate!.Value).Min();
        var nearestExpiryOptions = options.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = nearestExpiryOptions.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => s).ToList();

        // Both tracked expiries for the strike-level child tables (2026-09-12) -- "this week and
        // next week", per instruction. Deliberately independent of nearestExpiry/distinctStrikes
        // above, which stay near-week-only and untouched (the synthetic forward solve already
        // depends on them and is not being risked here).
        var allExpiries = options.Select(o => o.ExpiryDate!.Value).Distinct().OrderBy(e => e).ToList();
        var trackedChains = allExpiries.Take(2)
            .Select(expiry =>
            {
                var chainOptions = options.Where(o => o.ExpiryDate == expiry).ToList();
                return new ExpiryChain(
                    expiry,
                    chainOptions.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => s).ToList(),
                    chainOptions);
            })
            .ToList();

        var trackedTokens = new HashSet<string> { spot.Token, future.Token };
        if (vix is not null)
        {
            trackedTokens.Add(vix.Token);
        }
        foreach (var option in nearestExpiryOptions)
        {
            trackedTokens.Add(option.Token);
        }

        // One state tracker per option instrument across both tracked expiries -- a wider set
        // than the ATM+/-3 band actually persisted, so a strike's own volume/CVD/OHLC history is
        // never gapped just because it wasn't in-band on an earlier cadence and drifted in later.
        var optionStates = new Dictionary<string, OptionInstrumentState>();
        foreach (var expiry in trackedChains)
        {
            foreach (var option in options.Where(o => o.ExpiryDate == expiry.ExpiryDate))
            {
                trackedTokens.Add(option.Token);
                optionStates[option.Token] = new OptionInstrumentState(option.StrikePrice!.Value, option.OptionType, expiry.ExpiryDate);
            }
        }

        // .ToUniversalTime() here changes only the *displayed* offset, not the actual instant --
        // Npgsql refuses to write a DateTimeOffset with a non-zero offset to "timestamp with
        // time zone" (it insists on UTC), so every DateTimeOffset that will either be sent as a
        // query parameter or persisted (dayStart/dayEnd here, and every `boundary` derived from
        // dayStart in the loop below, which becomes CadenceContext.Timestamp) needs to arrive as
        // UTC. expiryMoment is deliberately left in IST -- it's only ever compared against other
        // DateTimeOffset values in memory (offset-agnostic by .NET's own comparison semantics),
        // never queried or persisted, so there's nothing here for Npgsql to reject.
        var dayStart = new DateTimeOffset(asOfDate.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();
        var expiryMoment = new DateTimeOffset(nearestExpiry.ToDateTime(new TimeOnly(15, 30)), IstOffset);

        var ticks = source.Ticks
            .Where(t => trackedTokens.Contains(t.Token) && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd)
            .OrderBy(t => t.ExchangeTimestamp)
            .ThenBy(t => t.Id)
            .AsAsyncEnumerable();

        var spotState = new InstrumentPriceState();
        var futureState = new InstrumentPriceState();
        var vixState = new InstrumentPriceState();
        var futureFlow = new FutureFlowAccumulator();
        var futureDepth = new DepthImbalanceAccumulator();
        var futureCvdProxy = new FutureCvdProxyAccumulator();
        var futureOiLookback = new OiLookbackWindow(FeatureWindowLengths.OiComparisonWindow);
        var depthImbalance5Min = new WelfordRollingWindow(TimeSpan.FromMinutes(5));
        var depthImbalance15Min = new WelfordRollingWindow(TimeSpan.FromMinutes(15));
        var cvdNet5Min = new RollingNetSumWindow(TimeSpan.FromMinutes(5));
        var cvdNet15Min = new RollingNetSumWindow(TimeSpan.FromMinutes(15));
        var latestOptionTick = new Dictionary<string, Tick>();

        long? latestFutureOi = null;
        long? futureDayOpenOi = null;

        var day = new DayContext(
            asOfDate, nearestExpiry, expiryMoment, riskFreeRate, future.Token,
            distinctStrikes, nearestExpiryOptions, latestOptionTick, trackedChains);

        var rows = new List<CadenceContext>();

        // Parallel to `rows`: strike-level child rows built at the same cadence boundary, buffered
        // until CadenceContexts.SaveChangesAsync() below assigns real Ids -- see the FK-stamping
        // loop after that save for why this can't be flattened into one list up front.
        var strikeRowsByCadenceIndex = new List<List<StrikeCadenceSnapshot>>();

        // Spot's own previous-cadence price, for OiBuildupClassifier below -- mirrors
        // LiveFeatureEngine's spotPriceChange (spotPrice - prevSpot.LastPrice), fixing a real
        // inconsistency flagged by external review (2026-09-12): this populator previously fed
        // the classifier the option's OWN mark-price delta instead of the underlying's, which the
        // live engine already got right. One shared value per cadence (spot has no per-strike
        // identity), not per-instrument state like PriorKnownMarkPrice.
        decimal? previousSpotPrice = null;

        await using var enumerator = ticks.GetAsyncEnumerator(cancellationToken);
        var hasNext = await enumerator.MoveNextAsync();

        for (var boundary = dayStart + CadenceLength; boundary <= dayEnd; boundary += CadenceLength)
        {
            while (hasNext && enumerator.Current.ExchangeTimestamp <= boundary)
            {
                var tick = enumerator.Current;
                if (tick.Token == spot.Token)
                {
                    spotState.ApplyTick(tick.LastPrice);
                }
                else if (tick.Token == future.Token)
                {
                    var mid = MidPrice(tick) ?? tick.LastPrice;
                    futureState.ApplyTick(mid);
                    futureFlow.ApplyTick(mid, tick.Volume);
                    if (tick.OpenInterest is { } oi)
                    {
                        latestFutureOi = oi;
                    }
                    if (tick.Depth is { } depth)
                    {
                        futureDepth.ApplyTick(depth);
                        // LastVolumeDelta reads the delta futureFlow.ApplyTick just computed above
                        // for this exact tick -- one shared baseline, so VWAP and this proxy can
                        // never disagree about how much volume traded this tick, only how to sign it.
                        futureCvdProxy.ApplyTick(tick.LastPrice, depth, futureFlow.LastVolumeDelta);
                    }
                }
                else if (vix is not null && tick.Token == vix.Token)
                {
                    vixState.ApplyTick(tick.LastPrice);
                }
                else
                {
                    // A nearest-expiry option's mid feeds the synthetic forward solve below --
                    // kept exactly as before, untouched.
                    latestOptionTick[tick.Token] = tick;

                    // Every tracked option across both expiries also feeds its own per-instrument
                    // state, for the strike-level child tables (2026-09-12) -- independent of the
                    // synthetic-forward bookkeeping above, which only ever reads latestOptionTick.
                    if (optionStates.TryGetValue(tick.Token, out var optionState))
                    {
                        optionState.ApplyTick(tick);
                    }
                }

                hasNext = await enumerator.MoveNextAsync();
            }

            // Same "don't emit anything until the fundamentals have ticked at least once" guard
            // LiveFeatureEngine.ComputeCadence already applies to Spot/Future.
            if (spotState.LatestKnownPrice is { } spotPrice && futureState.LatestKnownPrice is { } futurePrice)
            {
                var cadenceContext = BuildRow(
                    boundary, day, spotPrice, futurePrice,
                    spotState, futureState, vixState, futureFlow, futureDepth, futureCvdProxy,
                    futureOiLookback, latestFutureOi, ref futureDayOpenOi,
                    depthImbalance5Min, depthImbalance15Min, cvdNet5Min, cvdNet15Min);
                rows.Add(cadenceContext);
                var spotPriceChange = previousSpotPrice is { } prevSpot ? spotPrice - prevSpot : (decimal?)null;
                strikeRowsByCadenceIndex.Add(BuildStrikeRows(boundary, day, spotPrice, spotPriceChange, optionStates));
                previousSpotPrice = spotPrice;
            }

            spotState.ResetCadence();
            futureState.ResetCadence();
            vixState.ResetCadence();
            futureFlow.ResetCadence();
            futureDepth.Reset();
            futureCvdProxy.ResetCadence();
            foreach (var optionState in optionStates.Values)
            {
                optionState.ResetCadence();
            }
        }

        if (rows.Count == 0)
        {
            return new PopulationResult(PopulationOutcome.NoTradableData, 0);
        }

        destination.CadenceContexts.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);

        // Only after the save above do `rows[i].Id` values exist -- child rows were built and
        // buffered per cadence during the main loop, before any Id was assignable, so the FK is
        // stamped here rather than at construction time.
        var allStrikeRows = new List<StrikeCadenceSnapshot>();
        for (var i = 0; i < rows.Count; i++)
        {
            foreach (var strikeRow in strikeRowsByCadenceIndex[i])
            {
                strikeRow.CadenceContextId = rows[i].Id;
                allStrikeRows.Add(strikeRow);
            }
        }

        var bandRowCount = 0;
        if (allStrikeRows.Count > 0)
        {
            destination.StrikeCadenceSnapshots.AddRange(allStrikeRows);
            await destination.SaveChangesAsync(cancellationToken);

            var bandRows = BuildBandRows(rows, strikeRowsByCadenceIndex);
            if (bandRows.Count > 0)
            {
                destination.StrikeBandCadenceSnapshots.AddRange(bandRows);
                await destination.SaveChangesAsync(cancellationToken);
                bandRowCount = bandRows.Count;
            }
        }

        return new PopulationResult(PopulationOutcome.Populated, rows.Count, allStrikeRows.Count, bandRowCount);
    }

    static CadenceContext BuildRow(
        DateTimeOffset now, DayContext day, decimal spotPrice, decimal futurePrice,
        InstrumentPriceState spotState, InstrumentPriceState futureState, InstrumentPriceState vixState,
        FutureFlowAccumulator futureFlow, DepthImbalanceAccumulator futureDepth, FutureCvdProxyAccumulator futureCvdProxy,
        OiLookbackWindow futureOiLookback, long? latestFutureOi, ref long? futureDayOpenOi,
        WelfordRollingWindow depthImbalance5Min, WelfordRollingWindow depthImbalance15Min,
        RollingNetSumWindow cvdNet5Min, RollingNetSumWindow cvdNet15Min)
    {
        var syntheticForward = ComputeSyntheticForward(spotPrice, now, day);

        // Audit finding F50's OI-refresh reality applies here too: Lookback must run before
        // Record for this cadence, same before/after ordering LiveFeatureEngine.ComputeOiBuildupNet
        // already follows -- otherwise this cadence's own OI would contaminate its own lookback.
        var oiLookbackValue = futureOiLookback.Lookback(day.FutureToken, now);
        var futureOiChangeFromLastKnown = oiLookbackValue is { } prevOi && latestFutureOi is { } curOi
            ? curOi - prevOi
            : (long?)null;
        if (latestFutureOi is { } oiToRecord)
        {
            futureOiLookback.Record(day.FutureToken, now, oiToRecord);
        }

        futureDayOpenOi ??= latestFutureOi;
        var futureOiChangeForDay = latestFutureOi is { } cur && futureDayOpenOi is { } open
            ? cur - open
            : (long?)null;

        if (futureDepth.CadenceImbalance is { } imbalance)
        {
            depthImbalance5Min.Add(now, imbalance);
            depthImbalance15Min.Add(now, imbalance);
        }

        // Same "skip a null cadence, never zero-fill it" rule as the depth-imbalance windows
        // above -- a cadence where nothing was classifiable contributes no information, not a
        // fabricated zero net.
        if (futureCvdProxy.CadenceNet is { } cvdNet)
        {
            cvdNet5Min.Add(now, cvdNet);
            cvdNet15Min.Add(now, cvdNet);
        }

        return new CadenceContext
        {
            Timestamp = now,
            AsOfDate = day.AsOfDate,

            AtmStrikeBySpot = NearestStrike(day.DistinctStrikes, spotPrice),
            AtmStrikeByFuture = NearestStrike(day.DistinctStrikes, futurePrice),
            AtmStrikeBySyntheticForward = syntheticForward is { } forward ? NearestStrike(day.DistinctStrikes, (decimal)forward) : null,

            NearestExpiryDate = day.NearestExpiry,
            HoursToExpiryCalendar = TimeToExpiry.YearsUntilExpiry(day.NearestExpiry, now) * 365.0 * 24.0,
            HoursToExpiryTrading = TradingHoursRemaining(now, day.NearestExpiry, day.ExpiryMoment),

            TicksObservedSpot = spotState.TickCountThisCadence,
            TicksObservedFuture = futureState.TickCountThisCadence,
            TicksObservedVix = vixState.TickCountThisCadence,

            SpotCloseFromLastCadence = spotState.CadenceClose,
            SpotChangeFromLastCadence = spotState.CadenceClose is { } sc && spotState.CadenceOpen is { } so ? sc - so : null,
            SpotOpenFromLastCadence = spotState.CadenceOpen,
            SpotHighFromLastCadence = spotState.CadenceHigh,
            SpotLowFromLastCadence = spotState.CadenceLow,
            SpotChangeForDay = spotState.LatestKnownPrice is { } slp && spotState.DayOpen is { } sdo ? slp - sdo : null,
            SpotOpenForDay = spotState.DayOpen,
            SpotHighForDay = spotState.DayHigh,
            SpotLowForDay = spotState.DayLow,

            FutureCloseFromLastCadence = futureState.CadenceClose,
            FutureChangeFromLastCadence = futureState.CadenceClose is { } fc && futureState.CadenceOpen is { } fo ? fc - fo : null,
            FutureOpenFromLastCadence = futureState.CadenceOpen,
            FutureHighFromLastCadence = futureState.CadenceHigh,
            FutureLowFromLastCadence = futureState.CadenceLow,
            FutureChangeForDay = futureState.LatestKnownPrice is { } flp && futureState.DayOpen is { } fdo ? flp - fdo : null,
            FutureOpenForDay = futureState.DayOpen,
            FutureHighForDay = futureState.DayHigh,
            FutureLowForDay = futureState.DayLow,

            VixCloseFromLastCadence = vixState.CadenceClose,
            VixChangeFromLastCadence = vixState.CadenceClose is { } vc && vixState.CadenceOpen is { } vo ? vc - vo : null,
            VixOpenFromLastCadence = vixState.CadenceOpen,
            VixHighFromLastCadence = vixState.CadenceHigh,
            VixLowFromLastCadence = vixState.CadenceLow,
            VixChangeForDay = vixState.LatestKnownPrice is { } vlp && vixState.DayOpen is { } vdo ? vlp - vdo : null,
            VixOpenForDay = vixState.DayOpen,
            VixHighForDay = vixState.DayHigh,
            VixLowForDay = vixState.DayLow,

            FutureVolumeCumulativeDay = futureFlow.LatestCumulativeVolume,
            FutureVolumeDeltaThisCadence = futureFlow.CadenceVolumeDelta,
            FutureVwap = futureFlow.Vwap,

            FutureOpenInterest = latestFutureOi,
            FutureOiChangeFromLastKnown = futureOiChangeFromLastKnown,
            FutureOiChangeForDay = futureOiChangeForDay,

            FutureTotalBidQty = futureDepth.AverageBidQty,
            FutureTotalAskQty = futureDepth.AverageAskQty,
            FutureDepthImbalanceFromLastCadence = futureDepth.CadenceImbalance,
            FutureDepthImbalanceMean5Min = depthImbalance5Min.IsWarmedUp ? depthImbalance5Min.Mean : null,
            FutureDepthImbalanceMean15Min = depthImbalance15Min.IsWarmedUp ? depthImbalance15Min.Mean : null,

            FutureCvdProxyThisCadence = futureCvdProxy.CadenceNet,
            FutureCvdProxyCumulativeDay = futureCvdProxy.CumulativeNet,
            FutureCvdProxyNet5Min = cvdNet5Min.IsWarmedUp ? cvdNet5Min.Sum : null,
            FutureCvdProxyNet15Min = cvdNet15Min.IsWarmedUp ? cvdNet15Min.Sum : null,
        };
    }

    // Widened from 3 to 10 (2026-09-13) -- the actually-subscribed chain is ~20-21 distinct
    // strikes per expiry (confirmed against niftysignal_vm_copy.instruments, ~ATM+/-10 at 50-point
    // spacing), and two untested live components genuinely need that full width: GammaExposure/
    // VannaExposure/CharmExposure sum over the ENTIRE nearest-expiry chain (not a band), and IvSkew
    // targets a real expected-move OTM strike pair that lands ~5-6 strikes out on a normal week.
    // A band of 3 physically could not answer either question -- this was a real blocker, not a
    // style choice. Existing bands (Itm2Atm1/Strike3/Strike5/Strike7, all <= offset 3) are
    // untouched by this -- purely additive, more data available, nothing already built narrows.
    const int PersistedStrikeOffsetBand = 10;

    /// <summary>
    /// Builds this cadence's StrikeCadenceSnapshot rows -- one per (expiry, strike, option type)
    /// currently within ATM+/-<see cref="PersistedStrikeOffsetBand"/> of spot, across both
    /// tracked chains. ATM is spot-based (matches CadenceContext.AtmStrikeBySpot, the convention
    /// already used everywhere strike selection happens in this codebase), recomputed fresh every
    /// cadence since it drifts intraday -- band membership can genuinely change strike to strike
    /// across the day. <paramref name="optionStates"/> itself tracks every strike in both chains
    /// regardless of band membership, so a strike's own history is never gapped by drifting in or
    /// out of the persisted band.
    /// PENDING (external review, 2026-09-12): "ATM" here is nearest-strike-to-spot, but Greeks
    /// (see BuildStrikeRow below) are priced against the synthetic parity underlying S, a
    /// genuinely different number whenever basis is nonzero (50-100+ points on a volatile day) --
    /// StrikeOffsetFromAtm can be one strike off from "nearest to the S actually being priced
    /// against." Not fixed: this is a codebase-wide strike-selection convention (also used live,
    /// per this comment's own note), not a narrow bug -- changing it means deciding whether to
    /// move strike selection everywhere to an S-based ATM or leave live trading on spot-based ATM
    /// and only rebase this analysis-only bucketing, a real design decision, not a quick fix.
    /// </summary>
    static List<StrikeCadenceSnapshot> BuildStrikeRows(
        DateTimeOffset now, DayContext day, decimal spotPrice, decimal? spotPriceChange, Dictionary<string, OptionInstrumentState> optionStates)
    {
        var result = new List<StrikeCadenceSnapshot>();

        var atmByExpiry = new Dictionary<DateOnly, int>();
        var syntheticUnderlyingByExpiry = new Dictionary<DateOnly, double?>();
        foreach (var chain in day.TrackedChains)
        {
            if (chain.DistinctStrikes.Count == 0)
            {
                continue;
            }

            var atmStrike = chain.DistinctStrikes.OrderBy(s => Math.Abs(s - spotPrice)).First();
            atmByExpiry[chain.ExpiryDate] = chain.DistinctStrikes.IndexOf(atmStrike);

            // Each expiry's own put-call-parity underlying, not the tracked monthly future -- see
            // ComputeSyntheticUnderlyingForChain's own doc comment for why this specific swap
            // matters (a real, already-once-observed bug in this codebase when using the wrong
            // underlying for a short-dated weekly's Greeks).
            syntheticUnderlyingByExpiry[chain.ExpiryDate] =
                ComputeSyntheticUnderlyingForChain(spotPrice, now, chain, day.LatestOptionTick, day.RiskFreeRate);
        }

        foreach (var state in optionStates.Values)
        {
            if (!state.HasEverTicked || !atmByExpiry.TryGetValue(state.ExpiryDate, out var atmIndex))
            {
                continue;
            }

            var chain = day.TrackedChains.First(c => c.ExpiryDate == state.ExpiryDate);
            var strikeIndex = chain.DistinctStrikes.IndexOf(state.StrikePrice);
            var offset = strikeIndex - atmIndex;
            if (Math.Abs(offset) > PersistedStrikeOffsetBand)
            {
                continue;
            }

            var daysToExpiry = (state.ExpiryDate.ToDateTime(TimeOnly.MinValue) - day.AsOfDate.ToDateTime(TimeOnly.MinValue)).Days;
            var syntheticUnderlying = syntheticUnderlyingByExpiry.GetValueOrDefault(state.ExpiryDate);
            result.Add(BuildStrikeRow(now, day, syntheticUnderlying, spotPriceChange, state, offset, daysToExpiry));
        }

        return result;
    }

    static StrikeCadenceSnapshot BuildStrikeRow(
        DateTimeOffset now, DayContext day, double? syntheticUnderlying, decimal? spotPriceChange, OptionInstrumentState state, int offset, int daysToExpiry)
    {
        var markPrice = state.CadenceMarkPrice;
        var markPriceDelta = markPrice is { } mp && state.PriorKnownMarkPrice is { } prior ? mp - prior : (decimal?)null;
        var openInterestDelta = state.CadenceHasOiBaseline ? state.CadenceOpenInterestDelta : (long?)null;
        // Classified against the UNDERLYING's own move (spotPriceChange), not this strike's own
        // MarkPriceDelta (kept below as its own diagnostic column, unrelated to this classification
        // now) -- a vol-driven option-price pop with zero underlying move must not flip a
        // buildup/unwinding quadrant. Fixes an inconsistency with LiveFeatureEngine.ComputeCadence,
        // which already classified against spotPriceChange (F17); this populator was the one still
        // using the option's own price, flagged by external review (2026-09-12).
        var oiBuildup = spotPriceChange is { } spd && openInterestDelta is { } oid
            ? OiBuildupClassifier.Classify(spd, oid)
            : (OiBuildupClassification?)null;

        // Exactly once per cadence -- feeds the 1-minute valuation window with this cadence's own
        // MarkPrice before it's cleared by ResetCadence.
        var (oiNotional, oiChangeNotional) = state.RecordCadenceAndComputeOiNotional(now);

        double? iv = null;
        double? delta = null, gamma = null, thetaPerDay = null, vega = null, vanna = null, charmPerDay = null;
        // Underlying = this expiry's own put-call-parity synthetic (ComputeSyntheticUnderlyingForChain),
        // not the tracked monthly future -- see that method's doc comment. Null (not a fallback to
        // the future) when the parity solve itself can't run yet this cadence, matching this
        // codebase's own "never fabricate, always null when uncertain" convention.
        if (markPrice is { } mid && mid > 0 && syntheticUnderlying is { } underlying && underlying > 0)
        {
            var t = TimeToExpiry.YearsUntilExpiry(state.ExpiryDate, now);
            iv = ImpliedVolatilitySolver.Solve(state.OptionType, (double)mid, underlying, (double)state.StrikePrice, t, day.RiskFreeRate);
            if (iv is { } vol)
            {
                var greeks = BlackScholes.Calculate(state.OptionType, underlying, (double)state.StrikePrice, t, day.RiskFreeRate, vol).Greeks;
                delta = greeks.Delta;
                gamma = greeks.Gamma;
                thetaPerDay = greeks.ThetaPerDay;
                vega = greeks.Vega;
                vanna = greeks.Vanna;
                charmPerDay = greeks.CharmPerDay;
            }
        }

        return new StrikeCadenceSnapshot
        {
            AsOfDate = day.AsOfDate,
            Timestamp = now,
            Token = state.Token,
            ExpiryDate = state.ExpiryDate,
            DaysToExpiry = daysToExpiry,
            StrikePrice = state.StrikePrice,
            OptionType = state.OptionType,
            StrikeOffsetFromAtm = offset,

            MarkPrice = markPrice,
            BidPrice = state.CadenceBidPrice,
            AskPrice = state.CadenceAskPrice,

            OpenFromLastCadence = state.Price.CadenceOpen,
            HighFromLastCadence = state.Price.CadenceHigh,
            LowFromLastCadence = state.Price.CadenceLow,
            CloseFromLastCadence = state.Price.CadenceClose,

            VolumeDelta = state.CadenceVolumeDelta,
            NotionalDelta = state.CadenceNotionalDelta,
            OpenInterest = state.LatestOpenInterest,
            OpenInterestDelta = openInterestDelta,
            OiNotional = oiNotional,
            OiChangeNotional = oiChangeNotional,
            MarkPriceDelta = markPriceDelta,
            OiBuildup = oiBuildup,
            SpreadAbs = state.CadenceBidPrice is { } bp && state.CadenceAskPrice is { } ap ? ap - bp : null,
            SpreadPctOfMid = state.CadenceBidPrice is { } bp2 && state.CadenceAskPrice is { } ap2 && markPrice is { } mid2 && mid2 > 0
                ? (ap2 - bp2) / mid2
                : (decimal?)null,

            TotalBidQty = state.DepthImbalance.AverageBidQty,
            TotalAskQty = state.DepthImbalance.AverageAskQty,
            DepthImbalanceFromLastCadence = state.DepthImbalance.CadenceImbalance,

            CvdProxyVolumeThisCadence = state.CvdProxy.CadenceVolumeNet,
            CvdProxyNotionalThisCadence = state.CvdProxy.CadenceNotionalNet,

            ImpliedVolatility = iv,
            Delta = delta,
            Gamma = gamma,
            ThetaPerDay = thetaPerDay,
            Vega = vega,
            Vanna = vanna,
            CharmPerDay = charmPerDay,
        };
    }

    static readonly string[] BandDefinitions = ["Strike3", "Strike5", "Strike7", "Itm2Atm1"];
    static readonly int[] CadenceMinutesConfigurations = [5, 15];

    /// <summary>
    /// Per docs/CHILD_TABLE_SCHEMA.md's band table -- Itm2Atm1 is the only asymmetric,
    /// per-option-type-mirrored band (ITM sits on opposite sides of ATM for calls vs. puts by
    /// definition); the other three are identical ranges for both option types.
    /// </summary>
    public static bool OffsetInBand(string bandDefinition, OptionType optionType, int offset) => bandDefinition switch
    {
        "Strike3" => offset is >= -1 and <= 1,
        "Strike5" => offset is >= -2 and <= 2,
        "Strike7" => offset is >= -3 and <= 3,
        "Itm2Atm1" => optionType == OptionType.Call ? offset is >= -2 and <= 0 : offset is >= 0 and <= 2,
        _ => throw new ArgumentOutOfRangeException(nameof(bandDefinition), bandDefinition, "Unknown band definition."),
    };

    /// <summary>
    /// Second pass over the day's already-built StrikeCadenceSnapshot rows -- a real rollup of
    /// stored 15s data, not a guess at what happened between snapshots (see
    /// docs/CHILD_TABLE_SCHEMA.md). Buckets every <see cref="CadenceMinutesConfigurations"/> value
    /// independently (rows for each coexist in the same table), grouping by cadence INDEX rather
    /// than wall-clock alignment -- CadenceContext's own cadences sit at :15/:30/:45/:00 seconds,
    /// not clean 5-minute marks, so counting cadences from the day's first one gives a
    /// deterministic bucket boundary without depending on that offset.
    /// </summary>
    static List<StrikeBandCadenceSnapshot> BuildBandRows(
        List<CadenceContext> cadenceRows, List<List<StrikeCadenceSnapshot>> strikeRowsByCadenceIndex)
    {
        var result = new List<StrikeBandCadenceSnapshot>();

        foreach (var cadenceMinutes in CadenceMinutesConfigurations)
        {
            var cadencesPerBucket = (int)(TimeSpan.FromMinutes(cadenceMinutes) / CadenceLength);

            for (var bucketStart = 0; bucketStart < cadenceRows.Count; bucketStart += cadencesPerBucket)
            {
                var bucketEnd = Math.Min(bucketStart + cadencesPerBucket, cadenceRows.Count);
                if (bucketEnd - bucketStart < cadencesPerBucket)
                {
                    break; // a trailing partial bucket at day's end -- not a full window, not persisted
                }

                var bucketBoundaryCadence = cadenceRows[bucketEnd - 1];
                var strikeRowsInBucket = strikeRowsByCadenceIndex.Skip(bucketStart).Take(cadencesPerBucket).SelectMany(r => r).ToList();
                var lastCadenceStrikeRows = strikeRowsByCadenceIndex[bucketEnd - 1];

                foreach (var expiryDate in strikeRowsInBucket.Select(r => r.ExpiryDate).Distinct())
                {
                    var expiryRows = strikeRowsInBucket.Where(r => r.ExpiryDate == expiryDate).ToList();
                    var lastExpiryRows = lastCadenceStrikeRows.Where(r => r.ExpiryDate == expiryDate).ToList();
                    var daysToExpiry = expiryRows[0].DaysToExpiry;

                    foreach (var bandDefinition in BandDefinitions)
                    {
                        var callRows = expiryRows.Where(r => r.OptionType == OptionType.Call && OffsetInBand(bandDefinition, OptionType.Call, r.StrikeOffsetFromAtm)).ToList();
                        var putRows = expiryRows.Where(r => r.OptionType == OptionType.Put && OffsetInBand(bandDefinition, OptionType.Put, r.StrikeOffsetFromAtm)).ToList();
                        var lastCallRows = lastExpiryRows.Where(r => r.OptionType == OptionType.Call && OffsetInBand(bandDefinition, OptionType.Call, r.StrikeOffsetFromAtm)).ToList();
                        var lastPutRows = lastExpiryRows.Where(r => r.OptionType == OptionType.Put && OffsetInBand(bandDefinition, OptionType.Put, r.StrikeOffsetFromAtm)).ToList();

                        if (callRows.Count == 0 && putRows.Count == 0)
                        {
                            continue;
                        }

                        result.Add(new StrikeBandCadenceSnapshot
                        {
                            CadenceContextId = bucketBoundaryCadence.Id,
                            AsOfDate = bucketBoundaryCadence.AsOfDate,
                            Timestamp = bucketBoundaryCadence.Timestamp,
                            CadenceMinutes = cadenceMinutes,
                            ExpiryDate = expiryDate,
                            DaysToExpiry = daysToExpiry,
                            BandDefinition = bandDefinition,

                            CallStrikeCount = callRows.Select(r => r.StrikePrice).Distinct().Count(r => callRows.Any(x => x.StrikePrice == r && x.MarkPrice is not null)),
                            PutStrikeCount = putRows.Select(r => r.StrikePrice).Distinct().Count(r => putRows.Any(x => x.StrikePrice == r && x.MarkPrice is not null)),

                            CallVolumeSum = SumOrNull(callRows.Select(r => r.VolumeDelta)),
                            PutVolumeSum = SumOrNull(putRows.Select(r => r.VolumeDelta)),
                            CallNotionalSum = SumNotionalOrNull(callRows),
                            PutNotionalSum = SumNotionalOrNull(putRows),
                            CallOiChangeSum = SumOrNull(callRows.Select(r => r.OpenInterestDelta)),
                            PutOiChangeSum = SumOrNull(putRows.Select(r => r.OpenInterestDelta)),
                            CallOiSum = SumOrNull(lastCallRows.Select(r => r.OpenInterest)),
                            PutOiSum = SumOrNull(lastPutRows.Select(r => r.OpenInterest)),
                            CallOiChangeNotionalSum = SumDecimalOrNull(callRows.Select(r => r.OiChangeNotional)),
                            PutOiChangeNotionalSum = SumDecimalOrNull(putRows.Select(r => r.OiChangeNotional)),

                            CallAvgIv = OiWeightedAverageIv(lastCallRows),
                            PutAvgIv = OiWeightedAverageIv(lastPutRows),

                            CallCvdProxyVolumeNet = SumOrNull(callRows.Select(r => r.CvdProxyVolumeThisCadence)),
                            PutCvdProxyVolumeNet = SumOrNull(putRows.Select(r => r.CvdProxyVolumeThisCadence)),
                            CallCvdProxyNotionalNet = SumDecimalOrNull(callRows.Select(r => r.CvdProxyNotionalThisCadence)),
                            PutCvdProxyNotionalNet = SumDecimalOrNull(putRows.Select(r => r.CvdProxyNotionalThisCadence)),

                            CallDepthImbalanceAvg = AvgOrNull(callRows.Select(r => r.DepthImbalanceFromLastCadence)),
                            PutDepthImbalanceAvg = AvgOrNull(putRows.Select(r => r.DepthImbalanceFromLastCadence)),
                        });
                    }
                }
            }
        }

        return result;
    }

    static long? SumOrNull(IEnumerable<long?> values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count > 0 ? present.Sum() : null;
    }

    static double? AvgOrNull(IEnumerable<double?> values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count > 0 ? present.Average() : null;
    }

    static decimal? SumDecimalOrNull(IEnumerable<decimal?> values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count > 0 ? present.Sum() : null;
    }

    static decimal? SumNotionalOrNull(List<StrikeCadenceSnapshot> rows)
    {
        var present = rows.Where(r => r.MarkPrice is not null && r.VolumeDelta is not null).ToList();
        return present.Count > 0 ? present.Sum(r => r.MarkPrice!.Value * r.VolumeDelta!.Value) : null;
    }

    /// <summary>OI-weighted average IV across the band's strikes at the bucket's own boundary cadence (a level quantity, not summed across the bucket's cadences the way flow quantities are) -- a strike with null OI that cadence is excluded, not treated as zero-weight.</summary>
    static double? OiWeightedAverageIv(List<StrikeCadenceSnapshot> rowsAtBoundary)
    {
        var weighted = rowsAtBoundary.Where(r => r.ImpliedVolatility is not null && r.OpenInterest is > 0).ToList();
        if (weighted.Count == 0)
        {
            return null;
        }

        var totalWeight = weighted.Sum(r => (double)r.OpenInterest!.Value);
        return weighted.Sum(r => r.ImpliedVolatility!.Value * r.OpenInterest!.Value) / totalWeight;
    }

    /// <summary>
    /// Replicates LiveFeatureEngine.ComputeUnderlyingPrice's strike selection (5 strikes
    /// nearest spot with both legs quoted) without depending on Host -- SyntheticForward.Compute
    /// itself already lives in the shared NiftySignal.Pricing project, so only this small
    /// selection step needs restating here.
    /// </summary>
    static double? ComputeSyntheticForward(decimal spotPrice, DateTimeOffset now, DayContext day)
    {
        var t = TimeToExpiry.YearsUntilExpiry(day.NearestExpiry, now);
        var pairs = new List<(double Strike, double CallMid, double PutMid)>();

        foreach (var strike in day.DistinctStrikes.OrderBy(s => Math.Abs(s - spotPrice)).Take(SyntheticForwardStrikeCount))
        {
            var call = day.NearestExpiryOptions.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strike);
            var put = day.NearestExpiryOptions.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strike);
            if (call is null || put is null
                || !day.LatestOptionTick.TryGetValue(call.Token, out var callTick) || !day.LatestOptionTick.TryGetValue(put.Token, out var putTick)
                || MidPrice(callTick) is not { } callMid || MidPrice(putTick) is not { } putMid)
            {
                continue;
            }

            pairs.Add(((double)strike, (double)callMid, (double)putMid));
        }

        return SyntheticForward.Compute(pairs, t, day.RiskFreeRate);
    }

    /// <summary>
    /// Same technique as <see cref="ComputeSyntheticForward"/> above (put-call parity, 5 strikes
    /// nearest spot, SyntheticForward.Compute), generalized to run against any tracked
    /// <see cref="ExpiryChain"/> rather than only the near-week one -- built 2026-09-12 for
    /// StrikeCadenceSnapshot's own Greeks/IV, which need each expiry's own synthetic underlying,
    /// not the single one <see cref="ComputeSyntheticForward"/> computes for ATM-strike selection.
    /// Deliberately a separate method rather than a modification of that one: the near-week-only
    /// synthetic forward already feeds AtmStrikeBySyntheticForward on the parent table and stays
    /// untouched, so this addition carries zero risk to that already-working calculation.
    ///
    /// This is the fix a friend's review (2026-09-12) correctly identified as missing: using the
    /// tracked monthly future as the underlying for weekly options' Greeks embeds that contract's
    /// own month-end cost-of-carry into a 2-9-day option's IV solve, a real, already-once-observed
    /// bug in this exact codebase (see SyntheticForward's own doc comment: a live 2026-09-07 check
    /// found put IV running 7-8 vol points below call IV at the same strike, all day, the
    /// signature of a too-low underlying -- not genuine skew, which shows up across strikes, not
    /// as a same-strike call/put split). SyntheticForward.Compute's S-based parity solve, fed into
    /// this codebase's existing BlackScholes.Calculate (dividendYield=0) convention, is
    /// mathematically equivalent to Black-76 on that same expiry's own forward -- no second
    /// pricing formula is needed, only the correct underlying reaching the one already in use.
    /// </summary>
    static double? ComputeSyntheticUnderlyingForChain(decimal spotPrice, DateTimeOffset now, ExpiryChain chain, Dictionary<string, Tick> latestOptionTick, double riskFreeRate)
    {
        var t = TimeToExpiry.YearsUntilExpiry(chain.ExpiryDate, now);
        var pairs = new List<(double Strike, double CallMid, double PutMid)>();

        foreach (var strike in chain.DistinctStrikes.OrderBy(s => Math.Abs(s - spotPrice)).Take(SyntheticForwardStrikeCount))
        {
            var call = chain.Options.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strike);
            var put = chain.Options.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strike);
            if (call is null || put is null
                || !latestOptionTick.TryGetValue(call.Token, out var callTick) || !latestOptionTick.TryGetValue(put.Token, out var putTick)
                || MidPrice(callTick) is not { } callMid || MidPrice(putTick) is not { } putMid)
            {
                continue;
            }

            pairs.Add(((double)strike, (double)callMid, (double)putMid));
        }

        return SyntheticForward.Compute(pairs, t, riskFreeRate);
    }

    static decimal? NearestStrike(List<decimal> distinctStrikes, decimal price) =>
        distinctStrikes.Count == 0 ? null : distinctStrikes.OrderBy(s => Math.Abs(s - price)).First();

    /// <summary>
    /// Sums actual NSE trading-session overlap (09:15-15:30 IST) between <paramref name="now"/>
    /// and <paramref name="expiryMoment"/>, one calendar day at a time, skipping weekends. No
    /// NSE holiday calendar exists in this codebase -- market holidays are not excluded, same
    /// acknowledged gap as production's own trading-day-T discussion (see TimeToExpiry's doc
    /// comment). Floored at the same 2-minute minimum TimeToExpiry uses, for the same reason
    /// (a zero or near-zero T is numerically unstable for anything dividing by it downstream).
    /// </summary>
    public static double TradingHoursRemaining(DateTimeOffset now, DateOnly expiryDate, DateTimeOffset expiryMoment)
    {
        if (now >= expiryMoment)
        {
            return TimeToExpiry.Minimum.TotalHours;
        }

        // .ToOffset(IstOffset), not .DateTime directly -- .DateTime returns wall-clock time in
        // whatever offset `now` already carries (could be UTC, from PopulateDayAsync's own
        // now-UTC `boundary`), and reading that as if it were already IST would only happen to
        // give the right calendar date because a 09:15-15:30 IST session safely falls entirely
        // within a single UTC calendar day (03:45-10:00 UTC) -- true today, but a coincidence of
        // this specific session window, not something this method should quietly depend on.
        var totalHours = 0.0;
        for (var cursorDate = DateOnly.FromDateTime(now.ToOffset(IstOffset).DateTime); cursorDate <= expiryDate; cursorDate = cursorDate.AddDays(1))
        {
            if (cursorDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            var sessionStart = new DateTimeOffset(cursorDate.ToDateTime(MarketOpen), IstOffset);
            var sessionEnd = new DateTimeOffset(cursorDate.ToDateTime(MarketClose), IstOffset);

            var overlapStart = sessionStart > now ? sessionStart : now;
            var overlapEnd = sessionEnd < expiryMoment ? sessionEnd : expiryMoment;

            if (overlapEnd > overlapStart)
            {
                totalHours += (overlapEnd - overlapStart).TotalHours;
            }
        }

        return Math.Max(totalHours, TimeToExpiry.Minimum.TotalHours);
    }

    // Plan-established mid convention (bid1+ask1)/2 -- LTP fallback only when no two-sided
    // quote has arrived yet. Same helper LiveFeatureEngine.MidPrice already applies, restated
    // here since this project deliberately doesn't reference Host.
    static decimal? MidPrice(Tick tick)
    {
        if (tick.Depth is { } depth && depth.Bid1Price > 0 && depth.Ask1Price > 0)
        {
            return (depth.Bid1Price + depth.Ask1Price) / 2;
        }

        return tick.LastPrice > 0 ? tick.LastPrice : null;
    }
}

/// <summary>
/// Tracks one instrument's own OHLC state, both for "this cadence" (reset every boundary) and
/// "today so far" (grows across the whole day). <see cref="LatestKnownPrice"/> never resets --
/// it persists across a quiet cadence exactly like LiveFeatureEngine's own `_latest` dictionary,
/// so day-level fields (change/high/low for day) stay populated even through a tick gap, while
/// the intrabar fields (open/high/low/close from last cadence) correctly go null for a cadence
/// with zero real ticks rather than fabricating a bar that didn't happen.
/// </summary>
public sealed class InstrumentPriceState
{
    public decimal? LatestKnownPrice { get; private set; }
    public decimal? DayOpen { get; private set; }
    public decimal? DayHigh { get; private set; }
    public decimal? DayLow { get; private set; }

    public decimal? CadenceOpen { get; private set; }
    public decimal? CadenceHigh { get; private set; }
    public decimal? CadenceLow { get; private set; }
    public decimal? CadenceClose { get; private set; }
    public int TickCountThisCadence { get; private set; }

    public void ApplyTick(decimal price)
    {
        LatestKnownPrice = price;
        DayOpen ??= price;
        DayHigh = DayHigh is { } h ? Math.Max(h, price) : price;
        DayLow = DayLow is { } l ? Math.Min(l, price) : price;

        CadenceOpen ??= price;
        CadenceHigh = CadenceHigh is { } ch ? Math.Max(ch, price) : price;
        CadenceLow = CadenceLow is { } cl ? Math.Min(cl, price) : price;
        CadenceClose = price;
        TickCountThisCadence++;
    }

    public void ResetCadence()
    {
        CadenceOpen = null;
        CadenceHigh = null;
        CadenceLow = null;
        CadenceClose = null;
        TickCountThisCadence = 0;
    }
}

/// <summary>
/// The tracked future's own cumulative volume/VWAP state, accumulated per real tick (not per
/// cadence) -- the same "diff cumulative day volume against the previous tick, floored at 0
/// against a feed reset" pattern LiveFeatureEngine.OnTick already uses for this exact purpose.
/// One running baseline feeds both the day-long VWAP accumulation and the per-cadence volume
/// delta, so the two can never silently disagree with each other.
/// </summary>
public sealed class FutureFlowAccumulator
{
    long? _previousVolume;
    double _cumulativePriceVolume;
    double _cumulativeVolume;

    public long? LatestCumulativeVolume { get; private set; }

    public long CadenceVolumeDelta { get; private set; }

    /// <summary>This tick's own volume delta (not the cadence total) -- exposed so a caller (the CVD proxy accumulator) classifies the exact same delta VWAP just weighted, rather than re-deriving its own, potentially disagreeing, volume baseline.</summary>
    public long LastVolumeDelta { get; private set; }

    public double? Vwap => _cumulativeVolume > 0 ? _cumulativePriceVolume / _cumulativeVolume : null;

    public void ApplyTick(decimal price, long volume)
    {
        var delta = _previousVolume is { } previous ? Math.Max(0, volume - previous) : 0;
        _previousVolume = volume;
        LatestCumulativeVolume = volume;
        LastVolumeDelta = delta;

        _cumulativePriceVolume += (double)price * delta;
        _cumulativeVolume += delta;
        CadenceVolumeDelta += delta;
    }

    public void ResetCadence() => CadenceVolumeDelta = 0;
}

/// <summary>
/// One instrument's own order-book depth imbalance, averaged over every real tick observed within
/// one cadence -- built from complete historical tick data, not a 3-second-stride sample (live
/// production only samples that coarsely because it can't wait around on a real-time feed; this
/// offline pass has no such constraint, per explicit instruction). Genuinely generic (nothing here
/// is future-specific) -- named `DepthImbalanceAccumulator` until 2026-09-12, when option-level depth
/// imbalance needed the exact same computation and this was renamed rather than duplicated.
/// </summary>
public sealed class DepthImbalanceAccumulator
{
    double _imbalanceSum;
    int _imbalanceCount;
    double _bidQtySum;
    double _askQtySum;
    int _rawSampleCount;

    public void ApplyTick(MarketDepth depth)
    {
        var bid = depth.TotalBidQty;
        var ask = depth.TotalAskQty;

        if (bid + ask > 0)
        {
            _imbalanceSum += (double)(bid - ask) / (bid + ask);
            _imbalanceCount++;
        }

        _bidQtySum += bid;
        _askQtySum += ask;
        _rawSampleCount++;
    }

    /// <summary>Average of the per-tick imbalance ratio -- null if no depth-bearing tick arrived this cadence.</summary>
    public double? CadenceImbalance => _imbalanceCount > 0 ? _imbalanceSum / _imbalanceCount : null;

    public double? AverageBidQty => _rawSampleCount > 0 ? _bidQtySum / _rawSampleCount : null;

    public double? AverageAskQty => _rawSampleCount > 0 ? _askQtySum / _rawSampleCount : null;

    public void Reset()
    {
        _imbalanceSum = 0;
        _imbalanceCount = 0;
        _bidQtySum = 0;
        _askQtySum = 0;
        _rawSampleCount = 0;
    }
}

// FutureCvdProxyAccumulator and RollingNetSumWindow moved to NiftySignal.Features (2026-09-17,
// Phase 3 unification) so NiftySignal.Host/LiveFeatureEngine.cs can reference the SAME classes
// instead of maintaining its own hand-inlined port of their exact logic -- see
// NiftySignal.Features/FutureCvdProxyAccumulator.cs and RollingNetSumWindow.cs for the full doc
// comments (unchanged) and docs/SCORE_CANDIDATES.md's Phase 3 section for the rationale.

/// <summary>
/// Aggressor-volume-proxy classifier for one option instrument (2026-09-12; renamed in spirit,
/// not in code, per a friend's review -- "CVD" implies a real trade tape, which this feed doesn't
/// have; this is honestly a proxy, same as <see cref="FutureCvdProxyAccumulator"/>). Extended with
/// a notional (rupee) net alongside the volume net, since a strike's own price matters for
/// weighting call-vs-put comparisons the way it never needed to for a single future. Kept as its
/// own class rather than repurposing FutureCvdProxyAccumulator directly, so the future's own
/// already-tested behavior is never put at risk by this extension.
///
/// Classification rule tightened 2026-09-12, same review: the original mid-point rule
/// (LastPrice >= (bid+ask)/2 -> buy-leaning) is reasonable on the tracked future, whose spread is
/// usually a tick or two, but options carry much wider spreads -- a print sitting anywhere inside
/// a several-rupee-wide spread is genuinely ambiguous, and forcing a mid-based guess there is low
/// signal-to-noise. Now requires the print to reach or cross the actual touch (LastPrice >= Ask1
/// for buy-leaning, LastPrice &lt;= Bid1 for sell-leaning); a print strictly inside the spread
/// classifies as nothing (excluded, not zero) rather than being forced onto whichever side of a
/// wide mid it happens to sit nearer. This trades classified volume (fewer ticks qualify) for a
/// cleaner proxy.
///
/// No CumulativeDay equivalent here, deliberately -- FutureCvdProxyCumulativeDay's own
/// correlation check already showed a running-total-since-open version has a real staleness
/// flaw (a strong morning trend keeps dragging the total hours later); not worth re-learning
/// that lesson for the option side before it's even validated once.
/// </summary>
public sealed class CvdProxyAccumulator
{
    long _cadenceVolumeNet;
    decimal _cadenceNotionalNet;
    int _cadenceContributingTicks;

    /// <summary>Net buy-minus-sell classified contract volume this cadence. Null if no tick this cadence had both a two-sided depth quote and a print reaching the touch to classify.</summary>
    public long? CadenceVolumeNet => _cadenceContributingTicks > 0 ? _cadenceVolumeNet : null;

    /// <summary>Net buy-minus-sell classified notional this cadence, each classified tick valued at its own LastPrice (not a separately-computed mark) -- an honest turnover figure.</summary>
    public decimal? CadenceNotionalNet => _cadenceContributingTicks > 0 ? _cadenceNotionalNet : null;

    public void ApplyTick(decimal lastPrice, MarketDepth depth, long volumeDelta)
    {
        if (depth.Bid1Price <= 0 || depth.Ask1Price <= 0 || volumeDelta <= 0)
        {
            return;
        }

        bool isBuyLeaning;
        if (lastPrice >= depth.Ask1Price)
        {
            isBuyLeaning = true;
        }
        else if (lastPrice <= depth.Bid1Price)
        {
            isBuyLeaning = false;
        }
        else
        {
            return; // printed strictly inside the spread -- ambiguous on a wide option book, excluded rather than forced onto a side
        }

        _cadenceVolumeNet += isBuyLeaning ? volumeDelta : -volumeDelta;
        _cadenceNotionalNet += (isBuyLeaning ? 1 : -1) * volumeDelta * lastPrice;
        _cadenceContributingTicks++;
    }

    public void ResetCadence()
    {
        _cadenceVolumeNet = 0;
        _cadenceNotionalNet = 0;
        _cadenceContributingTicks = 0;
    }
}

/// <summary>
/// One option instrument's own state across the whole day (2026-09-12) -- bundles traded-price
/// OHLC (<see cref="Price"/>, reusing <see cref="InstrumentPriceState"/> unchanged), per-cadence
/// volume/OI deltas, the latest quote for Greeks/IV solving, and its own <see cref="CvdProxy"/>.
/// One instance per tracked token across both chains (near-week and next-week), regardless of
/// current ATM-band membership -- see BuildStrikeRows' own doc comment for why the tracked
/// universe is wider than what actually gets persisted each cadence.
/// </summary>
public sealed class OptionInstrumentState(decimal strikePrice, OptionType optionType, DateOnly expiryDate)
{
    public decimal StrikePrice { get; } = strikePrice;

    public OptionType OptionType { get; } = optionType;

    public DateOnly ExpiryDate { get; } = expiryDate;

    /// <summary>Set once, the first time a tick for this token is seen -- Dictionary construction happens before any token string is known per-instance, so this is assigned on first ApplyTick rather than through the constructor.</summary>
    public string Token { get; private set; } = string.Empty;

    public InstrumentPriceState Price { get; } = new();

    public CvdProxyAccumulator CvdProxy { get; } = new();

    public DepthImbalanceAccumulator DepthImbalance { get; } = new();

    /// <summary>
    /// Rolling mean of MarkPrice over a trailing 1 minute, fed once per cadence with this
    /// cadence's own MarkPrice. Originally built at 3 minutes on an assumed OI refresh rate;
    /// recalibrated 2026-09-12 after directly measuring the real gap between OI changes on
    /// populated data -- a tight, consistent ~60s across both a liquid near-ATM strike and a thin
    /// far strike (median 60s, p90 60s, max 75s), not ~3 minutes. User's own call: OI is truth,
    /// value it as soon as real data supports the average rather than padding the window past
    /// what the feed's actual refresh cadence needs. Valuing OI at the raw instantaneous price
    /// would let 15s-level price noise dominate a metric meant to track real OI-value change;
    /// averaging price over the same window OI actually refreshes at keeps OiNotional's swings
    /// attributable to OI genuinely changing, not the price bouncing around in between.
    /// </summary>
    readonly WelfordRollingWindow _oiValuationPriceWindow = new(TimeSpan.FromMinutes(1));

    long? _previousVolume;
    long? _priorKnownOpenInterest;
    decimal? _priorKnownMarkPrice;

    long _cadenceVolumeDelta;
    decimal _cadenceNotionalDelta;
    decimal? _cadenceMarkPrice;
    decimal? _cadenceBidPrice;
    decimal? _cadenceAskPrice;

    public bool HasEverTicked { get; private set; }

    /// <summary>
    /// True once a real volume baseline existed as of the START of this cadence -- captured only
    /// in ResetCadence (mirroring CadenceHasOiBaseline exactly), not read live off _previousVolume,
    /// which updates mid-cadence for correct intra-cadence diffing and would otherwise flip true
    /// partway through the very first cadence a token is ever seen in. Matches production's own
    /// StrikeSnapshot.VolumeDelta convention: "null on the first cadence after startup."
    /// </summary>
    public bool HasVolumeBaseline { get; private set; }

    /// <summary>Traded this cadence, matching StrikeSnapshot's own semantics -- null on the very first cadence a token is ever seen in (see HasVolumeBaseline), a real confirmed number (0 or otherwise) from the next cadence onward.</summary>
    public long? CadenceVolumeDelta => HasVolumeBaseline ? _cadenceVolumeDelta : null;

    /// <summary>
    /// Blind (unclassified) rupee notional traded this cadence -- sum of each tick's own LastPrice
    /// times that tick's own volume delta, not VolumeDelta x one end-of-cadence MarkPrice (a single
    /// closing price would misvalue a cadence where price moved across several ticks). Same
    /// per-tick-priced technique CvdProxy already uses for its own notional, just without the
    /// buy/sell classification -- "how much traded," not "which way it leaned." Same null-on-first-
    /// cadence semantics as CadenceVolumeDelta, since it shares the same baseline.
    /// </summary>
    public decimal? CadenceNotionalDelta => HasVolumeBaseline ? _cadenceNotionalDelta : null;

    public long? LatestOpenInterest { get; private set; }

    /// <summary>True once a real prior OI observation exists to diff against -- same null-vs-zero distinction as HasVolumeBaseline.</summary>
    public bool CadenceHasOiBaseline => _priorKnownOpenInterest is not null;

    /// <summary>
    /// PENDING fix (Batch 3 replication check, 2026-09-13, CoreScoreReplayDiff finding): this used
    /// to be a field accumulated per-tick in ApplyTick (`_cadenceOpenInterestDelta += oi - priorOi`
    /// on every OI-bearing tick), which double/triple/N-counted the SAME underlying OI change once
    /// per tick this cadence, since OI itself refreshes far slower than tick frequency (this
    /// project's own prior finding: ~60s) -- a strike ticking 10 times this cadence with an
    /// unchanged OI would add the same delta 10 times. Caught via NiftySignal.CoreScoreReplayDiff
    /// comparing this pipeline's OiChangeDiff15mRaw against live's own clean per-cadence diff:
    /// backtest values ran up to ~500M on a metric that should plausibly cap in the low millions.
    /// Now computed directly from <see cref="LatestOpenInterest"/> (held over across cadences,
    /// correctly reflecting "current known OI" regardless of whether THIS cadence had a fresh OI
    /// tick) minus the OI as of this cadence's own start -- a single, correct delta, computed once,
    /// with no per-tick accumulation to over-count. A quiet cadence (no new OI tick) now correctly
    /// yields 0 (OI genuinely unchanged), not an inflated multiple of some earlier tick's delta.
    /// </summary>
    public long? CadenceOpenInterestDelta => CadenceHasOiBaseline && LatestOpenInterest is { } latestOi ? latestOi - _priorKnownOpenInterest!.Value : null;

    /// <summary>
    /// Feeds this cadence's own MarkPrice (if any) into the 1-minute valuation window and returns
    /// (OiNotional, OiChangeNotional) from that one shared average -- must be called exactly once
    /// per cadence, before ResetCadence clears CadenceMarkPrice, mirroring how the parent
    /// CadencePopulator loop feeds its own rolling windows (depth imbalance, CVD net) at row-build
    /// time, inclusive of the current cadence's own contribution. Both values null until the
    /// window has 1 real minute of history; OiChangeNotional additionally requires a real OI
    /// baseline (same null-vs-zero distinction as OpenInterestDelta itself).
    ///
    /// OiNotional = OpenInterest (the level) x averaged price -- value of open positions.
    /// OiChangeNotional = OpenInterestDelta (the flow) x the SAME averaged price -- rupee value of
    /// this cadence's OI change specifically, mirroring exactly how NotionalDelta = VolumeDelta x
    /// price mirrors the blind volume total. Sharing one averaged price for both keeps them
    /// mutually consistent and avoids feeding the window twice.
    /// </summary>
    public (decimal? OiNotional, decimal? OiChangeNotional) RecordCadenceAndComputeOiNotional(DateTimeOffset now)
    {
        if (_cadenceMarkPrice is { } mark)
        {
            _oiValuationPriceWindow.Add(now, (double)mark);
        }

        if (!_oiValuationPriceWindow.IsWarmedUp)
        {
            return (null, null);
        }

        var avgPrice = (decimal)_oiValuationPriceWindow.Mean;
        var oiNotional = LatestOpenInterest is { } oi ? oi * avgPrice : (decimal?)null;
        var oiChangeNotional = CadenceOpenInterestDelta is { } oiDelta ? oiDelta * avgPrice : (decimal?)null;
        return (oiNotional, oiChangeNotional);
    }

    /// <summary>This cadence's own (bid1+ask1)/2 -- null (not held over) if no tick with a two-sided quote arrived this specific cadence, matching production's "instantaneous, not averaged" convention for BidPrice/AskPrice.</summary>
    public decimal? CadenceMarkPrice => _cadenceMarkPrice;

    /// <summary>Last known mark price from any earlier cadence that had one -- carried forward through quiet cadences so MarkPriceDelta doesn't go null just because the immediately preceding cadence happened to be quiet.</summary>
    public decimal? PriorKnownMarkPrice => _priorKnownMarkPrice;

    public decimal? CadenceBidPrice => _cadenceBidPrice;

    public decimal? CadenceAskPrice => _cadenceAskPrice;

    public void ApplyTick(Tick tick)
    {
        if (Token.Length == 0)
        {
            Token = tick.Token;
        }

        HasEverTicked = true;
        Price.ApplyTick(tick.LastPrice);

        var volumeDelta = _previousVolume is { } previousVolume ? Math.Max(0, tick.Volume - previousVolume) : 0;
        _previousVolume = tick.Volume;
        _cadenceVolumeDelta += volumeDelta;
        _cadenceNotionalDelta += volumeDelta * tick.LastPrice;

        if (tick.OpenInterest is { } oi)
        {
            // No per-tick delta accumulation here -- CadenceOpenInterestDelta is computed on
            // demand from LatestOpenInterest vs. _priorKnownOpenInterest (see its own doc comment
            // for why accumulating per-tick was a real bug: OI refreshes far slower than ticks
            // arrive, so a busy strike would count the same delta once per tick).
            LatestOpenInterest = oi;
        }

        if (tick.Depth is { } depth && depth.Bid1Price > 0 && depth.Ask1Price > 0)
        {
            _cadenceBidPrice = depth.Bid1Price;
            _cadenceAskPrice = depth.Ask1Price;
            _cadenceMarkPrice = (depth.Bid1Price + depth.Ask1Price) / 2m;
            CvdProxy.ApplyTick(tick.LastPrice, depth, volumeDelta);
            DepthImbalance.ApplyTick(depth); // resting liquidity, sampled every tick regardless of whether volume traded -- unlike CvdProxy, which only cares about executed flow
        }
    }

    public void ResetCadence()
    {
        Price.ResetCadence();
        CvdProxy.ResetCadence();
        DepthImbalance.Reset();

        if (LatestOpenInterest is { } oi)
        {
            _priorKnownOpenInterest = oi;
        }

        _priorKnownMarkPrice = _cadenceMarkPrice ?? _priorKnownMarkPrice;
        HasVolumeBaseline = _previousVolume is not null;

        _cadenceVolumeDelta = 0;
        _cadenceNotionalDelta = 0;
        _cadenceMarkPrice = null;
        _cadenceBidPrice = null;
        _cadenceAskPrice = null;
    }
}
