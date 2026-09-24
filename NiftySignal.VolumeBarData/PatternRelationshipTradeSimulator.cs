using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Execution;
using NiftySignal.Persistence;
using NiftySignal.Rules;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, first trade simulation of the FROZEN Pattern A/B relationship (see
/// docs/VolumeCandle_0DTE_Findings.md's "Trade Simulation" section). The signal source is
/// <see cref="RelationshipObservation"/>, produced by the completely unmodified
/// <see cref="UnderlyingOptionRelationshipRecorder"/> -- this file adds NO new signal engine.
/// Pattern A fires -&gt; BUY PE (the contract/strike already pinned at the signal event by the
/// frozen dynamic-ATM methodology: <see cref="RelationshipObservation.PeToken"/>/<see cref="RelationshipObservation.AtmStrike"/>).
/// Pattern B fires -&gt; BUY CE, symmetrically. One open position at a time -- a signal while a
/// position is open is recorded, never silently dropped. Exit: the OPPOSITE pattern firing while
/// a position is open (confirmed with the user before implementation), or the mandatory 15:15
/// IST forced close, whichever comes first -- no stop loss, no take profit, no other time exit.
/// Reversal handling mirrors <see cref="Vc0DteTradeSimulator"/>'s own established convention:
/// closing on an opposite signal and opening the new side on the SAME event are recorded as two
/// separate trade rows, never one combined transaction.
/// </summary>
public static class PatternRelationshipTradeSimulator
{
    static readonly TimeOnly NoNewEntriesAfter = new(15, 0);
    static readonly TimeOnly ForceCloseAt = new(15, 15);
    public const int Lots = 10;

    public enum ExitReason { OppositePatternSignal, ForcedEod }
    public enum SignalOutcome { Executed, AlreadyInPosition, After3Pm, MissingOptionData, ContractTransition, NoExecutableTick, NoStrikeInBand }

    public sealed record TradeRow(
        int TradeId, DateOnly TradingDate, string Pattern, OptionType OptionType, decimal Strike, int Dte,
        DateTimeOffset SignalTimestamp, DateTimeOffset EntryTimestamp, DateTimeOffset ExitTimestamp,
        decimal UnderlyingPriceAtSignal, decimal UnderlyingPriceAtEntry, decimal UnderlyingPriceAtExit,
        decimal SignalOptionLtp, decimal EntryPrice, decimal ExitPrice,
        int LotSize, int Lots, int Quantity,
        decimal GrossPnl, decimal Stt, decimal Gst, decimal OtherCosts, decimal NetPnl,
        decimal MaeRupees, decimal MaePercent, decimal MfeRupees, decimal MfePercent, decimal? MfeCaptured,
        TimeSpan HoldingDuration, string ExitReason)
    {
        public decimal PnlPerLot => Lots > 0 ? NetPnl / Lots : 0m;
        public decimal PnlPerOptionPoint => Quantity > 0 ? NetPnl / Quantity : 0m;
        public decimal PnlPercentOfEntryPremium => EntryPrice > 0 ? NetPnl / (EntryPrice * Quantity) * 100m : 0m;
    }

    public sealed record SignalAuditRow(DateOnly TradingDate, DateTimeOffset SignalTimestamp, string Pattern, OptionType OptionType, decimal? Strike, SignalOutcome Outcome);

    public sealed record SimulationResult(List<TradeRow> Trades, List<SignalAuditRow> SignalAudit);

    /// <param name="optionBars">Required only when <paramref name="minEntryPrice"/>/<paramref name="maxEntryPrice"/> are supplied -- gives each candidate contract's own live premium at the signal event so a band-selected strike (rather than the dynamic-ATM one) can be found.</param>
    /// <param name="minEntryPrice">2026-09-24 addendum, user's own explicit rule (same convention as <see cref="Vc0DteTradeSimulator"/>'s existing [100,150] band): when supplied together with <paramref name="maxEntryPrice"/>, entry walks the chain outward from the dynamic-ATM strike and selects the FIRST contract whose live premium at the signal event falls in [<paramref name="minEntryPrice"/>, <paramref name="maxEntryPrice"/>], instead of the pinned dynamic-ATM contract. Null (default) preserves the original frozen-relationship pinned-ATM behaviour unchanged.</param>
    public static async Task<SimulationResult> SimulateDayAsync(
        NiftySignalDbContext source, DateOnly asOfDate,
        IReadOnlyList<RelationshipObservation> rows, IReadOnlyList<Instrument> chain, IReadOnlyList<FutureEventBar> futureBars,
        TimeSpan istOffset, CancellationToken cancellationToken,
        IReadOnlyList<SynchronizedOptionEventBar>? optionBars = null, decimal? minEntryPrice = null, decimal? maxEntryPrice = null)
    {
        var trades = new List<TradeRow>();
        var audit = new List<SignalAuditRow>();
        if (rows.Count == 0 || futureBars.Count == 0)
        {
            return new SimulationResult(trades, audit);
        }

        var useBandSelection = minEntryPrice is not null && maxEntryPrice is not null;
        var instrumentByToken = chain.ToDictionary(i => i.Token);
        var callChain = chain.Where(i => i.OptionType == OptionType.Call).ToList();
        var putChain = chain.Where(i => i.OptionType == OptionType.Put).ToList();
        var barsByToken = (optionBars ?? []).GroupBy(b => b.Token).ToDictionary(g => g.Key, g => g.ToDictionary(b => b.EventId));
        var tickSeriesCache = new Dictionary<string, OptionTickSeries>();
        var costs = new CostsConfig(BrokeragePerOrder: 0m, SlippageTicks: 0); // this experiment's own assumption: ₹0 brokerage, no added slippage constant.

        // Same convention Vc0DteTradeSimulator.TryEnterAsync already uses: nearest-to-ATM strike
        // whose OWN live premium (this event's Close) falls in the band, never look-ahead (only
        // this event's own bar is consulted).
        Instrument? SelectBandStrike(OptionType side, decimal atmStrike, int eventId)
        {
            var sideChain = side == OptionType.Call ? callChain : putChain;
            return sideChain
                .OrderBy(i => Math.Abs(i.StrikePrice!.Value - atmStrike))
                .FirstOrDefault(i => barsByToken.TryGetValue(i.Token, out var bars)
                    && bars.TryGetValue(eventId, out var bar) && bar.Close is { } close
                    && close >= minEntryPrice!.Value && close <= maxEntryPrice!.Value);
        }

        async Task<OptionTickSeries> GetSeriesAsync(string token)
        {
            if (!tickSeriesCache.TryGetValue(token, out var series))
            {
                var dayStart = futureBars[0].StartTimestamp;
                var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(new TimeOnly(15, 30)), istOffset).ToUniversalTime();
                series = await OptionTickSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
                tickSeriesCache[token] = series;
            }
            return series;
        }

        decimal FuturesPriceAt(DateTimeOffset timestamp)
        {
            var bar = futureBars.LastOrDefault(b => b.EndTimestamp <= timestamp);
            return bar?.Close ?? futureBars[0].Close;
        }

        (string Pattern, OptionType Side, Instrument Instrument, decimal Strike, int Dte,
            DateTimeOffset SignalTimestamp, decimal SignalLtp, decimal UnderlyingAtSignal,
            DateTimeOffset EntryTimestamp, decimal EntryFillPrice, decimal UnderlyingAtEntry)? open = null;
        var tradeId = 0;

        async Task CloseAsync(DateTimeOffset atTimestamp, ExitReason reason)
        {
            var position = open!.Value;
            var series = await GetSeriesAsync(position.Instrument.Token);
            var exitEntry = reason == ExitReason.ForcedEod ? series.EntryAtOrBefore(atTimestamp) : series.EntryAtOrAfter(atTimestamp);
            if (exitEntry is not { } exit)
            {
                // No valid executable tick to close on -- keep the position open rather than fabricate an exit.
                return;
            }

            var quantity = position.Instrument.LotSize * Lots;
            var exitBasePrice = exit.Depth is { } depth ? depth.Bid1Price : exit.LastPrice;
            var exitFill = PaperTradeSimulator.FillExit(exitBasePrice, position.Instrument.TickSize, quantity, costs);
            var exitFillPrice = exitFill.FillPrice;
            var grossPnl = (exitFillPrice - position.EntryFillPrice) * quantity;
            var costBreakdown = TransactionCostCalculator.Compute(exitFill.GrossValue, costs.BrokeragePerOrder * 2);
            var netPnl = grossPnl - costBreakdown.Total;

            var pathPrices = series.AllEntries
                .Where(e => e.Timestamp > position.EntryTimestamp && e.Timestamp <= exit.Timestamp)
                .Select(e => e.LastPrice)
                .ToList();
            var maeMfe = MaeMfeCalculator.Compute(position.EntryFillPrice, pathPrices);
            decimal? mfeCaptured = maeMfe.MfePoints > 0 ? grossPnl / (maeMfe.MfePoints * quantity) : null;

            trades.Add(new TradeRow(
                tradeId++, asOfDate, position.Pattern, position.Side, position.Strike, position.Dte,
                position.SignalTimestamp, position.EntryTimestamp, exit.Timestamp,
                position.UnderlyingAtSignal, position.UnderlyingAtEntry, FuturesPriceAt(exit.Timestamp),
                position.SignalLtp, position.EntryFillPrice, exitFillPrice,
                position.Instrument.LotSize, Lots, quantity,
                grossPnl, costBreakdown.Stt, costBreakdown.Gst, costBreakdown.Other, netPnl,
                maeMfe.MaePoints, maeMfe.MaePercent, maeMfe.MfePoints, maeMfe.MfePercent, mfeCaptured,
                exit.Timestamp - position.EntryTimestamp, reason.ToString()));

            open = null;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var isPatternA = row.RelationshipCategory == ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
            var isPatternB = row.RelationshipCategory == ForwardValidationAnalysis.State4_BearishDivergence_PatternB;

            if (open is { } pos)
            {
                var opposes = (pos.Pattern == "PatternA" && isPatternB) || (pos.Pattern == "PatternB" && isPatternA);
                if (opposes)
                {
                    await CloseAsync(row.EndTimestamp, ExitReason.OppositePatternSignal);
                }
            }

            if (!isPatternA && !isPatternB) { continue; }

            var pattern = isPatternA ? "PatternA" : "PatternB";
            var side = isPatternA ? OptionType.Put : OptionType.Call;
            var pinnedToken = isPatternA ? row.PeToken : row.CeToken;
            var pinnedMissing = isPatternA ? row.PeMissingData : row.CeMissingData;
            var pinnedTransition = isPatternA ? row.PeContractTransition : row.CeContractTransition;

            if (open is not null)
            {
                audit.Add(new SignalAuditRow(asOfDate, row.EndTimestamp, pattern, side, row.AtmStrike, SignalOutcome.AlreadyInPosition));
                continue;
            }

            var istTime = TimeOnly.FromDateTime(row.EndTimestamp.ToOffset(istOffset).DateTime);
            if (istTime >= NoNewEntriesAfter)
            {
                audit.Add(new SignalAuditRow(asOfDate, row.EndTimestamp, pattern, side, row.AtmStrike, SignalOutcome.After3Pm));
                continue;
            }
            if (pinnedTransition)
            {
                audit.Add(new SignalAuditRow(asOfDate, row.EndTimestamp, pattern, side, row.AtmStrike, SignalOutcome.ContractTransition));
                continue;
            }

            string? token; decimal? signalLtp; Instrument? instrument;
            if (useBandSelection)
            {
                var candidate = SelectBandStrike(side, row.AtmStrike, row.EventId);
                if (candidate is null)
                {
                    audit.Add(new SignalAuditRow(asOfDate, row.EndTimestamp, pattern, side, row.AtmStrike, SignalOutcome.NoStrikeInBand));
                    continue;
                }
                instrument = candidate;
                token = candidate.Token;
                signalLtp = barsByToken[candidate.Token][row.EventId].Close;
            }
            else
            {
                token = pinnedToken;
                signalLtp = isPatternA ? row.PeAverageLtp : row.CeAverageLtp;
                instrument = pinnedMissing || string.IsNullOrEmpty(token) ? null : instrumentByToken.GetValueOrDefault(token);
            }

            if (pinnedMissing && !useBandSelection || string.IsNullOrEmpty(token) || signalLtp is null || instrument is null)
            {
                audit.Add(new SignalAuditRow(asOfDate, row.EndTimestamp, pattern, side, row.AtmStrike, SignalOutcome.MissingOptionData));
                continue;
            }

            var series = await GetSeriesAsync(token);
            var entryEntry = series.EntryAtOrAfter(row.EndTimestamp);
            if (entryEntry is not { } entry)
            {
                audit.Add(new SignalAuditRow(asOfDate, row.EndTimestamp, pattern, side, row.AtmStrike, SignalOutcome.NoExecutableTick));
                continue;
            }

            var entryBasePrice = entry.Depth is { } entryDepth ? entryDepth.Ask1Price : entry.LastPrice;
            var entryFill = PaperTradeSimulator.FillEntry(entryBasePrice, instrument.TickSize, instrument.LotSize * Lots, costs);
            open = (pattern, side, instrument, instrument.StrikePrice!.Value, row.Dte,
                row.EndTimestamp, signalLtp.Value, row.FuturesClose,
                entry.Timestamp, entryFill.FillPrice, FuturesPriceAt(entry.Timestamp));
            audit.Add(new SignalAuditRow(asOfDate, row.EndTimestamp, pattern, side, row.AtmStrike, SignalOutcome.Executed));
        }

        if (open is not null)
        {
            var forceCloseAt = new DateTimeOffset(asOfDate.ToDateTime(ForceCloseAt), istOffset).ToUniversalTime();
            await CloseAsync(forceCloseAt, ExitReason.ForcedEod);
        }

        return new SimulationResult(trades, audit);
    }

    public static void WriteTradesCsv(IReadOnlyList<TradeRow> trades, string path, TimeSpan istOffset)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("TradeId,TradingDate,Pattern,OptionType,Strike,Dte,SignalTimeIST,EntryTimeIST,ExitTimeIST,"
            + "UnderlyingAtSignal,UnderlyingAtEntry,UnderlyingAtExit,SignalOptionLtp,EntryPrice,ExitPrice,"
            + "LotSize,Lots,Quantity,GrossPnl,Stt,Gst,OtherCosts,NetPnl,"
            + "MaeRupees,MaePercent,MfeRupees,MfePercent,MfeCaptured,HoldingSeconds,ExitReason,"
            + "PnlPerLot,PnlPerOptionPoint,PnlPercentOfEntryPremium");
        foreach (var t in trades)
        {
            writer.WriteLine(string.Join(',',
                t.TradeId, t.TradingDate.ToString("yyyy-MM-dd"), t.Pattern, t.OptionType, t.Strike, t.Dte,
                t.SignalTimestamp.ToOffset(istOffset).ToString("HH:mm:ss.fff"), t.EntryTimestamp.ToOffset(istOffset).ToString("HH:mm:ss.fff"), t.ExitTimestamp.ToOffset(istOffset).ToString("HH:mm:ss.fff"),
                t.UnderlyingPriceAtSignal, t.UnderlyingPriceAtEntry, t.UnderlyingPriceAtExit, t.SignalOptionLtp, t.EntryPrice, t.ExitPrice,
                t.LotSize, t.Lots, t.Quantity, t.GrossPnl, t.Stt, t.Gst, t.OtherCosts, t.NetPnl,
                t.MaeRupees, t.MaePercent, t.MfeRupees, t.MfePercent, t.MfeCaptured, t.HoldingDuration.TotalSeconds, t.ExitReason,
                t.PnlPerLot, t.PnlPerOptionPoint, t.PnlPercentOfEntryPremium));
        }
    }

    public static void WriteSignalAuditCsv(IReadOnlyList<SignalAuditRow> audit, string path, TimeSpan istOffset)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("TradingDate,SignalTimeIST,Pattern,OptionType,Strike,Outcome");
        foreach (var a in audit)
        {
            writer.WriteLine($"{a.TradingDate:yyyy-MM-dd},{a.SignalTimestamp.ToOffset(istOffset):HH:mm:ss.fff},{a.Pattern},{a.OptionType},{a.Strike},{a.Outcome}");
        }
    }
}
