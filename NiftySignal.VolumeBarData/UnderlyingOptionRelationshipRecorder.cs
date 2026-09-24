using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, descriptive research layer -- see
/// <see cref="UnderlyingOptionRelationshipObservation"/>'s own doc comment for the full design
/// rationale. Builds one <see cref="RelationshipObservation"/> per futures event bar, using ONLY
/// the existing Experiment 1 event clock/bars/strike-selection/crossover engine, never a
/// re-implementation of any of them:
/// <list type="bullet">
/// <item><see cref="EventBarStrikeSelector.PickDynamicAtm"/> -- SAME dynamic-ATM rule, unchanged.</item>
/// <item><see cref="PriceCrossoverEngine"/> -- SAME fast=3/slow=10 SMA engine, unchanged;
/// independent instances for Futures/Spot/Ce/Pe, reset on CE/PE contract transition using the
/// exact same convention <see cref="Vc0DteBehaviorRecorder"/> already established.</item>
/// <item><see cref="OptionTickSeries"/> -- reused as-is for the spot instrument's own ticks too
/// (nothing in its implementation is option-specific despite the name).</item>
/// </list>
/// In-memory only, no new database table -- same convention every other file in this track uses.
/// </summary>
public static class UnderlyingOptionRelationshipRecorder
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketClose = new(15, 30);

    public const int FastBars = 3;
    public const int SlowBars = 10;

    public static Task<List<RelationshipObservation>> RecordAsync(
        NiftySignalDbContext source, DateOnly asOfDate,
        IReadOnlyList<Instrument> chain, IReadOnlyList<FutureEventBar> futureBars,
        IReadOnlyList<SynchronizedOptionEventBar> optionBars, CancellationToken cancellationToken)
        => RecordForUnderlyingAsync(source, asOfDate, chain, futureBars, optionBars, "NIFTY", cancellationToken);

    /// <summary>
    /// 2026-09-24, cross-index validation (Sensex). Identical to <see cref="RecordAsync"/> (same
    /// classification/crossover/change formulas, untouched) -- the only difference is the SPOT
    /// lookup filters by <paramref name="underlying"/> instead of the fixed "NIFTY" string, so a
    /// Sensex run never accidentally attaches NIFTY's own spot ticks (both underlyings' Index
    /// rows can coexist in the same database on the same dates). <see cref="RecordAsync"/> is
    /// unchanged and remains the sole implementation every existing 0-DTE/DTE-expansion/
    /// transition command uses.
    /// </summary>
    public static async Task<List<RelationshipObservation>> RecordForUnderlyingAsync(
        NiftySignalDbContext source, DateOnly asOfDate,
        IReadOnlyList<Instrument> chain, IReadOnlyList<FutureEventBar> futureBars,
        IReadOnlyList<SynchronizedOptionEventBar> optionBars, string underlying, CancellationToken cancellationToken)
    {
        var rows = new List<RelationshipObservation>();
        if (futureBars.Count == 0)
        {
            return rows;
        }

        var barsByToken = optionBars.GroupBy(b => b.Token).ToDictionary(g => g.Key, g => g.OrderBy(b => b.EventId).ToList());
        var callChain = chain.Where(c => c.OptionType == OptionType.Call).ToList();
        var putChain = chain.Where(c => c.OptionType == OptionType.Put).ToList();

        // Point 1's own spot-availability check: a NIFTY Index instrument may or may not exist
        // for this day -- never assumed, never fabricated if absent.
        var spotInstrument = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Index && i.Underlying == underlying)
            .FirstOrDefaultAsync(cancellationToken);

        OptionTickSeries? spotSeries = null;
        if (spotInstrument is not null)
        {
            var dayStart = futureBars[0].StartTimestamp;
            var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();
            spotSeries = await OptionTickSeries.LoadAsync(source, spotInstrument.Token, dayStart, dayEnd, cancellationToken);
        }
        var spotEntries = spotSeries?.AllEntries ?? [];
        var spotCursor = 0;

        var futuresEngine = new PriceCrossoverEngine(FastBars, SlowBars);
        var spotEngine = new PriceCrossoverEngine(FastBars, SlowBars);
        var ceEngine = new PriceCrossoverEngine(FastBars, SlowBars);
        var peEngine = new PriceCrossoverEngine(FastBars, SlowBars);
        string? lastCeToken = null, lastPeToken = null;
        decimal? previousFuturesClose = null, previousSpotClose = null, previousCeLtp = null, previousPeLtp = null;

        for (var eventId = 0; eventId < futureBars.Count; eventId++)
        {
            var futureBar = futureBars[eventId];

            // Spot aggregation for THIS event's own interval -- same cursor-based bucketing
            // approach SynchronizedOptionBarBuilder already uses, kept local here since spot is
            // not an option and doesn't belong in that option-specific builder.
            decimal? spotOpen = null, spotHigh = null, spotLow = null, spotClose = null;
            var spotTickCount = 0;
            decimal spotPriceSum = 0;
            while (spotCursor < spotEntries.Count && spotEntries[spotCursor].Timestamp <= futureBar.EndTimestamp)
            {
                if (spotEntries[spotCursor].Timestamp < futureBar.StartTimestamp)
                {
                    spotCursor++;
                    continue;
                }
                var e = spotEntries[spotCursor];
                spotOpen ??= e.LastPrice;
                spotHigh = spotHigh is { } h ? Math.Max(h, e.LastPrice) : e.LastPrice;
                spotLow = spotLow is { } l ? Math.Min(l, e.LastPrice) : e.LastPrice;
                spotClose = e.LastPrice;
                spotPriceSum += e.LastPrice;
                spotTickCount++;
                spotCursor++;
            }
            var spotMissingData = spotInstrument is not null && spotTickCount == 0;
            var spotAveragePrice = spotTickCount > 0 ? spotPriceSum / spotTickCount : (decimal?)null;

            var callAtm = EventBarStrikeSelector.PickDynamicAtm(callChain, OptionType.Call, futureBar.Close);
            var putAtm = EventBarStrikeSelector.PickDynamicAtm(putChain, OptionType.Put, futureBar.Close);
            var ceToken = callAtm?.Token ?? "";
            var peToken = putAtm?.Token ?? "";

            var ceTransition = lastCeToken is not null && lastCeToken != ceToken;
            var peTransition = lastPeToken is not null && lastPeToken != peToken;
            if (ceTransition) { ceEngine.Reset(); }
            if (peTransition) { peEngine.Reset(); }
            if (callAtm is not null) { lastCeToken = ceToken; }
            if (putAtm is not null) { lastPeToken = peToken; }

            var ceBar = callAtm is not null && barsByToken.TryGetValue(ceToken, out var ceBars) ? ceBars.FirstOrDefault(b => b.EventId == eventId) : null;
            var peBar = putAtm is not null && barsByToken.TryGetValue(peToken, out var peBars) ? peBars.FirstOrDefault(b => b.EventId == eventId) : null;

            var futuresStep = futuresEngine.Observe((double)futureBar.Close, 0.0);
            var spotStep = spotEngine.Observe(spotAveragePrice is { } sp ? (double)sp : null, 0.0);
            var ceStep = ceEngine.Observe(ceBar?.AverageLtp is { } clp ? (double)clp : null, 0.0);
            var peStep = peEngine.Observe(peBar?.AverageLtp is { } plp ? (double)plp : null, 0.0);

            var futuresChange1 = previousFuturesClose is { } pfc ? futureBar.Close - pfc : (decimal?)null;
            var spotChange1 = previousSpotClose is { } psc && spotClose is { } sc ? sc - psc : (decimal?)null;
            var ceChange1 = !ceTransition && previousCeLtp is { } pcl && ceBar?.AverageLtp is { } cl ? cl - pcl : (decimal?)null;
            var peChange1 = !peTransition && previousPeLtp is { } ppl && peBar?.AverageLtp is { } pl ? pl - ppl : (decimal?)null;

            var category = ClassifyRelationship(ceTransition, peTransition,
                ceBar is null || ceBar.MissingData, peBar is null || peBar.MissingData,
                RelationshipDirectionExtensions.Classify(futuresChange1),
                RelationshipDirectionExtensions.Classify(ceChange1),
                RelationshipDirectionExtensions.Classify(peChange1));

            rows.Add(new RelationshipObservation(
                asOfDate, eventId, futureBar.StartTimestamp, futureBar.EndTimestamp, ceBar?.Dte ?? peBar?.Dte ?? 0,
                futureBar.Open, futureBar.High, futureBar.Low, futureBar.Close, futureBar.Volume, futureBar.DurationMs,
                spotInstrument is not null, spotMissingData, spotAveragePrice, spotOpen, spotHigh, spotLow, spotClose, spotTickCount,
                callAtm?.StrikePrice ?? putAtm?.StrikePrice ?? 0m,
                ceToken, ceBar?.AverageLtp, ceBar is null || ceBar.MissingData, ceBar?.IsStale ?? false, ceBar?.TickCount ?? 0, ceTransition,
                peToken, peBar?.AverageLtp, peBar is null || peBar.MissingData, peBar?.IsStale ?? false, peBar?.TickCount ?? 0, peTransition,
                futuresChange1, spotChange1, ceChange1, peChange1,
                futuresStep.FastMa, futuresStep.SlowMa, CrossoverStateLabel(futuresStep.FastMa, futuresStep.SlowMa),
                spotStep.FastMa, spotStep.SlowMa, CrossoverStateLabel(spotStep.FastMa, spotStep.SlowMa),
                ceStep.FastMa, ceStep.SlowMa, CrossoverStateLabel(ceStep.FastMa, ceStep.SlowMa),
                peStep.FastMa, peStep.SlowMa, CrossoverStateLabel(peStep.FastMa, peStep.SlowMa),
                category));

            // Strictly the IMMEDIATELY PRECEDING event's own reading, never carried across a gap
            // of more than one event -- otherwise a "Change1" (1-event-bar change) computed after
            // several missing bars would silently span multiple bars while still being labeled
            // as a 1-bar change. A multi-bar gap simply means Change1 is null at the next event
            // too, exactly as intended.
            previousFuturesClose = futureBar.Close;
            previousSpotClose = spotClose; // null when this bar itself had no real spot tick.
            previousCeLtp = ceBar is not null && !ceBar.MissingData ? ceBar.AverageLtp : null;
            previousPeLtp = peBar is not null && !peBar.MissingData ? peBar.AverageLtp : null;
        }

        return rows;
    }

    static string CrossoverStateLabel(double? fast, double? slow) => (fast, slow) switch
    {
        (null, _) or (_, null) => "Unavailable",
        var (f, s) when f > s => "AboveSlow",
        var (f, s) when f < s => "BelowSlow",
        _ => "Equal",
    };

    /// <summary>
    /// Deterministic, descriptive-only label (spec section 9) -- precedence: a contract
    /// transition on either leg first (movement can't be attributed to one contract this event),
    /// then missing option data, then the three directions. Never labeled bullish/bearish/
    /// reversal/demand -- a provisional research description only.
    /// </summary>
    static string ClassifyRelationship(
        bool ceTransition, bool peTransition, bool ceMissing, bool peMissing,
        RelationshipDirection futuresDir, RelationshipDirection ceDir, RelationshipDirection peDir)
    {
        if (ceTransition || peTransition)
        {
            return "ContractTransition";
        }
        if (ceMissing || peMissing || futuresDir == RelationshipDirection.Unavailable || ceDir == RelationshipDirection.Unavailable || peDir == RelationshipDirection.Unavailable)
        {
            return "OptionDataIncomplete";
        }
        if (futuresDir == RelationshipDirection.Flat)
        {
            return ceDir != peDir ? "UnderlyingFlat_CEPEDivergence" : "UnderlyingFlat_NoDivergence";
        }
        return $"Underlying{futuresDir}_CE{ceDir}_PE{peDir}";
    }

    public static void WriteCsv(IReadOnlyList<RelationshipObservation> rows, string path, TimeSpan istOffset)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("TradingDate,EventId,StartTimeIST,EndTimeIST,Dte,"
            + "FuturesOpen,FuturesHigh,FuturesLow,FuturesClose,FuturesVolume,FuturesEventDurationMs,FuturesChange1,FuturesFastMa,FuturesSlowMa,FuturesCrossoverState,"
            + "SpotAvailable,SpotMissingData,SpotAveragePrice,SpotOpen,SpotHigh,SpotLow,SpotClose,SpotTickCount,SpotChange1,SpotFastMa,SpotSlowMa,SpotCrossoverState,"
            + "AtmStrike,"
            + "CeToken,CeAverageLtp,CeMissingData,CeIsStale,CeTickCount,CeContractTransition,CeChange1,CeFastMa,CeSlowMa,CeCrossoverState,"
            + "PeToken,PeAverageLtp,PeMissingData,PeIsStale,PeTickCount,PeContractTransition,PeChange1,PeFastMa,PeSlowMa,PeCrossoverState,"
            + "RelationshipCategory");
        foreach (var r in rows)
        {
            var start = r.StartTimestamp.ToOffset(istOffset);
            var end = r.EndTimestamp.ToOffset(istOffset);
            writer.WriteLine(string.Join(',',
                r.TradingDate.ToString("yyyy-MM-dd"), r.EventId, start.ToString("HH:mm:ss.fff"), end.ToString("HH:mm:ss.fff"), r.Dte,
                r.FuturesOpen, r.FuturesHigh, r.FuturesLow, r.FuturesClose, r.FuturesVolume, r.FuturesEventDurationMs, r.FuturesChange1, r.FuturesFastMa, r.FuturesSlowMa, r.FuturesCrossoverState,
                r.SpotAvailable, r.SpotMissingData, r.SpotAveragePrice, r.SpotOpen, r.SpotHigh, r.SpotLow, r.SpotClose, r.SpotTickCount, r.SpotChange1, r.SpotFastMa, r.SpotSlowMa, r.SpotCrossoverState,
                r.AtmStrike,
                r.CeToken, r.CeAverageLtp, r.CeMissingData, r.CeIsStale, r.CeTickCount, r.CeContractTransition, r.CeChange1, r.CeFastMa, r.CeSlowMa, r.CeCrossoverState,
                r.PeToken, r.PeAverageLtp, r.PeMissingData, r.PeIsStale, r.PeTickCount, r.PeContractTransition, r.PeChange1, r.PeFastMa, r.PeSlowMa, r.PeCrossoverState,
                r.RelationshipCategory));
        }
    }
}
