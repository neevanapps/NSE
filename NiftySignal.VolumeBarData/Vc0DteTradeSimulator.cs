using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Execution;
using NiftySignal.Persistence;
using NiftySignal.Rules;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec (sections 24-39), plus the user's own three
/// explicit trading-simulation rules for this pass: only open once the signal engine's slow
/// window is fully warmed (already inherent to <see cref="PriceCrossoverEngine"/> -- it never
/// reports a crossing before that), select the entry strike from the first live premium in
/// [100, 150] walking outward from the dynamic ATM, no new entries after 15:00 IST, and force
/// every open position closed at 15:15 IST.
///
/// IMPORTANT (see the plan's own "important distinction" note): this class selects and holds
/// whichever contract's live premium falls in [100,150] at signal time -- a DIFFERENT contract
/// from the one <see cref="Vc0DteBehaviorRecorder"/> studies (which always follows the dynamic-ATM
/// contract itself). The two are separate, non-comparable views by design.
/// </summary>
public static class Vc0DteTradeSimulator
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly NoNewEntriesAfter = new(15, 0);
    static readonly TimeOnly ForceCloseAt = new(15, 15);

    public enum ExitReason { CrossoverReversed, ForcedEod }
    public enum RejectReason { NoStrikeInBand, NoExecutableTick, OutsideSession }
    public enum ExecutionModel { Depth, LtpFallback }

    public sealed record TradeRow(
        int TradeId, DateOnly TradingDate, int Dte,
        DateTimeOffset SignalTimestamp, DateTimeOffset EntryTimestamp, DateTimeOffset ExitTimestamp,
        int EventBarEntry, int EventBarExit, OptionType OptionType, decimal Strike,
        string EntryReason, string ExitReason,
        decimal EntryLtp, decimal? EntryBid, decimal? EntryAsk, decimal ActualEntryPrice, ExecutionModel EntryExecutionModel,
        decimal ExitLtp, decimal? ExitBid, decimal? ExitAsk, decimal ActualExitPrice, ExecutionModel ExitExecutionModel,
        int Quantity, decimal GrossPnl, decimal ExecutionCost, decimal NetPnl,
        int HoldingEventBars, TimeSpan HoldingDuration);

    public sealed record RejectedSignalRow(
        DateTimeOffset SignalTimestamp, decimal? Strike, OptionType OptionType, string Signal, RejectReason ReasonRejected);

    sealed record OpenPosition(
        OptionType Side, Instrument Instrument, int EntryEventId, DateTimeOffset SignalTimestamp,
        DateTimeOffset EntryTimestamp, decimal EntryLtp, decimal? EntryBid, decimal? EntryAsk,
        FillResult EntryFill, ExecutionModel EntryExecutionModel);

    /// <summary>
    /// 2026-09-23, forensic-verification addendum (user's own explicit "explain the 87 trades"
    /// request): a full accounting of every crossover signal observed during the day and what
    /// happened to it, so a high trade count can be told apart from a construction bug rather
    /// than assumed to be either. <see cref="SignalsWhileWarmingUp"/> is always 0 BY CONSTRUCTION
    /// -- <see cref="PriceCrossoverEngine"/> can never report <c>CrossedUp</c>/<c>CrossedDown</c>
    /// before its slow window is fully warmed (see <see cref="PriceCrossoverEngineTests"/>'s own
    /// "returns null MAs until slow window is full" coverage) -- included as an explicit,
    /// provable zero rather than omitted, so this table can't be misread as having skipped that
    /// check.
    /// </summary>
    public sealed record SignalFunnel(
        int TotalCrossoverSignals, int CeCrossUp, int CeCrossDown, int PeCrossUp, int PeCrossDown,
        int SignalsWhileWarmingUp, int EntrySignalsConsideredWhileFlat, int EntrySignalsIgnoredPositionAlreadyOpen,
        int RejectedNoStrikeInBand, int RejectedNoExecutableTick, int RejectedOutsideSession,
        int TradesOpened, int CeTrades, int PeTrades, int ReversalExits, int ForcedExits, double TradesPerHour);

    public sealed record SimulationResult(List<TradeRow> Trades, List<RejectedSignalRow> RejectedSignals, SignalFunnel Funnel);

    public static async Task<SimulationResult> SimulateDayAsync(
        NiftySignalDbContext source, DateOnly asOfDate,
        IReadOnlyList<Instrument> chain, IReadOnlyList<FutureEventBar> futureBars,
        IReadOnlyList<SynchronizedOptionEventBar> optionBars, CancellationToken cancellationToken,
        int fastBars = 3, int slowBars = 10, double thresholdFraction = 0.0,
        decimal minEntryPrice = 100m, decimal maxEntryPrice = 150m,
        CostsConfig? costs = null)
    {
        costs ??= new CostsConfig(BrokeragePerOrder: 20, SlippageTicks: 2);
        var trades = new List<TradeRow>();
        var rejected = new List<RejectedSignalRow>();
        int ceCrossUp = 0, ceCrossDown = 0, peCrossUp = 0, peCrossDown = 0, ignoredPositionOpen = 0;

        if (futureBars.Count == 0)
        {
            return new SimulationResult(trades, rejected, new SignalFunnel(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        }

        var barsByToken = optionBars.GroupBy(b => b.Token).ToDictionary(g => g.Key, g => g.OrderBy(b => b.EventId).ToList());
        var tickSeriesCache = new Dictionary<string, OptionTickSeries>();

        async Task<OptionTickSeries> GetSeriesAsync(string token)
        {
            if (!tickSeriesCache.TryGetValue(token, out var series))
            {
                var dayStart = futureBars[0].StartTimestamp;
                var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(new TimeOnly(15, 30)), IstOffset).ToUniversalTime();
                series = await OptionTickSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
                tickSeriesCache[token] = series;
            }
            return series;
        }

        var callEngine = new PriceCrossoverEngine(fastBars, slowBars);
        var putEngine = new PriceCrossoverEngine(fastBars, slowBars);
        string? lastCallAtmToken = null, lastPutAtmToken = null;
        var callChain = chain.Where(c => c.OptionType == OptionType.Call).ToList();
        var putChain = chain.Where(c => c.OptionType == OptionType.Put).ToList();

        OpenPosition? open = null;
        var tradeId = 0;

        for (var eventId = 0; eventId < futureBars.Count; eventId++)
        {
            var futureBar = futureBars[eventId];

            var callAtm = EventBarStrikeSelector.PickDynamicAtm(callChain, OptionType.Call, futureBar.Close);
            var putAtm = EventBarStrikeSelector.PickDynamicAtm(putChain, OptionType.Put, futureBar.Close);

            if (callAtm is not null)
            {
                if (lastCallAtmToken is not null && lastCallAtmToken != callAtm.Token)
                {
                    callEngine.Reset();
                }
                lastCallAtmToken = callAtm.Token;
            }
            if (putAtm is not null)
            {
                if (lastPutAtmToken is not null && lastPutAtmToken != putAtm.Token)
                {
                    putEngine.Reset();
                }
                lastPutAtmToken = putAtm.Token;
            }

            var callStep = callAtm is not null && barsByToken.TryGetValue(callAtm.Token, out var callBars)
                ? callEngine.Observe(callBars.FirstOrDefault(b => b.EventId == eventId)?.AverageLtp is { } clp ? (double)clp : null, thresholdFraction)
                : default;
            var putStep = putAtm is not null && barsByToken.TryGetValue(putAtm.Token, out var putBars)
                ? putEngine.Observe(putBars.FirstOrDefault(b => b.EventId == eventId)?.AverageLtp is { } plp ? (double)plp : null, thresholdFraction)
                : default;

            var istTime = TimeOnly.FromDateTime(futureBar.EndTimestamp.ToOffset(IstOffset).DateTime);

            if (callStep.CrossedUp) { ceCrossUp++; }
            if (callStep.CrossedDown) { ceCrossDown++; }
            if (putStep.CrossedUp) { peCrossUp++; }
            if (putStep.CrossedDown) { peCrossDown++; }

            // 1) Exit check for the currently open position -- its own side's opposite crossing.
            if (open is { } position)
            {
                var exitSignal = position.Side == OptionType.Call ? callStep.CrossedDown : putStep.CrossedDown;
                if (exitSignal)
                {
                    open = await CloseAsync(position, futureBar.EndTimestamp, ExitReason.CrossoverReversed, eventId);
                }

                // An entry-direction crossing on EITHER side while still holding a position is
                // never acted on (single-position invariant) -- counted here so it isn't silently
                // invisible in the funnel (it produces no rejected-signal row either, since it was
                // never a candidate entry attempt to begin with).
                if (open is not null && (callStep.CrossedUp || putStep.CrossedUp))
                {
                    ignoredPositionOpen++;
                }
            }

            // 2) Entry check -- only if flat. If both sides signal on the same bar, Call takes
            // priority -- a documented, arbitrary but deterministic tie-break (single-position
            // invariant means only one can be taken). A signal after the 15:00 IST cutoff is
            // recorded as a rejected signal (spec 39, "outside session"), not silently dropped.
            if (open is null)
            {
                if (callStep.CrossedUp && callAtm is not null)
                {
                    if (istTime < NoNewEntriesAfter)
                    {
                        await TryEnterAsync(OptionType.Call, callAtm, callChain, eventId, "CrossedUp");
                    }
                    else
                    {
                        rejected.Add(new RejectedSignalRow(futureBar.EndTimestamp, callAtm.StrikePrice, OptionType.Call, "CrossedUp", RejectReason.OutsideSession));
                    }
                }
                else if (putStep.CrossedUp && putAtm is not null)
                {
                    if (istTime < NoNewEntriesAfter)
                    {
                        await TryEnterAsync(OptionType.Put, putAtm, putChain, eventId, "CrossedUp");
                    }
                    else
                    {
                        rejected.Add(new RejectedSignalRow(futureBar.EndTimestamp, putAtm.StrikePrice, OptionType.Put, "CrossedUp", RejectReason.OutsideSession));
                    }
                }
            }
        }

        // Forced end-of-day close (spec section 33 / user's explicit 15:15 rule) -- never a
        // manufactured 15:30 print, uses the last real executable tick at/before 15:15.
        if (open is { } stillOpen)
        {
            var forceCloseAt = new DateTimeOffset(asOfDate.ToDateTime(ForceCloseAt), IstOffset).ToUniversalTime();
            await CloseAsync(stillOpen, forceCloseAt, ExitReason.ForcedEod, futureBars.Count - 1);
        }

        // Fixed 09:15-15:30 IST session length (6.25h) -- the same market-hours convention every
        // other command in this project uses, not derived from these specific bars' own span.
        const double sessionHours = 6.25;
        // Every rejected signal AND every opened trade both came from the "CrossedUp while flat"
        // branch (TryEnterAsync/the OutsideSession check are only ever reached from there) -- so
        // this sum is exactly the count of entry-direction crossings considered while flat.
        var entrySignalsWhileFlat = trades.Count + rejected.Count;
        var funnel = new SignalFunnel(
            ceCrossUp + ceCrossDown + peCrossUp + peCrossDown, ceCrossUp, ceCrossDown, peCrossUp, peCrossDown,
            SignalsWhileWarmingUp: 0, EntrySignalsConsideredWhileFlat: entrySignalsWhileFlat, EntrySignalsIgnoredPositionAlreadyOpen: ignoredPositionOpen,
            RejectedNoStrikeInBand: rejected.Count(r => r.ReasonRejected == RejectReason.NoStrikeInBand),
            RejectedNoExecutableTick: rejected.Count(r => r.ReasonRejected == RejectReason.NoExecutableTick),
            RejectedOutsideSession: rejected.Count(r => r.ReasonRejected == RejectReason.OutsideSession),
            TradesOpened: trades.Count, CeTrades: trades.Count(t => t.OptionType == OptionType.Call), PeTrades: trades.Count(t => t.OptionType == OptionType.Put),
            ReversalExits: trades.Count(t => t.ExitReason == nameof(ExitReason.CrossoverReversed)), ForcedExits: trades.Count(t => t.ExitReason == nameof(ExitReason.ForcedEod)),
            TradesPerHour: trades.Count / sessionHours);

        return new SimulationResult(trades, rejected, funnel);

        async Task TryEnterAsync(OptionType side, Instrument atm, List<Instrument> sideChain, int signalEventId, string signalName)
        {
            var signalBar = futureBars[signalEventId];
            var candidate = sideChain
                .OrderBy(i => Math.Abs(i.StrikePrice!.Value - atm.StrikePrice!.Value))
                .FirstOrDefault(i => barsByToken.TryGetValue(i.Token, out var bars)
                    && bars.FirstOrDefault(b => b.EventId == signalEventId) is { Close: { } close }
                    && close >= minEntryPrice && close <= maxEntryPrice);

            if (candidate is null)
            {
                rejected.Add(new RejectedSignalRow(signalBar.EndTimestamp, atm.StrikePrice, side, signalName, RejectReason.NoStrikeInBand));
                return;
            }

            var series = await GetSeriesAsync(candidate.Token);
            var fillEntry = series.EntryAtOrAfter(signalBar.EndTimestamp);
            if (fillEntry is not { } entryEntry)
            {
                rejected.Add(new RejectedSignalRow(signalBar.EndTimestamp, candidate.StrikePrice, side, signalName, RejectReason.NoExecutableTick));
                return;
            }

            var (entryFill, entryModel) = FillEntryFromEntry(entryEntry, candidate.TickSize, candidate.LotSize, costs);
            open = new OpenPosition(
                side, candidate, signalEventId, signalBar.EndTimestamp, entryEntry.Timestamp,
                entryEntry.LastPrice, entryEntry.Depth?.Bid1Price, entryEntry.Depth?.Ask1Price, entryFill, entryModel);
        }

        async Task<OpenPosition?> CloseAsync(OpenPosition position, DateTimeOffset signalOrCutoffTimestamp, ExitReason reason, int exitEventId)
        {
            var series = await GetSeriesAsync(position.Instrument.Token);
            var exitEntry = reason == ExitReason.ForcedEod
                ? series.EntryAtOrBefore(signalOrCutoffTimestamp)
                : series.EntryAtOrAfter(signalOrCutoffTimestamp);

            if (exitEntry is not { } exit)
            {
                // No valid executable tick to close on -- keep the position open rather than
                // fabricate an exit; it will be retried on the next bar/at the final EOD flush.
                return position;
            }

            var (exitFill, exitModel) = FillExitFromEntry(exit, position.Instrument.TickSize, position.Instrument.LotSize, costs);
            var netPnl = PaperTradeSimulator.ComputeNetPnl(position.EntryFill, exitFill);

            trades.Add(new TradeRow(
                tradeId++, asOfDate, position.Instrument.ExpiryDate!.Value.DayNumber - asOfDate.DayNumber,
                position.SignalTimestamp, position.EntryTimestamp, exit.Timestamp,
                position.EntryEventId, exitEventId, position.Side, position.Instrument.StrikePrice!.Value,
                "Crossover", reason.ToString(),
                position.EntryLtp, position.EntryBid, position.EntryAsk, position.EntryFill.FillPrice, position.EntryExecutionModel,
                exit.LastPrice, exit.Depth?.Bid1Price, exit.Depth?.Ask1Price, exitFill.FillPrice, exitModel,
                position.Instrument.LotSize, exitFill.GrossValue - position.EntryFill.GrossValue,
                position.EntryFill.Brokerage + exitFill.Brokerage, netPnl,
                exitEventId - position.EntryEventId, exit.Timestamp - position.EntryTimestamp));

            return null;
        }
    }

    static (FillResult, ExecutionModel) FillEntryFromEntry(OptionTickSeries.Entry entry, decimal tickSize, int quantity, CostsConfig costs)
    {
        if (entry.Depth is { } depth)
        {
            return (PaperTradeSimulator.FillEntry(depth.Ask1Price, tickSize, quantity, costs), ExecutionModel.Depth);
        }
        return (PaperTradeSimulator.FillEntry(entry.LastPrice, tickSize, quantity, costs), ExecutionModel.LtpFallback);
    }

    static (FillResult, ExecutionModel) FillExitFromEntry(OptionTickSeries.Entry entry, decimal tickSize, int quantity, CostsConfig costs)
    {
        if (entry.Depth is { } depth)
        {
            return (PaperTradeSimulator.FillExit(depth.Bid1Price, tickSize, quantity, costs), ExecutionModel.Depth);
        }
        return (PaperTradeSimulator.FillExit(entry.LastPrice, tickSize, quantity, costs), ExecutionModel.LtpFallback);
    }

    public static void WriteTradesCsv(IReadOnlyList<TradeRow> trades, string path, TimeSpan istOffset)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("TradeId,TradingDate,Dte,SignalTimeIST,EntryTimeIST,ExitTimeIST,EventBarEntry,EventBarExit,OptionType,Strike,EntryReason,ExitReason,EntryLtp,EntryBid,EntryAsk,ActualEntryPrice,EntryExecutionModel,ExitLtp,ExitBid,ExitAsk,ActualExitPrice,ExitExecutionModel,Quantity,GrossPnl,ExecutionCost,NetPnl,HoldingEventBars,HoldingDurationSeconds");
        foreach (var t in trades)
        {
            writer.WriteLine(string.Join(',',
                t.TradeId, t.TradingDate.ToString("yyyy-MM-dd"), t.Dte,
                t.SignalTimestamp.ToOffset(istOffset).ToString("HH:mm:ss.fff"),
                t.EntryTimestamp.ToOffset(istOffset).ToString("HH:mm:ss.fff"),
                t.ExitTimestamp.ToOffset(istOffset).ToString("HH:mm:ss.fff"),
                t.EventBarEntry, t.EventBarExit, t.OptionType, t.Strike, t.EntryReason, t.ExitReason,
                t.EntryLtp, t.EntryBid, t.EntryAsk, t.ActualEntryPrice, t.EntryExecutionModel,
                t.ExitLtp, t.ExitBid, t.ExitAsk, t.ActualExitPrice, t.ExitExecutionModel,
                t.Quantity, t.GrossPnl, t.ExecutionCost, t.NetPnl, t.HoldingEventBars, t.HoldingDuration.TotalSeconds));
        }
    }

    public static void WriteRejectedCsv(IReadOnlyList<RejectedSignalRow> rejected, string path, TimeSpan istOffset)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("SignalTimeIST,Strike,OptionType,Signal,ReasonRejected");
        foreach (var r in rejected)
        {
            writer.WriteLine($"{r.SignalTimestamp.ToOffset(istOffset):HH:mm:ss.fff},{r.Strike},{r.OptionType},{r.Signal},{r.ReasonRejected}");
        }
    }
}
