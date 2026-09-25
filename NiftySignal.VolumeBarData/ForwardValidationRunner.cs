using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Execution;
using NiftySignal.Persistence;
using NiftySignal.Rules;
using NiftySignal.Scoring;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-25. Deterministic forward-validation runner for the FROZEN strategy version
/// 13K_180S_FULLSURFACE_V1. Kept as its own class (not inlined into Program.cs's top-level Main)
/// because Program.cs's single giant top-level statement method had already grown large enough
/// that adding this block inline triggered a genuine compiler/runtime code-generation failure
/// (InvalidProgramException at startup, affecting every command in the file, not just this one) --
/// confirmed by reverting the inline block and seeing the whole program work again. Extracting to
/// a dedicated static class avoids growing that single method further, matching this project's
/// existing convention of extracting pure/standalone logic into its own file.
/// </summary>
public static class ForwardValidationRunner
{
    public static async Task<int> RunAsync(DbContextOptions<NiftySignalDbContext> tradeSourceOptions, string fvBase)
    {
        const string strategyVersion = "13K_180S_FULLSURFACE_V1";
        const int targetSessionCount = 10;
        var fvIstOffset = TimeSpan.FromHours(5.5);
        const long fvThreshold = 13000L;
        const double fvTargetSeconds = 180.0;
        var researchDates = new HashSet<DateOnly>
        {
            new(2026, 9, 4), new(2026, 9, 8), new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11),
            new(2026, 9, 15), new(2026, 9, 16), new(2026, 9, 17), new(2026, 9, 18), new(2026, 9, 21),
            new(2026, 9, 22), new(2026, 9, 23), new(2026, 9, 24),
        };

        static bool FvFullSurface(string atmState, int ceValid, int cePos, int ceNeg, int peValid, int pePos, int peNeg) =>
            atmState switch { "A" => ceValid == 5 && ceNeg == 5 && peValid == 5 && pePos == 5, "B" => ceValid == 5 && cePos == 5 && peValid == 5 && peNeg == 5, _ => false };

        const int fvLots = PatternRelationshipTradeSimulator.Lots;
        var fvCosts = new CostsConfig(BrokeragePerOrder: 0m, SlippageTicks: 0);

        // ---- Strategy version freeze: reuse the existing pre-OOS freeze content, add the version tag. ----
        var freezeSourcePath = "vc-13k-final-pre-oos-freeze.json";
        var versionFreezePath = $"{fvBase}-{strategyVersion}-freeze.json";
        if (!File.Exists(versionFreezePath))
        {
            if (File.Exists(freezeSourcePath))
            {
                var sourceJson = File.ReadAllText(freezeSourcePath);
                var versioned = sourceJson.TrimEnd().TrimEnd('}').TrimEnd().TrimEnd(',') + $",\n  \"StrategyVersion\": \"{strategyVersion}\"\n}}\n";
                File.WriteAllText(versionFreezePath, versioned);
                Console.WriteLine($"Strategy version freeze written: {Path.GetFullPath(versionFreezePath)} (content reused verbatim from {freezeSourcePath}, StrategyVersion tag added -- confirmed identical, not re-derived).");
            }
            else
            {
                Console.WriteLine($"WARNING: {freezeSourcePath} not found -- writing a fresh freeze record instead of reusing (rules are unchanged from that study, only the source file could not be located for a content-equality confirmation).");
                File.WriteAllText(versionFreezePath, $"{{\n  \"StrategyVersion\": \"{strategyVersion}\",\n  \"BaseVolume\": \"13000\",\n  \"AdaptiveContextSeconds\": \"180\",\n  \"ExitRule\": \"OriginalOppositePatternExit\"\n}}\n");
            }
        }
        else { Console.WriteLine($"Strategy version freeze already exists: {Path.GetFullPath(versionFreezePath)} -- not rewritten (frozen, immutable)."); }
        Console.WriteLine();

        // ---- Discover already-processed forward sessions (idempotency: never reprocess a date already in the signals CSV). ----
        var signalsPath = $"{fvBase}-signals.csv"; var tradesPath = $"{fvBase}-trades.csv";
        var alreadyProcessed = new HashSet<DateOnly>();
        if (File.Exists(signalsPath))
        {
            foreach (var line in File.ReadLines(signalsPath).Skip(1))
            {
                var firstComma = line.IndexOf(',');
                if (firstComma > 0 && DateOnly.TryParseExact(line[..firstComma], "yyyy-MM-dd", out var d)) { alreadyProcessed.Add(d); }
            }
        }

