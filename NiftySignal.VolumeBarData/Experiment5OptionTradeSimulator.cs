using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Execution;
using NiftySignal.Persistence;
using NiftySignal.Rules;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (2026-09-22, Experiment 5 -- see docs/VOLUME_BAR_FINDINGS.md's dated
/// "Experiment 5" section). Builds one option trade per (qualifying bar, fixed horizon) pair, using
/// the SAME real-tick option-price/ATM-strike machinery this project already established
/// (<see cref="OptionPriceSeries"/> for LastPrice-based fills, <see cref="OptionAtmBarRow.AtmStrike"/>
/// -- the synthetic-forward-based ATM already precomputed by <see cref="OptionAtmPopulator"/>, read
/// directly rather than recomputed) and the existing <see cref="CostsConfig"/>/<see cref="PaperTradeSimulator"/>
/// cost model for the cost-sensitivity pass. NO stops/targets/trailing exits -- fixed-horizon only,
/// per the task's own explicit "do not optimize exits" instruction (item 14). Never touches
/// NiftySignal.Host/NiftySignal.Rules.EntryRuleEvaluator/LiveTradingEngine -- research-only, offline.
/// </summary>
public static class Experiment5OptionTradeSimulator
{
    /// <summary>
    /// One option trade's full outcome at one fixed horizon. Gross = raw entry/exit LastPrice, no
    /// costs. Net = the SAME CostsConfig/PaperTradeSimulator SlippageTicks+BrokeragePerOrder model
    /// the live paper-trade path uses (<see cref="PaperTradeSimulator.FillEntry"/>/<see cref="PaperTradeSimulator.FillExit"/>),
    /// applied to the option's own real <see cref="Instrument.TickSize"/> -- see the doc section's
    /// own "Cost Sensitivity" subsection for the explicit translation-assumption note (LastPrice
    /// stands in for "ask" on entry / "bid" on exit; this project's own OptionPriceSeries has no
    /// separate bid/ask series to draw from).
    /// </summary>
    public sealed record OptionTrade(
        DateOnly Date, int BarIndex, long BarVolumeThreshold, int Horizon, string Session, int Dte, bool IsZeroDte,
        int Direction, OptionType Side, decimal Strike,
        DateTimeOffset EntryTimestamp, DateTimeOffset ExitTimestamp,
        decimal EntryPriceGross, decimal ExitPriceGross, decimal GrossPnlPerUnit, decimal GrossPnlLot,
        decimal NetPnlLot, double UnderlyingFwdReturnPct, double? EntryIv, double? ExitIv,
        decimal MaePoints, decimal MfePoints, decimal MaePercent, decimal MfePercent, double HoldingMinutes)
    {
        public double GrossReturnPct => EntryPriceGross > 0 ? (double)(GrossPnlPerUnit / EntryPriceGross) * 100.0 : 0.0;

        /// <summary>Case A/B/C classification, item 8: Case A -- Nifty reversed AND option P&amp;L positive (gross); Case B -- Nifty reversed but option P&amp;L NEGATIVE (gross) -- the specifically-flagged case; Case C -- Nifty did not reverse (insufficient) and option P&amp;L negative.</summary>
        public string Case(bool niftyReversed) => (niftyReversed, GrossPnlPerUnit > 0) switch
        {
            (true, true) => "A",
            (true, false) => "B",
            (false, false) => "C",
            (false, true) => "Other (Nifty didn't reverse, option still won)",
        };
    }

    /// <summary>
    /// PREMIUM-BAND strike selection (2026-09-22 correction, see docs/VOLUME_BAR_FINDINGS.md's dated
    /// "Correction" section): the strike whose OWN entry-time premium (Call side for a bought call,
    /// Put side for a bought put -- the same <paramref name="side"/> the trade already trades) falls
    /// in [<see cref="PremiumBandLow"/>, <see cref="PremiumBandHigh"/>], NOT the pure
    /// synthetic-forward ATM strike (<see cref="OptionAtmBarRow.AtmStrike"/>) Experiment 5/6 used
    /// before this correction. Explicit design (per user instruction, 2026-09-22):
    /// <list type="bullet">
    /// <item>Tie-break: among all in-band strikes, the one nearest the OLD synthetic-forward ATM
    /// strike (<paramref name="atmStrikeForTieBreak"/>) -- the most defensible default, since it
    /// keeps the pick close to the original methodology's notion of "the money" when several
    /// strikes qualify. If no ATM strike is available for this bar (<paramref name="atmStrikeForTieBreak"/>
    /// is null), falls back to nearest the band's own midpoint (125) -- stated explicitly here since
    /// it's a secondary, less-tested rule.</item>
    /// <item>No match: if NO strike in the day's chain has an entry-time premium in the band, this
    /// returns null and the caller skips the trade entirely -- never forces a mismatched strike.</item>
    /// </list>
    /// Reuses the SAME <see cref="OptionPriceSeries.LoadAsync"/>/<see cref="OptionPriceSeries.PriceAtOrAfter"/>
    /// pricing path <see cref="BuildTradeAsync"/> already used for its old ATM-strike lookup -- no
    /// second pricing mechanism.
    /// </summary>
    public const decimal PremiumBandLow = 100m;
    public const decimal PremiumBandHigh = 150m;

    static async Task<(Instrument Instrument, decimal EntryPrice, OptionPriceSeries Series)?> SelectPremiumBandStrikeAsync(
        NiftySignalDbContext source, OptionType side, decimal? atmStrikeForTieBreak, DateOnly date,
        DateTimeOffset signalTimestamp, DateTimeOffset dayStart, DateTimeOffset dayEnd,
        IReadOnlyList<Instrument> optionChain,
        Dictionary<(DateOnly Date, string Token), OptionPriceSeries> priceCache,
        CancellationToken cancellationToken)
    {
        var tieBreakReference = atmStrikeForTieBreak ?? (PremiumBandLow + PremiumBandHigh) / 2m;
        Instrument? best = null;
        decimal? bestEntryPrice = null;
        OptionPriceSeries? bestSeries = null;

        foreach (var candidate in optionChain.Where(o => o.OptionType == side && o.StrikePrice is not null).OrderBy(o => o.StrikePrice))
        {
            var cacheKey = (date, candidate.Token);
            if (!priceCache.TryGetValue(cacheKey, out var candidateSeries))
            {
                candidateSeries = await OptionPriceSeries.LoadAsync(source, candidate.Token, dayStart, dayEnd, cancellationToken);
                priceCache[cacheKey] = candidateSeries;
            }

            var price = candidateSeries.PriceAtOrAfter(signalTimestamp);
            if (price is not { } p || p < PremiumBandLow || p > PremiumBandHigh)
            {
                continue;
            }

            if (best is null || Math.Abs(candidate.StrikePrice!.Value - tieBreakReference) < Math.Abs(best.StrikePrice!.Value - tieBreakReference))
            {
                best = candidate;
                bestEntryPrice = p;
                bestSeries = candidateSeries;
            }
        }

        return best is null ? null : (best, bestEntryPrice!.Value, bestSeries!);
    }

    /// <summary>
    /// Builds the trade for one (qualifying bar, horizon) pair, or null if no strike's premium fell
    /// in the [100,150] band at entry time, or a usable exit price could not be found (never
    /// fabricated). <paramref name="dayBars"/> must be the SAME day's futures bars, 0-indexed by
    /// <see cref="VolumeBarRow.BarIndex"/> (ascending, contiguous, as <see cref="VolumeBarPopulator"/>
    /// guarantees).
    /// </summary>
    public static async Task<OptionTrade?> BuildTradeAsync(
        NiftySignalDbContext source, Experiment5UnderlyingAnalyzer.QualifyingBar qb, int horizon,
        IReadOnlyList<VolumeBarRow> dayBars, IReadOnlyList<Instrument> optionChain,
        IReadOnlyDictionary<int, OptionAtmBarRow> atmByBarIndex,
        Dictionary<(DateOnly Date, string Token), OptionPriceSeries> priceCache,
        CostsConfig costs, int quantity, CancellationToken cancellationToken)
    {
        var exitIndex = qb.BarIndex + horizon;
        if (exitIndex >= dayBars.Count)
        {
            return null;
        }

        var side = qb.Direction > 0 ? OptionType.Put : OptionType.Call;
        var dayStart = dayBars[0].StartTimestamp;
        var dayEnd = dayBars[^1].EndTimestamp;

        var selected = await SelectPremiumBandStrikeAsync(
            source, side, qb.AtmStrike, qb.Date, qb.SignalTimestamp, dayStart, dayEnd, optionChain, priceCache, cancellationToken);
        if (selected is not { } sel)
        {
            return null;
        }

        var instrument = sel.Instrument;
        var strike = instrument.StrikePrice!.Value;
        var series = sel.Series;
        var entry = sel.EntryPrice;

        var exitBar = dayBars[exitIndex];
        var exitPrice = series.PriceAtOrBefore(exitBar.EndTimestamp);
        if (exitPrice is not { } exit)
        {
            return null;
        }

        var grossPnlPerUnit = exit - entry;
        var grossPnlLot = grossPnlPerUnit * quantity;

        var entryFill = PaperTradeSimulator.FillEntry(entry, instrument.TickSize, quantity, costs);
        var exitFill = PaperTradeSimulator.FillExit(exit, instrument.TickSize, quantity, costs);
        var netPnlLot = PaperTradeSimulator.ComputeNetPnl(entryFill, exitFill);

        var pricesDuringHold = series.AllPrices
            .Where(p => p.Timestamp >= qb.SignalTimestamp && p.Timestamp <= exitBar.EndTimestamp)
            .Select(p => p.Price)
            .ToList();
        if (pricesDuringHold.Count == 0)
        {
            pricesDuringHold = [entry, exit];
        }

        var maeMfe = MaeMfeCalculator.Compute(entry, pricesDuringHold);

        atmByBarIndex.TryGetValue(qb.BarIndex, out var entryAtm);
        atmByBarIndex.TryGetValue(exitIndex, out var exitAtm);
        var entryIv = side == OptionType.Call ? entryAtm?.AtmCallIv : entryAtm?.AtmPutIv;
        var exitIv = side == OptionType.Call ? exitAtm?.AtmCallIv : exitAtm?.AtmPutIv;

        var underlyingFwd = qb.NiftyPriceAtSignal > 0 ? (double)((exitBar.ClosePrice - qb.NiftyPriceAtSignal) / qb.NiftyPriceAtSignal) * 100.0 : 0.0;

        return new OptionTrade(
            qb.Date, qb.BarIndex, qb.BarVolumeThreshold, horizon, qb.Session, qb.Dte, qb.IsZeroDte,
            qb.Direction, side, strike, qb.SignalTimestamp, exitBar.EndTimestamp,
            entry, exit, grossPnlPerUnit, grossPnlLot, netPnlLot, underlyingFwd, entryIv, exitIv,
            maeMfe.MaePoints, maeMfe.MfePoints, maeMfe.MaePercent, maeMfe.MfePercent,
            (exitBar.EndTimestamp - qb.SignalTimestamp).TotalMinutes);
    }

    /// <summary>
    /// CORRECTION (2026-09-22, root-cause of the 269-513-"trades"-in-one-day bug -- see
    /// docs/VOLUME_BAR_FINDINGS.md's dated "Correction" section): <see cref="Experiment5UnderlyingAnalyzer.CollectByState"/>
    /// flags every qualifying bar independently with no notion of "is a previously-opened trade
    /// still open" -- fine for ITS OWN predictive-only purpose (measuring forward outcomes from each
    /// qualifying bar in isolation), but wrong for turning qualifying bars into a SEQUENCE of
    /// simulated option trades, since real trading can only hold one position at a time (this
    /// project's own <c>MaxConcurrentPositions</c>/single-open-trade discipline). This is the gate:
    /// given one day's qualifying bars (already in ascending <see cref="Experiment5UnderlyingAnalyzer.QualifyingBar.BarIndex"/>
    /// order, as <see cref="Experiment5UnderlyingAnalyzer.CollectByState"/> returns them) and the
    /// FIXED horizon a trade sequence will use, returns the subset that could actually have been
    /// traded one-at-a-time. Exact rule: track the exit bar index of the most recently OPENED trade
    /// (<c>entryBarIndex + horizon</c>); a later qualifying bar is skipped if its own
    /// <see cref="Experiment5UnderlyingAnalyzer.QualifyingBar.BarIndex"/> is &lt;= that still-open
    /// exit index (i.e. it falls within [entryBarIndex, entryBarIndex+horizon] of the still-open
    /// trade), otherwise it opens a new trade and becomes the new "most recently opened."
    /// Does NOT modify <see cref="Experiment5UnderlyingAnalyzer.Collect"/>/<c>CollectByState</c>
    /// themselves or their Experiment-3-style predictive-only callers -- this is purely a
    /// trade-BUILDING-step filter, applied by each Experiment 5/6 CLI command right before calling
    /// <see cref="BuildTradeAsync"/> in a loop.
    /// </summary>
    public static List<Experiment5UnderlyingAnalyzer.QualifyingBar> SelectNonOverlapping(
        IReadOnlyList<Experiment5UnderlyingAnalyzer.QualifyingBar> dayQualifyingBarsAscending, int horizon)
    {
        var result = new List<Experiment5UnderlyingAnalyzer.QualifyingBar>();
        var openUntilBarIndex = -1;
        foreach (var qb in dayQualifyingBarsAscending)
        {
            if (qb.BarIndex <= openUntilBarIndex)
            {
                continue;
            }

            result.Add(qb);
            openUntilBarIndex = qb.BarIndex + horizon;
        }

        return result;
    }

    /// <summary>
    /// Item 17's random-entry control baseline: for one day, picks the SAME NUMBER of entry bars as
    /// the real signal produced that day (matched in count per day, per the task's own instruction),
    /// via deterministic systematic (stride) sampling over every bar with a non-zero NetMove
    /// direction (bars with Direction==0 are skipped for the same reason the real signal excludes
    /// them -- neither trade side applies) -- NOT a fitted or cherry-picked selection, and
    /// reproducible without an RNG dependency. Each selected bar is then traded with the EXACT SAME
    /// direction rule as the real signal (Direction&gt;0 -&gt; Put, Direction&lt;0 -&gt; Call) and
    /// option-selection methodology, so the ONLY thing this baseline varies relative to the real
    /// signal is WHICH bars are entered, isolating "does the activity/efficiency state selection
    /// itself add anything over an unconditional entry."
    /// </summary>
    public static List<Experiment5UnderlyingAnalyzer.QualifyingBar> BuildRandomBaselineDay(
        IReadOnlyList<VolumeBarRow> dayBars, int targetCount, int dte, bool isZeroDte,
        IReadOnlyDictionary<int, OptionAtmBarRow> atmByBarIndex)
    {
        var eligible = new List<int>();
        for (var i = 0; i < dayBars.Count; i++)
        {
            var b = dayBars[i];
            var direction = b.ClosePrice > b.OpenPrice ? 1 : b.ClosePrice < b.OpenPrice ? -1 : 0;
            if (direction != 0)
            {
                eligible.Add(i);
            }
        }

        if (eligible.Count == 0 || targetCount <= 0)
        {
            return [];
        }

        var take = Math.Min(targetCount, eligible.Count);
        var stride = (double)eligible.Count / take;
        var result = new List<Experiment5UnderlyingAnalyzer.QualifyingBar>(take);
        var used = new HashSet<int>();
        for (var k = 0; k < take; k++)
        {
            var idx = (int)Math.Min(eligible.Count - 1, Math.Floor(k * stride));
            if (!used.Add(idx))
            {
                continue;
            }

            var i = eligible[idx];
            var b = dayBars[i];
            var direction = b.ClosePrice > b.OpenPrice ? 1 : -1;
            atmByBarIndex.TryGetValue(b.BarIndex, out var atm);
            result.Add(new Experiment5UnderlyingAnalyzer.QualifyingBar(
                b.AsOfDate, b.BarIndex, b.BarVolumeThreshold, b.EndTimestamp, b.ClosePrice, direction,
                Experiment5UnderlyingAnalyzer.SessionOf(b.EndTimestamp), dte, isZeroDte, atm?.AtmStrike,
                []));
        }

        return result;
    }
}