        await using var discoverySrc = new NiftySignalDbContext(tradeSourceOptions);
        var availableDates = await discoverySrc.Instruments.Where(i => i.InstrumentType == InstrumentType.Future && i.Underlying == "NIFTY" && i.AsOfDate > new DateOnly(2026, 9, 24))
            .Select(i => i.AsOfDate).Distinct().OrderBy(d => d).ToListAsync();
        var newSessions = availableDates.Where(d => !researchDates.Contains(d) && !alreadyProcessed.Contains(d)).ToList();

        Console.WriteLine($"Strategy version: {strategyVersion}. Already-processed forward sessions: {alreadyProcessed.Count}. Newly available, unprocessed sessions: {newSessions.Count} ({string.Join(", ", newSessions.Select(d => d.ToString("yyyy-MM-dd")))}).");
        if (newSessions.Count == 0) { Console.WriteLine("No new sessions to process this run."); return 0; }
        Console.WriteLine();

        // ---- Process each new session with the EXACT frozen rule set (identical code to the pre-OOS freeze command). ----
        var newSignalRows = new List<string>();
        var newTradeRows = new List<string>();
        var dailyReports = new List<string>();

        foreach (var date in newSessions)
        {
            await using var src = new NiftySignalDbContext(tradeSourceOptions);
            var bars = await FutureEventBarBuilder.BuildDayAsync(src, date, fvThreshold, CancellationToken.None);
            if (bars.Count == 0) { Console.WriteLine($"  {date:yyyy-MM-dd}: no futures bars -- skipped."); continue; }
            var expiries = await src.Instruments.Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.Underlying == "NIFTY").Select(i => i.ExpiryDate).Distinct().OrderBy(e => e).ToListAsync();
            if (expiries.Count == 0) { Console.WriteLine($"  {date:yyyy-MM-dd}: no option chain -- skipped."); continue; }
            var expiry = expiries[0]!.Value; var dte = expiry.DayNumber - date.DayNumber;
            var chain = await src.Instruments.Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate == expiry && i.Underlying == "NIFTY").ToListAsync();
            var chainByStrikeAndSide = chain.Where(i => i.StrikePrice is not null).ToLookup(i => (i.StrikePrice!.Value, i.OptionType));
            var distinctStrikes = chain.Where(i => i.StrikePrice is not null).Select(i => i.StrikePrice!.Value).Distinct().OrderBy(x => x).ToList();
            var tickCache = new Dictionary<string, OptionTickSeries>();
            async Task<OptionTickSeries> SeriesAsync(string token)
            {
                if (!tickCache.TryGetValue(token, out var s))
                {
                    var dayStart = bars[0].StartTimestamp;
                    var dayEnd = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 30)), fvIstOffset).ToUniversalTime();
                    s = await OptionTickSeries.LoadAsync(src, token, dayStart, dayEnd, CancellationToken.None);
                    tickCache[token] = s;
                }
                return s;
            }

            var rows = new List<(int EndIdx, int WinBars, double WDur, long RealizedVolume, decimal AtmStrike, string AtmState, int CeValid, int PeValid, int CePos, int CeNeg, int PePos, int PeNeg, decimal FStart, decimal FEnd, DateTimeOffset WEnd)>();
            for (var i = 0; i < bars.Count; i++)
            {
                var startIdx = AdaptiveWindowAnalysis.FindWindowStartIndex(bars, i, fvTargetSeconds);
                var startBar = bars[startIdx]; var endBar = bars[i];
                var fStart = startBar.Open; var fEnd = endBar.Close;
                var fChangePts = fEnd - fStart;
                var wDur = (endBar.EndTimestamp - startBar.StartTimestamp).TotalSeconds;
                var realizedVolume = bars.Skip(startIdx).Take(i - startIdx + 1).Sum(b => b.Volume);
                var insufficientHistory = startIdx == 0 && wDur < fvTargetSeconds;

                var atmCe = AtmStrikeSelector.PickAtm(chain, OptionType.Call, fEnd);
                var atmPe = AtmStrikeSelector.PickAtm(chain, OptionType.Put, fEnd);
                var atmStrikeVal = atmCe?.StrikePrice ?? atmPe?.StrikePrice ?? fEnd;
                decimal? atmCeStart = null, atmCeEnd = null, atmPeStart = null, atmPeEnd = null;
                var atmMissing = atmCe is null || atmPe is null;
                if (atmCe is not null) { var s = await SeriesAsync(atmCe.Token); atmCeStart = s.EntryAtOrBefore(startBar.StartTimestamp)?.LastPrice; atmCeEnd = s.EntryAtOrBefore(endBar.EndTimestamp)?.LastPrice; atmMissing |= atmCeStart is null || atmCeEnd is null; }
                if (atmPe is not null) { var s = await SeriesAsync(atmPe.Token); atmPeStart = s.EntryAtOrBefore(startBar.StartTimestamp)?.LastPrice; atmPeEnd = s.EntryAtOrBefore(endBar.EndTimestamp)?.LastPrice; atmMissing |= atmPeStart is null || atmPeEnd is null; }
                decimal? atmCeChg = atmCeStart is not null && atmCeEnd is not null ? atmCeEnd - atmCeStart : null;
                decimal? atmPeChg = atmPeStart is not null && atmPeEnd is not null ? atmPeEnd - atmPeStart : null;
                var rawState = "Other";
                if (!atmMissing) { if (fChangePts > 0 && atmCeChg < 0 && atmPeChg > 0) { rawState = "A"; } else if (fChangePts < 0 && atmCeChg > 0 && atmPeChg < 0) { rawState = "B"; } }

                var atmIdx = distinctStrikes.FindIndex(x => x == atmStrikeVal);
                int ceValid = 0, ceNeg = 0, cePos = 0, peValid = 0, peNeg = 0, pePos = 0;
                for (var b = -2; b <= 2; b++)
                {
                    var idx = atmIdx + b;
                    if (atmIdx < 0 || idx < 0 || idx >= distinctStrikes.Count) { continue; }
                    var strike = distinctStrikes[idx];
                    var ceInst = chain.FirstOrDefault(x => x.StrikePrice == strike && x.OptionType == OptionType.Call);
                    var peInst = chain.FirstOrDefault(x => x.StrikePrice == strike && x.OptionType == OptionType.Put);
                    if (ceInst is not null) { var s = await SeriesAsync(ceInst.Token); var st = s.EntryAtOrBefore(startBar.StartTimestamp)?.LastPrice; var en = s.EntryAtOrBefore(endBar.EndTimestamp)?.LastPrice; if (st is not null && en is not null && st != 0) { var ret = (en - st) / st * 100m; ceValid++; if (ret > 0) { cePos++; } else if (ret < 0) { ceNeg++; } } }
                    if (peInst is not null) { var s = await SeriesAsync(peInst.Token); var st = s.EntryAtOrBefore(startBar.StartTimestamp)?.LastPrice; var en = s.EntryAtOrBefore(endBar.EndTimestamp)?.LastPrice; if (st is not null && en is not null && st != 0) { var ret = (en - st) / st * 100m; peValid++; if (ret > 0) { pePos++; } else if (ret < 0) { peNeg++; } } }
                }
                rows.Add((i, i - startIdx + 1, wDur, realizedVolume, atmStrikeVal, insufficientHistory ? "Other" : rawState, ceValid, peValid, cePos, ceNeg, pePos, peNeg, fStart, fEnd, endBar.EndTimestamp.ToOffset(fvIstOffset)));
            }

            var ann = RollingStateAnalysis.Annotate(rows.Select(r => r.AtmState).ToList());
            var fsFlags = rows.Select(r => FvFullSurface(r.AtmState, r.CeValid, r.CePos, r.CeNeg, r.PeValid, r.PePos, r.PeNeg)).ToList();
            decimal? FwdPts(int endIdx, int h) { var t = rows.FirstOrDefault(x => x.EndIdx == endIdx + h); if (t.WEnd == default) { return null; } var b0 = rows.First(x => x.EndIdx == endIdx); return t.FEnd - b0.FEnd; }
            decimal FuturesPriceAt(DateTimeOffset ts) { var b = bars.LastOrDefault(x => x.EndTimestamp <= ts); return b?.Close ?? bars[0].Close; }

            var episodeCounter = 0;
            var dayTrades = new List<(string Pattern, DateTimeOffset SignalTs, DateTimeOffset EntryTs, DateTimeOffset ExitTs, string ExitReason, OptionType OptionType, decimal Strike, string Token,
                decimal EntryPremium, decimal ExitPremium, int Quantity, decimal GrossPnl, decimal Costs, decimal NetPnl, double HoldingSeconds, decimal FuturesEntry, decimal FuturesExit,
                decimal MaeRupees, decimal MfeRupees, double? SecToMfe, decimal? Mtm1, decimal? Mtm2, decimal? Mtm4)>();

            (string Pattern, Instrument Instrument, DateTimeOffset SignalTs, DateTimeOffset EntryTs, decimal EntryFill, decimal FuturesAtEntry, int EndIdx)? open = null;
            var executedSignalEndIdx = new HashSet<int>();
            for (var k = 0; k < rows.Count; k++)
            {
                var r = rows[k];
                var isEntryRow = ann[k].IsStateEntry && r.AtmState is "A" or "B";
                if (open is { } pos && isEntryRow && ((pos.Pattern == "A" && r.AtmState == "B") || (pos.Pattern == "B" && r.AtmState == "A")))
                {
                    var series = await SeriesAsync(pos.Instrument.Token);
                    var exitTickN = series.EntryAtOrAfter(r.WEnd);
                    if (exitTickN is not null)
                    {
                        var exitTick = exitTickN.Value;
                        var qty = pos.Instrument.LotSize * fvLots;
                        var exitBase = exitTick.Depth is { } xd ? xd.Bid1Price : exitTick.LastPrice;
                        var exitFill = PaperTradeSimulator.FillExit(exitBase, pos.Instrument.TickSize, qty, fvCosts);
                        var grossPnl = (exitFill.FillPrice - pos.EntryFill) * qty;
                        var costBreakdown = TransactionCostCalculator.Compute(exitFill.GrossValue, fvCosts.BrokeragePerOrder * 2);
                        var netPnl = grossPnl - costBreakdown.Total;
                        var path = series.AllEntries.Where(e => e.Timestamp > pos.EntryTs && e.Timestamp <= exitTick.Timestamp).OrderBy(e => e.Timestamp).ToList();
                        var maeMfe = MaeMfeCalculator.Compute(pos.EntryFill, path.Select(e => e.LastPrice).ToList());
                        double? secToMfe = null;
                        if (maeMfe.MfePoints > 0) { var mfeRow = path.FirstOrDefault(e => e.LastPrice - pos.EntryFill >= maeMfe.MfePoints); if (mfeRow.Timestamp != default) { secToMfe = (mfeRow.Timestamp - pos.EntryTs).TotalSeconds; } }
                        decimal? MtmAt(int h) { var t = rows.FirstOrDefault(x => x.EndIdx == pos.EndIdx + h); if (t.WEnd == default) { return null; } var tk = series.EntryAtOrBefore(t.WEnd); return tk is { } tv && pos.EntryFill != 0 ? (tv.LastPrice - pos.EntryFill) / pos.EntryFill * 100m : null; }
                        dayTrades.Add((pos.Pattern, pos.SignalTs, pos.EntryTs, exitTick.Timestamp, "OppositePatternSignal", pos.Instrument.OptionType, pos.Instrument.StrikePrice!.Value, pos.Instrument.Token,
                            pos.EntryFill, exitFill.FillPrice, qty, grossPnl, costBreakdown.Total, netPnl, (exitTick.Timestamp - pos.EntryTs).TotalSeconds, pos.FuturesAtEntry, FuturesPriceAt(exitTick.Timestamp),
                            maeMfe.MaePoints, maeMfe.MfePoints, secToMfe, MtmAt(1), MtmAt(2), MtmAt(4)));
                    }
                    open = null;
                }
                if (isEntryRow && r.AtmState != (open?.Pattern ?? "")) { episodeCounter++; }
                if (!isEntryRow || !fsFlags[k]) { continue; }
                if (open is not null) { continue; }
                var istTime = TimeOnly.FromDateTime(r.WEnd.DateTime);
                if (istTime >= new TimeOnly(15, 0)) { continue; }
                var side = r.AtmState == "A" ? OptionType.Put : OptionType.Call;
                var inst = chainByStrikeAndSide[(r.AtmStrike, side)].FirstOrDefault();
                if (inst is null) { continue; }
                var series2 = await SeriesAsync(inst.Token);
                var entryTickN2 = series2.EntryAtOrAfter(r.WEnd);
                if (entryTickN2 is null) { continue; }
                var entryTick2 = entryTickN2.Value;
                var entryBase2 = entryTick2.Depth is { } ed2 ? ed2.Ask1Price : entryTick2.LastPrice;
                var entryFill2 = PaperTradeSimulator.FillEntry(entryBase2, inst.TickSize, inst.LotSize * fvLots, fvCosts);
                open = (r.AtmState, inst, r.WEnd, entryTick2.Timestamp, entryFill2.FillPrice, FuturesPriceAt(entryTick2.Timestamp), r.EndIdx);
                executedSignalEndIdx.Add(r.EndIdx);
            }
            if (open is { } fo)
            {
                var forceClose = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 15)), fvIstOffset);
                var series = await SeriesAsync(fo.Instrument.Token);
                var exitTickN = series.EntryAtOrBefore(forceClose);
                if (exitTickN is not null)
                {
                    var exitTick = exitTickN.Value;
                    var qty = fo.Instrument.LotSize * fvLots;
                    var exitBase = exitTick.Depth is { } xd ? xd.Bid1Price : exitTick.LastPrice;
                    var exitFill = PaperTradeSimulator.FillExit(exitBase, fo.Instrument.TickSize, qty, fvCosts);
                    var grossPnl = (exitFill.FillPrice - fo.EntryFill) * qty;
                    var costBreakdown = TransactionCostCalculator.Compute(exitFill.GrossValue, fvCosts.BrokeragePerOrder * 2);
                    var netPnl = grossPnl - costBreakdown.Total;
                    var path = series.AllEntries.Where(e => e.Timestamp > fo.EntryTs && e.Timestamp <= exitTick.Timestamp).OrderBy(e => e.Timestamp).ToList();
                    var maeMfe = MaeMfeCalculator.Compute(fo.EntryFill, path.Select(e => e.LastPrice).ToList());
                    double? secToMfe = null;
                    if (maeMfe.MfePoints > 0) { var mfeRow = path.FirstOrDefault(e => e.LastPrice - fo.EntryFill >= maeMfe.MfePoints); if (mfeRow.Timestamp != default) { secToMfe = (mfeRow.Timestamp - fo.EntryTs).TotalSeconds; } }
                    decimal? MtmAt(int h) { var t = rows.FirstOrDefault(x => x.EndIdx == fo.EndIdx + h); if (t.WEnd == default) { return null; } var tk = series.EntryAtOrBefore(t.WEnd); return tk is { } tv && fo.EntryFill != 0 ? (tv.LastPrice - fo.EntryFill) / fo.EntryFill * 100m : null; }
                    dayTrades.Add((fo.Pattern, fo.SignalTs, fo.EntryTs, exitTick.Timestamp, "ForcedEod", fo.Instrument.OptionType, fo.Instrument.StrikePrice!.Value, fo.Instrument.Token,
                        fo.EntryFill, exitFill.FillPrice, qty, grossPnl, costBreakdown.Total, netPnl, (exitTick.Timestamp - fo.EntryTs).TotalSeconds, fo.FuturesAtEntry, FuturesPriceAt(exitTick.Timestamp),
                        maeMfe.MaePoints, maeMfe.MfePoints, secToMfe, MtmAt(1), MtmAt(2), MtmAt(4)));
                }
            }

            // ---- Same-session matched control (for signal-row reporting). ----
            var absMoves = new List<decimal>(); for (var k = 1; k < rows.Count; k++) { absMoves.Add(Math.Abs(rows[k].FEnd - rows[k - 1].FEnd)); }
            var (ctrlLow, ctrlHigh) = absMoves.Count > 0 ? ForwardValidationAnalysis.ComputeTerciles(absMoves) : (0m, 0m);
            var dirStates = new List<string>();
            for (var k = 0; k < rows.Count; k++) { dirStates.Add(k == 0 || rows[k].AtmState is "A" or "B" ? "Other" : (rows[k].FEnd > rows[k - 1].FEnd ? "A" : rows[k].FEnd < rows[k - 1].FEnd ? "B" : "Other")); }
            var dirAnn = RollingStateAnalysis.Annotate(dirStates);
            var aCtrl4 = new List<decimal>(); var bCtrl4 = new List<decimal>();
            for (var k = 1; k < rows.Count; k++)
            {
                if (!dirAnn[k].IsStateEntry) { continue; }
                var move = Math.Abs(rows[k].FEnd - rows[k - 1].FEnd);
                if (ConditionalMovementAnalysis.ClassifyTercileBucket(move, ctrlLow, ctrlHigh) != "Low") { continue; }
                var f = FwdPts(rows[k].EndIdx, 4);
                if (f is null) { continue; }
                if (dirStates[k] == "A") { aCtrl4.Add(-f.Value); } else { bCtrl4.Add(f.Value); }
            }
            static double Hr(List<decimal> v) => v.Count > 0 ? 100.0 * v.Count(x => x > 0) / v.Count : 0;

            // ---- Signal rows. ----
            var dayASignals = 0; var dayBSignals = 0;
            for (var k = 0; k < rows.Count; k++)
            {
                if (!ann[k].IsStateEntry || rows[k].AtmState is not ("A" or "B") || !fsFlags[k]) { continue; }
                var r = rows[k];
                if (r.AtmState == "A") { dayASignals++; } else { dayBSignals++; }
                var expectSign = r.AtmState == "A" ? -1m : 1m;
                var f1 = FwdPts(r.EndIdx, 1); var f2 = FwdPts(r.EndIdx, 2); var f4 = FwdPts(r.EndIdx, 4);
                var executed = executedSignalEndIdx.Contains(r.EndIdx);
                var reason = executed ? "" : (TimeOnly.FromDateTime(r.WEnd.DateTime) >= new TimeOnly(15, 0) ? "AfterCutoff" : "PositionAlreadyOpen");
                newSignalRows.Add(string.Join(',', date.ToString("yyyy-MM-dd"), expiry.ToString("yyyy-MM-dd"), dte, r.AtmState, r.WEnd.ToString("HH:mm:ss.fff"), episodeCounter,
                    r.WinBars, r.WDur, r.RealizedVolume, r.FEnd - r.FStart, r.FStart != 0 ? ((r.FEnd - r.FStart) / r.FStart * 100m).ToString() : "", r.AtmStrike, true,
                    f1, f2, f4, f1 is not null ? (Math.Sign(f1.Value) == Math.Sign(expectSign)).ToString() : "", f2 is not null ? (Math.Sign(f2.Value) == Math.Sign(expectSign)).ToString() : "", f4 is not null ? (Math.Sign(f4.Value) == Math.Sign(expectSign)).ToString() : "",
                    executed, reason));
            }

            foreach (var t in dayTrades)
            {
                newTradeRows.Add(string.Join(',', date.ToString("yyyy-MM-dd"), dte, t.Pattern, t.SignalTs.ToOffset(fvIstOffset).ToString("HH:mm:ss.fff"), t.EntryTs.ToOffset(fvIstOffset).ToString("HH:mm:ss.fff"),
                    t.ExitTs.ToOffset(fvIstOffset).ToString("HH:mm:ss.fff"), t.ExitReason, t.OptionType, t.Strike, t.Token, t.EntryPremium, t.ExitPremium, t.Quantity, t.GrossPnl, t.Costs, t.NetPnl,
                    t.HoldingSeconds, t.FuturesEntry, t.FuturesExit, t.FuturesExit - t.FuturesEntry, t.EntryPremium != 0 ? ((t.ExitPremium - t.EntryPremium) / t.EntryPremium * 100m).ToString() : "",
                    t.MaeRupees, t.MfeRupees, t.SecToMfe, t.Mtm1, t.Mtm2, t.Mtm4));
            }

            // ---- Daily report + failure attribution. ----
            var aTrades = dayTrades.Where(x => x.Pattern == "A").ToList(); var bTrades = dayTrades.Where(x => x.Pattern == "B").ToList();
            var wins = dayTrades.Count(x => x.NetPnl > 0); var gp = dayTrades.Where(x => x.NetPnl > 0).Sum(x => x.NetPnl); var gl = Math.Abs(dayTrades.Where(x => x.NetPnl <= 0).Sum(x => x.NetPnl));
            var losers = dayTrades.Where(x => x.NetPnl <= 0).ToList();
            int sigWrong = 0, correctOptLost = 0, giveback = 0, initFavReversed = 0, ambiguous = 0;
            foreach (var t in losers)
            {
                var expectSign = t.Pattern == "A" ? -1 : 1;
                var underlyingCorrect = Math.Sign(t.FuturesExit - t.FuturesEntry) == expectSign;
                var gb = t.MfeRupees > 0 ? (t.MfeRupees - (t.ExitPremium - t.EntryPremium)) / t.MfeRupees * 100m : (decimal?)null;
                if (!underlyingCorrect) { sigWrong++; }
                else if (t.MfeRupees <= 0) { correctOptLost++; }
                else if (gb is { } g && g >= 90m) { giveback++; }
                else if (underlyingCorrect && t.MfeRupees > 0) { initFavReversed++; }
                else { ambiguous++; }
            }
            var report = new System.Text.StringBuilder();
            report.AppendLine($"=== FORWARD DAY REPORT: {date:yyyy-MM-dd} (Strategy {strategyVersion}) ===");
            report.AppendLine($"  FullSurface signals: A={dayASignals} B={dayBSignals}. Executed: A={aTrades.Count} B={bTrades.Count}.");
            report.AppendLine($"  WinRate={(dayTrades.Count > 0 ? 100.0 * wins / dayTrades.Count : 0):F1}% PF={(gl > 0 ? (gp / gl).ToString("F2") : "n/a")} Gross={dayTrades.Sum(x => x.GrossPnl):F0} Costs={dayTrades.Sum(x => x.Costs):F0} NetPnl={dayTrades.Sum(x => x.NetPnl):F0} PnlPerTrade={(dayTrades.Count > 0 ? dayTrades.Average(x => x.NetPnl) : 0):F1}");
            if (dayTrades.Count > 0) { report.AppendLine($"  MedianHoldSec={dayTrades.Select(x => x.HoldingSeconds).OrderBy(x => x).ElementAt(dayTrades.Count / 2):F0} MedianMAE={dayTrades.Select(x => x.MaeRupees).OrderBy(x => x).ElementAt(dayTrades.Count / 2):F1} MedianMFE={dayTrades.Select(x => x.MfeRupees).OrderBy(x => x).ElementAt(dayTrades.Count / 2):F1}"); }
            foreach (var pattern in new[] { "A", "B" })
            {
                var expectSign = pattern == "A" ? -1m : 1m;
                var pop = rows.Select((r, idx) => (r, idx)).Where(x => ann[x.idx].IsStateEntry && x.r.AtmState == pattern && fsFlags[x.idx]).ToList();
                foreach (var (label, h) in new[] { ("+1", 1), ("+2", 2), ("+4", 4) })
                {
                    var v = pop.Select(x => FwdPts(x.r.EndIdx, h)).Where(x => x is not null).Select(x => x!.Value * expectSign).ToList();
                    if (v.Count == 0) { continue; }
                    report.AppendLine($"  [{pattern} {label}] hitRate={100.0 * v.Count(x => x > 0) / v.Count:F1}% (n={v.Count})");
                }
                var ctrl = pattern == "A" ? aCtrl4 : bCtrl4;
                if (ctrl.Count >= 5) { report.AppendLine($"  [{pattern} same-session control, +4] hit%={Hr(ctrl):F1}% (n={ctrl.Count})"); }
                else { report.AppendLine($"  [{pattern} same-session control]: sample too small (n={ctrl.Count})."); }
            }
            report.AppendLine($"  Failure attribution (losers n={losers.Count}): UnderlyingSignalWrong={sigWrong} UnderlyingCorrectOptionLost={correctOptLost} ExitLifecycleGiveback={giveback} InitiallyFavorableThenReversed={initFavReversed} Ambiguous={ambiguous}");
            dailyReports.Add(report.ToString());
            Console.Write(report.ToString());
            Console.WriteLine();
        }

        // ---- Append (never overwrite) the accumulated signals/trades CSVs. ----
        var signalsHeader = "TradingDate,ExpiryDate,Dte,Pattern,SignalTimeIST,EpisodeId,WindowBarCount,WindowDurationSeconds,ActualWindowVolume,FuturesContextMovePoints,FuturesContextReturnPct,AtmStrike,FullSurfaceAgreement,Forward1Pts,Forward2Pts,Forward4Pts,ExpectedDirectionCorrect1,ExpectedDirectionCorrect2,ExpectedDirectionCorrect4,Executed,NotExecutedReason";
        var tradesHeader = "TradingDate,Dte,Pattern,SignalTimeIST,EntryTimeIST,ExitTimeIST,ExitReason,OptionType,Strike,ContractToken,EntryPremium,ExitPremium,Quantity,GrossPnl,Costs,NetPnl,HoldingSeconds,UnderlyingEntry,UnderlyingExit,UnderlyingMovePoints,OptionReturnPct,MAE,MFE,SecondsToMfe,OptionMtm1,OptionMtm2,OptionMtm4";
        if (!File.Exists(signalsPath)) { File.WriteAllText(signalsPath, signalsHeader + "\n"); }
        File.AppendAllLines(signalsPath, newSignalRows);
        if (!File.Exists(tradesPath)) { File.WriteAllText(tradesPath, tradesHeader + "\n"); }
        File.AppendAllLines(tradesPath, newTradeRows);
        Console.WriteLine($"Appended {newSignalRows.Count} signal rows to {Path.GetFullPath(signalsPath)}.");
        Console.WriteLine($"Appended {newTradeRows.Count} trade rows to {Path.GetFullPath(tradesPath)}.");
        Console.WriteLine();

        // ================= Running cumulative summary (always fully recomputed from the accumulated trades CSV). =================
        var allTradeLines = File.ReadAllLines(tradesPath).Skip(1).Where(l => l.Length > 0).ToList();
        var allSignalLines = File.ReadAllLines(signalsPath).Skip(1).Where(l => l.Length > 0).ToList();
        var cumTrades = allTradeLines.Select(l =>
        {
            var f = l.Split(',');
            return (Date: DateOnly.ParseExact(f[0], "yyyy-MM-dd"), Pattern: f[2], EntryTs: f[4], NetPnl: decimal.Parse(f[15], System.Globalization.CultureInfo.InvariantCulture),
                HoldingSeconds: double.Parse(f[16], System.Globalization.CultureInfo.InvariantCulture), Mae: decimal.Parse(f[21], System.Globalization.CultureInfo.InvariantCulture), Mfe: decimal.Parse(f[22], System.Globalization.CultureInfo.InvariantCulture));
        }).OrderBy(x => x.Date).ThenBy(x => x.EntryTs).ToList();
        var distinctSessionsCompleted = allSignalLines.Select(l => l.Split(',')[0]).Distinct().Count();

        Console.WriteLine("### Running forward-validation cumulative summary (recomputed fresh from the full accumulated trade history each run) ###");
        Console.WriteLine($"  Sessions completed: {distinctSessionsCompleted} of target {targetSessionCount}.");
        if (cumTrades.Count > 0)
        {
            var wins = cumTrades.Count(x => x.NetPnl > 0); var gp = cumTrades.Where(x => x.NetPnl > 0).Sum(x => x.NetPnl); var gl = Math.Abs(cumTrades.Where(x => x.NetPnl <= 0).Sum(x => x.NetPnl));
            var sorted = cumTrades.Select(x => x.NetPnl).OrderBy(x => x).ToList();
            var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2m;
            Console.WriteLine($"  Trades={cumTrades.Count} A/B={cumTrades.Count(x => x.Pattern == "A")}/{cumTrades.Count(x => x.Pattern == "B")} WinRate={100.0 * wins / cumTrades.Count:F1}% PF={(gl > 0 ? (gp / gl).ToString("F2") : "n/a")} NetPnl={cumTrades.Sum(x => x.NetPnl):F0} PnlPerTrade={cumTrades.Average(x => x.NetPnl):F1} MedianPnl={median:F1}");
            var bySession = cumTrades.GroupBy(x => x.Date).Select(g => (g.Key, Pnl: g.Sum(x => x.NetPnl))).OrderByDescending(x => x.Pnl).ToList();
            var profSessions = bySession.Count(x => x.Pnl > 0);
            var medSessPnl = bySession.Select(x => x.Pnl).OrderBy(x => x).ElementAt(bySession.Count / 2);
            Console.WriteLine($"  ProfitableSessions={profSessions}/{bySession.Count} MedianSessionPnl={medSessPnl:F0} LargestWinningSession={bySession.First().Key:yyyy-MM-dd}({bySession.First().Pnl:F0}) LargestLosingSession={bySession.Last().Key:yyyy-MM-dd}({bySession.Last().Pnl:F0})");

            // ---- Chronological equity + drawdown. ----
            decimal equity = 0, peak = 0, maxDrawdown = 0;
            foreach (var t in cumTrades)
            {
                equity += t.NetPnl;
                if (equity > peak) { peak = equity; }
                var dd = peak - equity;
                if (dd > maxDrawdown) { maxDrawdown = dd; }
            }
            Console.WriteLine($"  Chronological equity: final={equity:F0} peak={peak:F0} maxDrawdown={maxDrawdown:F0} (rupee terms; no initial capital figure has been frozen for this project, so % drawdown is not computed -- not invented).");

            foreach (var pattern in new[] { "A", "B" })
            {
                var side = cumTrades.Where(x => x.Pattern == pattern).ToList();
                if (side.Count == 0) { continue; }
                var swins = side.Count(x => x.NetPnl > 0); var sgp = side.Where(x => x.NetPnl > 0).Sum(x => x.NetPnl); var sgl = Math.Abs(side.Where(x => x.NetPnl <= 0).Sum(x => x.NetPnl));
                Console.WriteLine($"  [{pattern}] n={side.Count} WinRate={100.0 * swins / side.Count:F1}% PF={(sgl > 0 ? (sgp / sgl).ToString("F2") : "n/a")} NetPnl={side.Sum(x => x.NetPnl):F0}");
            }
        }
        else { Console.WriteLine("  No trades executed yet across forward sessions processed so far."); }
        Console.WriteLine();

        if (distinctSessionsCompleted < targetSessionCount)
        {
            Console.WriteLine($"### {targetSessionCount - distinctSessionsCompleted} more forward session(s) needed before the end-of-{targetSessionCount}-session review (Section 13) can be produced. Not generated this run -- per instruction, only after all {targetSessionCount} are collected. ###");
        }
        else
        {
            Console.WriteLine($"### All {targetSessionCount} forward sessions collected -- end-of-review comparison should now be run as its own separate pass (historical validation vs. 2026-09-24 OOS vs. this forward set, kept separate, never pooled). ###");
        }

        return 0;
    }
}
