using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NiftySignal.BacktestData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.Rules;
using NiftySignal.VolumeBarData;

// CadenceContext.Timestamp / VolumeBarRow.StartTimestamp/EndTimestamp are all stored UTC (Npgsql's
// own requirement) -- same convention NiftySignal.MetricTrials/Program.cs already uses.
string FormatIst(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(5.5)).ToString("HH:mm:ss");

// 2026-09-20: PowerShell (and some other shells) silently drop/collapse empty-string ""
// placeholder args passed through `dotnet run --`, which shifts every later positional arg left
// by one -- confirmed the hard way when a stopLossPercent placeholder vanished and "5" (meant for
// depthBandWidth) landed in stopLossPercent instead, producing a nonsense 5%-stop run that looked
// like a real result. Fix: trailing optional args on "trade"/"calibrate" are --name=value flags,
// found anywhere in the arg list, so skipping one never requires a placeholder for the others.
(string[] Positional, Dictionary<string, string> Named) SplitNamedArgs(string[] rawArgs)
{
    var positional = new List<string>();
    var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var a in rawArgs)
    {
        if (a.StartsWith("--", StringComparison.Ordinal) && a.Contains('='))
        {
            var idx = a.IndexOf('=');
            named[a[2..idx]] = a[(idx + 1)..];
        }
        else
        {
            positional.Add(a);
        }
    }

    return (positional.ToArray(), named);
}

// 2026-09-17 volume-cadence plan. Usage:
//   dotnet run --project NiftySignal.VolumeBarData -- <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [barVolumeThreshold] [sourceDatabaseNameOverride]
//   dotnet run --project NiftySignal.VolumeBarData -- 2026-09-08 2026-09-16
//   dotnet run --project NiftySignal.VolumeBarData -- 2026-09-08 2026-09-16 650
//
//   dotnet run --project NiftySignal.VolumeBarData -- analyze <date:yyyy-MM-dd> [windowBars] [barVolumeThreshold]
//   dotnet run --project NiftySignal.VolumeBarData -- analyze 2026-09-15
// Reads an already-populated day's volume bars, ports TrendReversion onto them
// (VolumeBarTrendReversionTracker), and parity-checks the day's CVD/depth-imbalance numbers
// against the existing time-cadence pipeline (niftysignal_backtest_analysis) for the same day --
// see AnalyzeCommand.cs for what "parity" means here (CVD should closely match; depth imbalance
// is a softer sign/magnitude check).
//
//   dotnet run --project NiftySignal.VolumeBarData -- trade <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> <metric> [entryPercentile] [trendWindowBars] [barVolumeThreshold]
//   dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-08 2026-09-16 TrendReversion
//   dotnet run --project NiftySignal.VolumeBarData -- trade 2026-09-08 2026-09-16 FutureCvdNet 95
// The 2026-09-17 plan's "single-metric isolation" protocol: trades ONE chosen metric at a time
// (TrendReversion/FutureCvdNet/DepthImbalance/OrderFlowImbalance -- see VolumeBarMetric) against
// REAL option prices (tick-accurate, not rounded to a cadence -- see OptionPriceSeries), same
// hysteresis rule shape as NiftySignal.MetricTrials.CoreScoreOptionSimulator.SimulateDay so
// results are comparable across the two clocks.
//
// entryPercentile (2026-09-17, user's own explicit requirement -- "it should be dynamic", same
// discipline as docs/REVIEW_FINDINGS.md's DynamicHybrid mode) is NOT a fixed score level -- it's
// a percentile (0-100) against each day's OWN score-magnitude distribution so far. "95" means
// "only trade the most extreme 5% of readings today," self-calibrating per day and per metric
// instead of assuming one fixed absolute number means the same thing for every metric and every
// session. Defaults to 90; see the "calibrate" command below for finding the right value instead
// of guessing it.
//
//   dotnet run --project NiftySignal.VolumeBarData -- calibrate <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> <metric> [barVolumeThresholds]
//   dotnet run --project NiftySignal.VolumeBarData -- calibrate 2026-09-04 2026-09-16 DepthImbalance
//   dotnet run --project NiftySignal.VolumeBarData -- calibrate 2026-09-04 2026-09-16 DepthImbalance 650,1300,2600
// Sweeps entryPercentile (80/85/90/93/95/97/99) times barVolumeThreshold (default 650,1300,2600 --
// i.e. accumulating different numbers of bars, coarser thresholds meaning fewer/bigger bars) for
// one metric across the given date range, reporting trades/day for every combination and flagging
// the ones landing in a 7-20 trades/day target range. Pick a threshold pair from this output, then
// run "trade" with it for the full per-trade P&amp;L detail.
//
// Reads real historical future ticks READ-ONLY from the source database (default:
// niftysignal_vm_copy, same convention NiftySignal.BacktestData already uses). Writes
// VolumeBarRow rows to a brand-new, dedicated database (niftysignal_volume_bars) -- never the
// source database, and never anything NiftySignal.Persistence's live schema or
// NiftySignal.BacktestData's time-cadence schema touches.
//
// barVolumeThreshold defaults to 1300 (10 lots x 65 lot size x 2, i.e. 20 lots -- calibrated
// 2026-09-17 against this project's own real per-day future volume: at 1300, actual bar count per
// day (measured, not estimated) ranges 777-1658 across the 7 available days, a real 2.1x spread,
// landing close to or under the existing 15s time cadence's ~1500 bars/day -- see
// docs/SCORE_CANDIDATES.md's volume-cadence entry for the full calibration numbers). Pass a
// different threshold to compare calibrations -- each coexists in the same table, keyed by
// (AsOfDate, BarVolumeThreshold).
//
// Idempotent per (trading day, threshold): a day already populated at that threshold is skipped
// entirely. The only way to redo one is to delete its rows first, deliberately.
//
// Per standing instruction, this is a tool the user runs themselves -- it is not run
// automatically as part of any other process.

// Same sourcing convention as NiftySignal.BacktestData/Program.cs, shared by both subcommands.
var hostProjectPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NiftySignal.Host");
var configuration = new ConfigurationBuilder()
    .SetBasePath(hostProjectPath)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var baseConnectionString = configuration.GetConnectionString("NiftySignalDb")
    ?? throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set in NiftySignal.Host/appsettings.Local.json.");

var volumeBarConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = VolumeBarPopulator.VolumeBarDatabaseName }.ConnectionString;
var volumeBarOptions = new DbContextOptionsBuilder<VolumeBarDbContext>().UseNpgsql(volumeBarConnectionString).Options;

if (args.Length > 0 && string.Equals(args[0], "analyze", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2 || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var analyzeDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- analyze <date:yyyy-MM-dd> [windowBars] [barVolumeThreshold]");
        return 1;
    }

    var windowBars = args.Length > 2 ? int.Parse(args[2]) : 15;
    var analyzeThreshold = args.Length > 3 ? long.Parse(args[3]) : 1300L;

    var timeCadenceConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = CadencePopulator.BacktestAnalysisDatabaseName }.ConnectionString;
    var timeCadenceOptions = new DbContextOptionsBuilder<BacktestAnalysisDbContext>().UseNpgsql(timeCadenceConnectionString).Options;

    await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
    await using var timeCadence = new BacktestAnalysisDbContext(timeCadenceOptions);

    return await AnalyzeCommand.RunAsync(analyzeDate, windowBars, analyzeThreshold, volumeBars, timeCadence);
}

// 2026-09-21: parity-investigation diagnostic -- dumps the OFFICIAL (offline-recomputed)
// per-bar scaledScore/percentile/maxPainConfirmScore for OptionsScoreThreeWaySwitchMaxPainConfirmed,
// via TradeSimulator.SimulateDayAsync's own onBarEvaluated hook, so it can be compared directly
// against LiveOptionsScoreBars' own persisted values for the same bar range.
//   dotnet run --project NiftySignal.VolumeBarData -- dump-scores <date:yyyy-MM-dd> [barVolumeThreshold] [--band=N] [--from=N] [--to=N]
if (args.Length > 0 && string.Equals(args[0], "dump-scores", StringComparison.OrdinalIgnoreCase))
{
    var (dsPositional, dsNamed) = SplitNamedArgs(args);
    if (dsPositional.Length < 2 || !DateOnly.TryParseExact(dsPositional[1], "yyyy-MM-dd", out var dsDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- dump-scores <date:yyyy-MM-dd> [barVolumeThreshold] [--band=N] [--from=N] [--to=N]");
        return 1;
    }
    var dsThreshold = dsPositional.Length > 2 ? long.Parse(dsPositional[2]) : 2600L;
    var dsBand = dsNamed.TryGetValue("band", out var dsBandStr) ? int.Parse(dsBandStr) : OptionDepthPopulator.DefaultBandWidth;
    var dsFrom = dsNamed.TryGetValue("from", out var dsFromStr) ? int.Parse(dsFromStr) : 0;
    var dsTo = dsNamed.TryGetValue("to", out var dsToStr) ? int.Parse(dsToStr) : int.MaxValue;

    Console.WriteLine($"{"Bar",4} {"Time (IST)",10} {"ScaledScore",12} {"Percentile",11} {"MaxPainConfirm",15}");
    var dsSourceConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_vm_copy" }.ConnectionString;
    var dsSourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(dsSourceConnectionString).Options;
    await using (var dsSource = new NiftySignalDbContext(dsSourceOptions))
    await using (var dsVolumeBars = new VolumeBarDbContext(volumeBarOptions))
    {
        await TradeSimulator.SimulateDayAsync(dsSource, dsVolumeBars, dsDate, dsThreshold,
            VolumeBarMetric.OptionsScoreThreeWaySwitchMaxPainConfirmed, 90.0, 15, CancellationToken.None,
            sharedPriceCache: null, stopLossPercent: null, rollingSubBarThreshold: null, bandWidth: dsBand,
            onBarEvaluated: (bar, scaledScore, percentile, maxPainConfirmScore) =>
            {
                if (bar.BarIndex < dsFrom || bar.BarIndex > dsTo)
                {
                    return;
                }
                Console.WriteLine($"{bar.BarIndex,4} {FormatIst(bar.EndTimestamp),10} " +
                    $"{(scaledScore?.ToString("F4") ?? "--"),12} {(percentile?.ToString("F4") ?? "--"),11} " +
                    $"{(maxPainConfirmScore?.ToString("F4") ?? "--"),15}");
            });
    }
    return 0;
}

// 2026-09-21: plain-data diagnostic requested by the user after a live whipsaw concern -- dumps,
// bar by bar for one day, exactly what the Open-leg ATM depth-imbalance metric sees: the future's
// own close price alongside the raw Call/Put resting-depth components and the resulting imbalance
// score, so it can be eyeballed directly rather than inferred from aggregate win-rate numbers.
// No entry/exit logic here at all -- this is upstream of trading, just the metric's own raw values.
//   dotnet run --project NiftySignal.VolumeBarData -- depth-report <date:yyyy-MM-dd> [barVolumeThreshold] [--band=N]
if (args.Length > 0 && string.Equals(args[0], "depth-report", StringComparison.OrdinalIgnoreCase))
{
    var (drPositional, drNamed) = SplitNamedArgs(args);
    if (drPositional.Length < 2 || !DateOnly.TryParseExact(drPositional[1], "yyyy-MM-dd", out var drDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- depth-report <date:yyyy-MM-dd> [barVolumeThreshold] [--band=N]");
        return 1;
    }
    var drThreshold = drPositional.Length > 2 ? long.Parse(drPositional[2]) : 650L;
    var drBand = drNamed.TryGetValue("band", out var drBandStr) ? int.Parse(drBandStr) : OptionDepthPopulator.DefaultBandWidth;
    var drCsv = drNamed.TryGetValue("csv", out var drCsvPath) ? drCsvPath : null;

    await using var drDb = new VolumeBarDbContext(volumeBarOptions);
    var drFutureBars = await drDb.VolumeBars
        .Where(b => b.AsOfDate == drDate && b.BarVolumeThreshold == drThreshold)
        .OrderBy(b => b.BarIndex)
        .ToListAsync();
    var drDepthBars = await drDb.OptionDepthBars
        .Where(b => b.AsOfDate == drDate && b.BarVolumeThreshold == drThreshold && b.BandWidth == drBand)
        .ToDictionaryAsync(b => b.BarIndex);

    if (drFutureBars.Count == 0)
    {
        Console.Error.WriteLine($"No VolumeBars for {drDate:yyyy-MM-dd} @ {drThreshold}. Populate it first (populate-volume-bars / populate-options-depth --band={drBand}).");
        return 1;
    }

    Console.WriteLine($"=== Depth report: {drDate:yyyy-MM-dd}, BarVolumeThreshold={drThreshold}, BandWidth={drBand} (ATM+/-{(drBand - 1) / 2}) ===");
    Console.WriteLine("Formula: DepthImbalance = (CallBid+PutBid-CallAsk-PutAsk) / (CallBid+PutBid+CallAsk+PutAsk); traded score is the NEGATIVE of this value.");
    Console.WriteLine();

    using var drWriter = drCsv is not null ? new StreamWriter(drCsv) : null;
    void WriteRow(string line) { Console.WriteLine(line); drWriter?.WriteLine(line.Replace(" | ", ",")); }

    WriteRow("Bar | TimeIST | IndexPrice | CallDepth | PutDepth | CallBid | CallAsk | PutBid | PutAsk | DepthImbalance");

    foreach (var fb in drFutureBars)
    {
        drDepthBars.TryGetValue(fb.BarIndex, out var db);
        var callDepth = db is not null && db.CallBidQtyAvg is { } cb && db.CallAskQtyAvg is { } ca ? cb + ca : (double?)null;
        var putDepth = db is not null && db.PutBidQtyAvg is { } pb && db.PutAskQtyAvg is { } pa ? pb + pa : (double?)null;
        double? imbalance = null;
        if (db is not null && db.CallBidQtyAvg is { } cbi && db.PutBidQtyAvg is { } pbi && db.CallAskQtyAvg is { } cai && db.PutAskQtyAvg is { } pai)
        {
            var bidSum = cbi + pbi;
            var askSum = cai + pai;
            var total = bidSum + askSum;
            imbalance = total != 0 ? (bidSum - askSum) / total : (double?)null;
        }

        WriteRow($"{fb.BarIndex} | {FormatIst(fb.EndTimestamp)} | {fb.ClosePrice:F2} | " +
            $"{(callDepth?.ToString("F0") ?? "")} | {(putDepth?.ToString("F0") ?? "")} | " +
            $"{(db?.CallBidQtyAvg?.ToString("F0") ?? "")} | {(db?.CallAskQtyAvg?.ToString("F0") ?? "")} | " +
            $"{(db?.PutBidQtyAvg?.ToString("F0") ?? "")} | {(db?.PutAskQtyAvg?.ToString("F0") ?? "")} | " +
            $"{(imbalance?.ToString("F4") ?? "")}");
    }

    Console.WriteLine();
    Console.WriteLine($"Total bars: {drFutureBars.Count}, bars with depth data: {drDepthBars.Count(kv => kv.Value.CallBidQtyAvg is not null)}");
    if (drCsv is not null)
    {
        Console.WriteLine($"CSV written to {drCsv}");
    }
    return 0;
}

// Phase A (docs/LIVE_PARITY_PLAN.md) debug helper: dumps which (AsOfDate, BarVolumeThreshold)
// combinations already have rows in each of the 4 Phase-A tables, so a parity-test run can pick a
// date/threshold that's already offline-populated to compare a live replay against.
//   dotnet run --project NiftySignal.VolumeBarData -- list-populated [barVolumeThreshold]
if (args.Length > 0 && string.Equals(args[0], "list-populated", StringComparison.OrdinalIgnoreCase))
{
    var lpThreshold = args.Length > 1 ? (long?)long.Parse(args[1]) : null;
    await using var lpDb = new VolumeBarDbContext(volumeBarOptions);

    async Task DumpAsync<T>(string label, IQueryable<T> query, Func<T, DateOnly> dateSel, Func<T, long> threshSel)
    {
        var rows = await query.ToListAsync();
        var groups = rows.GroupBy(r => (Date: dateSel(r), Threshold: threshSel(r)))
            .Where(g => lpThreshold is null || g.Key.Threshold == lpThreshold)
            .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Threshold);
        Console.WriteLine($"--- {label} ---");
        foreach (var g in groups)
        {
            Console.WriteLine($"  {g.Key.Date:yyyy-MM-dd} @ {g.Key.Threshold}: {g.Count()} rows");
        }
    }

    await DumpAsync("VolumeBars", lpDb.VolumeBars, b => b.AsOfDate, b => b.BarVolumeThreshold);
    await DumpAsync("OptionAtmBars", lpDb.OptionAtmBars, b => b.AsOfDate, b => b.BarVolumeThreshold);
    await DumpAsync("OptionDepthBars", lpDb.OptionDepthBars, b => b.AsOfDate, b => b.BarVolumeThreshold);
    await DumpAsync("OptionMaxPainBars", lpDb.OptionMaxPainBars, b => b.AsOfDate, b => b.BarVolumeThreshold);
    return 0;
}

// Phase A (docs/LIVE_PARITY_PLAN.md) verification harness: replays one day's already-recorded
// ticks through the LIVE incremental writers (LiveVolumeBarPopulator/LiveOptionAtmPopulator/
// LiveOptionDepthPopulator/LiveOptionMaxPainPopulator) as if they were arriving live -- polling at
// a simulated cadence, cutting off at each poll's own "now" so no future tick is ever visible early
// -- into a SEPARATE test database (niftysignal_volume_bars_livetest, never the real
// niftysignal_volume_bars the offline populators use, so this can be re-run freely without
// colliding with real data). Then compares the resulting rows, table by table, against the SAME
// day's rows already produced by the offline populators in the real database.
//   dotnet run --project NiftySignal.VolumeBarData -- replay-live <date:yyyy-MM-dd> [barVolumeThreshold=2600] [--restart-after=N] [--poll-seconds=10]
if (args.Length > 0 && string.Equals(args[0], "replay-live", StringComparison.OrdinalIgnoreCase))
{
    var (rlPositional, rlNamed) = SplitNamedArgs(args);
    if (rlPositional.Length < 2 || !DateOnly.TryParseExact(rlPositional[1], "yyyy-MM-dd", out var rlDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- replay-live <date:yyyy-MM-dd> [barVolumeThreshold=2600] [--restart-after=N] [--poll-seconds=10]");
        return 1;
    }

    var rlThreshold = rlPositional.Length > 2 ? long.Parse(rlPositional[2]) : 2600L;
    var rlRestartAfter = rlNamed.TryGetValue("restart-after", out var rlRestartStr) ? (int?)int.Parse(rlRestartStr) : null;
    var rlPollSeconds = rlNamed.TryGetValue("poll-seconds", out var rlPollStr) ? int.Parse(rlPollStr) : 10;

    return await ReplayLiveCommand.RunAsync(baseConnectionString, rlDate, rlThreshold, rlRestartAfter, rlPollSeconds);
}

// Phase C (docs/LIVE_PARITY_PLAN.md) verification harness: drives TradingDaySession (the exact class
// NiftySignal.Host.LiveOptionsScoreEngine uses live) over one or more already-populated historical
// days' bars (read from the OFFICIAL niftysignal_volume_bars database -- Phase A already proved a
// live replay reproduces those bars row-for-row, so this only re-proves the SCORING/ENTRY side on
// top of them) and compares its per-bar score + entry-signal output against what
// TradeSimulator.SimulateDayAsync itself computed for the same day (via that method's own Phase C
// onBarEvaluated diagnostic hook). Passing 2+ dates also proves day-boundary/no-cross-day-leakage,
// since each date gets an independently-constructed TradingDaySession.
//   dotnet run --project NiftySignal.VolumeBarData -- replay-live-score <date1:yyyy-MM-dd> [date2:yyyy-MM-dd ...] [--threshold=2600]
if (args.Length > 0 && string.Equals(args[0], "replay-live-score", StringComparison.OrdinalIgnoreCase))
{
    var (rsPositional, rsNamed) = SplitNamedArgs(args);
    var rsDates = rsPositional.Skip(1).Select(a => DateOnly.ParseExact(a, "yyyy-MM-dd")).ToList();
    if (rsDates.Count == 0)
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- replay-live-score <date1:yyyy-MM-dd> [date2:yyyy-MM-dd ...] [--threshold=2600]");
        return 1;
    }

    var rsThreshold = rsNamed.TryGetValue("threshold", out var rsThresholdStr) ? long.Parse(rsThresholdStr) : 2600L;
    if (rsNamed.TryGetValue("restart-after", out var rsRestartStr))
    {
        return await ReplayLiveScoreCommand.RunRestartTestAsync(baseConnectionString, rsDates[0], rsThreshold, int.Parse(rsRestartStr), CancellationToken.None);
    }

    return await ReplayLiveScoreCommand.RunAsync(baseConnectionString, rsDates, rsThreshold, CancellationToken.None);
}

// Phase D (docs/LIVE_PARITY_PLAN.md) verification harness: drives TradingDaySession (same as
// replay-live-score) plus LivePaperTradeExecutor (the exact class NiftySignal.Host.LiveOptionsScoreEngine
// calls live to open/close paper trades) over one or more already-populated historical days, and
// compares the resulting LivePaperTradeRow rows (entry bar/strike/direction/exit reason -- fill price
// deltas logged, not compared exactly, per the plan's own fill-price policy) against
// TradeSimulator.SimulateDayAsync's own trades for the same day/config.
//   dotnet run --project NiftySignal.VolumeBarData -- replay-live-papertrade <date1:yyyy-MM-dd> [date2:yyyy-MM-dd ...] [--threshold=2600]
if (args.Length > 0 && string.Equals(args[0], "replay-live-papertrade", StringComparison.OrdinalIgnoreCase))
{
    var (rpPositional, rpNamed) = SplitNamedArgs(args);
    var rpDates = rpPositional.Skip(1).Select(a => DateOnly.ParseExact(a, "yyyy-MM-dd")).ToList();
    if (rpDates.Count == 0)
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- replay-live-papertrade <date1:yyyy-MM-dd> [date2:yyyy-MM-dd ...] [--threshold=2600]");
        return 1;
    }

    var rpThreshold = rpNamed.TryGetValue("threshold", out var rpThresholdStr) ? long.Parse(rpThresholdStr) : 2600L;
    // 2026-09-20, Phase E: --destination-database= lets this proven harness write into the REAL
    // niftysignal_volume_bars database instead of its own disposable scratch one -- used once to
    // produce genuine persisted LivePaperTradeRow rows for verify-parity to read (see
    // ReplayLivePaperTradeCommand.RunAsync's own doc comment). Omit for the original always-fresh
    // scratch-database behavior.
    var rpDestinationDatabase = rpNamed.TryGetValue("destination-database", out var rpDestDb) ? rpDestDb : null;
    return await ReplayLivePaperTradeCommand.RunAsync(baseConnectionString, rpDates, rpThreshold, CancellationToken.None, rpDestinationDatabase);
}

// Phase E (docs/LIVE_PARITY_PLAN.md) -- THE MANDATORY NIGHTLY PARITY TOOL ("This tool is mandatory.
// We will run it every day."). Re-runs the official backtest (TradeSimulator.SimulateDayAsync for
// OptionsScoreThreeWaySwitchMaxPainConfirmed @ 2600/90) for one finished trading day, reads whatever
// LivePaperTradeRow rows the REAL live system already persisted for that day, and diffs them
// trade-by-trade: missing trade, extra trade, and for matched trades -- entry bar/strike/direction/
// exit reason (must match exactly) and entry/exit fill price (expected to differ per the documented
// fill-timing policy -- delta reported, only flagged as "out of range" past VerifyParityCommand's own
// documented threshold, never itself a hard FAIL). See VerifyParityCommand.cs for the full comparison
// logic and PASS/FAIL rule.
//   dotnet run --project NiftySignal.VolumeBarData -- verify-parity <date:yyyy-MM-dd> [--threshold=2600] [--live-database=<name>]
// --live-database overrides which database's LivePaperTradeRow rows are read as "live" (default: the
// real niftysignal_volume_bars) -- exists ONLY so VerifyParitySelfTestCommand can point it at a
// scratch copy with a deliberately-corrupted row, proving the tool actually detects a mismatch rather
// than just printing PASS on already-known-good days. The official backtest re-derivation always
// reads the real database regardless of this flag -- the source of truth is never swappable.
if (args.Length > 0 && string.Equals(args[0], "verify-parity", StringComparison.OrdinalIgnoreCase))
{
    var (vpPositional, vpNamed) = SplitNamedArgs(args);
    if (vpPositional.Length < 2 || !DateOnly.TryParseExact(vpPositional[1], "yyyy-MM-dd", out var vpDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- verify-parity <date:yyyy-MM-dd> [--threshold=2600] [--live-database=<name>]");
        return 1;
    }

    var vpThreshold = vpNamed.TryGetValue("threshold", out var vpThresholdStr) ? long.Parse(vpThresholdStr) : 2600L;
    var vpLiveDatabase = vpNamed.TryGetValue("live-database", out var vpLiveDb) ? vpLiveDb : null;
    return await VerifyParityCommand.RunAsync(baseConnectionString, vpDate, vpThreshold, CancellationToken.None, vpLiveDatabase);
}

// Phase E self-test: proves VerifyParityCommand actually DETECTS a real mismatch, not just that it
// prints PASS on already-known-good days. Copies one day's real, already-verified LivePaperTradeRow
// rows into a disposable scratch database (niftysignal_volume_bars_paritytest -- never the real
// niftysignal_volume_bars), deliberately corrupts ONE row three different ways in turn (wrong strike,
// wrong direction, missing trade), asserts VerifyParityCommand reports FAIL with the right diagnosis
// each time, reverts, and asserts PASS again. See VerifyParitySelfTestCommand.cs.
//   dotnet run --project NiftySignal.VolumeBarData -- verify-parity-selftest <date:yyyy-MM-dd> [--threshold=2600]
if (args.Length > 0 && string.Equals(args[0], "verify-parity-selftest", StringComparison.OrdinalIgnoreCase))
{
    var (vsPositional, vsNamed) = SplitNamedArgs(args);
    if (vsPositional.Length < 2 || !DateOnly.TryParseExact(vsPositional[1], "yyyy-MM-dd", out var vsDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- verify-parity-selftest <date:yyyy-MM-dd> [--threshold=2600]");
        return 1;
    }

    var vsThreshold = vsNamed.TryGetValue("threshold", out var vsThresholdStr) ? long.Parse(vsThresholdStr) : 2600L;
    var vsSourceDatabase = vsNamed.TryGetValue("source-database", out var vsSrcDb) ? vsSrcDb : null;
    return await VerifyParitySelfTestCommand.RunAsync(baseConnectionString, vsDate, vsThreshold, CancellationToken.None, vsSourceDatabase);
}

// Futures-crossover live-wiring task (2026-09-21, docs/LIVE_PARITY_PLAN.md "Futures crossover:
// wired live" section) -- three harnesses mirroring the options side's own Phase C/D/restart
// proofs, plus the task's own required no-collision proof (see ReplayLiveFuturesCrossoverCommand's
// own doc comment for what each one proves).
//   dotnet run --project NiftySignal.VolumeBarData -- replay-live-futures-crossover <date1:yyyy-MM-dd> [date2 ...] [--threshold=2600] [--fast=8] [--slow=40] [--gapthreshold=5] [--restart-after=N] [--destination-database=<name>]
//   dotnet run --project NiftySignal.VolumeBarData -- replay-live-futures-crossover-both <date:yyyy-MM-dd> [--threshold=2600]   (no-collision proof, both strategies interleaved)
if (args.Length > 0 && string.Equals(args[0], "replay-live-futures-crossover", StringComparison.OrdinalIgnoreCase))
{
    var (fxPositional, fxNamed) = SplitNamedArgs(args);
    var fxDates = fxPositional.Skip(1).Select(a => DateOnly.ParseExact(a, "yyyy-MM-dd")).ToList();
    if (fxDates.Count == 0)
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- replay-live-futures-crossover <date1:yyyy-MM-dd> [date2 ...] [--threshold=2600] [--fast=8] [--slow=40] [--gapthreshold=5] [--restart-after=N] [--destination-database=<name>]");
        return 1;
    }

    var fxThreshold = fxNamed.TryGetValue("threshold", out var fxThresholdStr) ? long.Parse(fxThresholdStr) : 2600L;
    var fxFast = fxNamed.TryGetValue("fast", out var fxFastStr) ? int.Parse(fxFastStr) : LiveFuturesCrossoverSession.FastBars;
    var fxSlow = fxNamed.TryGetValue("slow", out var fxSlowStr) ? int.Parse(fxSlowStr) : LiveFuturesCrossoverSession.SlowBars;
    var fxGapThreshold = fxNamed.TryGetValue("gapthreshold", out var fxGapStr) ? double.Parse(fxGapStr) : LiveFuturesCrossoverSession.ThresholdPoints;

    if (fxNamed.TryGetValue("restart-after", out var fxRestartStr))
    {
        return await ReplayLiveFuturesCrossoverCommand.RunRestartTestAsync(baseConnectionString, fxDates[0], fxThreshold, int.Parse(fxRestartStr), CancellationToken.None);
    }

    var fxDestinationDatabase = fxNamed.TryGetValue("destination-database", out var fxDestDb) ? fxDestDb : null;
    return await ReplayLiveFuturesCrossoverCommand.RunAsync(baseConnectionString, fxDates, fxThreshold, fxFast, fxSlow, fxGapThreshold, CancellationToken.None, fxDestinationDatabase);
}

if (args.Length > 0 && string.Equals(args[0], "replay-live-futures-crossover-both", StringComparison.OrdinalIgnoreCase))
{
    var (fbPositional, fbNamed) = SplitNamedArgs(args);
    if (fbPositional.Length < 2 || !DateOnly.TryParseExact(fbPositional[1], "yyyy-MM-dd", out var fbDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- replay-live-futures-crossover-both <date:yyyy-MM-dd> [--threshold=2600]");
        return 1;
    }

    var fbThreshold = fbNamed.TryGetValue("threshold", out var fbThresholdStr) ? long.Parse(fbThresholdStr) : 2600L;
    return await ReplayLiveFuturesCrossoverCommand.RunBothStrategiesAsync(baseConnectionString, fbDate, fbThreshold, CancellationToken.None);
}

var tradeSourceConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_vm_copy" }.ConnectionString;
var tradeSourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(tradeSourceConnectionString).Options;

// Phase G (docs/LIVE_PARITY_PLAN.md) performance-review helper: measures, READ-ONLY against the
// real historical source (niftysignal_vm_copy -- never written to), the wall-clock cost of the
// SINGLE most expensive thing LiveOptionAtmPopulator/LiveOptionMaxPainPopulator do on every poll
// that has at least one new pending bar: reloading each touched option token's FULL day-so-far
// quote/OI history (OptionQuoteSeries.LoadAsync / OptionOiSeries.LoadAsync), at the worst point in
// the day (dayEnd = market close, i.e. the largest possible day-so-far range). This reproduces the
// exact query shape both live populators already issue -- see their own doc comments in
// LiveOptionAtmPopulator.cs/LiveOptionMaxPainPopulator.cs for why they reload rather than
// incrementally update -- without mutating any table.
//   dotnet run --project NiftySignal.VolumeBarData -- perf-check <date:yyyy-MM-dd> [barVolumeThreshold=2600]
if (args.Length > 0 && string.Equals(args[0], "perf-check", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2 || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var pcDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- perf-check <date:yyyy-MM-dd> [barVolumeThreshold=2600]");
        return 1;
    }

    var pcThreshold = args.Length > 2 ? long.Parse(args[2]) : 2600L;

    await using var pcSource = new NiftySignalDbContext(tradeSourceOptions);
    await using var pcVolumeBars = new VolumeBarDbContext(volumeBarOptions);

    var pcFutureBars = await pcVolumeBars.VolumeBars
        .Where(b => b.AsOfDate == pcDate && b.BarVolumeThreshold == pcThreshold)
        .OrderBy(b => b.BarIndex)
        .ToListAsync();
    if (pcFutureBars.Count == 0)
    {
        Console.Error.WriteLine($"No VolumeBars found for {pcDate:yyyy-MM-dd} @ {pcThreshold} in niftysignal_volume_bars -- populate it first (see docs/LIVE_PARITY_PLAN.md Phase A).");
        return 1;
    }

    var pcDayStart = pcFutureBars[0].StartTimestamp;
    var pcDayEnd = pcFutureBars[^1].EndTimestamp;

    // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
    // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
    var pcAllOptions = await pcSource.Instruments
        .Where(i => i.AsOfDate == pcDate && i.InstrumentType == NiftySignal.Domain.Enums.InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
        .ToListAsync();
    var pcNearestExpiry = pcAllOptions.Select(o => o.ExpiryDate!.Value).Min();
    var pcChain = pcAllOptions.Where(o => o.ExpiryDate == pcNearestExpiry).ToList();
    var pcDistinctStrikes = pcChain.Select(o => o.StrikePrice!.Value).Distinct().ToList();

    Console.WriteLine($"=== perf-check {pcDate:yyyy-MM-dd} @ {pcThreshold}: {pcDistinctStrikes.Count} distinct strikes, {pcChain.Count} option tokens in nearest-expiry chain, day range {pcDayStart:HH:mm} - {pcDayEnd:HH:mm} IST-equivalent ===");

    // MaxPain's own reload shape: EVERY distinct strike in the chain, both sides -- see
    // LiveOptionMaxPainPopulator.WriteNewBarsAsync's own foreach (var strike in distinctStrikes) loop.
    var pcMaxPainTokens = pcChain.Select(o => o.Token).Distinct().ToList();
    var pcMaxPainSw = System.Diagnostics.Stopwatch.StartNew();
    var pcMaxPainTotalRows = 0;
    foreach (var token in pcMaxPainTokens)
    {
        var series = await OptionOiSeries.LoadAsync(pcSource, token, pcDayStart, pcDayEnd, CancellationToken.None);
        pcMaxPainTotalRows += series.RowCountForDiagnostics;
    }

    pcMaxPainSw.Stop();
    Console.WriteLine($"MaxPain-shaped reload: {pcMaxPainTokens.Count} tokens, {pcMaxPainTotalRows} total OI-bearing rows read, {pcMaxPainSw.ElapsedMilliseconds}ms wall-clock (full day-so-far range, worst case = last bar of the day).");

    // ATM's own reload shape: only the SyntheticForwardStrikeCount=5 nearest-to-close strikes, both
    // sides -- see LiveOptionAtmPopulator's own distinctStrikes.OrderBy(...).Take(5) loop. Using the
    // day's OWN closing future price as the anchor (same "nearest to bar.ClosePrice" ordering, just
    // evaluated once at the day's last close for this worst-case measurement).
    var pcLastFuturePrice = pcFutureBars[^1].ClosePrice;
    var pcAtmTokens = pcDistinctStrikes.OrderBy(s => Math.Abs(s - pcLastFuturePrice)).Take(5)
        .SelectMany(strike => pcChain.Where(o => o.StrikePrice == strike).Select(o => o.Token))
        .ToList();
    var pcAtmSw = System.Diagnostics.Stopwatch.StartNew();
    var pcAtmTotalRows = 0;
    foreach (var token in pcAtmTokens)
    {
        var series = await OptionQuoteSeries.LoadAsync(pcSource, token, pcDayStart, pcDayEnd, CancellationToken.None);
        pcAtmTotalRows += series.RowCountForDiagnostics;
    }

    pcAtmSw.Stop();
    Console.WriteLine($"ATM-shaped reload: {pcAtmTokens.Count} tokens, {pcAtmTotalRows} total quote-bearing rows read, {pcAtmSw.ElapsedMilliseconds}ms wall-clock (full day-so-far range, worst case = last bar of the day).");
    Console.WriteLine($"BEFORE (from-scratch every poll): combined worst-case single-poll reload cost = {pcMaxPainSw.ElapsedMilliseconds + pcAtmSw.ElapsedMilliseconds}ms against a 10000ms poll budget.");
    Console.WriteLine();

    // AFTER: same two reload shapes, but through LiveOptionSeriesCache warmed up to
    // (dayEnd - 1 poll interval) first -- i.e. simulating "the cache already has everything except
    // the last ~10s of ticks," the real steady-state shape once the Phase G fix is running live. The
    // warm-up call's own cost is excluded from the comparison (it corresponds to every EARLIER poll's
    // own already-amortized cost, not to what THIS poll adds) -- only the final incremental top-up is
    // timed, since that is what actually runs inside the live 10s poll loop after the cache is warm.
    var pcCache = new LiveOptionSeriesCache();
    var pcWarmDayEnd = pcDayEnd.AddSeconds(-10);
    foreach (var token in pcMaxPainTokens)
    {
        await pcCache.GetOiSeriesAsync(pcSource, pcDate, token, pcDayStart, pcWarmDayEnd, CancellationToken.None);
    }

    foreach (var token in pcAtmTokens)
    {
        await pcCache.GetQuoteSeriesAsync(pcSource, pcDate, token, pcDayStart, pcWarmDayEnd, CancellationToken.None);
    }

    var pcMaxPainAfterSw = System.Diagnostics.Stopwatch.StartNew();
    var pcMaxPainAfterRows = 0;
    foreach (var token in pcMaxPainTokens)
    {
        var series = await pcCache.GetOiSeriesAsync(pcSource, pcDate, token, pcDayStart, pcDayEnd, CancellationToken.None);
        pcMaxPainAfterRows += series.RowCountForDiagnostics;
    }

    pcMaxPainAfterSw.Stop();

    var pcAtmAfterSw = System.Diagnostics.Stopwatch.StartNew();
    var pcAtmAfterRows = 0;
    foreach (var token in pcAtmTokens)
    {
        var series = await pcCache.GetQuoteSeriesAsync(pcSource, pcDate, token, pcDayStart, pcDayEnd, CancellationToken.None);
        pcAtmAfterRows += series.RowCountForDiagnostics;
    }

    pcAtmAfterSw.Stop();

    Console.WriteLine($"AFTER (LiveOptionSeriesCache warm, this poll only adds the last ~10s of ticks):");
    Console.WriteLine($"  MaxPain-shaped incremental top-up: {pcMaxPainAfterRows} total rows (same series content), {pcMaxPainAfterSw.ElapsedMilliseconds}ms wall-clock.");
    Console.WriteLine($"  ATM-shaped incremental top-up:     {pcAtmAfterRows} total rows (same series content), {pcAtmAfterSw.ElapsedMilliseconds}ms wall-clock.");
    Console.WriteLine($"  Combined AFTER cost: {pcMaxPainAfterSw.ElapsedMilliseconds + pcAtmAfterSw.ElapsedMilliseconds}ms against a 10000ms poll budget (was {pcMaxPainSw.ElapsedMilliseconds + pcAtmSw.ElapsedMilliseconds}ms).");
    Console.WriteLine($"  Row-count parity check: MaxPain {pcMaxPainTotalRows} (before) vs {pcMaxPainAfterRows} (after) -- {(pcMaxPainTotalRows == pcMaxPainAfterRows ? "MATCH" : "MISMATCH")}. ATM {pcAtmTotalRows} (before) vs {pcAtmAfterRows} (after) -- {(pcAtmTotalRows == pcAtmAfterRows ? "MATCH" : "MISMATCH")}.");

    return 0;
}

// 2026-09-17, perf: lives for the WHOLE process run (not per RunRangeAsync call), so a
// "calibrate" sweep -- which calls RunRangeAsync dozens of times, once per (threshold,
// percentile) combination -- loads each (day, option token)'s real tick-derived price series
// exactly once, however many sweep cells end up touching it, instead of re-scanning raw ticks
// every single cell. See TradeSimulator.SimulateDayAsync's own doc comment on this parameter.
// Declared here (right after both DB options are built, before any command block) so every
// command below can call RunRangeAsync regardless of source order -- its captured variable still
// needs to be definitely-assigned along the actual execution path, not just declared later on.
var sharedOptionPriceCache = new Dictionary<(DateOnly, string), OptionPriceSeries>();

// Shared by "trade", "calibrate", and "session-phase" -- runs one (metric, entryPercentile,
// trendWindowBars, barVolumeThreshold) combination across a date range and returns every trade fired.
async Task<List<VolumeBarTrade>> RunRangeAsync(DateOnly from, DateOnly to, VolumeBarMetric metric, double entryPercentile, int trendWindowBars, long threshold, decimal? stopLossPercent = null, long? rollingSubBarThreshold = null, int depthBandWidth = OptionDepthPopulator.DefaultBandWidth, TimeSpan? optionsSwitchTime = null, TimeSpan? entryWindowStartOverride = null, (double Open, double Mid, double Close)? sessionWeightsOnFutures = null, int? smoothingWindowBars = null, double? minHoldMinutes = null, decimal? minEntryPrice = null, decimal? maxEntryPrice = null, decimal? tp1ProfitPct = null, decimal? tp1Fraction = null, decimal? dailyLossCapPct = null, TimeSpan? entryWindowEndOverride = null, int? rollingWindowDays = null)
{
    var result = new List<VolumeBarTrade>();
    for (var date = from; date <= to; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        result.AddRange(await TradeSimulator.SimulateDayAsync(source, volumeBars, date, threshold, metric, entryPercentile, trendWindowBars, CancellationToken.None, sharedOptionPriceCache, stopLossPercent, rollingSubBarThreshold, depthBandWidth, optionsSwitchTime, entryWindowStartOverride, entryWindowEndOverride, sessionWeightsOnFutures, onBarEvaluated: null, smoothingWindowBars, minHoldMinutes, minEntryPrice, maxEntryPrice, tp1ProfitPct, tp1Fraction, dailyLossCapPct, rollingWindowDays));
    }

    return result;
}

// 2026-09-18, options phase: populates OptionAtmBarRow for an already-populated future date
// range/threshold -- the future's own VolumeBars must exist first (same clock, locked decision).
//   dotnet run --project NiftySignal.VolumeBarData -- populate-options-atm <fromDate> <toDate> [barVolumeThreshold]
if (args.Length > 0 && string.Equals(args[0], "populate-options-atm", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3
        || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var atmFromDate)
        || !DateOnly.TryParseExact(args[2], "yyyy-MM-dd", out var atmToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- populate-options-atm <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [barVolumeThreshold]");
        return 1;
    }

    var atmThreshold = args.Length > 3 ? long.Parse(args[3]) : 1300L;

    for (var date = atmFromDate; date <= atmToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var result = await OptionAtmPopulator.PopulateDayAsync(source, volumeBars, date, atmThreshold, CancellationToken.None);
        Console.WriteLine($"{date:yyyy-MM-dd} @ {atmThreshold}: {result.Outcome} ({result.RowCount} rows)");
    }

    return 0;
}

// 2026-09-18, options phase, Phase 1 metric 2: populates OptionBandFlowBarRow the same way --
// requires the future's own VolumeBars to already exist for the requested date/threshold.
//   dotnet run --project NiftySignal.VolumeBarData -- populate-options-band-flow <fromDate> <toDate> [barVolumeThreshold]
if (args.Length > 0 && string.Equals(args[0], "populate-options-band-flow", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3
        || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var flowFromDate)
        || !DateOnly.TryParseExact(args[2], "yyyy-MM-dd", out var flowToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- populate-options-band-flow <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [barVolumeThreshold] [bandWidth=3, i.e. ATM+/-1; pass 5 for ATM+/-2]");
        return 1;
    }

    var flowThreshold = args.Length > 3 ? long.Parse(args[3]) : 1300L;
    var flowBandWidth = args.Length > 4 ? int.Parse(args[4]) : OptionBandFlowPopulator.DefaultBandWidth;

    for (var date = flowFromDate; date <= flowToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var result = await OptionBandFlowPopulator.PopulateDayAsync(source, volumeBars, date, flowThreshold, CancellationToken.None, flowBandWidth);
        Console.WriteLine($"{date:yyyy-MM-dd} @ {flowThreshold}, band={flowBandWidth}: {result.Outcome} ({result.RowCount} rows)");
    }

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "populate-options-oi", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3
        || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var oiFromDate)
        || !DateOnly.TryParseExact(args[2], "yyyy-MM-dd", out var oiToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- populate-options-oi <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [barVolumeThreshold] [bandWidth=3, i.e. ATM+/-1; pass 5 for ATM+/-2]");
        return 1;
    }

    var oiThreshold = args.Length > 3 ? long.Parse(args[3]) : 1300L;
    var oiBandWidth = args.Length > 4 ? int.Parse(args[4]) : OptionOiPopulator.DefaultBandWidth;

    for (var date = oiFromDate; date <= oiToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var result = await OptionOiPopulator.PopulateDayAsync(source, volumeBars, date, oiThreshold, CancellationToken.None, oiBandWidth);
        Console.WriteLine($"{date:yyyy-MM-dd} @ {oiThreshold}, band={oiBandWidth}: {result.Outcome} ({result.RowCount} rows)");
    }

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "populate-options-skew25delta", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3
        || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var skewFromDate)
        || !DateOnly.TryParseExact(args[2], "yyyy-MM-dd", out var skewToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- populate-options-skew25delta <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [barVolumeThreshold]");
        return 1;
    }

    var skewThreshold = args.Length > 3 ? long.Parse(args[3]) : 1300L;

    for (var date = skewFromDate; date <= skewToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var result = await OptionSkew25DeltaPopulator.PopulateDayAsync(source, volumeBars, date, skewThreshold, CancellationToken.None);
        Console.WriteLine($"{date:yyyy-MM-dd} @ {skewThreshold}: {result.Outcome} ({result.RowCount} rows)");
    }

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "populate-options-maxpain", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3
        || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var mpFromDate)
        || !DateOnly.TryParseExact(args[2], "yyyy-MM-dd", out var mpToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- populate-options-maxpain <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [barVolumeThreshold]");
        return 1;
    }

    var mpThreshold = args.Length > 3 ? long.Parse(args[3]) : 1300L;

    for (var date = mpFromDate; date <= mpToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var result = await OptionMaxPainPopulator.PopulateDayAsync(source, volumeBars, date, mpThreshold, CancellationToken.None);
        Console.WriteLine($"{date:yyyy-MM-dd} @ {mpThreshold}: {result.Outcome} ({result.RowCount} rows)");
    }

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "correlate-options", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3
        || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var corrFromDate)
        || !DateOnly.TryParseExact(args[2], "yyyy-MM-dd", out var corrToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- correlate-options <fromDate> <toDate> [barVolumeThreshold]");
        return 1;
    }

    // 2026-09-20, Phase 3: extended from the original 4-metric version to all 8 confirmed
    // standalone/filter candidates, each computed with its OWN exact locked formula (matching
    // TradeSimulator's own dispatch, not re-derived differently here) -- IV raw and price-signed
    // are two genuinely different series from the same underlying data, kept separate rather than
    // assumed redundant. Depth Imbalance uses ATM±2 (its own adopted band); everything else stays
    // at whatever bandWidth argument is passed (default ATM±1, matching every other locked config).
    var corrThreshold = args.Length > 3 ? long.Parse(args[3]) : 1300L;
    var corrBandWidth = args.Length > 4 ? int.Parse(args[4]) : OptionDepthPopulator.DefaultBandWidth;
    const int depthImbalanceBandWidth = 5; // ATM+/-2, its own adopted config -- independent of corrBandWidth.

    string[] names = ["IvRaw", "IvPriceSigned", "VolDelta", "OiDelta", "SkewChange", "DepthImbalance", "TobDivergence", "MaxPainDist"];
    var series = names.ToDictionary(n => n, _ => new List<double>());
    var dayBreak = new List<(DateOnly Date, int Count)>();

    for (var date = corrFromDate; date <= corrToDate; date = date.AddDays(1))
    {
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var bars = await volumeBars.VolumeBars
            .Where(b => b.AsOfDate == date && b.BarVolumeThreshold == corrThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync();
        if (bars.Count == 0)
        {
            continue;
        }

        var atmByBar = await volumeBars.OptionAtmBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == corrThreshold).ToDictionaryAsync(b => b.BarIndex);
        var flowByBar = await volumeBars.OptionBandFlowBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == corrThreshold && b.BandWidth == corrBandWidth).ToDictionaryAsync(b => b.BarIndex);
        var oiByBar = await volumeBars.OptionOiBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == corrThreshold && b.BandWidth == corrBandWidth).ToDictionaryAsync(b => b.BarIndex);
        var skewByBar = await volumeBars.OptionSkew25DeltaBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == corrThreshold).ToDictionaryAsync(b => b.BarIndex);
        var depthByBar = await volumeBars.OptionDepthBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == corrThreshold && b.BandWidth == depthImbalanceBandWidth).ToDictionaryAsync(b => b.BarIndex);
        var tobDepthByBar = await volumeBars.OptionDepthBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == corrThreshold && b.BandWidth == corrBandWidth).ToDictionaryAsync(b => b.BarIndex);
        var maxPainByBar = await volumeBars.OptionMaxPainBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == corrThreshold).ToDictionaryAsync(b => b.BarIndex);

        decimal? previousClose = null;
        double? previousAtmIv = null;
        double? previousSkewRatio = null;
        var dayCount = 0;

        foreach (var bar in bars)
        {
            double? ivDelta = null;
            if (atmByBar.TryGetValue(bar.BarIndex, out var atmBar) && atmBar.AtmIv is { } curIv && previousAtmIv is { } prevIv)
            {
                ivDelta = curIv - prevIv;
            }

            double? ivRaw = ivDelta;
            double? ivPriceSigned = ivDelta is { } d0 && previousClose is { } prevClose0 ? -Math.Sign(bar.ClosePrice - prevClose0) * d0 : null;
            double? volRaw = flowByBar.TryGetValue(bar.BarIndex, out var flowBar) ? (double)(flowBar.PutNotionalVolume - flowBar.CallNotionalVolume) : null;
            double? oiRaw = oiByBar.TryGetValue(bar.BarIndex, out var oiBar) ? (double)(oiBar.CallOiChangeNotional - oiBar.PutOiChangeNotional) : null;

            double? skewRaw = null;
            if (skewByBar.TryGetValue(bar.BarIndex, out var skewBar) && skewBar.SkewRatio is { } curRatio && previousSkewRatio is { } prevRatio)
            {
                skewRaw = curRatio - prevRatio;
            }

            double? depthRaw = null;
            if (depthByBar.TryGetValue(bar.BarIndex, out var depthBar))
            {
                var bidSum = depthBar.CallBidQtyAvg + depthBar.PutBidQtyAvg;
                var askSum = depthBar.CallAskQtyAvg + depthBar.PutAskQtyAvg;
                if (bidSum is { } bs && askSum is { } aSum && bs + aSum != 0)
                {
                    depthRaw = -(bs - aSum) / (bs + aSum); // negated -- matches the adopted sign flip
                }
            }

            double? tobDivRaw = null;
            if (tobDepthByBar.TryGetValue(bar.BarIndex, out var tobBar))
            {
                var fullBid = tobBar.CallBidQtyAvg + tobBar.PutBidQtyAvg;
                var fullAsk = tobBar.CallAskQtyAvg + tobBar.PutAskQtyAvg;
                var touchBid = tobBar.CallTobBidQtyAvg + tobBar.PutTobBidQtyAvg;
                var touchAsk = tobBar.CallTobAskQtyAvg + tobBar.PutTobAskQtyAvg;
                if (fullBid is { } fb && fullAsk is { } fa && fb + fa != 0 && touchBid is { } tb && touchAsk is { } ta && tb + ta != 0)
                {
                    tobDivRaw = (fb - fa) / (fb + fa) - (tb - ta) / (tb + ta);
                }
            }

            double? maxPainRaw = maxPainByBar.TryGetValue(bar.BarIndex, out var mpBar) && mpBar.MaxPainStrike is { } mp
                ? -(double)(bar.ClosePrice - mp)
                : null;

            if (ivRaw is { } iv && ivPriceSigned is { } ivps && volRaw is { } vol && oiRaw is { } oi && skewRaw is { } sk
                && depthRaw is { } dep && tobDivRaw is { } tdv && maxPainRaw is { } mpr)
            {
                series["IvRaw"].Add(iv);
                series["IvPriceSigned"].Add(ivps);
                series["VolDelta"].Add(vol);
                series["OiDelta"].Add(oi);
                series["SkewChange"].Add(sk);
                series["DepthImbalance"].Add(dep);
                series["TobDivergence"].Add(tdv);
                series["MaxPainDist"].Add(mpr);
                dayCount++;
            }

            previousClose = bar.ClosePrice;
            previousAtmIv = atmByBar.TryGetValue(bar.BarIndex, out var ab) ? ab.AtmIv ?? previousAtmIv : previousAtmIv;
            previousSkewRatio = skewByBar.TryGetValue(bar.BarIndex, out var sb) ? sb.SkewRatio ?? previousSkewRatio : previousSkewRatio;
        }

        dayBreak.Add((date, dayCount));
    }

    static double Pearson(List<double> a, List<double> b)
    {
        var n = a.Count;
        var meanA = a.Average();
        var meanB = b.Average();
        var cov = 0.0;
        var varA = 0.0;
        var varB = 0.0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }

        return varA > 0 && varB > 0 ? cov / Math.Sqrt(varA * varB) : 0.0;
    }

    var totalCount = series[names[0]].Count;
    Console.WriteLine($"=== Correlation, pooled bars with all 8 present: {totalCount} ===");
    Console.WriteLine($"Per-day usable-bar counts: {string.Join(", ", dayBreak.Select(d => $"{d.Date:yyyy-MM-dd}={d.Count}"))}");
    Console.WriteLine();
    Console.Write($"{"",16}");
    foreach (var n in names)
    {
        Console.Write($"{n,15}");
    }

    Console.WriteLine();
    foreach (var a in names)
    {
        Console.Write($"{a,-16}");
        foreach (var b in names)
        {
            Console.Write($"{Pearson(series[a], series[b]),15:F3}");
        }

        Console.WriteLine();
    }

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "session-phase", StringComparison.OrdinalIgnoreCase))
{
    var (spPositional, spNamed) = SplitNamedArgs(args);
    if (spPositional.Length < 6
        || !DateOnly.TryParseExact(spPositional[1], "yyyy-MM-dd", out var spFromDate)
        || !DateOnly.TryParseExact(spPositional[2], "yyyy-MM-dd", out var spToDate)
        || !Enum.TryParse<VolumeBarMetric>(spPositional[3], ignoreCase: true, out var spMetric))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- session-phase <fromDate> <toDate> <metric> <entryPercentile> <barVolumeThreshold> [--exclude=date,date] [--stop=N] [--band=N]");
        return 1;
    }

    var spPercentile = double.Parse(spPositional[4]);
    var spThreshold = long.Parse(spPositional[5]);
    var spExcludeDates = spNamed.TryGetValue("exclude", out var spExcludeStr)
        ? spExcludeStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd")).ToHashSet()
        : [];
    var spStopLossPercent = spNamed.TryGetValue("stop", out var spStopStr) ? (decimal?)(decimal.Parse(spStopStr) / 100m) : null;
    var spBandWidth = spNamed.TryGetValue("band", out var spBandStr) ? int.Parse(spBandStr) : OptionDepthPopulator.DefaultBandWidth;

    var spTrades = new List<VolumeBarTrade>();
    for (var date = spFromDate; date <= spToDate; date = date.AddDays(1))
    {
        if (spExcludeDates.Contains(date))
        {
            continue;
        }

        spTrades.AddRange(await RunRangeAsync(date, date, spMetric, spPercentile, 15, spThreshold, spStopLossPercent, rollingSubBarThreshold: null, depthBandWidth: spBandWidth));
    }

    static TimeSpan Ist(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay;
    var openStart = new TimeSpan(9, 30, 0);
    var openEnd = new TimeSpan(10, 0, 0);
    var midEnd = new TimeSpan(13, 30, 0);
    var closeEnd = new TimeSpan(15, 15, 0);

    string Phase(DateTimeOffset entryTime)
    {
        var t = Ist(entryTime);
        if (t >= openStart && t < openEnd) return "Open  (09:30-10:00)";
        if (t >= openEnd && t < midEnd) return "Mid   (10:00-13:30)";
        return "Close (13:30-15:15)";
    }

    Console.WriteLine($"=== Session-phase split: {spMetric} @ {spThreshold}/{spPercentile}, {spFromDate:yyyy-MM-dd}..{spToDate:yyyy-MM-dd}{(spExcludeDates.Count > 0 ? $", excluding {string.Join(",", spExcludeDates)}" : "")} ===");
    foreach (var phase in new[] { "Open  (09:30-10:00)", "Mid   (10:00-13:30)", "Close (13:30-15:15)" })
    {
        var bucket = spTrades.Where(t => Phase(t.EntryTime) == phase).ToList();
        if (bucket.Count == 0)
        {
            Console.WriteLine($"{phase}: no trades");
            continue;
        }

        var winRate = 100.0 * bucket.Count(t => t.NetPnlPoints > 0) / bucket.Count;
        var net = bucket.Sum(t => t.NetPnlPoints);
        Console.WriteLine($"{phase}: {bucket.Count,4} trades, {winRate,5:F1}% win, net {net,9:F2} pts, {net / bucket.Count,6:F2} pts/trade");
    }

    var totalWin = 100.0 * spTrades.Count(t => t.NetPnlPoints > 0) / Math.Max(1, spTrades.Count);
    var totalNet = spTrades.Sum(t => t.NetPnlPoints);
    Console.WriteLine($"--- Total: {spTrades.Count} trades, {totalWin:F1}% win, net {totalNet:F2} pts ---");

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "populate-options-depth", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3
        || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var pdFromDate)
        || !DateOnly.TryParseExact(args[2], "yyyy-MM-dd", out var pdToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- populate-options-depth <fromDate> <toDate> [barVolumeThreshold] [bandWidth=3, i.e. ATM+/-1; pass 5 for ATM+/-2]");
        return 1;
    }

    var pdThreshold = args.Length > 3 ? long.Parse(args[3]) : 1300L;
    var pdBandWidth = args.Length > 4 ? int.Parse(args[4]) : OptionDepthPopulator.DefaultBandWidth;

    for (var date = pdFromDate; date <= pdToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var result = await OptionDepthPopulator.PopulateDayAsync(source, volumeBars, date, pdThreshold, CancellationToken.None, pdBandWidth);
        Console.WriteLine($"{date:yyyy-MM-dd} @ {pdThreshold}, band={pdBandWidth}: {result.Outcome} ({result.RowCount} rows)");
    }

    return 0;
}

// 2026-09-22, "Depth Imbalance, transparent research" track. Usage:
//   dotnet run --project NiftySignal.VolumeBarData -- populate-call-depth-imbalance <fromDate> <toDate> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put]
if (args.Length > 0 && string.Equals(args[0], "populate-call-depth-imbalance", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3
        || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var cdiFromDate)
        || !DateOnly.TryParseExact(args[2], "yyyy-MM-dd", out var cdiToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- populate-call-depth-imbalance <fromDate> <toDate> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put]");
        return 1;
    }

    var cdiThreshold = args.Length > 3 ? long.Parse(args[3]) : 650L;
    var cdiBandWidth = args.Length > 4 ? int.Parse(args[4]) : DepthImbalanceSumPopulator.DefaultBandWidth;
    var cdiSide = args.Length > 5 ? Enum.Parse<OptionType>(args[5], ignoreCase: true) : OptionType.Call;

    for (var date = cdiFromDate; date <= cdiToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var result = await DepthImbalanceSumPopulator.PopulateDayAsync(source, volumeBars, date, cdiThreshold, cdiSide, CancellationToken.None, cdiBandWidth);
        Console.WriteLine($"{date:yyyy-MM-dd} @ {cdiThreshold}, band={cdiBandWidth}, side={cdiSide}: {result.Outcome} ({result.RowCount} rows)");
    }

    return 0;
}

//   dotnet run --project NiftySignal.VolumeBarData -- call-depth-series <date> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put]
// Dumps every bar's raw diff sum / notional diff sum + future close, for text-chart reporting.
if (args.Length > 0 && string.Equals(args[0], "call-depth-series", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2 || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var csDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- call-depth-series <date> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put]");
        return 1;
    }

    var csThreshold = args.Length > 2 ? long.Parse(args[2]) : 650L;
    var csBandWidth = args.Length > 3 ? int.Parse(args[3]) : DepthImbalanceSumPopulator.DefaultBandWidth;
    var csSide = args.Length > 4 ? Enum.Parse<OptionType>(args[4], ignoreCase: true) : OptionType.Call;

    await using var csDb = new VolumeBarDbContext(volumeBarOptions);
    var csBars = await csDb.DepthImbalanceSumBars
        .Where(b => b.AsOfDate == csDate && b.BarVolumeThreshold == csThreshold && b.BandWidth == csBandWidth && b.Side == csSide)
        .OrderBy(b => b.BarIndex)
        .ToListAsync();

    Console.WriteLine($"BarIndex,EndTimeIst,FutureClose,AtmStrike,DTE,RawQtyDiffSum,NotionalDiffSum");
    foreach (var b in csBars)
    {
        Console.WriteLine($"{b.BarIndex},{FormatIst(b.EndTimestamp)},{b.FutureClosePrice},{b.AtmStrike},{b.DaysToExpiry},{b.RawQtyDiffSum:F0},{b.NotionalDiffSum:F0}");
    }

    return 0;
}

//   dotnet run --project NiftySignal.VolumeBarData -- call-depth-trial <fromDate> <toDate> [barVolumeThreshold=650] [bandWidth=3] [minWindow=5] [maxWindow=30] [side=Call|Put] [--minprice=100] [--maxprice=150]
// Sweeps the rolling-window length (default 5..30) x {raw, notional} over every day in range, sign-only
// entry/exit (see DepthImbalanceTrialSimulator's own doc comment for why no gating is added here).
// 2026-09-22, user's own explicit instruction: default entry price band is now [100,150] -- pass
// --minprice=/--maxprice= to override, or --minprice=0 --maxprice= (empty not allowed; pass a huge
// number) to fall back to pure-ATM (Days 1-3's original, unfiltered behavior).
if (args.Length > 0 && string.Equals(args[0], "call-depth-trial", StringComparison.OrdinalIgnoreCase))
{
    var (cdtPositional, cdtNamed) = SplitNamedArgs(args);
    if (cdtPositional.Length < 3
        || !DateOnly.TryParseExact(cdtPositional[1], "yyyy-MM-dd", out var cdtFromDate)
        || !DateOnly.TryParseExact(cdtPositional[2], "yyyy-MM-dd", out var cdtToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- call-depth-trial <fromDate> <toDate> [barVolumeThreshold=650] [bandWidth=3] [minWindow=5] [maxWindow=30] [side=Call|Put] [--minprice=100] [--maxprice=150]");
        return 1;
    }

    var cdtThreshold = cdtPositional.Length > 3 ? long.Parse(cdtPositional[3]) : 650L;
    var cdtBandWidth = cdtPositional.Length > 4 ? int.Parse(cdtPositional[4]) : DepthImbalanceSumPopulator.DefaultBandWidth;
    var cdtMinWindow = cdtPositional.Length > 5 ? int.Parse(cdtPositional[5]) : 5;
    var cdtMaxWindow = cdtPositional.Length > 6 ? int.Parse(cdtPositional[6]) : 30;
    var cdtSide = cdtPositional.Length > 7 ? Enum.Parse<OptionType>(cdtPositional[7], ignoreCase: true) : OptionType.Call;
    var cdtMinPrice = cdtNamed.TryGetValue("minprice", out var cdtMinPriceStr) ? decimal.Parse(cdtMinPriceStr) : 100m;
    var cdtMaxPrice = cdtNamed.TryGetValue("maxprice", out var cdtMaxPriceStr) ? decimal.Parse(cdtMaxPriceStr) : 150m;

    var cdtDates = new List<DateOnly>();
    for (var date = cdtFromDate; date <= cdtToDate; date = date.AddDays(1))
    {
        cdtDates.Add(date);
    }

    var cdtSharedPriceCache = new Dictionary<(DateOnly, string), OptionPriceSeries>();

    Console.WriteLine($"# Entry price band: [{cdtMinPrice},{cdtMaxPrice}]");
    Console.WriteLine("Day,Side,Variant,Window,Trades,WinRate%,AvgMAE%,AvgMFE%,NetPts,DTE(min-max)");
    var cdtAllTrials = new List<(DateOnly Day, DepthImbalanceTrialSimulator.Trial Trial)>();

    foreach (var date in cdtDates)
    {
        foreach (var useNotional in new[] { false, true })
        {
            for (var w = cdtMinWindow; w <= cdtMaxWindow; w++)
            {
                await using var source = new NiftySignalDbContext(tradeSourceOptions);
                await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
                var trials = await DepthImbalanceTrialSimulator.SimulateDayAsync(
                    source, volumeBars, date, cdtThreshold, cdtBandWidth, cdtSide, w, useNotional, CancellationToken.None, cdtSharedPriceCache, cdtMinPrice, cdtMaxPrice);

                foreach (var t in trials)
                {
                    cdtAllTrials.Add((date, t));
                }

                var variant = useNotional ? "Notional" : "RawQty";
                if (trials.Count == 0)
                {
                    Console.WriteLine($"{date:yyyy-MM-dd},{cdtSide},{variant},{w},0,,,,0.00,");
                    continue;
                }

                var winRate = 100.0 * trials.Count(t => t.Trade.NetPnlPoints > 0) / trials.Count;
                var avgMae = trials.Average(t => t.MaePercent);
                var avgMfe = trials.Average(t => t.MfePercent);
                var net = trials.Sum(t => t.Trade.NetPnlPoints);
                var dteMin = trials.Min(t => t.DaysToExpiry);
                var dteMax = trials.Max(t => t.DaysToExpiry);
                Console.WriteLine($"{date:yyyy-MM-dd},{cdtSide},{variant},{w},{trials.Count},{winRate:F1},{avgMae:F2},{avgMfe:F2},{net:F2},{dteMin}-{dteMax}");
            }
        }
    }

    return 0;
}

//   dotnet run --project NiftySignal.VolumeBarData -- call-depth-detail <date> <window> <variant:raw|notional> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put] [--minprice=100] [--maxprice=150]
// Per-trade detail (entry/exit time+price, MAE/MFE, rolling value at entry) for one day/window/variant.
if (args.Length > 0 && string.Equals(args[0], "call-depth-detail", StringComparison.OrdinalIgnoreCase))
{
    var (cddPositional, cddNamed) = SplitNamedArgs(args);
    if (cddPositional.Length < 4 || !DateOnly.TryParseExact(cddPositional[1], "yyyy-MM-dd", out var cddDate) || !int.TryParse(cddPositional[2], out var cddWindow))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- call-depth-detail <date> <window> <variant:raw|notional> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put] [--minprice=100] [--maxprice=150]");
        return 1;
    }

    var cddUseNotional = string.Equals(cddPositional[3], "notional", StringComparison.OrdinalIgnoreCase);
    var cddThreshold = cddPositional.Length > 4 ? long.Parse(cddPositional[4]) : 650L;
    var cddBandWidth = cddPositional.Length > 5 ? int.Parse(cddPositional[5]) : DepthImbalanceSumPopulator.DefaultBandWidth;
    var cddSide = cddPositional.Length > 6 ? Enum.Parse<OptionType>(cddPositional[6], ignoreCase: true) : OptionType.Call;
    var cddMinPrice = cddNamed.TryGetValue("minprice", out var cddMinPriceStr) ? decimal.Parse(cddMinPriceStr) : 100m;
    var cddMaxPrice = cddNamed.TryGetValue("maxprice", out var cddMaxPriceStr) ? decimal.Parse(cddMaxPriceStr) : 150m;

    await using var cddSource = new NiftySignalDbContext(tradeSourceOptions);
    await using var cddVolumeBars = new VolumeBarDbContext(volumeBarOptions);
    var cddTrials = await DepthImbalanceTrialSimulator.SimulateDayAsync(
        cddSource, cddVolumeBars, cddDate, cddThreshold, cddBandWidth, cddSide, cddWindow, cddUseNotional, CancellationToken.None, sharedPriceCache: null, cddMinPrice, cddMaxPrice);

    Console.WriteLine($"EntryTimeIst,EntryPrice,Strike,ExitTimeIst,ExitPrice,ExitReason,NetPnl,NetPnl%,MAE%,MFE%,RollingAtEntry,DTE");
    foreach (var t in cddTrials)
    {
        Console.WriteLine($"{FormatIst(t.Trade.EntryTime)},{t.Trade.EntryPrice},{t.Trade.StrikePrice},{FormatIst(t.Trade.ExitTime)},{t.Trade.ExitPrice},{t.Trade.ExitReason},{t.Trade.NetPnlPoints:F2},{t.Trade.NetPnlPercent:F1},{t.MaePercent:F1},{t.MfePercent:F1},{t.RollingValueAtEntry:F0},{t.DaysToExpiry}");
    }

    if (cddTrials.Count > 0)
    {
        var win = 100.0 * cddTrials.Count(t => t.Trade.NetPnlPoints > 0) / cddTrials.Count;
        Console.WriteLine($"--- {cddTrials.Count} trades, {win:F1}% win, net {cddTrials.Sum(t => t.Trade.NetPnlPoints):F2} pts, biggest win {cddTrials.Max(t => t.Trade.NetPnlPoints):F2}, biggest loss {cddTrials.Min(t => t.Trade.NetPnlPoints):F2} ---");
    }

    return 0;
}

//   dotnet run --project NiftySignal.VolumeBarData -- depth-spread-trial <fromDate> <toDate> <mode:rollsum|ema> <n> [barVolumeThreshold=650] [bandWidth=3] [--minprice=100] [--maxprice=150]
// 2026-09-22, final scope-limited Call-minus-Put depth spread test (see chat) -- one summary row per day, no N sweep.
if (args.Length > 0 && string.Equals(args[0], "depth-spread-trial", StringComparison.OrdinalIgnoreCase))
{
    var (dstPositional, dstNamed) = SplitNamedArgs(args);
    if (dstPositional.Length < 5
        || !DateOnly.TryParseExact(dstPositional[1], "yyyy-MM-dd", out var dstFromDate)
        || !DateOnly.TryParseExact(dstPositional[2], "yyyy-MM-dd", out var dstToDate)
        || !int.TryParse(dstPositional[4], out var dstN))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- depth-spread-trial <fromDate> <toDate> <mode:rollsum|ema> <n> [barVolumeThreshold=650] [bandWidth=3] [--minprice=100] [--maxprice=150]");
        return 1;
    }

    var dstMode = string.Equals(dstPositional[3], "ema", StringComparison.OrdinalIgnoreCase)
        ? DepthSpreadTrialSimulator.SmoothingMode.Ema
        : DepthSpreadTrialSimulator.SmoothingMode.RollingSum;
    var dstThreshold = dstPositional.Length > 5 ? long.Parse(dstPositional[5]) : 650L;
    var dstBandWidth = dstPositional.Length > 6 ? int.Parse(dstPositional[6]) : DepthImbalanceSumPopulator.DefaultBandWidth;
    var dstMinPrice = dstNamed.TryGetValue("minprice", out var dstMinPriceStr) ? decimal.Parse(dstMinPriceStr) : 100m;
    var dstMaxPrice = dstNamed.TryGetValue("maxprice", out var dstMaxPriceStr) ? decimal.Parse(dstMaxPriceStr) : 150m;

    var dstDates = new List<DateOnly>();
    for (var date = dstFromDate; date <= dstToDate; date = date.AddDays(1))
    {
        dstDates.Add(date);
    }

    var dstSharedPriceCache = new Dictionary<(DateOnly, string), OptionPriceSeries>();
    var dstAllTrials = new List<(DateOnly Day, DepthSpreadTrialSimulator.Trial Trial)>();

    Console.WriteLine($"# Mode={dstMode} N={dstN} PriceBand=[{dstMinPrice},{dstMaxPrice}]");
    Console.WriteLine("Day,Trades,WinRate%,AvgMAE%,AvgMFE%,NetPts,BiggestWin,BiggestLoss,DTE(min-max)");

    foreach (var date in dstDates)
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var trials = await DepthSpreadTrialSimulator.SimulateDayAsync(
            source, volumeBars, date, dstThreshold, dstBandWidth, dstMode, dstN, useNotional: false, CancellationToken.None, dstSharedPriceCache, dstMinPrice, dstMaxPrice);

        foreach (var t in trials)
        {
            dstAllTrials.Add((date, t));
        }

        if (trials.Count == 0)
        {
            Console.WriteLine($"{date:yyyy-MM-dd},0,,,,0.00,,,");
            continue;
        }

        var winRate = 100.0 * trials.Count(t => t.Trade.NetPnlPoints > 0) / trials.Count;
        var avgMae = trials.Average(t => t.MaePercent);
        var avgMfe = trials.Average(t => t.MfePercent);
        var net = trials.Sum(t => t.Trade.NetPnlPoints);
        var dteMin = trials.Min(t => t.DaysToExpiry);
        var dteMax = trials.Max(t => t.DaysToExpiry);
        Console.WriteLine($"{date:yyyy-MM-dd},{trials.Count},{winRate:F1},{avgMae:F2},{avgMfe:F2},{net:F2},{trials.Max(t => t.Trade.NetPnlPoints):F2},{trials.Min(t => t.Trade.NetPnlPoints):F2},{dteMin}-{dteMax}");
    }

    if (dstAllTrials.Count > 0)
    {
        var allWin = 100.0 * dstAllTrials.Count(t => t.Trial.Trade.NetPnlPoints > 0) / dstAllTrials.Count;
        var allNet = dstAllTrials.Sum(t => t.Trial.Trade.NetPnlPoints);
        var daysPositive = dstAllTrials.GroupBy(t => t.Day).Count(g => g.Sum(x => x.Trial.Trade.NetPnlPoints) > 0);
        var totalDays = dstAllTrials.Select(t => t.Day).Distinct().Count();
        Console.WriteLine($"=== TOTAL: {dstAllTrials.Count} trades, {allWin:F1}% win, net {allNet:F2} pts, {daysPositive}/{totalDays} days net positive ===");
    }

    return 0;
}

// 2026-09-22, "Options CVD Approximate, transparent research" track -- same shape as the
// populate-call-depth-imbalance / call-depth-* commands, pointed at CvdProxySumBars. Usage:
//   dotnet run --project NiftySignal.VolumeBarData -- populate-cvd-proxy <fromDate> <toDate> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put]
if (args.Length > 0 && string.Equals(args[0], "populate-cvd-proxy", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3
        || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var pcpFromDate)
        || !DateOnly.TryParseExact(args[2], "yyyy-MM-dd", out var pcpToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- populate-cvd-proxy <fromDate> <toDate> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put]");
        return 1;
    }

    var pcpThreshold = args.Length > 3 ? long.Parse(args[3]) : 650L;
    var pcpBandWidth = args.Length > 4 ? int.Parse(args[4]) : CvdProxySumPopulator.DefaultBandWidth;
    var pcpSide = args.Length > 5 ? Enum.Parse<OptionType>(args[5], ignoreCase: true) : OptionType.Call;

    for (var date = pcpFromDate; date <= pcpToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var result = await CvdProxySumPopulator.PopulateDayAsync(source, volumeBars, date, pcpThreshold, pcpSide, CancellationToken.None, pcpBandWidth);
        Console.WriteLine($"{date:yyyy-MM-dd} @ {pcpThreshold}, band={pcpBandWidth}, side={pcpSide}: {result.Outcome} ({result.RowCount} rows)");
    }

    return 0;
}

//   dotnet run --project NiftySignal.VolumeBarData -- cvd-proxy-series <date> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put]
if (args.Length > 0 && string.Equals(args[0], "cvd-proxy-series", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2 || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var cpsDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- cvd-proxy-series <date> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put]");
        return 1;
    }

    var cpsThreshold = args.Length > 2 ? long.Parse(args[2]) : 650L;
    var cpsBandWidth = args.Length > 3 ? int.Parse(args[3]) : CvdProxySumPopulator.DefaultBandWidth;
    var cpsSide = args.Length > 4 ? Enum.Parse<OptionType>(args[4], ignoreCase: true) : OptionType.Call;

    await using var cpsDb = new VolumeBarDbContext(volumeBarOptions);
    var cpsBars = await cpsDb.CvdProxySumBars
        .Where(b => b.AsOfDate == cpsDate && b.BarVolumeThreshold == cpsThreshold && b.BandWidth == cpsBandWidth && b.Side == cpsSide)
        .OrderBy(b => b.BarIndex)
        .ToListAsync();

    Console.WriteLine($"BarIndex,EndTimeIst,FutureClose,AtmStrike,DTE,RawCvdSum,NotionalCvdSum");
    foreach (var b in cpsBars)
    {
        Console.WriteLine($"{b.BarIndex},{FormatIst(b.EndTimestamp)},{b.FutureClosePrice},{b.AtmStrike},{b.DaysToExpiry},{b.RawCvdSum:F0},{b.NotionalCvdSum:F0}");
    }

    return 0;
}

//   dotnet run --project NiftySignal.VolumeBarData -- cvd-proxy-trial <fromDate> <toDate> [barVolumeThreshold=650] [bandWidth=3] [minWindow=5] [maxWindow=30] [side=Call|Put] [--minprice=100] [--maxprice=150]
if (args.Length > 0 && string.Equals(args[0], "cvd-proxy-trial", StringComparison.OrdinalIgnoreCase))
{
    var (cptPositional, cptNamed) = SplitNamedArgs(args);
    if (cptPositional.Length < 3
        || !DateOnly.TryParseExact(cptPositional[1], "yyyy-MM-dd", out var cptFromDate)
        || !DateOnly.TryParseExact(cptPositional[2], "yyyy-MM-dd", out var cptToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- cvd-proxy-trial <fromDate> <toDate> [barVolumeThreshold=650] [bandWidth=3] [minWindow=5] [maxWindow=30] [side=Call|Put] [--minprice=100] [--maxprice=150]");
        return 1;
    }

    var cptThreshold = cptPositional.Length > 3 ? long.Parse(cptPositional[3]) : 650L;
    var cptBandWidth = cptPositional.Length > 4 ? int.Parse(cptPositional[4]) : CvdProxySumPopulator.DefaultBandWidth;
    var cptMinWindow = cptPositional.Length > 5 ? int.Parse(cptPositional[5]) : 5;
    var cptMaxWindow = cptPositional.Length > 6 ? int.Parse(cptPositional[6]) : 30;
    var cptSide = cptPositional.Length > 7 ? Enum.Parse<OptionType>(cptPositional[7], ignoreCase: true) : OptionType.Call;
    var cptMinPrice = cptNamed.TryGetValue("minprice", out var cptMinPriceStr) ? decimal.Parse(cptMinPriceStr) : 100m;
    var cptMaxPrice = cptNamed.TryGetValue("maxprice", out var cptMaxPriceStr) ? decimal.Parse(cptMaxPriceStr) : 150m;

    var cptDates = new List<DateOnly>();
    for (var date = cptFromDate; date <= cptToDate; date = date.AddDays(1))
    {
        cptDates.Add(date);
    }

    var cptSharedPriceCache = new Dictionary<(DateOnly, string), OptionPriceSeries>();

    Console.WriteLine($"# Entry price band: [{cptMinPrice},{cptMaxPrice}]");
    Console.WriteLine("Day,Side,Variant,Window,Trades,WinRate%,AvgMAE%,AvgMFE%,NetPts,DTE(min-max)");

    foreach (var date in cptDates)
    {
        foreach (var useNotional in new[] { false, true })
        {
            for (var w = cptMinWindow; w <= cptMaxWindow; w++)
            {
                await using var source = new NiftySignalDbContext(tradeSourceOptions);
                await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
                var trials = await CvdProxyTrialSimulator.SimulateDayAsync(
                    source, volumeBars, date, cptThreshold, cptBandWidth, cptSide, w, useNotional, CancellationToken.None, cptSharedPriceCache, cptMinPrice, cptMaxPrice);

                var variant = useNotional ? "Notional" : "RawQty";
                if (trials.Count == 0)
                {
                    Console.WriteLine($"{date:yyyy-MM-dd},{cptSide},{variant},{w},0,,,,0.00,");
                    continue;
                }

                var winRate = 100.0 * trials.Count(t => t.Trade.NetPnlPoints > 0) / trials.Count;
                var avgMae = trials.Average(t => t.MaePercent);
                var avgMfe = trials.Average(t => t.MfePercent);
                var net = trials.Sum(t => t.Trade.NetPnlPoints);
                var dteMin = trials.Min(t => t.DaysToExpiry);
                var dteMax = trials.Max(t => t.DaysToExpiry);
                Console.WriteLine($"{date:yyyy-MM-dd},{cptSide},{variant},{w},{trials.Count},{winRate:F1},{avgMae:F2},{avgMfe:F2},{net:F2},{dteMin}-{dteMax}");
            }
        }
    }

    return 0;
}

//   dotnet run --project NiftySignal.VolumeBarData -- cvd-proxy-detail <date> <window> <variant:raw|notional> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put] [--minprice=100] [--maxprice=150]
if (args.Length > 0 && string.Equals(args[0], "cvd-proxy-detail", StringComparison.OrdinalIgnoreCase))
{
    var (cpdPositional, cpdNamed) = SplitNamedArgs(args);
    if (cpdPositional.Length < 4 || !DateOnly.TryParseExact(cpdPositional[1], "yyyy-MM-dd", out var cpdDate) || !int.TryParse(cpdPositional[2], out var cpdWindow))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- cvd-proxy-detail <date> <window> <variant:raw|notional> [barVolumeThreshold=650] [bandWidth=3] [side=Call|Put] [--minprice=100] [--maxprice=150]");
        return 1;
    }

    var cpdUseNotional = string.Equals(cpdPositional[3], "notional", StringComparison.OrdinalIgnoreCase);
    var cpdThreshold = cpdPositional.Length > 4 ? long.Parse(cpdPositional[4]) : 650L;
    var cpdBandWidth = cpdPositional.Length > 5 ? int.Parse(cpdPositional[5]) : CvdProxySumPopulator.DefaultBandWidth;
    var cpdSide = cpdPositional.Length > 6 ? Enum.Parse<OptionType>(cpdPositional[6], ignoreCase: true) : OptionType.Call;
    var cpdMinPrice = cpdNamed.TryGetValue("minprice", out var cpdMinPriceStr) ? decimal.Parse(cpdMinPriceStr) : 100m;
    var cpdMaxPrice = cpdNamed.TryGetValue("maxprice", out var cpdMaxPriceStr) ? decimal.Parse(cpdMaxPriceStr) : 150m;

    await using var cpdSource = new NiftySignalDbContext(tradeSourceOptions);
    await using var cpdVolumeBars = new VolumeBarDbContext(volumeBarOptions);
    var cpdTrials = await CvdProxyTrialSimulator.SimulateDayAsync(
        cpdSource, cpdVolumeBars, cpdDate, cpdThreshold, cpdBandWidth, cpdSide, cpdWindow, cpdUseNotional, CancellationToken.None, sharedPriceCache: null, cpdMinPrice, cpdMaxPrice);

    Console.WriteLine($"EntryTimeIst,EntryPrice,Strike,ExitTimeIst,ExitPrice,ExitReason,NetPnl,NetPnl%,MAE%,MFE%,RollingAtEntry,DTE");
    foreach (var t in cpdTrials)
    {
        Console.WriteLine($"{FormatIst(t.Trade.EntryTime)},{t.Trade.EntryPrice},{t.Trade.StrikePrice},{FormatIst(t.Trade.ExitTime)},{t.Trade.ExitPrice},{t.Trade.ExitReason},{t.Trade.NetPnlPoints:F2},{t.Trade.NetPnlPercent:F1},{t.MaePercent:F1},{t.MfePercent:F1},{t.RollingValueAtEntry:F0},{t.DaysToExpiry}");
    }

    if (cpdTrials.Count > 0)
    {
        var win = 100.0 * cpdTrials.Count(t => t.Trade.NetPnlPoints > 0) / cpdTrials.Count;
        Console.WriteLine($"--- {cpdTrials.Count} trades, {win:F1}% win, net {cpdTrials.Sum(t => t.Trade.NetPnlPoints):F2} pts, biggest win {cpdTrials.Max(t => t.Trade.NetPnlPoints):F2}, biggest loss {cpdTrials.Min(t => t.Trade.NetPnlPoints):F2} ---");
    }

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "crossover", StringComparison.OrdinalIgnoreCase))
{
    var (coPositional, coNamed) = SplitNamedArgs(args);
    if (coPositional.Length < 3
        || !DateOnly.TryParseExact(coPositional[1], "yyyy-MM-dd", out var coFromDate)
        || !DateOnly.TryParseExact(coPositional[2], "yyyy-MM-dd", out var coToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- crossover <fromDate> <toDate> [fastBars=4] [slowBars=12] [thresholdPoints=2] [barVolumeThreshold=2600] [--metric=SessionGatedDepthDurationConfirmed|OptionsScoreThreeWaySwitchMaxPainConfirmed] [--band=5]");
        return 1;
    }

    var coFastBars = coPositional.Length > 3 ? int.Parse(coPositional[3]) : 4;
    var coSlowBars = coPositional.Length > 4 ? int.Parse(coPositional[4]) : 12;
    var coThreshold = coPositional.Length > 5 ? double.Parse(coPositional[5]) : 2.0;
    var coBarVolumeThreshold = coPositional.Length > 6 ? long.Parse(coPositional[6]) : 2600L;
    var coScoreMetric = coNamed.TryGetValue("metric", out var coMetricStr)
        ? Enum.Parse<VolumeBarMetric>(coMetricStr, ignoreCase: true)
        : VolumeBarMetric.SessionGatedDepthDurationConfirmed;
    var coBandWidth = coNamed.TryGetValue("band", out var coBandStr) ? int.Parse(coBandStr) : 5;
    var coMinEntryPrice = coNamed.TryGetValue("minprice", out var coMinPriceStr) ? (decimal?)decimal.Parse(coMinPriceStr) : null;
    var coMaxEntryPrice = coNamed.TryGetValue("maxprice", out var coMaxPriceStr) ? (decimal?)decimal.Parse(coMaxPriceStr) : null;
    // 2026-09-21, same 4 risk-rule flags as "trade" -- see that command's own comment for the
    // meaning of each. --stop= was previously missing from this command entirely (only "trade"
    // had it); added here following the exact same pattern/convention.
    var coStopLossPercent = coNamed.TryGetValue("stop", out var coStopStr) ? (decimal?)(decimal.Parse(coStopStr) / 100m) : null;
    var coTp1ProfitPct = coNamed.TryGetValue("tp1pct", out var coTp1PctStr) ? (decimal?)(decimal.Parse(coTp1PctStr) / 100m) : null;
    var coTp1Fraction = coNamed.TryGetValue("tp1frac", out var coTp1FracStr) ? (decimal?)(decimal.Parse(coTp1FracStr) / 100m) : null;
    var coDailyLossCapPct = coNamed.TryGetValue("dailyloss", out var coDailyLossStr) ? (decimal?)decimal.Parse(coDailyLossStr) : null;

    Console.WriteLine($"=== Crossover simulation: metric={coScoreMetric} fast={coFastBars} slow={coSlowBars} thresholdPoints={coThreshold} barThreshold={coBarVolumeThreshold}{(coMinEntryPrice is not null || coMaxEntryPrice is not null ? $", entryPriceBand=[{coMinEntryPrice?.ToString() ?? "-inf"},{coMaxEntryPrice?.ToString() ?? "+inf"}]" : "")}{(coStopLossPercent is { } coSlp ? $", stopLoss={coSlp:P0}" : "")}{(coTp1ProfitPct is { } coTp1p ? $", tp1={coTp1p:P0}@{coTp1Fraction:P0}" : "")}{(coDailyLossCapPct is { } coDlc ? $", dailyLossCap={coDlc}%" : "")} ===");
    var coAllTrades = new List<VolumeBarTrade>();
    for (var date = coFromDate; date <= coToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var dayTrades = await TradeSimulator.SimulateCrossoverDayAsync(source, volumeBars, date, coBarVolumeThreshold, coFastBars, coSlowBars, coThreshold, CancellationToken.None, sharedOptionPriceCache, coScoreMetric, coBandWidth, optionsSwitchTime: null, coMinEntryPrice, coMaxEntryPrice, coStopLossPercent, coTp1ProfitPct, coTp1Fraction, coDailyLossCapPct);
        if (dayTrades.Count == 0)
        {
            continue;
        }

        coAllTrades.AddRange(dayTrades);
        var dayWinRate = 100.0 * dayTrades.Count(t => t.NetPnlPoints > 0) / dayTrades.Count;
        var dayNet = dayTrades.Sum(t => t.NetPnlPoints);
        Console.WriteLine($"{date:yyyy-MM-dd}: {dayTrades.Count} trades, {dayWinRate:F1}% win rate, net {dayNet:F2} pts");
        foreach (var t in dayTrades)
        {
            var side = t.Side == OptionType.Call ? "Call" : "Put";
            Console.WriteLine($"    {FormatIst(t.EntryTime)} Long {side}@{t.StrikePrice} entry={t.EntryPrice:F2} diff={t.EntryScore:F1} -> {FormatIst(t.ExitTime)} exit={t.ExitPrice:F2} ({t.ExitReason}) netPnl={t.NetPnlPoints:F2} ({t.NetPnlPercent:F1}%)");
        }
    }

    if (coAllTrades.Count > 0)
    {
        var winRate = 100.0 * coAllTrades.Count(t => t.NetPnlPoints > 0) / coAllTrades.Count;
        var net = coAllTrades.Sum(t => t.NetPnlPoints);
        Console.WriteLine($"--- Crossover total: {coAllTrades.Count} trades, {winRate:F1}% win rate, net {net:F2} pts ---");
    }
    else
    {
        Console.WriteLine("No trades fired across the requested range.");
    }

    return 0;
}

// 2026-09-22, option-price moving-average-crossover research task (docs/VOLUME_BAR_FINDINGS.md's
// dated section) -- calls the wholly separate TradeSimulator.SimulatePriceCrossoverDayAsync, never
// TradeSimulator.SimulateCrossoverDayAsync's own dispatch chain.
//   dotnet run --project NiftySignal.VolumeBarData -- price-crossover <fromDate> <toDate> <side:Call|Put|DualAgreement|DualAgreementBothExit> [fastBars=3] [slowBars=10] [thresholdPct=1.0] [barVolumeThreshold=2600] [--band=3|5] [--bandmode=Symmetric|ItmSide|OtmSide] [--putfast=] [--putslow=] [--fasttype=Sma|Ema] [--slowtype=Sma|Ema] [--minprice=] [--maxprice=]
if (args.Length > 0 && string.Equals(args[0], "price-crossover", StringComparison.OrdinalIgnoreCase))
{
    var (pcPositional, pcNamed) = SplitNamedArgs(args);
    if (pcPositional.Length < 4
        || !DateOnly.TryParseExact(pcPositional[1], "yyyy-MM-dd", out var pcFromDate)
        || !DateOnly.TryParseExact(pcPositional[2], "yyyy-MM-dd", out var pcToDate)
        || !Enum.TryParse<PriceCrossoverSide>(pcPositional[3], ignoreCase: true, out var pcSide))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- price-crossover <fromDate> <toDate> <side:Call|Put|DualAgreement|DualAgreementBothExit> [fastBars=3] [slowBars=10] [thresholdPct=1.0] [barVolumeThreshold=2600] [--band=3|5] [--bandmode=Symmetric|ItmSide|OtmSide] [--putfast=] [--putslow=] [--fasttype=Sma|Ema] [--slowtype=Sma|Ema] [--minprice=] [--maxprice=]");
        return 1;
    }

    var pcFastBars = pcPositional.Length > 4 ? int.Parse(pcPositional[4]) : 3;
    var pcSlowBars = pcPositional.Length > 5 ? int.Parse(pcPositional[5]) : 10;
    var pcThresholdPct = pcPositional.Length > 6 ? double.Parse(pcPositional[6]) : 1.0;
    var pcBarVolumeThreshold = pcPositional.Length > 7 ? long.Parse(pcPositional[7]) : 2600L;
    var pcBandWidth = pcNamed.TryGetValue("band", out var pcBandStr) ? (int?)int.Parse(pcBandStr) : null;
    var pcBandComposition = pcNamed.TryGetValue("bandmode", out var pcBandModeStr) ? Enum.Parse<PriceBandComposition>(pcBandModeStr, ignoreCase: true) : PriceBandComposition.Symmetric;
    var pcPutFastBars = pcNamed.TryGetValue("putfast", out var pcPutFastStr) ? (int?)int.Parse(pcPutFastStr) : null;
    var pcPutSlowBars = pcNamed.TryGetValue("putslow", out var pcPutSlowStr) ? (int?)int.Parse(pcPutSlowStr) : null;
    var pcFastType = pcNamed.TryGetValue("fasttype", out var pcFastTypeStr) ? Enum.Parse<MaType>(pcFastTypeStr, ignoreCase: true) : MaType.Sma;
    var pcSlowType = pcNamed.TryGetValue("slowtype", out var pcSlowTypeStr) ? Enum.Parse<MaType>(pcSlowTypeStr, ignoreCase: true) : MaType.Sma;
    var pcMinPrice = pcNamed.TryGetValue("minprice", out var pcMinPriceStr) ? (decimal?)decimal.Parse(pcMinPriceStr) : null;
    var pcMaxPrice = pcNamed.TryGetValue("maxprice", out var pcMaxPriceStr) ? (decimal?)decimal.Parse(pcMaxPriceStr) : null;
    var pcEntryStart = pcNamed.TryGetValue("entrystart", out var pcEntryStartStr) ? (TimeSpan?)TimeSpan.Parse(pcEntryStartStr) : null;
    var pcEntryEnd = pcNamed.TryGetValue("entryend", out var pcEntryEndStr) ? (TimeSpan?)TimeSpan.Parse(pcEntryEndStr) : null;

    Console.WriteLine($"=== Price-crossover simulation: side={pcSide} fast={pcFastBars}({pcFastType}) slow={pcSlowBars}({pcSlowType}) thresholdPct={pcThresholdPct} barThreshold={pcBarVolumeThreshold}{(pcBandWidth is { } pcbw ? $", band={pcbw}({pcBandComposition})" : ", single ATM strike")}{(pcPutFastBars is not null || pcPutSlowBars is not null ? $", putWindows={pcPutFastBars ?? pcFastBars}/{pcPutSlowBars ?? pcSlowBars}" : "")}{(pcMinPrice is not null || pcMaxPrice is not null ? $", entryPriceBand=[{pcMinPrice?.ToString() ?? "-inf"},{pcMaxPrice?.ToString() ?? "+inf"}]" : "")}{(pcEntryStart is not null || pcEntryEnd is not null ? $", entryWindow=[{pcEntryStart?.ToString() ?? "09:30:00"},{pcEntryEnd?.ToString() ?? "15:00:00"}]" : "")} ===");
    var pcAllTrades = new List<VolumeBarTrade>();
    for (var date = pcFromDate; date <= pcToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var dayTrades = await TradeSimulator.SimulatePriceCrossoverDayAsync(source, volumeBars, date, pcBarVolumeThreshold, pcSide, pcFastBars, pcSlowBars, pcThresholdPct / 100.0, CancellationToken.None, sharedOptionPriceCache, pcBandWidth, pcPutFastBars, pcPutSlowBars, pcFastType, pcSlowType, pcMinPrice, pcMaxPrice, pcBandComposition, pcEntryStart, pcEntryEnd);
        if (dayTrades.Count == 0)
        {
            continue;
        }

        pcAllTrades.AddRange(dayTrades);
        var dayWinRate = 100.0 * dayTrades.Count(t => t.NetPnlPoints > 0) / dayTrades.Count;
        var dayNet = dayTrades.Sum(t => t.NetPnlPoints);
        Console.WriteLine($"{date:yyyy-MM-dd}: {dayTrades.Count} trades, {dayWinRate:F1}% win rate, net {dayNet:F2} pts");
        foreach (var t in dayTrades)
        {
            var tside = t.Side == OptionType.Call ? "Call" : "Put";
            Console.WriteLine($"    {FormatIst(t.EntryTime)} Long {tside}@{t.StrikePrice} entry={t.EntryPrice:F2} diag={t.EntryScore:F2} -> {FormatIst(t.ExitTime)} exit={t.ExitPrice:F2} ({t.ExitReason}) netPnl={t.NetPnlPoints:F2} ({t.NetPnlPercent:F1}%)");
        }
    }

    if (pcAllTrades.Count > 0)
    {
        var winRate = 100.0 * pcAllTrades.Count(t => t.NetPnlPoints > 0) / pcAllTrades.Count;
        var net = pcAllTrades.Sum(t => t.NetPnlPoints);
        Console.WriteLine($"--- Price-crossover total: {pcAllTrades.Count} trades, {winRate:F1}% win rate, net {net:F2} pts ---");
    }
    else
    {
        Console.WriteLine("No trades fired across the requested range.");
    }

    return 0;
}

// "price-crossover-calibrate" -- 2026-09-22 exhaustive fast/slow (/threshold) grid search for
// PriceCrossoverCalculator, following the exact same one-process/internal-loop pattern as
// "crossover-calibrate" above (never shells out to a fresh `dotnet run` per combo -- that would be
// prohibitively slow for a grid this size). Adds a DTE split (0-DTE vs non-0-DTE vs pooled) on
// EVERY cell, per the user's explicit request that 0-DTE and non-0-DTE days may want genuinely
// different windows -- each cell runs three separate SimulatePriceCrossoverDayAsync passes (one
// per date subset), not a post-hoc filter of one pooled run, so there's no look-ahead/warm-up
// leakage across the DTE boundary (same discipline the DTE-conditioned bar-size investigation in
// docs/VOLUME_BAR_FINDINGS.md already established).
if (args.Length > 0 && string.Equals(args[0], "price-crossover-calibrate", StringComparison.OrdinalIgnoreCase))
{
    var (pccPositional, pccNamed) = SplitNamedArgs(args);
    if (pccPositional.Length < 4
        || !DateOnly.TryParseExact(pccPositional[1], "yyyy-MM-dd", out var pccFromDate)
        || !DateOnly.TryParseExact(pccPositional[2], "yyyy-MM-dd", out var pccToDate)
        || !Enum.TryParse<PriceCrossoverSide>(pccPositional[3], ignoreCase: true, out var pccSide))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- price-crossover-calibrate <fromDate> <toDate> <side:Call|Put|DualAgreement|DualAgreementBothExit> [fastList=2,3,4,5,6,7,8,9,10] [slowList=10,15,20,25,30,35,40,45,50] [thresholdList=5] [barVolumeThreshold=650] [--band=3|5] [--bandmode=Symmetric|ItmSide|OtmSide] [--zerodte=2026-09-08,2026-09-15] [--targetmin=5] [--targetmax=15] [--fasttype=Sma|Ema] [--slowtype=Sma|Ema] [--minprice=] [--maxprice=] [--entrystart=] [--entryend=] [--minsample=10] [--notop5]");
        return 1;
    }

    var pccFastOptions = pccPositional.Length > 4 ? pccPositional[4].Split(',').Select(int.Parse).ToArray() : [2, 3, 4, 5, 6, 7, 8, 9, 10];
    var pccSlowOptions = pccPositional.Length > 5 ? pccPositional[5].Split(',').Select(int.Parse).ToArray() : [10, 15, 20, 25, 30, 35, 40, 45, 50];
    var pccThresholdOptions = pccPositional.Length > 6 ? pccPositional[6].Split(',').Select(double.Parse).ToArray() : [5.0];
    var pccBarVolumeThreshold = pccPositional.Length > 7 ? long.Parse(pccPositional[7]) : 650L;
    var pccBandWidth = pccNamed.TryGetValue("band", out var pccBandStr) ? (int?)int.Parse(pccBandStr) : null;
    var pccBandComposition = pccNamed.TryGetValue("bandmode", out var pccBandModeStr) ? Enum.Parse<PriceBandComposition>(pccBandModeStr, ignoreCase: true) : PriceBandComposition.Symmetric;
    var pccFastType = pccNamed.TryGetValue("fasttype", out var pccFastTypeStr) ? Enum.Parse<MaType>(pccFastTypeStr, ignoreCase: true) : MaType.Sma;
    var pccSlowType = pccNamed.TryGetValue("slowtype", out var pccSlowTypeStr) ? Enum.Parse<MaType>(pccSlowTypeStr, ignoreCase: true) : MaType.Sma;
    var pccMinPrice = pccNamed.TryGetValue("minprice", out var pccMinPriceStr) ? (decimal?)decimal.Parse(pccMinPriceStr) : null;
    var pccMaxPrice = pccNamed.TryGetValue("maxprice", out var pccMaxPriceStr) ? (decimal?)decimal.Parse(pccMaxPriceStr) : null;
    var pccEntryStart = pccNamed.TryGetValue("entrystart", out var pccEntryStartStr) ? (TimeSpan?)TimeSpan.Parse(pccEntryStartStr) : null;
    var pccEntryEnd = pccNamed.TryGetValue("entryend", out var pccEntryEndStr) ? (TimeSpan?)TimeSpan.Parse(pccEntryEndStr) : null;
    var pccZeroDteDates = (pccNamed.TryGetValue("zerodte", out var pccZeroDteStr)
        ? pccZeroDteStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd"))
        : [DateOnly.Parse("2026-09-08"), DateOnly.Parse("2026-09-15")]).ToHashSet();
    // 2026-09-23, user's own explicit standing target: "5 to 15 trades per day... ideal for
    // simulation and live trading" -- replaces the previous 5-10 default (still overridable).
    var pccTargetMin = pccNamed.TryGetValue("targetmin", out var pccTargetMinStr) ? double.Parse(pccTargetMinStr) : 5.0;
    var pccTargetMax = pccNamed.TryGetValue("targetmax", out var pccTargetMaxStr) ? double.Parse(pccTargetMaxStr) : 15.0;
    // 2026-09-23, user's own explicit request: every sweep must surface top-5-by-metric tables
    // (win rate, MFE, MAE, net), not just the raw grid -- --notop5 skips this (e.g. for a huge
    // grid where the caller only wants the raw numbers, or is scripting further analysis).
    var pccMinSample = pccNamed.TryGetValue("minsample", out var pccMinSampleStr) ? int.Parse(pccMinSampleStr) : 10;
    var pccShowTop5 = !pccNamed.ContainsKey("notop5");

    // Real trading days present in range, split by DTE regime, for trades/day denominators --
    // same "don't divide by the padded calendar span" discipline "calibrate" uses above.
    var pccAllDates = new List<DateOnly>();
    for (var d = pccFromDate; d <= pccToDate; d = d.AddDays(1))
    {
        await using var pccDayCheckDb = new VolumeBarDbContext(volumeBarOptions);
        if (await pccDayCheckDb.VolumeBars.AnyAsync(b => b.AsOfDate == d && b.BarVolumeThreshold == pccBarVolumeThreshold))
        {
            pccAllDates.Add(d);
        }
    }

    var pccZeroDays = pccAllDates.Where(d => pccZeroDteDates.Contains(d)).ToList();
    var pccNonZeroDays = pccAllDates.Where(d => !pccZeroDteDates.Contains(d)).ToList();

    Console.WriteLine($"=== Price-crossover calibration sweep: side={pccSide}, maType=fast:{pccFastType}/slow:{pccSlowType}, barThreshold={pccBarVolumeThreshold}{(pccBandWidth is { } pccbw ? $", band={pccbw}({pccBandComposition})" : ", single ATM strike")}{(pccMinPrice is not null || pccMaxPrice is not null ? $", entryPriceBand=[{pccMinPrice?.ToString() ?? "-inf"},{pccMaxPrice?.ToString() ?? "+inf"}]" : "")}, {pccFromDate:yyyy-MM-dd}..{pccToDate:yyyy-MM-dd}, {pccAllDates.Count} trading days ({pccZeroDays.Count} 0-DTE, {pccNonZeroDays.Count} non-0-DTE), target {pccTargetMin}-{pccTargetMax} trades/day (pooled) ===");
    // 2026-09-23, user's own explicit complaint: a day COUNT ("2 0-DTE days") is not enough to
    // verify what was actually simulated -- the exact dates must always be stated, not just a count.
    Console.WriteLine($"    0-DTE dates:     {(pccZeroDays.Count > 0 ? string.Join(", ", pccZeroDays.Select(d => d.ToString("yyyy-MM-dd"))) : "(none in range)")}");
    Console.WriteLine($"    Non-0-DTE dates: {(pccNonZeroDays.Count > 0 ? string.Join(", ", pccNonZeroDays.Select(d => d.ToString("yyyy-MM-dd"))) : "(none in range)")}");
    // 2026-09-23: every "Trades" count below is POOLED (summed) across every day in that column's
    // group, never a single day's own count -- the (/day=X.X) figure alongside each is that
    // pooled count divided by the number of days in the group, shown so per-day and pooled are
    // never ambiguous side by side.
    Console.WriteLine($"    All trade/net figures below are POOLED across every day in the named group (Pooled={pccAllDates.Count} days, 0DTE={pccZeroDays.Count} days, Non0DTE={pccNonZeroDays.Count} days) -- (/day=X.X) is that pooled count divided by the day count.");
    Console.WriteLine($"{"Fast",5} | {"Slow",5} | {"Thr%",5} || {"PooledTrades",12} {"Win%",6} {"PooledNet",9} || {"0DTETrades",10} {"Win%",6} {"0DTENet",9} || {"N0DTETrades",11} {"Win%",6} {"N0DTENet",9} |In target?");

    // One SimulatePriceCrossoverDayAsync call per date (not per DTE-subset) -- a day's own trades
    // are the same regardless of which subset we're about to bucket them into, so running each
    // date once and tagging its trades by DTE afterward is equivalent to (and 2x cheaper than)
    // re-running the zero/non-zero subsets from scratch, while still being a genuinely separate
    // per-day simulation (no cross-day state), so there's no look-ahead/warm-up leakage introduced
    // by the DTE split.
    async Task<List<VolumeBarTrade>> RunDayAsync(DateOnly date, int fast, int slow, double thresholdPct)
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        return await TradeSimulator.SimulatePriceCrossoverDayAsync(source, volumeBars, date, pccBarVolumeThreshold, pccSide, fast, slow, thresholdPct / 100.0, CancellationToken.None, sharedOptionPriceCache, pccBandWidth, null, null, pccFastType, pccSlowType, pccMinPrice, pccMaxPrice, pccBandComposition, pccEntryStart, pccEntryEnd);
    }

    static (int Count, double WinPct, decimal Net) Summarize(List<VolumeBarTrade> trades)
        => (trades.Count, trades.Count > 0 ? 100.0 * trades.Count(t => t.NetPnlPoints > 0) / trades.Count : 0.0, trades.Sum(t => t.NetPnlPoints));

    // 2026-09-23: one row per (fast, slow, threshold) cell, kept for the top-5-by-metric summary
    // at the end -- cheap (win rate/net only, already computed for the printed grid), MAE/MFE
    // deliberately NOT computed per-cell here (would require per-trade tick loading for every
    // trade of every one of potentially thousands of cells -- prohibitively expensive). Instead,
    // MAE/MFE is computed only for the handful of cells that make a top-5-by-winrate-or-net list,
    // by re-running just those cells (cheap -- the option price series are already cached).
    var pccCellRows = new List<(int Fast, int Slow, double Threshold,
        (int Count, double WinPct, decimal Net) Pooled, (int Count, double WinPct, decimal Net) ZeroDte, (int Count, double WinPct, decimal Net) NonZeroDte)>();

    // 2026-09-23: when the caller asks for exactly ONE (fast, slow, threshold) combination (not a
    // grid), print every individual trade as it's simulated, not just the aggregate row -- a single
    // specific configuration is being reviewed in detail, not scanned for a winner, so full
    // trade-by-trade transparency (date, DTE tag, entry/exit time, strike, prices, exit reason,
    // net P&L) belongs in the console, not just the summary numbers.
    var pccIsSingleCell = pccFastOptions.Length == 1 && pccSlowOptions.Length == 1 && pccThresholdOptions.Length == 1;

    foreach (var fast in pccFastOptions)
    {
        foreach (var slow in pccSlowOptions)
        {
            if (fast >= slow)
            {
                continue;
            }

            foreach (var threshold in pccThresholdOptions)
            {
                var pooledTrades = new List<VolumeBarTrade>();
                var zeroDteTrades = new List<VolumeBarTrade>();
                var nonZeroDteTrades = new List<VolumeBarTrade>();
                if (pccIsSingleCell)
                {
                    Console.WriteLine($"=== Trade-by-trade detail: side={pccSide} fast={fast} slow={slow} thr={threshold}% barThreshold={pccBarVolumeThreshold} ===");
                }
                foreach (var date in pccAllDates)
                {
                    var dayTrades = await RunDayAsync(date, fast, slow, threshold);
                    pooledTrades.AddRange(dayTrades);
                    var isZeroDteDay = pccZeroDteDates.Contains(date);
                    (isZeroDteDay ? zeroDteTrades : nonZeroDteTrades).AddRange(dayTrades);

                    if (pccIsSingleCell)
                    {
                        var dayLabel = isZeroDteDay ? "0-DTE" : "non-0-DTE";
                        if (dayTrades.Count == 0)
                        {
                            Console.WriteLine($"  {date:yyyy-MM-dd} ({dayLabel}): 0 trades");
                            continue;
                        }
                        var dayWinPct = 100.0 * dayTrades.Count(t => t.NetPnlPoints > 0) / dayTrades.Count;
                        var dayNet = dayTrades.Sum(t => t.NetPnlPoints);
                        Console.WriteLine($"  {date:yyyy-MM-dd} ({dayLabel}): {dayTrades.Count} trades, {dayWinPct:F1}% win, net {dayNet:F2} pts");
                        foreach (var t in dayTrades)
                        {
                            var tside = t.Side == OptionType.Call ? "Call" : "Put";
                            Console.WriteLine($"      {FormatIst(t.EntryTime)} Long {tside}@{t.StrikePrice} entry={t.EntryPrice:F2} -> {FormatIst(t.ExitTime)} exit={t.ExitPrice:F2} ({t.ExitReason}) netPnl={t.NetPnlPoints:F2} ({t.NetPnlPercent:F1}%)");
                        }
                    }
                }
                if (pccIsSingleCell)
                {
                    Console.WriteLine();
                }

                var pooled = Summarize(pooledTrades);
                var zeroDte = Summarize(zeroDteTrades);
                var nonZeroDte = Summarize(nonZeroDteTrades);
                pccCellRows.Add((fast, slow, threshold, pooled, zeroDte, nonZeroDte));

                var tradesPerDay = pccAllDates.Count > 0 ? pooled.Count / (double)pccAllDates.Count : 0;
                var inTarget = tradesPerDay >= pccTargetMin && tradesPerDay <= pccTargetMax;

                // 2026-09-22 addition: target computed against 0-DTE trades/0-DTE-day specifically
                // (not the pooled all-days rate above) -- the user's own explicit request ("bring in
                // between 10 to 15 trades for 0 DTE days first"). Additive only, pooled target flag
                // above is unchanged for any other caller relying on it.
                var tradesPerZeroDteDay = pccZeroDays.Count > 0 ? zeroDte.Count / (double)pccZeroDays.Count : 0;
                var zeroDteInTarget = pccZeroDays.Count > 0 && tradesPerZeroDteDay >= pccTargetMin && tradesPerZeroDteDay <= pccTargetMax;

                // Same additive pattern, mirrored for non-0-DTE days specifically (user's follow-up
                // request: "calibrating between 7 to 10 trades per day" for non-0-DTE days only).
                var tradesPerNonZeroDteDay = pccNonZeroDays.Count > 0 ? nonZeroDte.Count / (double)pccNonZeroDays.Count : 0;
                var nonZeroDteInTarget = pccNonZeroDays.Count > 0 && tradesPerNonZeroDteDay >= pccTargetMin && tradesPerNonZeroDteDay <= pccTargetMax;

                // 2026-09-23 bugfix: was {threshold,5:F0} -- rounded fractional thresholds (e.g.
                // 0.5, 1.5, 2.5) to the nearest whole number in the DISPLAY ONLY (the actual
                // simulation always used the exact double value), silently making two different
                // threshold cells print with the same misleading label. F2 shows the true value.
                Console.WriteLine($"{fast,5} | {slow,5} | {threshold,6:F2} || {pooled.Count,12} {pooled.WinPct,5:F1}% {pooled.Net,9:F2} (/day={tradesPerDay,4:F1}) || {zeroDte.Count,10} {zeroDte.WinPct,5:F1}% {zeroDte.Net,9:F2} (/day={tradesPerZeroDteDay,4:F1}) || {nonZeroDte.Count,11} {nonZeroDte.WinPct,5:F1}% {nonZeroDte.Net,9:F2} (/day={tradesPerNonZeroDteDay,4:F1}) |{(inTarget ? "  <-- POOLED TARGET" : "")}{(zeroDteInTarget ? "  <-- 0DTE TARGET" : "")}{(nonZeroDteInTarget ? "  <-- NON0DTE TARGET" : "")}");
            }
        }
    }

    if (!pccShowTop5)
    {
        return 0;
    }

    // 2026-09-23, user's own explicit standing request: every sweep surfaces top-5-by-metric
    // tables (win rate, MFE, MAE, net), split by DTE regime -- not one pre-chosen "winner". Win
    // rate/net come straight from the grid above (already computed, free). MAE/MFE requires
    // per-trade tick loading, so it's only computed for the small UNION of cells that already
    // make a top-5-by-winrate-or-net list in either regime (re-running just those cells is cheap
    // -- the option price series are already cached in sharedOptionPriceCache).
    async Task<(decimal AvgMaePct, decimal AvgMfePct)> ComputeAvgMaeMfeAsync(List<VolumeBarTrade> trades)
    {
        if (trades.Count == 0)
        {
            return (0m, 0m);
        }

        await using var mmSource = new NiftySignalDbContext(tradeSourceOptions);
        var maePcts = new List<decimal>();
        var mfePcts = new List<decimal>();
        var chainCache = new Dictionary<DateOnly, List<NiftySignal.Domain.Entities.Instrument>>();
        foreach (var t in trades)
        {
            var tradeDate = DateOnly.FromDateTime(t.EntryTime.UtcDateTime);
            if (!chainCache.TryGetValue(tradeDate, out var chain))
            {
                var allOptions = await mmSource.Instruments
                    .Where(i => i.AsOfDate == tradeDate && i.InstrumentType == NiftySignal.Domain.Enums.InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
                    .ToListAsync();
                chain = allOptions;
                chainCache[tradeDate] = chain;
            }

            var instrument = chain.FirstOrDefault(i => i.StrikePrice == t.StrikePrice && i.OptionType == t.Side);
            if (instrument is null)
            {
                continue;
            }

            var series = await OptionPriceSeries.LoadAsync(mmSource, instrument.Token, t.EntryTime, t.ExitTime, CancellationToken.None);
            var prices = series.AllPrices.Select(p => p.Price).ToList();
            var result = MaeMfeCalculator.Compute(t.EntryPrice, prices);
            maePcts.Add(result.MaePercent);
            mfePcts.Add(result.MfePercent);
        }

        return (maePcts.Count > 0 ? maePcts.Average() : 0m, mfePcts.Count > 0 ? mfePcts.Average() : 0m);
    }

    async Task PrintTop5Async(string regimeLabel, bool isZeroDte)
    {
        (int Count, double WinPct, decimal Net) Select((int Fast, int Slow, double Threshold, (int Count, double WinPct, decimal Net) Pooled, (int Count, double WinPct, decimal Net) ZeroDte, (int Count, double WinPct, decimal Net) NonZeroDte) row)
            => isZeroDte ? row.ZeroDte : row.NonZeroDte;

        // 2026-09-23: a single specific (fast, slow, threshold) request has exactly one candidate
        // to show -- bypass the minsample gate (which exists to keep a big grid's top-5 lists from
        // being dominated by tiny-n noise) rather than hide the only result there is; still shown
        // with its own trade count so a small sample is visible, not disguised.
        var eligible = pccIsSingleCell ? pccCellRows : pccCellRows.Where(r => Select(r).Count >= pccMinSample).ToList();
        if (eligible.Count == 0)
        {
            Console.WriteLine($"--- {regimeLabel}: no cell reached the minimum sample size (n>={pccMinSample}) ---");
            return;
        }

        var byWinRate = eligible.OrderByDescending(r => Select(r).WinPct).Take(5).ToList();
        var byNet = eligible.OrderByDescending(r => Select(r).Net).Take(5).ToList();

        // MAE/MFE only computed for the union of cells appearing in either top-5 above -- re-runs
        // just those cells' day-by-day trades (cached price series make this cheap) to get the
        // exact trade list for this regime, then averages MAE%/MFE% across them.
        var shortlist = byWinRate.Concat(byNet).Distinct().ToList();
        var maeMfeByCell = new Dictionary<(int, int, double), (decimal MaePct, decimal MfePct)>();
        foreach (var cell in shortlist)
        {
            var regimeTrades = new List<VolumeBarTrade>();
            foreach (var date in (isZeroDte ? pccZeroDays : pccNonZeroDays))
            {
                regimeTrades.AddRange(await RunDayAsync(date, cell.Fast, cell.Slow, cell.Threshold));
            }
            maeMfeByCell[(cell.Fast, cell.Slow, cell.Threshold)] = await ComputeAvgMaeMfeAsync(regimeTrades);
        }

        var byMfe = shortlist.OrderByDescending(r => maeMfeByCell[(r.Fast, r.Slow, r.Threshold)].MfePct).Take(5).ToList();
        var byMae = shortlist.OrderBy(r => maeMfeByCell[(r.Fast, r.Slow, r.Threshold)].MaePct).Take(5).ToList();

        var regimeDayCount = (isZeroDte ? pccZeroDays : pccNonZeroDays).Count;

        void PrintRows(string label, List<(int Fast, int Slow, double Threshold, (int Count, double WinPct, decimal Net) Pooled, (int Count, double WinPct, decimal Net) ZeroDte, (int Count, double WinPct, decimal Net) NonZeroDte)> rows)
        {
            Console.WriteLine($"  Top 5 by {label}:");
            // 2026-09-23, user's own explicit complaint: earlier output didn't make clear whether
            // "Trades" was per-day or pooled across the whole range -- TradesTotal/TradesPerDay are
            // now BOTH always shown, no ambiguity. TradesTotal is POOLED across every day in this
            // regime (regimeDayCount days); Net is likewise the SUM across all of those days, not a
            // single day's own number.
            Console.WriteLine($"    {"Fast",5} {"Slow",5} {"Thr%",6} {"TradesTotal",11} {"TradesPerDay",12} {"Win%",6} {"NetTotal",10} {"MAE%",7} {"MFE%",7}");
            foreach (var r in rows)
            {
                var s = Select(r);
                var (maePct, mfePct) = maeMfeByCell.TryGetValue((r.Fast, r.Slow, r.Threshold), out var mm) ? mm : (0m, 0m);
                var perDay = regimeDayCount > 0 ? s.Count / (double)regimeDayCount : 0;
                Console.WriteLine($"    {r.Fast,5} {r.Slow,5} {r.Threshold,6:F2} {s.Count,11} {perDay,12:F1} {s.WinPct,5:F1}% {s.Net,10:F2} {maePct,6:F2}% {mfePct,6:F2}%");
            }
        }

        var regimeDates = (isZeroDte ? pccZeroDays : pccNonZeroDays).Select(d => d.ToString("yyyy-MM-dd"));
        Console.WriteLine($"=== {regimeLabel} (min sample n>={pccMinSample} POOLED trades across {regimeDayCount} days: {string.Join(", ", regimeDates)} -- TradesTotal is pooled, TradesPerDay = TradesTotal / {regimeDayCount}) ===");
        PrintRows("Win rate", byWinRate);
        PrintRows("Net", byNet);
        PrintRows("MFE%", byMfe);
        PrintRows("MAE% (lowest first)", byMae);
        Console.WriteLine();
    }

    Console.WriteLine();
    Console.WriteLine("############# TOP-5-BY-METRIC SUMMARY #############");
    await PrintTop5Async("0-DTE", isZeroDte: true);
    await PrintTop5Async("Non-0-DTE", isZeroDte: false);

    return 0;
}

// "signal-audit" -- 2026-09-23 correctness-review investigation, structural-only (no trade
// simulation, no threshold optimization -- explicit user instruction). Reports, per day and side:
// market-structure stats (bar count, distinct ATM strikes, strike changes, run-length distribution,
// runs >=10/20/40/60 bars) which are ARCHITECTURE-INDEPENDENT (a property of the raw ATM sequence),
// plus signal-generation stats (resets, warmed-up bars, raw sign flips, threshold-gated crossings,
// trade candidates) for one or all three candidate signal architectures
// (RollingReset/FixedAtm/RollingBackAdjusted -- see SignalArchitectureAudit's own doc comment).
//   dotnet run --project NiftySignal.VolumeBarData -- signal-audit <fromDate> <toDate> <side:Call|Put> <fastBars> <slowBars> [thresholdPct=5] [barVolumeThreshold=2600] [--architecture=RollingReset|FixedAtm|RollingBackAdjusted|All] [--entrystart=09:30] [--entryend=15:00]
if (args.Length > 0 && string.Equals(args[0], "signal-audit", StringComparison.OrdinalIgnoreCase))
{
    var (saPositional, saNamed) = SplitNamedArgs(args);
    if (saPositional.Length < 6
        || !DateOnly.TryParseExact(saPositional[1], "yyyy-MM-dd", out var saFromDate)
        || !DateOnly.TryParseExact(saPositional[2], "yyyy-MM-dd", out var saToDate)
        || !Enum.TryParse<OptionType>(saPositional[3], ignoreCase: true, out var saSide)
        || !int.TryParse(saPositional[4], out var saFastBars)
        || !int.TryParse(saPositional[5], out var saSlowBars))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- signal-audit <fromDate> <toDate> <side:Call|Put> <fastBars> <slowBars> [thresholdPct=5] [barVolumeThreshold=2600] [--architecture=RollingReset|FixedAtm|RollingBackAdjusted|All] [--entrystart=09:30] [--entryend=15:00]");
        return 1;
    }

    var saThresholdPct = saPositional.Length > 6 ? double.Parse(saPositional[6]) : 5.0;
    var saBarVolumeThreshold = saPositional.Length > 7 ? long.Parse(saPositional[7]) : 2600L;
    var saArchitectures = saNamed.TryGetValue("architecture", out var saArchStr) && !string.Equals(saArchStr, "All", StringComparison.OrdinalIgnoreCase)
        ? [Enum.Parse<SignalArchitectureAudit.SignalArchitecture>(saArchStr, ignoreCase: true)]
        : Enum.GetValues<SignalArchitectureAudit.SignalArchitecture>();
    var saEntryStart = saNamed.TryGetValue("entrystart", out var saEntryStartStr) ? (TimeSpan?)TimeSpan.Parse(saEntryStartStr) : null;
    var saEntryEnd = saNamed.TryGetValue("entryend", out var saEntryEndStr) ? (TimeSpan?)TimeSpan.Parse(saEntryEndStr) : null;

    Console.WriteLine($"=== Signal audit: side={saSide} fast={saFastBars} slow={saSlowBars} thr={saThresholdPct}% barThreshold={saBarVolumeThreshold} ===");
    Console.WriteLine();
    Console.WriteLine("--- Market structure (architecture-independent) ---");
    Console.WriteLine($"{"Date",-12} {"Bars",6} {"Strikes",8} {"Changes",8} {"MaxRun",7} {">=10",5} {">=20",5} {">=40",5} {">=60",5}");
    for (var date = saFromDate; date <= saToDate; date = date.AddDays(1))
    {
        await using var saSource0 = new NiftySignalDbContext(tradeSourceOptions);
        await using var saVolumeBars0 = new VolumeBarDbContext(volumeBarOptions);
        var ms = await SignalArchitectureAudit.ComputeMarketStructureAsync(saSource0, saVolumeBars0, date, saBarVolumeThreshold, saSide, CancellationToken.None);
        if (ms.BarCount == 0)
        {
            continue;
        }
        Console.WriteLine($"{date:yyyy-MM-dd,-12} {ms.BarCount,6} {ms.DistinctStrikesSeen,8} {ms.StrikeChanges,8} {ms.MaxStableRun,7} {ms.RunsAtLeast10,5} {ms.RunsAtLeast20,5} {ms.RunsAtLeast40,5} {ms.RunsAtLeast60,5}");
    }

    foreach (var arch in saArchitectures)
    {
        Console.WriteLine();
        Console.WriteLine($"--- Signal generation, architecture={arch} ---");
        Console.WriteLine($"{"Date",-12} {"Resets",7} {"WarmBars",8} {"RawFlips",8} {"ThrUp",6} {"ThrDn",6} {"Candidates",10} {"Fallbacks",9}");
        int totResets = 0, totWarm = 0, totRaw = 0, totThrUp = 0, totThrDn = 0, totCand = 0, totFallback = 0;
        for (var date = saFromDate; date <= saToDate; date = date.AddDays(1))
        {
            await using var saSource1 = new NiftySignalDbContext(tradeSourceOptions);
            await using var saVolumeBars1 = new VolumeBarDbContext(volumeBarOptions);
            var (_, stats) = await SignalArchitectureAudit.RunDayAsync(saSource1, saVolumeBars1, date, saBarVolumeThreshold, saSide, saFastBars, saSlowBars, saThresholdPct / 100.0, arch, CancellationToken.None, saEntryStart, saEntryEnd);
            if (stats.WarmedUpBars == 0 && stats.EngineResets == 0 && stats.RawSignFlips == 0)
            {
                continue;
            }
            Console.WriteLine($"{date:yyyy-MM-dd,-12} {stats.EngineResets,7} {stats.WarmedUpBars,8} {stats.RawSignFlips,8} {stats.ThresholdCrossingsUp,6} {stats.ThresholdCrossingsDown,6} {stats.TradeCandidates,10} {stats.FallbackAdjustments,9}");
            totResets += stats.EngineResets;
            totWarm += stats.WarmedUpBars;
            totRaw += stats.RawSignFlips;
            totThrUp += stats.ThresholdCrossingsUp;
            totThrDn += stats.ThresholdCrossingsDown;
            totCand += stats.TradeCandidates;
            totFallback += stats.FallbackAdjustments;
        }
        Console.WriteLine($"{"TOTAL",-12} {totResets,7} {totWarm,8} {totRaw,8} {totThrUp,6} {totThrDn,6} {totCand,10} {totFallback,9}");
    }

    return 0;
}

// "signal-trace" -- 2026-09-23 correctness-review investigation: bar-by-bar visual trace for ONE
// day, so the actual algorithm behavior (future price -> selected ATM strike -> option token ->
// option price -> engine state -> reset/continue -> signal) can be read directly rather than
// inferred. Only prints bars where something interesting happened (a strike change/reset, a raw
// sign flip, or a threshold-gated crossing) by default, to keep output readable on a busy day --
// pass --all to dump every bar.
//   dotnet run --project NiftySignal.VolumeBarData -- signal-trace <date> <side:Call|Put> <fastBars> <slowBars> [thresholdPct=5] [barVolumeThreshold=2600] [--architecture=RollingReset|FixedAtm|RollingBackAdjusted] [--all]
if (args.Length > 0 && string.Equals(args[0], "signal-trace", StringComparison.OrdinalIgnoreCase))
{
    var (stPositional, stNamed) = SplitNamedArgs(args);
    if (stPositional.Length < 5
        || !DateOnly.TryParseExact(stPositional[1], "yyyy-MM-dd", out var stDate)
        || !Enum.TryParse<OptionType>(stPositional[2], ignoreCase: true, out var stSide)
        || !int.TryParse(stPositional[3], out var stFastBars)
        || !int.TryParse(stPositional[4], out var stSlowBars))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- signal-trace <date> <side:Call|Put> <fastBars> <slowBars> [thresholdPct=5] [barVolumeThreshold=2600] [--architecture=RollingReset|FixedAtm|RollingBackAdjusted] [--all]");
        return 1;
    }

    var stThresholdPct = stPositional.Length > 5 ? double.Parse(stPositional[5]) : 5.0;
    var stBarVolumeThreshold = stPositional.Length > 6 ? long.Parse(stPositional[6]) : 2600L;
    var stArchitecture = stNamed.TryGetValue("architecture", out var stArchStr) ? Enum.Parse<SignalArchitectureAudit.SignalArchitecture>(stArchStr, ignoreCase: true) : SignalArchitectureAudit.SignalArchitecture.RollingReset;
    var stAll = stNamed.ContainsKey("all");

    await using var stSource = new NiftySignalDbContext(tradeSourceOptions);
    await using var stVolumeBars = new VolumeBarDbContext(volumeBarOptions);
    var (stTrace, stStats) = await SignalArchitectureAudit.RunDayAsync(stSource, stVolumeBars, stDate, stBarVolumeThreshold, stSide, stFastBars, stSlowBars, stThresholdPct / 100.0, stArchitecture, CancellationToken.None);

    Console.WriteLine($"=== Signal trace: {stDate:yyyy-MM-dd} side={stSide} fast={stFastBars} slow={stSlowBars} thr={stThresholdPct}% bar={stBarVolumeThreshold} architecture={stArchitecture} ===");
    Console.WriteLine($"{"Bar",4} {"TimeIST",8} {"FuturePx",9} {"AtmStrike",9} {"RawPx",8} {"EnginePx",8} {"Chg",3} {"Rst",3} {"FastMa",8} {"SlowMa",8} {"Diff%",7} {"Flip",4} {"Up",2} {"Dn",2}");
    foreach (var b in stTrace)
    {
        if (!stAll && !b.StrikeChangedThisBar && !b.RawSignFlip && !b.ThresholdCrossedUp && !b.ThresholdCrossedDown)
        {
            continue;
        }
        Console.WriteLine($"{b.BarIndex,4} {FormatIst(b.Timestamp),8} {b.FuturePrice,9:F2} {(b.AtmStrike?.ToString("F0") ?? "--"),9} {(b.RawPrice?.ToString("F2") ?? "--"),8} {(b.EngineInputPrice?.ToString("F2") ?? "--"),8} {(b.StrikeChangedThisBar ? "Y" : ""),3} {(b.EngineReset ? "Y" : ""),3} {(b.FastMa?.ToString("F2") ?? "--"),8} {(b.SlowMa?.ToString("F2") ?? "--"),8} {(b.DiffFraction is { } d ? (d * 100).ToString("F2") : "--"),7} {(b.RawSignFlip ? "Y" : ""),4} {(b.ThresholdCrossedUp ? "Y" : ""),2} {(b.ThresholdCrossedDown ? "Y" : ""),2}");
    }

    Console.WriteLine();
    Console.WriteLine($"--- Resets={stStats.EngineResets}, WarmedUpBars={stStats.WarmedUpBars}, RawSignFlips={stStats.RawSignFlips}, ThresholdCrossings={stStats.ThresholdCrossings}, TradeCandidates={stStats.TradeCandidates}, FallbackAdjustments={stStats.FallbackAdjustments} ---");
    return 0;
}

// "simulate-cadence-trades" -- 2026-09-23, per-strike time-cadence trading simulation, step 2 (see
// PerStrikeCadenceSimulator's own doc comment for the exact mechanism -- no future/ATM anywhere,
// every strike in the chain gets its own independent fast/slow signal on its own AvgPrice series,
// single open position at a time, entry/exit fills use real last-traded price).
//   dotnet run --project NiftySignal.VolumeBarData -- simulate-cadence-trades <fromDate> <toDate> <side:Call|Put> [fastBars=2] [slowBars=10] [thresholdPct=0] [--interval=15] [--minprice=100] [--maxprice=150]
if (args.Length > 0 && string.Equals(args[0], "simulate-cadence-trades", StringComparison.OrdinalIgnoreCase))
{
    var (sctPositional, sctNamed) = SplitNamedArgs(args);
    if (sctPositional.Length < 4
        || !DateOnly.TryParseExact(sctPositional[1], "yyyy-MM-dd", out var sctFromDate)
        || !DateOnly.TryParseExact(sctPositional[2], "yyyy-MM-dd", out var sctToDate)
        || !Enum.TryParse<OptionType>(sctPositional[3], ignoreCase: true, out var sctSide))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- simulate-cadence-trades <fromDate> <toDate> <side:Call|Put> [fastBars=2] [slowBars=10] [thresholdPct=0] [--interval=15] [--minprice=100] [--maxprice=150]");
        return 1;
    }

    var sctFastBars = sctPositional.Length > 4 ? int.Parse(sctPositional[4]) : 2;
    var sctSlowBars = sctPositional.Length > 5 ? int.Parse(sctPositional[5]) : 10;
    var sctThresholdPct = sctPositional.Length > 6 ? double.Parse(sctPositional[6]) : 0.0;
    var sctIntervalSeconds = sctNamed.TryGetValue("interval", out var sctIntervalStr) ? int.Parse(sctIntervalStr) : 15;
    var sctMinPrice = sctNamed.TryGetValue("minprice", out var sctMinPriceStr) ? decimal.Parse(sctMinPriceStr) : 100m;
    var sctMaxPrice = sctNamed.TryGetValue("maxprice", out var sctMaxPriceStr) ? decimal.Parse(sctMaxPriceStr) : 150m;

    Console.WriteLine($"=== Per-strike time-cadence simulation: side={sctSide} fast={sctFastBars} slow={sctSlowBars} thr={sctThresholdPct}% interval={sctIntervalSeconds}s entryPriceBand=[{sctMinPrice},{sctMaxPrice}] (NO future/ATM reference -- every strike scanned independently) ===");
    var sctAllTrades = new List<PerStrikeCadenceSimulator.CadenceTrade>();
    for (var date = sctFromDate; date <= sctToDate; date = date.AddDays(1))
    {
        await using var sctSource = new NiftySignalDbContext(tradeSourceOptions);
        var dayTrades = await PerStrikeCadenceSimulator.SimulateDayAsync(sctSource, date, sctSide, CancellationToken.None, sctIntervalSeconds, sctFastBars, sctSlowBars, sctThresholdPct / 100.0, sctMinPrice, sctMaxPrice);
        if (dayTrades.Count == 0)
        {
            continue;
        }

        sctAllTrades.AddRange(dayTrades);
        var dayWinRate = 100.0 * dayTrades.Count(t => t.NetPnlPoints > 0) / dayTrades.Count;
        var dayNet = dayTrades.Sum(t => t.NetPnlPoints);
        Console.WriteLine($"{date:yyyy-MM-dd}: {dayTrades.Count} trades THIS DAY, {dayWinRate:F1}% win rate, net {dayNet:F2} pts");
        foreach (var t in dayTrades)
        {
            Console.WriteLine($"    {FormatIst(t.EntryTime)} Long {sctSide}@{t.StrikePrice} entry={t.EntryPrice:F2} -> {FormatIst(t.ExitTime)} exit={t.ExitPrice:F2} ({t.ExitReason}) netPnl={t.NetPnlPoints:F2} ({t.NetPnlPercent:F1}%)");
        }
    }

    Console.WriteLine();
    if (sctAllTrades.Count > 0)
    {
        var winRate = 100.0 * sctAllTrades.Count(t => t.NetPnlPoints > 0) / sctAllTrades.Count;
        var net = sctAllTrades.Sum(t => t.NetPnlPoints);
        var dayCount = (sctToDate.DayNumber - sctFromDate.DayNumber) + 1;
        Console.WriteLine($"--- TOTAL (POOLED): {sctAllTrades.Count} trades pooled, {winRate:F1}% win rate, net {net:F2} pts pooled ---");
    }
    else
    {
        Console.WriteLine("No trades fired across the requested range.");
    }

    return 0;
}

// "export-put-cadence" -- 2026-09-23, per-strike time-cadence experiment, step 1 (raw data export
// only, no signal/crossover logic yet -- user's own explicit "clean and slow" instruction). For ONE
// day, builds a fixed 15-second (default) cadence DIRECTLY on each PUT option's own ticks -- no
// future/underlying price involved anywhere in this path. Writes a long-format CSV: one row per
// (strike, 15-second bucket).
//   dotnet run --project NiftySignal.VolumeBarData -- export-put-cadence <date> [--interval=15] [--out=path.csv] [--strike=23750] [--fastbars=8] [--slowbars=40]
if (args.Length > 0 && string.Equals(args[0], "export-put-cadence", StringComparison.OrdinalIgnoreCase))
{
    var (epcPositional, epcNamed) = SplitNamedArgs(args);
    if (epcPositional.Length < 2 || !DateOnly.TryParseExact(epcPositional[1], "yyyy-MM-dd", out var epcDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- export-put-cadence <date:yyyy-MM-dd> [--interval=15] [--out=path.csv] [--strike=23750] [--fastbars=8] [--slowbars=40]");
        return 1;
    }

    var epcIntervalSeconds = epcNamed.TryGetValue("interval", out var epcIntervalStr) ? int.Parse(epcIntervalStr) : 15;
    var epcStrikeFilter = epcNamed.TryGetValue("strike", out var epcStrikeStr) ? (decimal?)decimal.Parse(epcStrikeStr) : null;
    var epcFastBars = epcNamed.TryGetValue("fastbars", out var epcFastBarsStr) ? int.Parse(epcFastBarsStr) : 8;
    var epcSlowBars = epcNamed.TryGetValue("slowbars", out var epcSlowBarsStr) ? int.Parse(epcSlowBarsStr) : 40;
    var epcOutPath = epcNamed.TryGetValue("out", out var epcOutStr) ? epcOutStr : $"put-cadence-{epcDate:yyyy-MM-dd}{(epcStrikeFilter is { } esf ? $"-{esf}" : "")}.csv";

    await using var epcSource = new NiftySignalDbContext(tradeSourceOptions);
    var epcResult = await PutCadenceExporter.BuildAsync(epcSource, epcDate, CancellationToken.None, epcIntervalSeconds, epcStrikeFilter, epcFastBars, epcSlowBars);

    if (epcResult.StrikeCount == 0)
    {
        Console.Error.WriteLine($"No PUT instruments found for {epcDate:yyyy-MM-dd} -- nothing to export.");
        return 1;
    }

    Console.WriteLine($"=== export-put-cadence: {epcDate:yyyy-MM-dd}, interval={epcIntervalSeconds}s, fastBars={epcFastBars} ({epcFastBars * epcIntervalSeconds}s), slowBars={epcSlowBars} ({epcSlowBars * epcIntervalSeconds}s) ===");
    Console.WriteLine($"Strikes found (nearest expiry, PUT side): {epcResult.StrikeCount}");
    Console.WriteLine($"Buckets per strike (09:15-15:30 IST session / {epcIntervalSeconds}s): {epcResult.BucketCount}");
    Console.WriteLine($"Total rows (strikes x buckets): {epcResult.RowCount}");
    var epcNullCount = epcResult.Rows.Count(r => r.Price is null);
    Console.WriteLine($"Rows with no price available yet at that bucket (gap, not fabricated): {epcNullCount} ({100.0 * epcNullCount / epcResult.RowCount:F1}%)");

    PutCadenceExporter.WriteCsv(epcResult, epcOutPath, TimeSpan.FromHours(5.5));
    Console.WriteLine($"CSV written to: {Path.GetFullPath(epcOutPath)}");

    return 0;
}

// "price-crossover-time" -- 2026-09-23, tests the price-crossover engine on TIME-based bars (fixed
// wall-clock interval, default 1 minute) instead of volume-threshold bars -- fast/slow are now
// MINUTES, not bar counts (fastMinutes=2/slowMinutes=10 means a genuine 2-minute vs 10-minute SMA).
// Bars built fresh IN-MEMORY per date (never persisted -- no new dataset). Reports per-day AND
// pooled trades/win/net, explicitly labeled, same as price-crossover-dynamic.
//   dotnet run --project NiftySignal.VolumeBarData -- price-crossover-time <fromDate> <toDate> <side:Call|Put|DualAgreement|DualAgreementBothExit> <fastMinutes> <slowMinutes> [thresholdPct=1.0] [--interval=1] [--minprice=100] [--maxprice=150] [--zerodte=2026-09-08,2026-09-15]
if (args.Length > 0 && string.Equals(args[0], "price-crossover-time", StringComparison.OrdinalIgnoreCase))
{
    var (ptPositional, ptNamed) = SplitNamedArgs(args);
    if (ptPositional.Length < 6
        || !DateOnly.TryParseExact(ptPositional[1], "yyyy-MM-dd", out var ptFromDate)
        || !DateOnly.TryParseExact(ptPositional[2], "yyyy-MM-dd", out var ptToDate)
        || !Enum.TryParse<PriceCrossoverSide>(ptPositional[3], ignoreCase: true, out var ptSide)
        || !int.TryParse(ptPositional[4], out var ptFastMinutes)
        || !int.TryParse(ptPositional[5], out var ptSlowMinutes))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- price-crossover-time <fromDate> <toDate> <side:Call|Put|DualAgreement|DualAgreementBothExit> <fastMinutes> <slowMinutes> [thresholdPct=1.0] [--interval=1] [--minprice=100] [--maxprice=150] [--zerodte=2026-09-08,2026-09-15]");
        return 1;
    }

    var ptThresholdPct = ptPositional.Length > 6 ? double.Parse(ptPositional[6]) : 1.0;
    var ptIntervalMinutes = ptNamed.TryGetValue("interval", out var ptIntervalStr) ? int.Parse(ptIntervalStr) : 1;
    if (ptFastMinutes % ptIntervalMinutes != 0 || ptSlowMinutes % ptIntervalMinutes != 0)
    {
        Console.Error.WriteLine($"fastMinutes ({ptFastMinutes}) and slowMinutes ({ptSlowMinutes}) must both be exact multiples of --interval ({ptIntervalMinutes}) -- e.g. interval=1 with fast=2/slow=10, or interval=2 with fast=2/slow=10.");
        return 1;
    }
    var ptFastBars = ptFastMinutes / ptIntervalMinutes;
    var ptSlowBars = ptSlowMinutes / ptIntervalMinutes;
    var ptMinPrice = ptNamed.TryGetValue("minprice", out var ptMinPriceStr) ? (decimal?)decimal.Parse(ptMinPriceStr) : 100m;
    var ptMaxPrice = ptNamed.TryGetValue("maxprice", out var ptMaxPriceStr) ? (decimal?)decimal.Parse(ptMaxPriceStr) : 150m;
    var ptZeroDteDates = (ptNamed.TryGetValue("zerodte", out var ptZeroDteStr)
        ? ptZeroDteStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd"))
        : [DateOnly.Parse("2026-09-08"), DateOnly.Parse("2026-09-15")]).ToHashSet();

    Console.WriteLine($"=== Time-based price-crossover: side={ptSide} fast={ptFastMinutes}min slow={ptSlowMinutes}min (interval={ptIntervalMinutes}min, fastBars={ptFastBars}, slowBars={ptSlowBars}) thr={ptThresholdPct}% entryPriceBand=[{ptMinPrice},{ptMaxPrice}] ===");
    Console.WriteLine();

    var ptDayResults = new List<(DateOnly Date, bool IsZeroDte, int Trades, double WinPct, decimal Net, int BarCount)>();
    for (var date = ptFromDate; date <= ptToDate; date = date.AddDays(1))
    {
        await using var ptSource = new NiftySignalDbContext(tradeSourceOptions);
        var bars = await TimeBasedBarBuilder.BuildDayAsync(ptSource, date, CancellationToken.None, ptIntervalMinutes);
        if (bars.Count == 0)
        {
            continue;
        }

        await using var ptSource2 = new NiftySignalDbContext(tradeSourceOptions);
        await using var ptVolumeBars = new VolumeBarDbContext(volumeBarOptions); // unused when prebuiltBars is supplied, but still required by the method signature
        var dayTrades = await TradeSimulator.SimulatePriceCrossoverDayAsync(ptSource2, ptVolumeBars, date, barVolumeThreshold: ptIntervalMinutes, ptSide, ptFastBars, ptSlowBars, ptThresholdPct / 100.0, CancellationToken.None, sharedOptionPriceCache, minEntryPrice: ptMinPrice, maxEntryPrice: ptMaxPrice, prebuiltBars: bars);

        var dayWin = dayTrades.Count > 0 ? 100.0 * dayTrades.Count(t => t.NetPnlPoints > 0) / dayTrades.Count : 0.0;
        var dayNet = dayTrades.Sum(t => t.NetPnlPoints);
        var isZeroDte = ptZeroDteDates.Contains(date);
        ptDayResults.Add((date, isZeroDte, dayTrades.Count, dayWin, dayNet, bars.Count));

        Console.WriteLine($"{date:yyyy-MM-dd} ({(isZeroDte ? "0-DTE" : "non-0-DTE")}): {dayTrades.Count} trades THIS DAY, {dayWin:F1}% win rate, net {dayNet:F2} pts -- {bars.Count} time-bars built");
        foreach (var t in dayTrades)
        {
            var tside = t.Side == OptionType.Call ? "Call" : "Put";
            Console.WriteLine($"    {FormatIst(t.EntryTime)} Long {tside}@{t.StrikePrice} entry={t.EntryPrice:F2} -> {FormatIst(t.ExitTime)} exit={t.ExitPrice:F2} ({t.ExitReason}) netPnl={t.NetPnlPoints:F2} ({t.NetPnlPercent:F1}%)");
        }
    }

    Console.WriteLine();
    if (ptDayResults.Count == 0)
    {
        Console.WriteLine("No trades fired across the requested range.");
        return 0;
    }

    var ptAllDatesStr = string.Join(", ", ptDayResults.Select(r => r.Date.ToString("yyyy-MM-dd")));
    var ptZeroDatesStr = string.Join(", ", ptDayResults.Where(r => r.IsZeroDte).Select(r => r.Date.ToString("yyyy-MM-dd")));
    var ptNonZeroDatesStr = string.Join(", ", ptDayResults.Where(r => !r.IsZeroDte).Select(r => r.Date.ToString("yyyy-MM-dd")));

    async Task<(int Count, double WinPct, decimal Net)> PtPooledStatsAsync(List<DateOnly> dates)
    {
        var all = new List<VolumeBarTrade>();
        foreach (var date in dates)
        {
            await using var s = new NiftySignalDbContext(tradeSourceOptions);
            var bars = await TimeBasedBarBuilder.BuildDayAsync(s, date, CancellationToken.None, ptIntervalMinutes);
            if (bars.Count == 0)
            {
                continue;
            }
            await using var s2 = new NiftySignalDbContext(tradeSourceOptions);
            await using var vb = new VolumeBarDbContext(volumeBarOptions);
            all.AddRange(await TradeSimulator.SimulatePriceCrossoverDayAsync(s2, vb, date, ptIntervalMinutes, ptSide, ptFastBars, ptSlowBars, ptThresholdPct / 100.0, CancellationToken.None, sharedOptionPriceCache, minEntryPrice: ptMinPrice, maxEntryPrice: ptMaxPrice, prebuiltBars: bars));
        }
        return (all.Count, all.Count > 0 ? 100.0 * all.Count(t => t.NetPnlPoints > 0) / all.Count : 0.0, all.Sum(t => t.NetPnlPoints));
    }

    var ptAllStats = await PtPooledStatsAsync(ptDayResults.Select(r => r.Date).ToList());
    var ptZeroStats = await PtPooledStatsAsync(ptDayResults.Where(r => r.IsZeroDte).Select(r => r.Date).ToList());
    var ptNonZeroStats = await PtPooledStatsAsync(ptDayResults.Where(r => !r.IsZeroDte).Select(r => r.Date).ToList());
    var ptZeroDayCount = ptDayResults.Count(r => r.IsZeroDte);
    var ptNonZeroDayCount = ptDayResults.Count(r => !r.IsZeroDte);

    Console.WriteLine("############# SUMMARY -- POOLED ACROSS ALL DAYS IN EACH GROUP, NOT PER-DAY #############");
    Console.WriteLine($"ALL DAYS:      {ptAllStats.Count} trades POOLED over {ptDayResults.Count} days [{ptAllDatesStr}] ({ptAllStats.Count / (double)ptDayResults.Count:F1}/day), {ptAllStats.WinPct:F1}% win rate (pooled), net {ptAllStats.Net:F2} pts POOLED");
    Console.WriteLine($"0-DTE ONLY:    {ptZeroStats.Count} trades POOLED over {ptZeroDayCount} days [{ptZeroDatesStr}] ({(ptZeroDayCount > 0 ? ptZeroStats.Count / (double)ptZeroDayCount : 0):F1}/day), {ptZeroStats.WinPct:F1}% win rate (pooled), net {ptZeroStats.Net:F2} pts POOLED");
    Console.WriteLine($"NON-0-DTE:     {ptNonZeroStats.Count} trades POOLED over {ptNonZeroDayCount} days [{ptNonZeroDatesStr}] ({(ptNonZeroDayCount > 0 ? ptNonZeroStats.Count / (double)ptNonZeroDayCount : 0):F1}/day), {ptNonZeroStats.WinPct:F1}% win rate (pooled), net {ptNonZeroStats.Net:F2} pts POOLED");

    return 0;
}

// "price-crossover-dynamic" -- 2026-09-23, tests DynamicTickVelocityBarBuilder's adaptive bar
// sizing against the price-crossover engine, in place of a fixed-threshold VolumeBarRow series.
// Bars are built fresh IN-MEMORY for each requested date (never persisted -- no new dataset).
// Reports trades/win/net BOTH per-day AND pooled across the whole range, explicitly labeled, per
// the user's own explicit complaint that this distinction was unclear in earlier output.
//   dotnet run --project NiftySignal.VolumeBarData -- price-crossover-dynamic <fromDate> <toDate> <side:Call|Put|DualAgreement|DualAgreementBothExit> [fastBars] [slowBars] [thresholdPct] [--low=2600] [--medium=1300] [--high=650] [--minprice=100] [--maxprice=150] [--zerodte=2026-09-08,2026-09-15]
if (args.Length > 0 && string.Equals(args[0], "price-crossover-dynamic", StringComparison.OrdinalIgnoreCase))
{
    var (pdPositional, pdNamed) = SplitNamedArgs(args);
    if (pdPositional.Length < 6
        || !DateOnly.TryParseExact(pdPositional[1], "yyyy-MM-dd", out var pdFromDate)
        || !DateOnly.TryParseExact(pdPositional[2], "yyyy-MM-dd", out var pdToDate)
        || !Enum.TryParse<PriceCrossoverSide>(pdPositional[3], ignoreCase: true, out var pdSide)
        || !int.TryParse(pdPositional[4], out var pdFastBars)
        || !int.TryParse(pdPositional[5], out var pdSlowBars))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- price-crossover-dynamic <fromDate> <toDate> <side:Call|Put|DualAgreement|DualAgreementBothExit> <fastBars> <slowBars> [thresholdPct=1.0] [--low=2600] [--medium=1300] [--high=650] [--minprice=100] [--maxprice=150] [--zerodte=2026-09-08,2026-09-15]");
        return 1;
    }

    var pdThresholdPct = pdPositional.Length > 6 ? double.Parse(pdPositional[6]) : 1.0;
    var pdLow = pdNamed.TryGetValue("low", out var pdLowStr) ? long.Parse(pdLowStr) : 2600L;
    var pdMedium = pdNamed.TryGetValue("medium", out var pdMediumStr) ? long.Parse(pdMediumStr) : 1300L;
    var pdHigh = pdNamed.TryGetValue("high", out var pdHighStr) ? long.Parse(pdHighStr) : 650L;
    var pdMinPrice = pdNamed.TryGetValue("minprice", out var pdMinPriceStr) ? (decimal?)decimal.Parse(pdMinPriceStr) : 100m;
    var pdMaxPrice = pdNamed.TryGetValue("maxprice", out var pdMaxPriceStr) ? (decimal?)decimal.Parse(pdMaxPriceStr) : 150m;
    var pdZeroDteDates = (pdNamed.TryGetValue("zerodte", out var pdZeroDteStr)
        ? pdZeroDteStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd"))
        : [DateOnly.Parse("2026-09-08"), DateOnly.Parse("2026-09-15")]).ToHashSet();

    Console.WriteLine($"=== Dynamic tick-velocity price-crossover: side={pdSide} fast={pdFastBars} slow={pdSlowBars} thr={pdThresholdPct}% thresholds(low/med/high)={pdLow}/{pdMedium}/{pdHigh} entryPriceBand=[{pdMinPrice},{pdMaxPrice}] ===");
    Console.WriteLine();

    var pdDayResults = new List<(DateOnly Date, bool IsZeroDte, int Trades, double WinPct, decimal Net, int LowBars, int MedBars, int HighBars)>();
    for (var date = pdFromDate; date <= pdToDate; date = date.AddDays(1))
    {
        await using var pdSource = new NiftySignalDbContext(tradeSourceOptions);
        var buildResult = await DynamicTickVelocityBarBuilder.BuildDayAsync(pdSource, date, CancellationToken.None, pdLow, pdMedium, pdHigh);
        if (buildResult.Bars.Count == 0)
        {
            continue;
        }

        await using var pdSource2 = new NiftySignalDbContext(tradeSourceOptions);
        await using var pdVolumeBars = new VolumeBarDbContext(volumeBarOptions); // unused when prebuiltBars is supplied, but still required by the method signature
        var dayTrades = await TradeSimulator.SimulatePriceCrossoverDayAsync(pdSource2, pdVolumeBars, date, barVolumeThreshold: pdMedium, pdSide, pdFastBars, pdSlowBars, pdThresholdPct / 100.0, CancellationToken.None, sharedOptionPriceCache, minEntryPrice: pdMinPrice, maxEntryPrice: pdMaxPrice, prebuiltBars: buildResult.Bars);

        var dayWin = dayTrades.Count > 0 ? 100.0 * dayTrades.Count(t => t.NetPnlPoints > 0) / dayTrades.Count : 0.0;
        var dayNet = dayTrades.Sum(t => t.NetPnlPoints);
        var isZeroDte = pdZeroDteDates.Contains(date);
        pdDayResults.Add((date, isZeroDte, dayTrades.Count, dayWin, dayNet, buildResult.LowCount, buildResult.MediumCount, buildResult.HighCount));

        Console.WriteLine($"{date:yyyy-MM-dd} ({(isZeroDte ? "0-DTE" : "non-0-DTE")}): {dayTrades.Count} trades THIS DAY, {dayWin:F1}% win rate, net {dayNet:F2} pts -- bar regime counts: low={buildResult.LowCount} med={buildResult.MediumCount} high={buildResult.HighCount} (total bars={buildResult.Bars.Count})");
        foreach (var t in dayTrades)
        {
            var tside = t.Side == OptionType.Call ? "Call" : "Put";
            Console.WriteLine($"    {FormatIst(t.EntryTime)} Long {tside}@{t.StrikePrice} entry={t.EntryPrice:F2} -> {FormatIst(t.ExitTime)} exit={t.ExitPrice:F2} ({t.ExitReason}) netPnl={t.NetPnlPoints:F2} ({t.NetPnlPercent:F1}%)");
        }
    }

    Console.WriteLine();
    if (pdDayResults.Count == 0)
    {
        Console.WriteLine("No trades fired across the requested range.");
        return 0;
    }

    Console.WriteLine("############# SUMMARY -- POOLED ACROSS ALL DAYS IN EACH GROUP, NOT PER-DAY #############");
    Console.WriteLine("(each day's own trades/win-rate/net were already printed above, per day, as they ran)");

    // Pooled win rate needs the actual trade list, not just per-day win%, to average correctly
    // (a simple average of daily win% would over-weight low-trade days) -- recompute properly here.
    async Task<(int Count, double WinPct, decimal Net)> PooledStatsAsync(List<DateOnly> dates)
    {
        var all = new List<VolumeBarTrade>();
        foreach (var date in dates)
        {
            await using var s = new NiftySignalDbContext(tradeSourceOptions);
            var buildResult = await DynamicTickVelocityBarBuilder.BuildDayAsync(s, date, CancellationToken.None, pdLow, pdMedium, pdHigh);
            if (buildResult.Bars.Count == 0)
            {
                continue;
            }
            await using var s2 = new NiftySignalDbContext(tradeSourceOptions);
            await using var vb = new VolumeBarDbContext(volumeBarOptions);
            all.AddRange(await TradeSimulator.SimulatePriceCrossoverDayAsync(s2, vb, date, pdMedium, pdSide, pdFastBars, pdSlowBars, pdThresholdPct / 100.0, CancellationToken.None, sharedOptionPriceCache, minEntryPrice: pdMinPrice, maxEntryPrice: pdMaxPrice, prebuiltBars: buildResult.Bars));
        }
        return (all.Count, all.Count > 0 ? 100.0 * all.Count(t => t.NetPnlPoints > 0) / all.Count : 0.0, all.Sum(t => t.NetPnlPoints));
    }

    var allStats = await PooledStatsAsync(pdDayResults.Select(r => r.Date).ToList());
    var zeroStats = await PooledStatsAsync(pdDayResults.Where(r => r.IsZeroDte).Select(r => r.Date).ToList());
    var nonZeroStats = await PooledStatsAsync(pdDayResults.Where(r => !r.IsZeroDte).Select(r => r.Date).ToList());
    var zeroDayCount = pdDayResults.Count(r => r.IsZeroDte);
    var nonZeroDayCount = pdDayResults.Count(r => !r.IsZeroDte);

    Console.WriteLine();
    // 2026-09-23: dates spelled out explicitly, not just a count -- a day COUNT alone isn't enough
    // to verify what was actually simulated.
    var pdAllDatesStr = string.Join(", ", pdDayResults.Select(r => r.Date.ToString("yyyy-MM-dd")));
    var pdZeroDatesStr = string.Join(", ", pdDayResults.Where(r => r.IsZeroDte).Select(r => r.Date.ToString("yyyy-MM-dd")));
    var pdNonZeroDatesStr = string.Join(", ", pdDayResults.Where(r => !r.IsZeroDte).Select(r => r.Date.ToString("yyyy-MM-dd")));
    Console.WriteLine($"ALL DAYS:      {allStats.Count} trades POOLED over {pdDayResults.Count} days [{pdAllDatesStr}] ({allStats.Count / (double)pdDayResults.Count:F1}/day), {allStats.WinPct:F1}% win rate (pooled), net {allStats.Net:F2} pts POOLED");
    Console.WriteLine($"0-DTE ONLY:    {zeroStats.Count} trades POOLED over {zeroDayCount} days [{pdZeroDatesStr}] ({(zeroDayCount > 0 ? zeroStats.Count / (double)zeroDayCount : 0):F1}/day), {zeroStats.WinPct:F1}% win rate (pooled), net {zeroStats.Net:F2} pts POOLED");
    Console.WriteLine($"NON-0-DTE:     {nonZeroStats.Count} trades POOLED over {nonZeroDayCount} days [{pdNonZeroDatesStr}] ({(nonZeroDayCount > 0 ? nonZeroStats.Count / (double)nonZeroDayCount : 0):F1}/day), {nonZeroStats.WinPct:F1}% win rate (pooled), net {nonZeroStats.Net:F2} pts POOLED");

    return 0;
}

// "entry-bar-quality" -- 2026-09-23, first-pass check on the user's question: does the ENTRY bar's
// own TickCount (how many raw ticks filled it) or DurationSeconds (how long, wall-clock, it took to
// fill) relate to trade outcome? Re-runs the real simulator to get actual trades, joins each trade's
// entry bar back to its own VolumeBarRow (already stores both fields), and splits win/loss by
// tick-count/duration terciles. Read-only, no trading logic touched.
//   dotnet run --project NiftySignal.VolumeBarData -- entry-bar-quality <fromDate> <toDate> <side:Call|Put> <fastBars> <slowBars> [thresholdPct=5] [barVolumeThreshold=2600] [--minprice=] [--maxprice=]
if (args.Length > 0 && string.Equals(args[0], "entry-bar-quality", StringComparison.OrdinalIgnoreCase))
{
    var (ebqPositional, ebqNamed) = SplitNamedArgs(args);
    if (ebqPositional.Length < 6
        || !DateOnly.TryParseExact(ebqPositional[1], "yyyy-MM-dd", out var ebqFromDate)
        || !DateOnly.TryParseExact(ebqPositional[2], "yyyy-MM-dd", out var ebqToDate)
        || !Enum.TryParse<PriceCrossoverSide>(ebqPositional[3], ignoreCase: true, out var ebqSide)
        || !int.TryParse(ebqPositional[4], out var ebqFastBars)
        || !int.TryParse(ebqPositional[5], out var ebqSlowBars))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- entry-bar-quality <fromDate> <toDate> <side:Call|Put> <fastBars> <slowBars> [thresholdPct=5] [barVolumeThreshold=2600] [--minprice=] [--maxprice=]");
        return 1;
    }

    var ebqThresholdPct = ebqPositional.Length > 6 ? double.Parse(ebqPositional[6]) : 5.0;
    var ebqBarVolumeThreshold = ebqPositional.Length > 7 ? long.Parse(ebqPositional[7]) : 2600L;
    var ebqMinPrice = ebqNamed.TryGetValue("minprice", out var ebqMinPriceStr) ? (decimal?)decimal.Parse(ebqMinPriceStr) : null;
    var ebqMaxPrice = ebqNamed.TryGetValue("maxprice", out var ebqMaxPriceStr) ? (decimal?)decimal.Parse(ebqMaxPriceStr) : null;

    var ebqRows = new List<(VolumeBarTrade Trade, int TickCount, double DurationSeconds)>();
    for (var date = ebqFromDate; date <= ebqToDate; date = date.AddDays(1))
    {
        await using var ebqSource = new NiftySignalDbContext(tradeSourceOptions);
        await using var ebqVolumeBars = new VolumeBarDbContext(volumeBarOptions);
        var dayTrades = await TradeSimulator.SimulatePriceCrossoverDayAsync(ebqSource, ebqVolumeBars, date, ebqBarVolumeThreshold, ebqSide, ebqFastBars, ebqSlowBars, ebqThresholdPct / 100.0, CancellationToken.None, sharedOptionPriceCache, minEntryPrice: ebqMinPrice, maxEntryPrice: ebqMaxPrice);
        if (dayTrades.Count == 0)
        {
            continue;
        }

        var dayBars = await ebqVolumeBars.VolumeBars
            .Where(b => b.AsOfDate == date && b.BarVolumeThreshold == ebqBarVolumeThreshold)
            .ToListAsync();
        foreach (var t in dayTrades)
        {
            var entryBar = dayBars.FirstOrDefault(b => b.EndTimestamp == t.EntryTime);
            if (entryBar is not null)
            {
                ebqRows.Add((t, entryBar.TickCount, entryBar.DurationSeconds));
            }
        }
    }

    if (ebqRows.Count == 0)
    {
        Console.WriteLine("No trades fired across the requested range.");
        return 0;
    }

    Console.WriteLine($"=== Entry-bar quality check: {ebqRows.Count} trades, side={ebqSide} fast={ebqFastBars} slow={ebqSlowBars} thr={ebqThresholdPct}% bar={ebqBarVolumeThreshold} ===");
    Console.WriteLine($"{"EntryTime",10} | {"TickCount",9} | {"DurationSec",11} | {"NetPnl",8} | Win?");
    foreach (var r in ebqRows.OrderBy(r => r.Trade.EntryTime))
    {
        Console.WriteLine($"{FormatIst(r.Trade.EntryTime),10} | {r.TickCount,9} | {r.DurationSeconds,11:F1} | {r.Trade.NetPnlPoints,8:F2} | {(r.Trade.NetPnlPoints > 0 ? "W" : "L")}");
    }

    var winners = ebqRows.Where(r => r.Trade.NetPnlPoints > 0).ToList();
    var losers = ebqRows.Where(r => r.Trade.NetPnlPoints <= 0).ToList();
    Console.WriteLine();
    Console.WriteLine($"--- Winners (n={winners.Count}): avg TickCount={(winners.Count > 0 ? winners.Average(r => r.TickCount) : 0):F1}, avg DurationSec={(winners.Count > 0 ? winners.Average(r => r.DurationSeconds) : 0):F1} ---");
    Console.WriteLine($"--- Losers  (n={losers.Count}): avg TickCount={(losers.Count > 0 ? losers.Average(r => r.TickCount) : 0):F1}, avg DurationSec={(losers.Count > 0 ? losers.Average(r => r.DurationSeconds) : 0):F1} ---");

    // Tercile split by TickCount (bar "busyness") and by DurationSeconds (bar "speed") --
    // reports win rate in each tercile so a relationship (if any) is visible directly, not just
    // as an average difference that could hide a non-monotonic pattern.
    void PrintTerciles(string label, Func<(VolumeBarTrade Trade, int TickCount, double DurationSeconds), double> selector)
    {
        var sorted = ebqRows.OrderBy(selector).ToList();
        var third = Math.Max(1, sorted.Count / 3);
        var low = sorted.Take(third).ToList();
        var high = sorted.Skip(sorted.Count - third).ToList();
        var mid = sorted.Skip(third).Take(sorted.Count - 2 * third).ToList();
        static string Stat(List<(VolumeBarTrade Trade, int TickCount, double DurationSeconds)> g)
            => g.Count > 0 ? $"n={g.Count}, win%={100.0 * g.Count(r => r.Trade.NetPnlPoints > 0) / g.Count:F1}, net={g.Sum(r => r.Trade.NetPnlPoints):F2}" : "n=0";
        Console.WriteLine($"--- {label} terciles: LOW [{Stat(low)}]  MID [{Stat(mid)}]  HIGH [{Stat(high)}] ---");
    }

    PrintTerciles("TickCount", r => r.TickCount);
    PrintTerciles("DurationSeconds", r => r.DurationSeconds);

    return 0;
}

// "atm-drift-check" -- 2026-09-23 correctness-review diagnostic, user's explicit request: confirm
// SimulatePriceCrossoverDayAsync's rolling-ATM signal price doesn't silently splice together
// different strikes' premiums within one crossover window (e.g. computing a "fast MA" partly from
// 24500CE and partly from 24400CE without anyone noticing). Read-only, no trade logic touched --
// runs the REAL price-crossover simulator to get the actual trades fired, then independently
// recomputes the Call/Put ATM strike at every bar (same AtmStrikeSelector.PickAtm call the
// simulator itself uses) and checks whether the ATM strike stayed constant across the trailing
// slowBars-wide window ending at each trade's own entry bar -- i.e., was the fast/slow MA that
// decided to enter actually built from ONE instrument's price history, or from a strike that
// rolled underneath the measurement.
//   dotnet run --project NiftySignal.VolumeBarData -- atm-drift-check <fromDate> <toDate> <side:Call|Put> <fastBars> <slowBars> [thresholdPct=5] [barVolumeThreshold=2600] [--minprice=] [--maxprice=]
if (args.Length > 0 && string.Equals(args[0], "atm-drift-check", StringComparison.OrdinalIgnoreCase))
{
    var (adPositional, adNamed) = SplitNamedArgs(args);
    if (adPositional.Length < 6
        || !DateOnly.TryParseExact(adPositional[1], "yyyy-MM-dd", out var adFromDate)
        || !DateOnly.TryParseExact(adPositional[2], "yyyy-MM-dd", out var adToDate)
        || !Enum.TryParse<OptionType>(adPositional[3], ignoreCase: true, out var adSide)
        || !int.TryParse(adPositional[4], out var adFastBars)
        || !int.TryParse(adPositional[5], out var adSlowBars))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- atm-drift-check <fromDate> <toDate> <side:Call|Put> <fastBars> <slowBars> [thresholdPct=5] [barVolumeThreshold=2600] [--minprice=] [--maxprice=]");
        return 1;
    }

    var adThresholdPct = adPositional.Length > 6 ? double.Parse(adPositional[6]) : 5.0;
    var adBarVolumeThreshold = adPositional.Length > 7 ? long.Parse(adPositional[7]) : 2600L;
    var adMinPrice = adNamed.TryGetValue("minprice", out var adMinPriceStr) ? (decimal?)decimal.Parse(adMinPriceStr) : null;
    var adMaxPrice = adNamed.TryGetValue("maxprice", out var adMaxPriceStr) ? (decimal?)decimal.Parse(adMaxPriceStr) : null;
    var adCrossoverSide = adSide == OptionType.Call ? PriceCrossoverSide.Call : PriceCrossoverSide.Put;

    Console.WriteLine($"=== ATM drift check: side={adSide} fast={adFastBars} slow={adSlowBars} thr={adThresholdPct}% barThreshold={adBarVolumeThreshold}{(adMinPrice is not null || adMaxPrice is not null ? $" entryPriceBand=[{adMinPrice},{adMaxPrice}]" : "")} ===");
    var adStableCount = 0;
    var adRolledCount = 0;
    var adTotalTrades = 0;
    for (var date = adFromDate; date <= adToDate; date = date.AddDays(1))
    {
        await using var adSource1 = new NiftySignalDbContext(tradeSourceOptions);
        await using var adVolumeBars1 = new VolumeBarDbContext(volumeBarOptions);
        var adTrades = await TradeSimulator.SimulatePriceCrossoverDayAsync(adSource1, adVolumeBars1, date, adBarVolumeThreshold, adCrossoverSide, adFastBars, adSlowBars, adThresholdPct / 100.0, CancellationToken.None, sharedOptionPriceCache, bandWidth: null, minEntryPrice: adMinPrice, maxEntryPrice: adMaxPrice);
        if (adTrades.Count == 0)
        {
            continue;
        }

        await using var adSource2 = new NiftySignalDbContext(tradeSourceOptions);
        await using var adVolumeBars2 = new VolumeBarDbContext(volumeBarOptions);
        var adBars = await adVolumeBars2.VolumeBars
            .Where(b => b.AsOfDate == date && b.BarVolumeThreshold == adBarVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync();
        var adAllOptions = await adSource2.Instruments
            .Where(i => i.AsOfDate == date && i.InstrumentType == NiftySignal.Domain.Enums.InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .ToListAsync();
        if (adBars.Count == 0 || adAllOptions.Count == 0)
        {
            continue;
        }

        var adNearestExpiry = adAllOptions.Select(o => o.ExpiryDate!.Value).Min();
        var adChain = adAllOptions.Where(o => o.ExpiryDate == adNearestExpiry).ToList();
        // Same PickAtm call the real simulator uses, recomputed independently per bar for this check.
        var adAtmStrikePerBar = adBars.Select(b => NiftySignal.Scoring.AtmStrikeSelector.PickAtm(adChain, adSide, b.ClosePrice)?.StrikePrice).ToList();

        foreach (var trade in adTrades)
        {
            var entryBarIndex = adBars.FindIndex(b => b.EndTimestamp == trade.EntryTime);
            if (entryBarIndex < 0)
            {
                Console.WriteLine($"  {date:yyyy-MM-dd} {FormatIst(trade.EntryTime)}: WARNING -- could not locate entry bar, skipping");
                continue;
            }

            var windowStart = Math.Max(0, entryBarIndex - adSlowBars + 1);
            var strikesInWindow = adAtmStrikePerBar.Skip(windowStart).Take(entryBarIndex - windowStart + 1).Distinct().ToList();
            adTotalTrades++;
            if (strikesInWindow.Count <= 1)
            {
                adStableCount++;
            }
            else
            {
                adRolledCount++;
                Console.WriteLine($"  {date:yyyy-MM-dd} {FormatIst(trade.EntryTime)} entryStrike={trade.StrikePrice}: ATM ROLLED within the trailing {adSlowBars}-bar signal window -- strikes seen: {string.Join(" -> ", strikesInWindow)}");
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine($"--- ATM drift summary: {adTotalTrades} trades checked, {adStableCount} with a STABLE signal-window ATM strike, {adRolledCount} where ATM rolled mid-window ({(adTotalTrades > 0 ? 100.0 * adRolledCount / adTotalTrades : 0):F1}%) ---");

    // 2026-09-23: day-level report independent of whether any trades fired at all -- explains WHY a
    // reset-on-roll fix can end up firing zero trades (if the ATM strike changes almost every bar,
    // a slowBars-long same-strike run practically never accumulates, regardless of any crossover
    // logic). Reports raw strike-change frequency and the longest same-strike run, per date.
    Console.WriteLine();
    Console.WriteLine("--- Day-level ATM strike stability (independent of trades) ---");
    for (var date = adFromDate; date <= adToDate; date = date.AddDays(1))
    {
        await using var adSource3 = new NiftySignalDbContext(tradeSourceOptions);
        await using var adVolumeBars3 = new VolumeBarDbContext(volumeBarOptions);
        var adDayBars = await adVolumeBars3.VolumeBars
            .Where(b => b.AsOfDate == date && b.BarVolumeThreshold == adBarVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync();
        var adDayOptions = await adSource3.Instruments
            .Where(i => i.AsOfDate == date && i.InstrumentType == NiftySignal.Domain.Enums.InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .ToListAsync();
        if (adDayBars.Count == 0 || adDayOptions.Count == 0)
        {
            continue;
        }

        var adDayExpiry = adDayOptions.Select(o => o.ExpiryDate!.Value).Min();
        var adDayChain = adDayOptions.Where(o => o.ExpiryDate == adDayExpiry).ToList();
        var adDayStrikes = adDayBars.Select(b => NiftySignal.Scoring.AtmStrikeSelector.PickAtm(adDayChain, adSide, b.ClosePrice)?.StrikePrice).ToList();

        var adChanges = 0;
        var adLongestRun = 0;
        var adCurrentRun = 0;
        decimal? adPrev = null;
        foreach (var s in adDayStrikes)
        {
            if (s is null)
            {
                continue;
            }
            if (adPrev is { } p && s != p)
            {
                adChanges++;
                adCurrentRun = 1;
            }
            else
            {
                adCurrentRun++;
            }
            adLongestRun = Math.Max(adLongestRun, adCurrentRun);
            adPrev = s;
        }

        Console.WriteLine($"  {date:yyyy-MM-dd}: {adDayBars.Count} bars, {adDayStrikes.Distinct().Count()} distinct ATM strikes seen, {adChanges} strike changes, longest same-strike run = {adLongestRun} bars (need >= {adSlowBars} for this window's slow leg to ever warm up)");
    }

    return 0;
}

// "momentum-relationship" -- 2026-09-22 relationship-research-matrix task. Measures whether
// option-premium Spread/Velocity has a real predictive relationship with NIFTY's OWN forward
// return, separately from whether it merely predicts the option's OWN forward return (the
// autocorrelation-vs-prediction distinction this task exists to settle). Every forward-return
// number this prints is a forward-looking RESEARCH LABEL, not a tradeable signal -- see
// MomentumRelationshipAnalyzer's own doc comment. Never opens a trade, never touches
// TradeSimulator's dispatch chain.
if (args.Length > 0 && string.Equals(args[0], "momentum-relationship", StringComparison.OrdinalIgnoreCase))
{
    var (mrPositional, mrNamed) = SplitNamedArgs(args);
    if (mrPositional.Length < 6
        || !DateOnly.TryParseExact(mrPositional[1], "yyyy-MM-dd", out var mrFromDate)
        || !DateOnly.TryParseExact(mrPositional[2], "yyyy-MM-dd", out var mrToDate)
        || !Enum.TryParse<OptionType>(mrPositional[3], ignoreCase: true, out var mrSide)
        || !int.TryParse(mrPositional[4], out var mrFastBars)
        || !int.TryParse(mrPositional[5], out var mrSlowBars))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- momentum-relationship <fromDate> <toDate> <side:Call|Put> <fastBars> <slowBars> [barVolumeThreshold=2600] [--band=3|5] [--strikemode=Rolling|SignalFixed] [--buckets=8]");
        return 1;
    }

    var mrBarVolumeThreshold = mrPositional.Length > 6 ? long.Parse(mrPositional[6]) : 2600L;
    var mrBandWidth = mrNamed.TryGetValue("band", out var mrBandStr) ? (int?)int.Parse(mrBandStr) : null;
    var mrStrikeMode = mrNamed.TryGetValue("strikemode", out var mrStrikeModeStr)
        ? Enum.Parse<MomentumRelationshipAnalyzer.StrikeMode>(mrStrikeModeStr, ignoreCase: true)
        : MomentumRelationshipAnalyzer.StrikeMode.Rolling;
    var mrBuckets = mrNamed.TryGetValue("buckets", out var mrBucketsStr) ? int.Parse(mrBucketsStr) : 8;

    Console.WriteLine($"=== Momentum relationship research: side={mrSide} fast={mrFastBars} slow={mrSlowBars} bar={mrBarVolumeThreshold} strikeMode={mrStrikeMode}{(mrBandWidth is { } mrbw ? $" band={mrbw}" : " single ATM")} ===");

    var mrAllSamples = new List<MomentumRelationshipAnalyzer.Sample>();
    for (var date = mrFromDate; date <= mrToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var daySamples = await MomentumRelationshipAnalyzer.CollectDayAsync(source, volumeBars, date, mrBarVolumeThreshold, mrSide, mrFastBars, mrSlowBars, mrStrikeMode, mrBandWidth, CancellationToken.None, sharedOptionPriceCache);
        mrAllSamples.AddRange(daySamples);
    }

    Console.WriteLine($"Pooled samples with a real Spread reading: {mrAllSamples.Count}");

    static double? Pearson(IEnumerable<(double X, double Y)> pairs)
    {
        var list = pairs.ToList();
        if (list.Count < 3)
        {
            return null;
        }

        var mx = list.Average(p => p.X);
        var my = list.Average(p => p.Y);
        var cov = list.Sum(p => (p.X - mx) * (p.Y - my));
        var sx = Math.Sqrt(list.Sum(p => (p.X - mx) * (p.X - mx)));
        var sy = Math.Sqrt(list.Sum(p => (p.Y - my) * (p.Y - my)));
        return sx > 0 && sy > 0 ? cov / (sx * sy) : null;
    }

    void PrintHorizon(string label, Func<MomentumRelationshipAnalyzer.Sample, double?> niftyFwd, Func<MomentumRelationshipAnalyzer.Sample, double?> optFwd)
    {
        var spreadNifty = Pearson(mrAllSamples.Where(s => niftyFwd(s) is not null).Select(s => (s.Spread, niftyFwd(s)!.Value)));
        var spreadOpt = Pearson(mrAllSamples.Where(s => optFwd(s) is not null).Select(s => (s.Spread, optFwd(s)!.Value)));
        var velNifty = Pearson(mrAllSamples.Where(s => s.Velocity is not null && niftyFwd(s) is not null).Select(s => (s.Velocity!.Value, niftyFwd(s)!.Value)));
        var velOpt = Pearson(mrAllSamples.Where(s => s.Velocity is not null && optFwd(s) is not null).Select(s => (s.Velocity!.Value, optFwd(s)!.Value)));
        Console.WriteLine($"--- Horizon {label} bars --- Pearson r: Spread-vs-NiftyFwd={spreadNifty:F4}  Spread-vs-OwnOptionFwd={spreadOpt:F4}  Velocity-vs-NiftyFwd={velNifty:F4}  Velocity-vs-OwnOptionFwd={velOpt:F4}");

        var buckets = MomentumRelationshipAnalyzer.BucketByQuantile(mrAllSamples, mrBuckets, niftyFwd, optFwd);
        Console.WriteLine($"{"SpreadBucket",-24} | {"n",5} | {"MeanNiftyFwd%",13} | {"MedNiftyFwd%",12} | {"P(Up)%",7} | {"MeanOwnOptFwd%",14} | {"MedOwnOptFwd%",13}");
        foreach (var b in buckets)
        {
            Console.WriteLine($"{b.BucketLabel,-24} | {b.Count,5} | {b.MeanNiftyFwd,13:F3} | {b.MedianNiftyFwd,12:F3} | {b.ProbNiftyUp,7:F1} | {b.MeanOptionFwd,14:F3} | {b.MedianOptionFwd,13:F3}");
        }
    }

    PrintHorizon("5", s => s.NiftyFwd5, s => s.OptionFwd5);
    PrintHorizon("10", s => s.NiftyFwd10, s => s.OptionFwd10);
    PrintHorizon("20", s => s.NiftyFwd20, s => s.OptionFwd20);

    return 0;
}

// "tick-activity-research" -- 2026-09-22 tick-activity research task, Parts 3-8. Reads only
// VolumeBarRow (no option data) -- tests whether TickActivityFeatures (tick density/velocity,
// price/range efficiency, churn) carries real forward-looking information about NIFTY FUTURE's own
// next move, on its own and against FutureDepthImbalance. Every forward number printed is a
// RESEARCH LABEL (computed from bars strictly after the reading), never a tradeable signal --
// same discipline as momentum-relationship. Never opens a trade.
if (args.Length > 0 && string.Equals(args[0], "tick-activity-research", StringComparison.OrdinalIgnoreCase))
{
    var (taPositional, taNamed) = SplitNamedArgs(args);
    if (taPositional.Length < 3
        || !DateOnly.TryParseExact(taPositional[1], "yyyy-MM-dd", out var taFromDate)
        || !DateOnly.TryParseExact(taPositional[2], "yyyy-MM-dd", out var taToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- tick-activity-research <fromDate> <toDate> [barVolumeThreshold=1300] [--buckets=8] [--rolling=75]");
        return 1;
    }

    var taBarVolumeThreshold = taPositional.Length > 3 ? long.Parse(taPositional[3]) : 1300L;
    var taBuckets = taNamed.TryGetValue("buckets", out var taBucketsStr) ? int.Parse(taBucketsStr) : 8;
    var taRolling = taNamed.TryGetValue("rolling", out var taRollingStr) ? int.Parse(taRollingStr) : 75;

    Console.WriteLine($"=== Tick-activity research: bar={taBarVolumeThreshold} range={taFromDate:yyyy-MM-dd}..{taToDate:yyyy-MM-dd} ===");

    var taAllSamples = new List<TickActivityAnalyzer.Sample>();
    var taDayCount = 0;
    for (var date = taFromDate; date <= taToDate; date = date.AddDays(1))
    {
        await using var taVolumeBars = new VolumeBarDbContext(volumeBarOptions);
        var taBars = await taVolumeBars.VolumeBars
            .Where(b => b.AsOfDate == date && b.BarVolumeThreshold == taBarVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync();
        if (taBars.Count == 0)
        {
            continue;
        }

        taDayCount++;
        taAllSamples.AddRange(TickActivityAnalyzer.CollectDay(taBars));
    }

    taAllSamples = TickActivityAnalyzer.RankSamples(taAllSamples, taRolling);
    Console.WriteLine($"Days with data: {taDayCount}, pooled bars: {taAllSamples.Count}");

    // --- Part 4/5: bucket each key feature against forward Nifty return, at 5/10/20 bars. ---
    void PrintBuckets(string featureName, Func<TickActivityAnalyzer.Sample, double?> selector)
    {
        foreach (var (label, fwd, mfe, mae) in new[] { ("5", (Func<TickActivityAnalyzer.Sample, double?>)(s => s.NiftyFwd5), (Func<TickActivityAnalyzer.Sample, double?>)(s => s.Mfe5), (Func<TickActivityAnalyzer.Sample, double?>)(s => s.Mae5)), ("10", s => s.NiftyFwd10, s => s.Mfe10, s => s.Mae10), ("20", s => s.NiftyFwd20, s => s.Mfe20, s => s.Mae20) })
        {
            var buckets = TickActivityAnalyzer.BucketByQuantile(taAllSamples, taBuckets, selector, fwd, mfe, mae);
            Console.WriteLine($"--- {featureName} buckets, horizon {label} bars ---");
            Console.WriteLine($"{"Bucket",-28} | {"n",5} | {"MeanFwd%",9} | {"MedFwd%",8} | {"P(Up)%",7} | {"MeanAbsFwd%",11} | {"MeanMFE%",9} | {"MeanMAE%",9}");
            foreach (var b in buckets)
            {
                Console.WriteLine($"{b.Label,-28} | {b.Count,5} | {b.MeanFwd,9:F3} | {b.MedianFwd,8:F3} | {b.ProbUp,7:F1} | {b.MeanAbsFwd,11:F3} | {b.MeanMfe,9:F3} | {b.MeanMae,9:F3}");
            }
        }
    }

    PrintBuckets("TickDensity", s => s.Features.TickDensity);
    PrintBuckets("TickVelocity", s => s.Features.TickVelocity);
    PrintBuckets("PriceEfficiency", s => s.Features.PriceEfficiency);
    PrintBuckets("Churn", s => s.Features.Churn);

    // --- Part 3: raw vs session-percentile vs rolling-percentile informativeness (Pearson r vs NiftyFwd10). ---
    static double? Pearson(IEnumerable<(double X, double Y)> pairs)
    {
        var list = pairs.ToList();
        if (list.Count < 3)
        {
            return null;
        }

        var mx = list.Average(p => p.X);
        var my = list.Average(p => p.Y);
        var cov = list.Sum(p => (p.X - mx) * (p.Y - my));
        var sx = Math.Sqrt(list.Sum(p => (p.X - mx) * (p.X - mx)));
        var sy = Math.Sqrt(list.Sum(p => (p.Y - my) * (p.Y - my)));
        return sx > 0 && sy > 0 ? cov / (sx * sy) : null;
    }

    void PrintNormalizationComparison(string name, Func<TickActivityAnalyzer.Sample, double?> raw, Func<TickActivityAnalyzer.Sample, double?> sessionPct, Func<TickActivityAnalyzer.Sample, double?> rollingPct)
    {
        double? RRaw(Func<TickActivityAnalyzer.Sample, double?> sel) => Pearson(taAllSamples.Where(s => sel(s) is not null && s.NiftyFwd10 is not null).Select(s => (sel(s)!.Value, s.NiftyFwd10!.Value)));
        Console.WriteLine($"Part3 {name}: r(raw)={RRaw(raw):F4}  r(sessionPct)={RRaw(sessionPct):F4}  r(rollingPct)={RRaw(rollingPct):F4}  [vs NiftyFwd10]");
    }

    PrintNormalizationComparison("TickDensity", s => s.Features.TickDensity, s => s.TickDensitySessionPct, s => s.TickDensityRollingPct);
    PrintNormalizationComparison("TickVelocity", s => s.Features.TickVelocity, s => s.TickVelocitySessionPct, s => s.TickVelocityRollingPct);
    PrintNormalizationComparison("PriceEfficiency", s => s.Features.PriceEfficiency, s => s.PriceEfficiencySessionPct, s => s.PriceEfficiencyRollingPct);
    PrintNormalizationComparison("Churn", s => s.Features.Churn, s => s.ChurnSessionPct, s => s.ChurnRollingPct);

    // --- Part 6: 2x2 Activity x Efficiency state split. ---
    Console.WriteLine("--- Part 6: 2x2 TickVelocity x PriceEfficiency states (horizon 10 bars) ---");
    var taStates = TickActivityAnalyzer.TwoByTwoStates(taAllSamples, s => s.NiftyFwd10, s => s.Mfe10, s => s.Mae10);
    Console.WriteLine($"{"State",-32} | {"n",5} | {"P(Up)%",7} | {"MeanFwd%",9} | {"MeanAbsFwd%",11} | {"MeanMFE%",9} | {"MeanMAE%",9}");
    foreach (var st in taStates)
    {
        Console.WriteLine($"{st.State,-32} | {st.Count,5} | {st.ProbUp,7:F1} | {st.MeanFwd,9:F3} | {st.MeanAbsFwd,11:F3} | {st.MeanMfe,9:F3} | {st.MeanMae,9:F3}");
    }

    // High-activity+high-efficiency split further by direction (Part 6's second requirement).
    var hahe = taAllSamples.Where(s => s.Features.TickVelocity is not null && s.Features.PriceEfficiency is not null).ToList();
    if (hahe.Count >= 4)
    {
        var actMed = hahe.Select(s => s.Features.TickVelocity!.Value).OrderBy(v => v).ToList();
        var effMed = hahe.Select(s => s.Features.PriceEfficiency!.Value).OrderBy(v => v).ToList();
        var aM = actMed[actMed.Count / 2];
        var eM = effMed[effMed.Count / 2];
        var haheOnly = hahe.Where(s => s.Features.TickVelocity!.Value >= aM && s.Features.PriceEfficiency!.Value >= eM).ToList();
        var posMove = haheOnly.Where(s => s.Features.Direction > 0).ToList();
        var negMove = haheOnly.Where(s => s.Features.Direction < 0).ToList();
        Console.WriteLine($"HighAct+HighEff, positive-NetMove bars (n={posMove.Count}): meanFwd10%={(posMove.Count > 0 ? posMove.Where(s => s.NiftyFwd10 is not null).Select(s => s.NiftyFwd10!.Value).DefaultIfEmpty(0).Average() * 100 : 0):F3}, P(Up)%={(posMove.Count(s => s.NiftyFwd10 is not null) > 0 ? 100.0 * posMove.Count(s => s.NiftyFwd10 > 0) / posMove.Count(s => s.NiftyFwd10 is not null) : 0):F1}");
        Console.WriteLine($"HighAct+HighEff, negative-NetMove bars (n={negMove.Count}): meanFwd10%={(negMove.Count > 0 ? negMove.Where(s => s.NiftyFwd10 is not null).Select(s => s.NiftyFwd10!.Value).DefaultIfEmpty(0).Average() * 100 : 0):F3}, P(Up)%={(negMove.Count(s => s.NiftyFwd10 is not null) > 0 ? 100.0 * negMove.Count(s => s.NiftyFwd10 > 0) / negMove.Count(s => s.NiftyFwd10 is not null) : 0):F1}");

        var haleZero = hahe.Where(s => s.Features.TickVelocity!.Value >= aM && s.Features.PriceEfficiency!.Value < eM && Math.Abs((double)s.Features.NetMove) <= (double)hahe.Select(x => Math.Abs(x.Features.NetMove)).OrderBy(x => x).ElementAt(hahe.Count / 4)).ToList();
        var haleFwd = haleZero.Where(s => s.NiftyFwd10 is not null).Select(s => s.NiftyFwd10!.Value).ToList();
        Console.WriteLine($"HighAct+LowEff+near-zero-NetMove ('struggle') bars (n={haleZero.Count}): meanFwd10%={(haleFwd.Count > 0 ? haleFwd.Average() * 100 : 0):F3}, meanAbsFwd10%={(haleFwd.Count > 0 ? haleFwd.Select(Math.Abs).Average() * 100 : 0):F3} -- investigates activity->struggle->release hypothesis, does NOT assume it");
    }

    // --- Part 7: transitions vs levels -- does a rapid Delta predict better than the raw level? ---
    Console.WriteLine("--- Part 7: transition (Delta) vs level, Pearson r vs NiftyFwd10 ---");
    Console.WriteLine($"TickDensity:    level r={Pearson(taAllSamples.Where(s => s.Features.TickDensity is not null && s.NiftyFwd10 is not null).Select(s => (s.Features.TickDensity!.Value, s.NiftyFwd10!.Value))):F4}  delta r={Pearson(taAllSamples.Where(s => s.TickDensityDelta is not null && s.NiftyFwd10 is not null).Select(s => (s.TickDensityDelta!.Value, s.NiftyFwd10!.Value))):F4}");
    Console.WriteLine($"TickVelocity:   level r={Pearson(taAllSamples.Where(s => s.Features.TickVelocity is not null && s.NiftyFwd10 is not null).Select(s => (s.Features.TickVelocity!.Value, s.NiftyFwd10!.Value))):F4}  delta r={Pearson(taAllSamples.Where(s => s.TickVelocityDelta is not null && s.NiftyFwd10 is not null).Select(s => (s.TickVelocityDelta!.Value, s.NiftyFwd10!.Value))):F4}");
    Console.WriteLine($"PriceEfficiency:level r={Pearson(taAllSamples.Where(s => s.Features.PriceEfficiency is not null && s.NiftyFwd10 is not null).Select(s => (s.Features.PriceEfficiency!.Value, s.NiftyFwd10!.Value))):F4}  delta r={Pearson(taAllSamples.Where(s => s.PriceEfficiencyDelta is not null && s.NiftyFwd10 is not null).Select(s => (s.PriceEfficiencyDelta!.Value, s.NiftyFwd10!.Value))):F4}");
    Console.WriteLine($"Churn:          level r={Pearson(taAllSamples.Where(s => s.Features.Churn is not null && s.NiftyFwd10 is not null).Select(s => (s.Features.Churn!.Value, s.NiftyFwd10!.Value))):F4}  delta r={Pearson(taAllSamples.Where(s => s.ChurnDelta is not null && s.NiftyFwd10 is not null).Select(s => (s.ChurnDelta!.Value, s.NiftyFwd10!.Value))):F4}");

    // --- Part 8: Model A (DepthImbalance) vs B (tick features) vs C (both), by session and by DTE. ---
    Console.WriteLine("--- Part 8: Model A (DepthImbalance) vs B (tick features composite) vs C (both) ---");
    var taZeroDte = new HashSet<DateOnly>(taNamed.TryGetValue("zerodte", out var taZeroDteStr) ? taZeroDteStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd")) : [DateOnly.Parse("2026-09-08"), DateOnly.Parse("2026-09-15"), DateOnly.Parse("2026-09-22")]);

    void PrintModelComparison(string scopeLabel, IReadOnlyList<TickActivityAnalyzer.Sample> scope)
    {
        // Model B composite: average of PriceEfficiency and TickVelocity SESSION percentiles
        // (both scaled to a comparable percentile axis) -- a simple, non-overfit linear combination,
        // not a fitted regression (11 days is too small a sample to fit weights honestly).
        double? ModelBScore(TickActivityAnalyzer.Sample s) =>
            s.TickVelocitySessionPct is { } tv && s.PriceEfficiencySessionPct is { } pe ? (tv + pe) / 2.0 : null;

        var rA = Pearson(scope.Where(s => s.DepthImbalance is not null && s.NiftyFwd10 is not null).Select(s => (s.DepthImbalance!.Value, s.NiftyFwd10!.Value)));
        var rB = Pearson(scope.Where(s => ModelBScore(s) is not null && s.NiftyFwd10 is not null).Select(s => (ModelBScore(s)!.Value, s.NiftyFwd10!.Value)));
        // Model C: DepthImbalance direction combined with ModelB score, simple average of z-scored
        // proxies is out of scope for an 11-day sample -- report both correlations side by side and
        // whether they agree in sign (crude "do they carry the same or different information" check)
        // rather than fit a joint model that would be overfit noise.
        Console.WriteLine($"{scopeLabel} (n={scope.Count}): r(DepthImbalance,Fwd10)={rA:F4}  r(TickCompositePct,Fwd10)={rB:F4}  sameSign={(rA is { } a && rB is { } b ? (Math.Sign(a) == Math.Sign(b)).ToString() : "n/a")}");
    }

    PrintModelComparison("ALL", taAllSamples);
    foreach (var sess in new[] { "Open(<10:00)", "Mid(10:00-13:30)", "Close(>13:30)" })
    {
        PrintModelComparison($"Session={sess}", taAllSamples.Where(s => s.Session == sess).ToList());
    }
    PrintModelComparison("DTE=0", taAllSamples.Where(s => taZeroDte.Contains(s.Date)).ToList());
    PrintModelComparison("DTE>0", taAllSamples.Where(s => !taZeroDte.Contains(s.Date)).ToList());

    return 0;
}

// "mean-reversion-validation" -- Experiment 3 (2026-09-22, docs/VOLUME_BAR_FINDINGS.md's
// "Experiment 3" section). Robustness-checks the HighActivity+HighEfficiency, NetMove-conditioned
// mean-reversion split (E4) found by "tick-activity-research": per-day breakdown (all 11 days),
// session breakdown, 0-DTE/non-0-DTE breakdown, 5/10/20-horizon breakdown, and a leave-best-day-out
// recompute. Every forward number is a RESEARCH LABEL, never a tradeable signal. Never opens a trade.
if (args.Length > 0 && string.Equals(args[0], "mean-reversion-validation", StringComparison.OrdinalIgnoreCase))
{
    var (mrPositional, mrNamed) = SplitNamedArgs(args);
    if (mrPositional.Length < 3
        || !DateOnly.TryParseExact(mrPositional[1], "yyyy-MM-dd", out var mrFromDate)
        || !DateOnly.TryParseExact(mrPositional[2], "yyyy-MM-dd", out var mrToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- mean-reversion-validation <fromDate> <toDate> [--barSizes=650,1300,2600] [--zerodte=yyyy-MM-dd,...]");
        return 1;
    }

    var mrBarSizes = mrNamed.TryGetValue("barSizes", out var mrBarSizesStr)
        ? mrBarSizesStr.Split(',').Select(long.Parse).ToList()
        : [650L, 1300L, 2600L];
    var mrZeroDte = new HashSet<DateOnly>(mrNamed.TryGetValue("zerodte", out var mrZeroDteStr)
        ? mrZeroDteStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd"))
        : [DateOnly.Parse("2026-09-08"), DateOnly.Parse("2026-09-15"), DateOnly.Parse("2026-09-22")]);

    foreach (var mrBarSize in mrBarSizes)
    {
        Console.WriteLine($"=== Experiment 3, mean-reversion validation: bar={mrBarSize} range={mrFromDate:yyyy-MM-dd}..{mrToDate:yyyy-MM-dd} ===");

        var mrByDay = new SortedDictionary<DateOnly, List<TickActivityAnalyzer.Sample>>();
        for (var date = mrFromDate; date <= mrToDate; date = date.AddDays(1))
        {
            await using var mrVolumeBars = new VolumeBarDbContext(volumeBarOptions);
            var mrBars = await mrVolumeBars.VolumeBars
                .Where(b => b.AsOfDate == date && b.BarVolumeThreshold == mrBarSize)
                .OrderBy(b => b.BarIndex)
                .ToListAsync();
            if (mrBars.Count == 0)
            {
                continue;
            }

            mrByDay[date] = TickActivityAnalyzer.CollectDay(mrBars);
        }

        var mrPooled = mrByDay.Values.SelectMany(x => x).ToList();
        Console.WriteLine($"Days with data: {mrByDay.Count}, pooled bars: {mrPooled.Count}");

        var mrThreshold = TickActivityAnalyzer.ComputeThreshold(mrPooled);
        Console.WriteLine($"Pooled threshold: TickVelocityMedian={mrThreshold.ActivityMedian:F4} PriceEfficiencyMedian={mrThreshold.EfficiencyMedian:F6}");

        void PrintStat(TickActivityAnalyzer.MeanReversionStat st)
        {
            Console.WriteLine($"{st.Label,-24} | pos: n={st.NPos,5} P(Up)={st.PosProbUp,6:F1}% meanFwd={st.PosMeanFwd,7:F3}% | neg: n={st.NNeg,5} P(Up)={st.NegProbUp,6:F1}% meanFwd={st.NegMeanFwd,7:F3}%");
        }

        // --- Per-day breakdown, horizon=10 (E4's own implicit primary horizon). ---
        Console.WriteLine("--- Per-day breakdown (horizon=10) ---");
        var mrDayScores = new List<(DateOnly Date, double Score)>();
        foreach (var (date, daySamples) in mrByDay)
        {
            var st = TickActivityAnalyzer.MeanReversionSplit(daySamples, date.ToString("yyyy-MM-dd"), mrThreshold, s => s.NiftyFwd10);
            PrintStat(st);
            if (st.NPos > 0 && st.NNeg > 0 && st.PosProbUp is { } posP && st.NegProbUp is { } negP)
            {
                var score = (st.NPos * (50.0 - posP) + st.NNeg * (negP - 50.0)) / (st.NPos + st.NNeg);
                mrDayScores.Add((date, score));
            }
            else
            {
                Console.WriteLine($"  ({date:yyyy-MM-dd} excluded from day-ranking: one or both NetMove cells empty at this bar size)");
            }
        }

        // --- Session breakdown, horizon=10. ---
        Console.WriteLine("--- Session breakdown (horizon=10) ---");
        foreach (var sess in new[] { "Open(<10:00)", "Mid(10:00-13:30)", "Close(>13:30)" })
        {
            PrintStat(TickActivityAnalyzer.MeanReversionSplit(mrPooled.Where(s => s.Session == sess).ToList(), sess, mrThreshold, s => s.NiftyFwd10));
        }

        // --- DTE breakdown, horizon=10. ---
        Console.WriteLine("--- DTE breakdown (horizon=10) ---");
        PrintStat(TickActivityAnalyzer.MeanReversionSplit(mrPooled.Where(s => mrZeroDte.Contains(s.Date)).ToList(), "DTE=0", mrThreshold, s => s.NiftyFwd10));
        PrintStat(TickActivityAnalyzer.MeanReversionSplit(mrPooled.Where(s => !mrZeroDte.Contains(s.Date)).ToList(), "DTE>0", mrThreshold, s => s.NiftyFwd10));

        // --- Horizon breakdown (pooled, all days), 5/10/20. ---
        Console.WriteLine("--- Horizon breakdown (pooled) ---");
        PrintStat(TickActivityAnalyzer.MeanReversionSplit(mrPooled, "Horizon=5", mrThreshold, s => s.NiftyFwd5));
        PrintStat(TickActivityAnalyzer.MeanReversionSplit(mrPooled, "Horizon=10", mrThreshold, s => s.NiftyFwd10));
        PrintStat(TickActivityAnalyzer.MeanReversionSplit(mrPooled, "Horizon=20", mrThreshold, s => s.NiftyFwd20));

        // --- Leave-best-day-out. ---
        Console.WriteLine("--- Leave-best-day-out (horizon=10) ---");
        if (mrDayScores.Count == 0)
        {
            Console.WriteLine("No day had both NetMove cells populated -- cannot identify a best day at this bar size.");
        }
        else
        {
            var best = mrDayScores.OrderByDescending(x => x.Score).First();
            Console.WriteLine($"Best-performing day (highest weighted favorable deviation from 50%): {best.Date:yyyy-MM-dd}, score={best.Score:F3}");
            var mrPooledExclBest = mrByDay.Where(kv => kv.Key != best.Date).SelectMany(kv => kv.Value).ToList();
            PrintStat(TickActivityAnalyzer.MeanReversionSplit(mrPooledExclBest, $"Excl-{best.Date:yyyy-MM-dd}", mrThreshold, s => s.NiftyFwd10));
        }

        Console.WriteLine();
    }

    return 0;
}

// "tradeability-experiment5" -- Experiment 5 (2026-09-22, docs/VOLUME_BAR_FINDINGS.md's "Experiment 5"
// section). Reuses Experiment 3's LOCKED HighActivity+HighEfficiency/NetMove-conditioned mean-reversion
// state definition (TickActivityAnalyzer.ComputeThreshold, unchanged) to ask a DIFFERENT question: does
// that predictive pattern translate into a tradeable option-buying opportunity after real option prices,
// MFE/MAE, holding duration, and cost sensitivity. Research-only -- never touches NiftySignal.Host/
// NiftySignal.Rules.EntryRuleEvaluator/LiveTradingEngine, never adds to the production composite.
if (args.Length > 0 && string.Equals(args[0], "tradeability-experiment5", StringComparison.OrdinalIgnoreCase))
{
    var (e5Positional, e5Named) = SplitNamedArgs(args);
    if (e5Positional.Length < 3
        || !DateOnly.TryParseExact(e5Positional[1], "yyyy-MM-dd", out var e5FromDate)
        || !DateOnly.TryParseExact(e5Positional[2], "yyyy-MM-dd", out var e5ToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- tradeability-experiment5 <fromDate> <toDate> [--barSizes=650,1300,2600] [--zerodte=yyyy-MM-dd,...]");
        return 1;
    }

    var e5BarSizes = e5Named.TryGetValue("barSizes", out var e5BarSizesStr)
        ? e5BarSizesStr.Split(',').Select(long.Parse).ToList()
        : [650L, 1300L, 2600L];
    var e5ZeroDte = new HashSet<DateOnly>(e5Named.TryGetValue("zerodte", out var e5ZeroDteStr)
        ? e5ZeroDteStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd"))
        : [DateOnly.Parse("2026-09-08"), DateOnly.Parse("2026-09-15"), DateOnly.Parse("2026-09-22")]);

    const int e5PrimaryHorizon = 10;
    var e5Costs = new CostsConfig(20m, 2);
    const int e5LotSize = 65;
    const int e5LotsPerTrade = 2;
    var e5Quantity = e5LotSize * e5LotsPerTrade;

    void PrintUnderlyingTable(string label, List<Experiment5UnderlyingAnalyzer.UnderlyingHorizonStat> stats)
    {
        Console.WriteLine($"--- Underlying (Nifty) forward outcomes: {label} ---");
        Console.WriteLine($"{"H",3} | {"n",5} | {"MeanFwd%",8} | {"AbsFwd%",7} | {"P(Up)%",7} | {"MeanMFE%",8} | {"MeanMAE%",8} | {"BarsToMFE",9} | {"BarsToMAE",9} | {"RevProb%",8} | {"FavEx%",7} | {"AdvEx%",7}");
        foreach (var s in stats)
        {
            Console.WriteLine($"{s.Horizon,3} | {s.N,5} | {s.MeanFwdPct,8:F3} | {s.MeanAbsFwdPct,7:F3} | {s.ProbUpPct,7:F1} | {s.MeanMfePct,8:F3} | {s.MeanMaePct,8:F3} | {s.MeanBarsToMfe,9:F2} | {s.MeanBarsToMae,9:F2} | {s.ReversalProbPct,8:F1} | {s.MeanReversalFavorableExcursionPct,7:F3} | {s.MeanReversalAdverseExcursionPct,7:F3}");
        }
    }

    void PrintTradeStats(string label, List<Experiment5OptionTradeSimulator.OptionTrade> trades, bool net)
    {
        var pnls = trades.OrderBy(t => t.Date).ThenBy(t => t.BarIndex)
            .Select(t => net ? t.NetPnlLot : t.GrossPnlLot).ToList();
        var mfePct = trades.Select(t => (double?)t.MfePercent).ToList();
        var maePct = trades.Select(t => (double?)t.MaePercent).ToList();
        var hold = trades.Select(t => t.HoldingMinutes).ToList();
        var st = OptionTradeQualityStats.Compute(pnls, mfePct, maePct, hold);
        Console.WriteLine($"{label} [{(net ? "NET" : "GROSS")}]: n={st.Count} win%={st.WinRatePct:F1} avgWin={st.AvgWinner:F1} avgLoss={st.AvgLoser:F1} medWin={st.MedianWinner:F1} medLoss={st.MedianLoser:F1} PF={st.ProfitFactor:F2} expect={st.Expectancy:F1} net={st.NetPnl:F1} maxDD={st.MaxDrawdown:F1} MFE%={st.MeanMfe:F2} MAE%={st.MeanMae:F2} medMFE%={st.MedianMfe:F2} medMAE%={st.MedianMae:F2} avgHoldMin={st.AvgHoldingMinutes:F1} medHoldMin={st.MedianHoldingMinutes:F1} top1%={st.TopKContributionPct[1]:F1} top3%={st.TopKContributionPct[3]:F1} top5%={st.TopKContributionPct[5]:F1}");
    }

    foreach (var e5BarSize in e5BarSizes)
    {
        Console.WriteLine($"=== Experiment 5, bar={e5BarSize} range={e5FromDate:yyyy-MM-dd}..{e5ToDate:yyyy-MM-dd} ===");

        var e5ByDayBars = new SortedDictionary<DateOnly, List<VolumeBarRow>>();
        var e5ByDayAtm = new Dictionary<DateOnly, Dictionary<int, OptionAtmBarRow>>();
        var e5ByDayDte = new Dictionary<DateOnly, (int Dte, bool IsZeroDte)>();

        for (var date = e5FromDate; date <= e5ToDate; date = date.AddDays(1))
        {
            await using var e5VolumeBars = new VolumeBarDbContext(volumeBarOptions);
            var e5Bars = await e5VolumeBars.VolumeBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == e5BarSize).OrderBy(b => b.BarIndex).ToListAsync();
            if (e5Bars.Count == 0)
            {
                continue;
            }

            var e5Atm = await e5VolumeBars.OptionAtmBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == e5BarSize).ToDictionaryAsync(b => b.BarIndex);
            e5ByDayBars[date] = e5Bars;
            e5ByDayAtm[date] = e5Atm;

            await using var e5Source = new NiftySignalDbContext(tradeSourceOptions);
            var e5DayOptions = await e5Source.Instruments.Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY").ToListAsync();
            var e5NearestExpiry = e5DayOptions.Count > 0 ? e5DayOptions.Select(i => i.ExpiryDate!.Value).Min() : (DateOnly?)null;
            var e5Dte = e5NearestExpiry is { } exp ? exp.DayNumber - date.DayNumber : 0;
            e5ByDayDte[date] = (e5Dte, e5ZeroDte.Contains(date));
        }

        Console.WriteLine($"Days with data: {e5ByDayBars.Count}");
        if (e5ByDayBars.Count == 0)
        {
            continue;
        }

        // Pooled threshold, reusing Experiment 3's ComputeThreshold verbatim on the SAME pooled sample set.
        var e5PooledSamplesForThreshold = e5ByDayBars.Values.SelectMany(TickActivityAnalyzer.CollectDay).ToList();
        var e5Threshold = TickActivityAnalyzer.ComputeThreshold(e5PooledSamplesForThreshold);
        Console.WriteLine($"Locked threshold (Experiment 3, unchanged): TickVelocityMedian={e5Threshold.ActivityMedian:F4} PriceEfficiencyMedian={e5Threshold.EfficiencyMedian:F6}");

        // Qualifying bars (state D: HighAct+HighEff), per day.
        var e5QualByDay = new Dictionary<DateOnly, List<Experiment5UnderlyingAnalyzer.QualifyingBar>>();
        foreach (var (date, bars) in e5ByDayBars)
        {
            var (dte, isZeroDte) = e5ByDayDte[date];
            e5QualByDay[date] = Experiment5UnderlyingAnalyzer.Collect(bars, e5Threshold, e5ByDayAtm[date], dte, isZeroDte);
        }

        var e5AllQual = e5QualByDay.Values.SelectMany(x => x).ToList();
        var e5Pos = e5AllQual.Where(q => q.Direction > 0).ToList();
        var e5Neg = e5AllQual.Where(q => q.Direction < 0).ToList();
        Console.WriteLine($"Qualifying bars: total={e5AllQual.Count} pos(NetMove>0,->Put)={e5Pos.Count} neg(NetMove<0,->Call)={e5Neg.Count}");

        // --- Predictive results (item 2), before any option simulation. ---
        PrintUnderlyingTable("positive-NetMove cell (ALL days)", Experiment5UnderlyingAnalyzer.SummarizeUnderlying(e5Pos));
        PrintUnderlyingTable("negative-NetMove cell (ALL days)", Experiment5UnderlyingAnalyzer.SummarizeUnderlying(e5Neg));

        // --- Build every option trade (all qualifying bars x all horizons). ---
        var e5AllTrades = new List<Experiment5OptionTradeSimulator.OptionTrade>();
        var e5SkippedNoPrice = 0;
        foreach (var (date, quals) in e5QualByDay)
        {
            if (quals.Count == 0)
            {
                continue;
            }

            await using var e5Source2 = new NiftySignalDbContext(tradeSourceOptions);
            var e5DayOptions2 = await e5Source2.Instruments.Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY").ToListAsync();
            if (e5DayOptions2.Count == 0)
            {
                continue;
            }

            var e5NearestExpiry2 = e5DayOptions2.Select(i => i.ExpiryDate!.Value).Min();
            var e5Chain = e5DayOptions2.Where(i => i.ExpiryDate == e5NearestExpiry2).ToList();
            var e5PriceCache = new Dictionary<(DateOnly, string), OptionPriceSeries>();
            var e5DayBars = e5ByDayBars[date];

            // CORRECTION (2026-09-22): each horizon gets its OWN non-overlapping trade sequence --
            // a bar that qualifies as a new entry at horizon=1 (short hold) may still fall inside a
            // still-open trade at horizon=20 (long hold), so the single-open-trade gate must be
            // applied per horizon, not once for all horizons pooled. See
            // Experiment5OptionTradeSimulator.SelectNonOverlapping's own doc comment for the rule.
            foreach (var h in Experiment5UnderlyingAnalyzer.Horizons)
            {
                var nonOverlapping = Experiment5OptionTradeSimulator.SelectNonOverlapping(quals, h);
                foreach (var qb in nonOverlapping)
                {
                    var trade = await Experiment5OptionTradeSimulator.BuildTradeAsync(e5Source2, qb, h, e5DayBars, e5Chain, e5ByDayAtm[date], e5PriceCache, e5Costs, e5Quantity, CancellationToken.None);
                    if (trade is null)
                    {
                        e5SkippedNoPrice++;
                        continue;
                    }

                    e5AllTrades.Add(trade);
                }
            }
        }

        Console.WriteLine($"Option trades built: {e5AllTrades.Count} (skipped, no tradable instrument/price: {e5SkippedNoPrice})");

        // --- Option tradeability results (items 6/7), per horizon, gross then net. ---
        Console.WriteLine("--- Option tradeability by horizon (ALL qualifying bars, both directions pooled) ---");
        foreach (var h in Experiment5UnderlyingAnalyzer.Horizons)
        {
            var atH = e5AllTrades.Where(t => t.Horizon == h).ToList();
            PrintTradeStats($"Horizon={h}", atH, net: false);
            PrintTradeStats($"Horizon={h}", atH, net: true);
        }

        // --- Case A/B/C (item 8), at the primary horizon. ---
        Console.WriteLine($"--- Case A/B/C breakdown, horizon={e5PrimaryHorizon} (Nifty-reversed x option-P&L-sign) ---");
        var e5AtPrimary = e5AllTrades.Where(t => t.Horizon == e5PrimaryHorizon).ToList();
        var e5QualByKey = e5AllQual.ToDictionary(q => (q.Date, q.BarIndex));
        var e5CaseGroups = e5AtPrimary
            .Select(t => (Trade: t, Reversed: e5QualByKey.TryGetValue((t.Date, t.BarIndex), out var qb) ? qb.Reversed(e5PrimaryHorizon) : null))
            .Where(x => x.Reversed is not null)
            .GroupBy(x => x.Trade.Case(x.Reversed!.Value))
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (caseLabel, group) in e5CaseGroups.OrderBy(kv => kv.Key))
        {
            Console.WriteLine($"Case {caseLabel}: n={group.Count} ({100.0 * group.Count / Math.Max(1, e5AtPrimary.Count):F1}% of horizon={e5PrimaryHorizon} trades)");
        }

        if (e5CaseGroups.TryGetValue("B", out var e5CaseB) && e5CaseB.Count > 0)
        {
            var e5CaseBWithIv = e5CaseB.Where(x => x.Trade.EntryIv is not null && x.Trade.ExitIv is not null).ToList();
            if (e5CaseBWithIv.Count > 0)
            {
                var e5MeanIvChange = e5CaseBWithIv.Average(x => x.Trade.ExitIv!.Value - x.Trade.EntryIv!.Value);
                Console.WriteLine($"Case B (n={e5CaseB.Count}, {e5CaseBWithIv.Count} with entry+exit ATM IV available): mean ATM IV change (exit-entry) = {e5MeanIvChange:F4} -- investigative only, not a proven cause (see doc discussion)");
            }
            else
            {
                Console.WriteLine($"Case B (n={e5CaseB.Count}): no trades had both entry and exit ATM IV available -- insufficient evidence for a specific cause.");
            }
        }

        // --- 0-DTE vs non-0-DTE (item 9). ---
        Console.WriteLine("--- 0-DTE vs non-0-DTE (all horizons) ---");
        foreach (var h in Experiment5UnderlyingAnalyzer.Horizons)
        {
            var atH = e5AllTrades.Where(t => t.Horizon == h);
            PrintTradeStats($"H={h} DTE=0", atH.Where(t => t.IsZeroDte).ToList(), net: true);
            PrintTradeStats($"H={h} DTE>0", atH.Where(t => !t.IsZeroDte).ToList(), net: true);
        }

        // --- Session (item 10). ---
        Console.WriteLine($"--- Session breakdown, horizon={e5PrimaryHorizon} ---");
        foreach (var sess in new[] { "Open(<10:00)", "Mid(10:00-13:30)", "Close(>13:30)" })
        {
            PrintTradeStats(sess, e5AtPrimary.Where(t => t.Session == sess).ToList(), net: true);
        }

        // --- Cost sensitivity (item 13) already printed via gross/net pairs above; explicit summary. ---
        Console.WriteLine($"--- Cost sensitivity summary, horizon={e5PrimaryHorizon} (gross vs net, CostsConfig BrokeragePerOrder=20/SlippageTicks=2) ---");
        PrintTradeStats("ALL", e5AtPrimary, net: false);
        PrintTradeStats("ALL", e5AtPrimary, net: true);

        // --- Robustness: day-level (every day), best-day removal, best-trade removal (item 15). ---
        Console.WriteLine($"--- Day-level breakdown, horizon={e5PrimaryHorizon} (net) ---");
        var e5DayNet = new List<(DateOnly Date, decimal Net)>();
        foreach (var (date, _) in e5ByDayBars)
        {
            var dayTrades = e5AtPrimary.Where(t => t.Date == date).ToList();
            if (dayTrades.Count == 0)
            {
                continue;
            }

            PrintTradeStats(date.ToString("yyyy-MM-dd"), dayTrades, net: true);
            e5DayNet.Add((date, dayTrades.Sum(t => t.NetPnlLot)));
        }

        if (e5DayNet.Count > 0)
        {
            var e5BestDay = e5DayNet.OrderByDescending(x => x.Net).First();
            Console.WriteLine($"Best day by net P&L: {e5BestDay.Date:yyyy-MM-dd} ({e5BestDay.Net:F1})");
            PrintTradeStats($"Excl-{e5BestDay.Date:yyyy-MM-dd}", e5AtPrimary.Where(t => t.Date != e5BestDay.Date).ToList(), net: true);
        }

        var e5ByNetDesc = e5AtPrimary.OrderByDescending(t => t.NetPnlLot).ToList();
        foreach (var k in new[] { 1, 3, 5 })
        {
            if (e5ByNetDesc.Count <= k)
            {
                continue;
            }

            var e5ExclTopK = e5ByNetDesc.Skip(k).ToList();
            PrintTradeStats($"Excl-top{k}-trade(s)", e5ExclTopK, net: true);
        }

        // --- Interaction check (item 16): 4-cell Activity x Efficiency, same tradeability framework, horizon primary only. ---
        Console.WriteLine($"--- Activity x Efficiency interaction check, horizon={e5PrimaryHorizon} (net, both directions pooled) ---");
        foreach (var (label, highAct, highEff) in new[] { ("D: High+High (real signal)", true, true), ("C: High+Low", true, false), ("B: Low+High", false, true), ("A: Low+Low", false, false) })
        {
            var cellQualByDay = new Dictionary<DateOnly, List<Experiment5UnderlyingAnalyzer.QualifyingBar>>();
            foreach (var (date, bars) in e5ByDayBars)
            {
                var (dte, isZeroDte) = e5ByDayDte[date];
                cellQualByDay[date] = Experiment5UnderlyingAnalyzer.CollectByState(bars, e5Threshold, e5ByDayAtm[date], dte, isZeroDte, highAct, highEff);
            }

            var cellTrades = new List<Experiment5OptionTradeSimulator.OptionTrade>();
            foreach (var (date, quals) in cellQualByDay)
            {
                if (quals.Count == 0)
                {
                    continue;
                }

                await using var cellSource = new NiftySignalDbContext(tradeSourceOptions);
                var cellOptions = await cellSource.Instruments.Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY").ToListAsync();
                if (cellOptions.Count == 0)
                {
                    continue;
                }

                var cellExpiry = cellOptions.Select(i => i.ExpiryDate!.Value).Min();
                var cellChain = cellOptions.Where(i => i.ExpiryDate == cellExpiry).ToList();
                var cellCache = new Dictionary<(DateOnly, string), OptionPriceSeries>();
                // CORRECTION (2026-09-22): same single-open-trade gate as the main build loop above.
                var cellNonOverlapping = Experiment5OptionTradeSimulator.SelectNonOverlapping(quals, e5PrimaryHorizon);
                foreach (var qb in cellNonOverlapping)
                {
                    var trade = await Experiment5OptionTradeSimulator.BuildTradeAsync(cellSource, qb, e5PrimaryHorizon, e5ByDayBars[date], cellChain, e5ByDayAtm[date], cellCache, e5Costs, e5Quantity, CancellationToken.None);
                    if (trade is not null)
                    {
                        cellTrades.Add(trade);
                    }
                }
            }

            PrintTradeStats(label, cellTrades, net: true);
        }

        // --- Random baseline (item 17), horizon primary, matched count per day. ---
        Console.WriteLine($"--- Random-entry baseline, horizon={e5PrimaryHorizon} (net, matched count per day, deterministic stride sample) ---");
        var e5RandomTrades = new List<Experiment5OptionTradeSimulator.OptionTrade>();
        foreach (var (date, quals) in e5QualByDay)
        {
            if (quals.Count == 0)
            {
                continue;
            }

            var (dte, isZeroDte) = e5ByDayDte[date];
            // CORRECTION (2026-09-22): match the CORRECTED (non-overlapping) real-signal count, not
            // the raw qualifying-bar count -- otherwise the random baseline would still be sized off
            // the buggy, inflated count.
            var quolsNonOverlapping = Experiment5OptionTradeSimulator.SelectNonOverlapping(quals, e5PrimaryHorizon);
            var randomBars = Experiment5OptionTradeSimulator.BuildRandomBaselineDay(e5ByDayBars[date], quolsNonOverlapping.Count, dte, isZeroDte, e5ByDayAtm[date]);
            randomBars = Experiment5OptionTradeSimulator.SelectNonOverlapping(randomBars, e5PrimaryHorizon);

            await using var randSource = new NiftySignalDbContext(tradeSourceOptions);
            var randOptions = await randSource.Instruments.Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY").ToListAsync();
            if (randOptions.Count == 0)
            {
                continue;
            }

            var randExpiry = randOptions.Select(i => i.ExpiryDate!.Value).Min();
            var randChain = randOptions.Where(i => i.ExpiryDate == randExpiry).ToList();
            var randCache = new Dictionary<(DateOnly, string), OptionPriceSeries>();
            foreach (var qb in randomBars)
            {
                var trade = await Experiment5OptionTradeSimulator.BuildTradeAsync(randSource, qb, e5PrimaryHorizon, e5ByDayBars[date], randChain, e5ByDayAtm[date], randCache, e5Costs, e5Quantity, CancellationToken.None);
                if (trade is not null)
                {
                    e5RandomTrades.Add(trade);
                }
            }
        }

        PrintTradeStats("RANDOM baseline", e5RandomTrades, net: true);
        PrintTradeStats("REAL signal (for comparison)", e5AtPrimary, net: true);

        Console.WriteLine();
    }

    return 0;
}

// "experiment6-gates" -- 2026-09-22 "Experiment 6 -- Standalone Mean-Reversion Strategy + Gates"
// task (see docs/VOLUME_BAR_FINDINGS.md's dated section). Reuses Experiment 5's EXACT base
// signal/trade-simulation pipeline (TickActivityAnalyzer.ComputeThreshold,
// Experiment5UnderlyingAnalyzer.Collect, Experiment5OptionTradeSimulator.BuildTradeAsync -- all
// unchanged) at ONE fixed primary horizon (10, matching Experiment 5's own primary), then adds
// gate-feature attachment (GateFeatureExtractor), winner/loser distribution stats, one-gate-at-a-
// time evaluation, and an equity curve -- all via Experiment6GateAnalyzer, additive only. NOT a
// composite score: gates are always evaluated individually, never combined/weighted here.
if (args.Length > 0 && string.Equals(args[0], "experiment6-gates", StringComparison.OrdinalIgnoreCase))
{
    var (e6Positional, e6Named) = SplitNamedArgs(args);
    if (e6Positional.Length < 3
        || !DateOnly.TryParseExact(e6Positional[1], "yyyy-MM-dd", out var e6FromDate)
        || !DateOnly.TryParseExact(e6Positional[2], "yyyy-MM-dd", out var e6ToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- experiment6-gates <fromDate> <toDate> [--barSizes=650] [--zerodte=yyyy-MM-dd,...]");
        return 1;
    }

    var e6BarSizes = e6Named.TryGetValue("barSizes", out var e6BarSizesStr)
        ? e6BarSizesStr.Split(',').Select(long.Parse).ToList()
        : [650L];
    var e6ZeroDte = new HashSet<DateOnly>(e6Named.TryGetValue("zerodte", out var e6ZeroDteStr)
        ? e6ZeroDteStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd"))
        : [DateOnly.Parse("2026-09-08"), DateOnly.Parse("2026-09-15"), DateOnly.Parse("2026-09-22")]);

    const int e6Horizon = 10; // Experiment 5's own primary horizon -- reused, not re-chosen.
    var e6Costs = new CostsConfig(20m, 2);
    const int e6LotSize = 65;
    const int e6LotsPerTrade = 2;
    var e6Quantity = e6LotSize * e6LotsPerTrade;

    void E6PrintStats(string label, OptionTradeQualityStats.Stats st)
    {
        Console.WriteLine($"{label}: n={st.Count} win%={st.WinRatePct:F1} avgWin={st.AvgWinner:F1} avgLoss={st.AvgLoser:F1} PF={st.ProfitFactor:F2} expect={st.Expectancy:F1} net={st.NetPnl:F1} maxDD={st.MaxDrawdown:F1} MFE%={st.MeanMfe:F2} MAE%={st.MeanMae:F2} avgHoldMin={st.AvgHoldingMinutes:F1} top1%={st.TopKContributionPct[1]:F1} top3%={st.TopKContributionPct[3]:F1}");
    }

    void E6PrintGate(Experiment6GateAnalyzer.GateResult r)
    {
        Console.WriteLine($"GATE [{r.Label}]: kept={r.Kept}/{r.BaselineCount} removed={r.Removed} profitableRetained%={r.PctProfitableBaseRetained:F1} losingRemoved%={r.PctLosingBaseRemoved:F1}");
        E6PrintStats("  GROSS", r.Gross);
        E6PrintStats("  NET  ", r.Net);
    }

    foreach (var e6BarSize in e6BarSizes)
    {
        Console.WriteLine($"=== Experiment 6, bar={e6BarSize} horizon={e6Horizon} range={e6FromDate:yyyy-MM-dd}..{e6ToDate:yyyy-MM-dd} ===");

        var e6ByDayBars = new SortedDictionary<DateOnly, List<VolumeBarRow>>();
        var e6ByDayAtm = new Dictionary<DateOnly, Dictionary<int, OptionAtmBarRow>>();
        var e6ByDayDte = new Dictionary<DateOnly, (int Dte, bool IsZeroDte)>();

        for (var date = e6FromDate; date <= e6ToDate; date = date.AddDays(1))
        {
            await using var e6VolumeBars = new VolumeBarDbContext(volumeBarOptions);
            var e6Bars = await e6VolumeBars.VolumeBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == e6BarSize).OrderBy(b => b.BarIndex).ToListAsync();
            if (e6Bars.Count == 0)
            {
                continue;
            }

            var e6Atm = await e6VolumeBars.OptionAtmBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == e6BarSize).ToDictionaryAsync(b => b.BarIndex);
            e6ByDayBars[date] = e6Bars;
            e6ByDayAtm[date] = e6Atm;

            await using var e6Source = new NiftySignalDbContext(tradeSourceOptions);
            var e6DayOptions = await e6Source.Instruments.Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY").ToListAsync();
            var e6NearestExpiry = e6DayOptions.Count > 0 ? e6DayOptions.Select(i => i.ExpiryDate!.Value).Min() : (DateOnly?)null;
            var e6Dte = e6NearestExpiry is { } exp ? exp.DayNumber - date.DayNumber : 0;
            e6ByDayDte[date] = (e6Dte, e6ZeroDte.Contains(date));
        }

        Console.WriteLine($"Days with data: {e6ByDayBars.Count}");
        if (e6ByDayBars.Count == 0)
        {
            continue;
        }

        // Locked base signal (Experiment 3's threshold, unchanged) -- reused byte-for-byte.
        var e6PooledSamplesForThreshold = e6ByDayBars.Values.SelectMany(TickActivityAnalyzer.CollectDay).ToList();
        var e6Threshold = TickActivityAnalyzer.ComputeThreshold(e6PooledSamplesForThreshold);
        Console.WriteLine($"Locked threshold (Experiment 3, unchanged): TickVelocityMedian={e6Threshold.ActivityMedian:F4} PriceEfficiencyMedian={e6Threshold.EfficiencyMedian:F6}");

        var e6QualByDay = new Dictionary<DateOnly, List<Experiment5UnderlyingAnalyzer.QualifyingBar>>();
        foreach (var (date, bars) in e6ByDayBars)
        {
            var (dte, isZeroDte) = e6ByDayDte[date];
            e6QualByDay[date] = Experiment5UnderlyingAnalyzer.Collect(bars, e6Threshold, e6ByDayAtm[date], dte, isZeroDte);
        }

        var e6AllQual = e6QualByDay.Values.SelectMany(x => x).ToList();
        Console.WriteLine($"Qualifying bars (state D): total={e6AllQual.Count}");

        // Build the base-signal option trades at the fixed primary horizon only.
        var e6BaseTrades = new List<Experiment5OptionTradeSimulator.OptionTrade>();
        foreach (var (date, quals) in e6QualByDay)
        {
            if (quals.Count == 0)
            {
                continue;
            }

            await using var e6Source2 = new NiftySignalDbContext(tradeSourceOptions);
            var e6DayOptions2 = await e6Source2.Instruments.Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY").ToListAsync();
            if (e6DayOptions2.Count == 0)
            {
                continue;
            }

            var e6NearestExpiry2 = e6DayOptions2.Select(i => i.ExpiryDate!.Value).Min();
            var e6Chain = e6DayOptions2.Where(i => i.ExpiryDate == e6NearestExpiry2).ToList();
            var e6PriceCache = new Dictionary<(DateOnly, string), OptionPriceSeries>();
            var e6DayBars = e6ByDayBars[date];

            // CORRECTION (2026-09-22): single-open-trade gate -- see
            // Experiment5OptionTradeSimulator.SelectNonOverlapping's own doc comment for the rule and
            // the root-cause this fixes (269-513 overlapping "trades" on a single day before this).
            var e6NonOverlapping = Experiment5OptionTradeSimulator.SelectNonOverlapping(quals, e6Horizon);
            foreach (var qb in e6NonOverlapping)
            {
                var trade = await Experiment5OptionTradeSimulator.BuildTradeAsync(e6Source2, qb, e6Horizon, e6DayBars, e6Chain, e6ByDayAtm[date], e6PriceCache, e6Costs, e6Quantity, CancellationToken.None);
                if (trade is not null)
                {
                    e6BaseTrades.Add(trade);
                }
            }
        }

        var e6BaseOrdered = e6BaseTrades.OrderBy(t => t.Date).ThenBy(t => t.BarIndex).ToList();
        Console.WriteLine($"Base-signal option trades built: {e6BaseOrdered.Count}");

        // --- Section 3: baseline trade report (gross/net). ---
        var e6BaseGrossStats = OptionTradeQualityStats.Compute(e6BaseOrdered.Select(t => t.GrossPnlLot).ToList(), e6BaseOrdered.Select(t => (double?)t.MfePercent).ToList(), e6BaseOrdered.Select(t => (double?)t.MaePercent).ToList(), e6BaseOrdered.Select(t => t.HoldingMinutes).ToList());
        var e6BaseNetStats = OptionTradeQualityStats.Compute(e6BaseOrdered.Select(t => t.NetPnlLot).ToList(), e6BaseOrdered.Select(t => (double?)t.MfePercent).ToList(), e6BaseOrdered.Select(t => (double?)t.MaePercent).ToList(), e6BaseOrdered.Select(t => t.HoldingMinutes).ToList());
        Console.WriteLine("--- Baseline (Experiment 5 methodology, horizon=10) ---");
        E6PrintStats("BASE GROSS", e6BaseGrossStats);
        E6PrintStats("BASE NET  ", e6BaseNetStats);

        // --- Section 4: mandatory per-day table. ---
        Console.WriteLine("--- Baseline per-day P&L (Date, Trades, Wins, Losses, GrossPnL, Costs, NetPnL) ---");
        foreach (var (date, _) in e6ByDayBars)
        {
            var dayTrades = e6BaseOrdered.Where(t => t.Date == date).ToList();
            if (dayTrades.Count == 0)
            {
                continue;
            }

            var wins = dayTrades.Count(t => t.NetPnlLot > 0);
            var losses = dayTrades.Count(t => t.NetPnlLot <= 0);
            var gross = dayTrades.Sum(t => t.GrossPnlLot);
            var net = dayTrades.Sum(t => t.NetPnlLot);
            var costs = gross - net;
            Console.WriteLine($"{date:yyyy-MM-dd} | trades={dayTrades.Count} | wins={wins} | losses={losses} | gross={gross:F1} | costs={costs:F1} | net={net:F1}");
        }

        // --- Section 5: equity curve, every 25th trade plus the last. ---
        var e6Equity = Experiment6GateAnalyzer.BuildEquityCurve(e6BaseOrdered);
        Console.WriteLine("--- Equity curve (every 25th trade) ---");
        for (var i = 0; i < e6Equity.Count; i++)
        {
            if ((i + 1) % 25 == 0 || i == e6Equity.Count - 1)
            {
                var p = e6Equity[i];
                Console.WriteLine($"trade#{p.TradeNumber,5} {p.Date:yyyy-MM-dd} cumGross={p.CumulativeGross,10:F1} cumCosts={p.CumulativeCosts,10:F1} cumNet={p.CumulativeNet,10:F1}");
            }
        }

        // --- Section 6: winner vs loser distributions per gate-candidate feature. ---
        var e6Gated = Experiment6GateAnalyzer.Attach(e6BaseOrdered, e6ByDayBars, e6Threshold);
        Console.WriteLine("--- Winner vs loser distribution, per gate-candidate feature (Net P&L defines winner/loser) ---");
        (string Name, Func<Experiment6GateAnalyzer.GateFeatures2, double?> Sel)[] e6Features =
        [
            ("A:DepthImbalance", f => f.DepthImbalance),
            ("A:OFI", f => f.Ofi),
            ("A:CvdNet", f => f.CvdNet),
            ("A:VwapRelPct", f => f.VwapRelPct),
            ("A:OiAtClose", f => f.OiAtClose),
            ("A:OiChangeFromPrev", f => f.OiChangeFromPrev),
            ("B:TickDensity", f => f.TickDensity),
            ("B:DurationSeconds", f => f.DurationSeconds),
            ("B:TickCount", f => f.TickCount),
            ("B:Churn", f => f.Churn),
            ("E:TickVelocityExcess", f => f.TickVelocityExcess),
            ("E:PriceEfficiencyExcess", f => f.PriceEfficiencyExcess),
            ("E:AbsNetMovePct", f => f.AbsNetMovePct),
        ];
        foreach (var (name, sel) in e6Features)
        {
            var d = Experiment6GateAnalyzer.SummarizeFeature(e6Gated, name, sel);
            Console.WriteLine($"{name}: winners(n={d.NWinners}) median={d.WinnerMedian:F4} mean={d.WinnerMean:F4} p25={d.WinnerP25:F4} p75={d.WinnerP75:F4} | losers(n={d.NLosers}) median={d.LoserMedian:F4} mean={d.LoserMean:F4} p25={d.LoserP25:F4} p75={d.LoserP75:F4} | {d.OverlapNote}");
        }

        // --- Sections 7-11: one gate at a time, median split + plateau sweep, per numeric feature. ---
        Console.WriteLine("--- Gate Group A/B/E: one-feature-at-a-time median-split gates (both directions), plus plateau sweep ---");
        foreach (var (name, sel) in e6Features)
        {
            var pooled = Experiment6GateAnalyzer.PooledPercentiles(e6Gated, sel);
            var median = pooled[0.50];
            if (double.IsNaN(median))
            {
                Console.WriteLine($"{name}: no non-null values, skipped.");
                continue;
            }

            var gateHigh = Experiment6GateAnalyzer.Evaluate($"{name}>=median({median:F4})", e6Gated, g => sel(new Experiment6GateAnalyzer.GateFeatures2(g.Features)) is { } v && v >= median);
            var gateLow = Experiment6GateAnalyzer.Evaluate($"{name}<median({median:F4})", e6Gated, g => sel(new Experiment6GateAnalyzer.GateFeatures2(g.Features)) is { } v && v < median);
            E6PrintGate(gateHigh);
            E6PrintGate(gateLow);

            Console.Write($"  Plateau sweep {name} (keep >= pooled percentile P), net expectancy by P: ");
            foreach (var p in Experiment6GateAnalyzer.PlateauPercentiles)
            {
                var thr = pooled[p];
                var g = Experiment6GateAnalyzer.Evaluate($"{name}>=P{p:P0}", e6Gated, gt => sel(new Experiment6GateAnalyzer.GateFeatures2(gt.Features)) is { } v && v >= thr);
                Console.Write($"P{p:P0}={g.Net.Expectancy:F1}(n={g.Kept}) ");
            }

            Console.WriteLine();
        }

        // --- Gate Group C: DTE. ---
        Console.WriteLine("--- Gate Group C: DTE ---");
        E6PrintGate(Experiment6GateAnalyzer.Evaluate("DTE=0", e6Gated, g => g.Trade.IsZeroDte));
        E6PrintGate(Experiment6GateAnalyzer.Evaluate("DTE>0", e6Gated, g => !g.Trade.IsZeroDte));

        // --- Gate Group D: Session. ---
        Console.WriteLine("--- Gate Group D: Session ---");
        foreach (var sess in new[] { "Open(<10:00)", "Mid(10:00-13:30)", "Close(>13:30)" })
        {
            E6PrintGate(Experiment6GateAnalyzer.Evaluate($"Session={sess}", e6Gated, g => g.Trade.Session == sess));
        }

        // --- Day-wise trade summary for the leading candidate gate (TickVelocityExcess<median),
        // requested directly (2026-09-22, follow-up to Experiment 6's own leading-candidate finding)
        // -- mirrors the mandatory baseline per-day table above, filtered to gate-passing trades only.
        var e6TveMedianForDayWise = Experiment6GateAnalyzer.PooledPercentiles(e6Gated, f => f.TickVelocityExcess)[0.50];
        var e6GatedTrades = e6Gated.Where(g => g.Features.TickVelocityExcess is { } v && v < e6TveMedianForDayWise).Select(g => g.Trade).OrderBy(t => t.Date).ThenBy(t => t.BarIndex).ToList();
        Console.WriteLine($"--- Day-wise trade summary, GATED (TickVelocityExcess<median={e6TveMedianForDayWise:F4}) ---");
        foreach (var (date, _) in e6ByDayBars)
        {
            var dayTrades = e6GatedTrades.Where(t => t.Date == date).ToList();
            if (dayTrades.Count == 0)
            {
                Console.WriteLine($"{date:yyyy-MM-dd} | trades=0");
                continue;
            }

            var wins = dayTrades.Count(t => t.NetPnlLot > 0);
            var losses = dayTrades.Count(t => t.NetPnlLot <= 0);
            var gross = dayTrades.Sum(t => t.GrossPnlLot);
            var net = dayTrades.Sum(t => t.NetPnlLot);
            var costs = gross - net;
            var winRate = dayTrades.Count > 0 ? 100.0 * wins / dayTrades.Count : 0.0;
            Console.WriteLine($"{date:yyyy-MM-dd} | trades={dayTrades.Count,3} | wins={wins,3} | losses={losses,3} | win%={winRate,5:F1} | gross={gross,9:F1} | costs={costs,7:F1} | net={net,9:F1}");
        }

        var e6GatedGrossAll = OptionTradeQualityStats.Compute(e6GatedTrades.Select(t => t.GrossPnlLot).ToList());
        var e6GatedNetAll = OptionTradeQualityStats.Compute(e6GatedTrades.Select(t => t.NetPnlLot).ToList());
        Console.WriteLine("--- GATED overall totals ---");
        E6PrintStats("GATED GROSS", e6GatedGrossAll);
        E6PrintStats("GATED NET  ", e6GatedNetAll);

        // --- Robustness: best-day removal on the FULL baseline (item 15). ---
        var e6DayNet = e6ByDayBars.Keys.Select(d => (Date: d, Net: e6BaseOrdered.Where(t => t.Date == d).Sum(t => t.NetPnlLot))).Where(x => x.Net != 0m || e6BaseOrdered.Any(t => t.Date == x.Date)).ToList();
        if (e6DayNet.Count > 0)
        {
            var e6BestDay = e6DayNet.OrderByDescending(x => x.Net).First();
            Console.WriteLine($"Best day by net P&L: {e6BestDay.Date:yyyy-MM-dd} ({e6BestDay.Net:F1})");
            var exclBest = OptionTradeQualityStats.Compute(e6BaseOrdered.Where(t => t.Date != e6BestDay.Date).Select(t => t.NetPnlLot).ToList());
            E6PrintStats("Excl-best-day NET", exclBest);
        }

        // --- Item 15: coarse early/late split attempt (SAME locked threshold/gate value, never
        // re-derived per slice -- only the EVALUATION scope is split, per Experiment 3's own
        // discipline). Explicitly NOT claimed as rigorous out-of-sample validation (see doc). ---
        var e6DatesOrdered = e6ByDayBars.Keys.OrderBy(d => d).ToList();
        if (e6DatesOrdered.Count >= 4)
        {
            var half = e6DatesOrdered.Count / 2;
            var earlyDates = new HashSet<DateOnly>(e6DatesOrdered.Take(half));
            var lateDates = new HashSet<DateOnly>(e6DatesOrdered.Skip(half));
            Console.WriteLine($"--- Early/late split attempt (early={string.Join(",", earlyDates.OrderBy(d => d))}; late={string.Join(",", lateDates.OrderBy(d => d))}) ---");
            var e6TveMedian = Experiment6GateAnalyzer.PooledPercentiles(e6Gated, f => f.TickVelocityExcess)[0.50];
            var earlyGated = e6Gated.Where(g => earlyDates.Contains(g.Trade.Date)).ToList();
            var lateGated = e6Gated.Where(g => lateDates.Contains(g.Trade.Date)).ToList();
            E6PrintGate(Experiment6GateAnalyzer.Evaluate("EARLY baseline (no gate)", earlyGated, _ => true));
            E6PrintGate(Experiment6GateAnalyzer.Evaluate($"EARLY TickVelocityExcess<{e6TveMedian:F4}", earlyGated, g => g.Features.TickVelocityExcess is { } v && v < e6TveMedian));
            E6PrintGate(Experiment6GateAnalyzer.Evaluate("LATE baseline (no gate)", lateGated, _ => true));
            E6PrintGate(Experiment6GateAnalyzer.Evaluate($"LATE TickVelocityExcess<{e6TveMedian:F4}", lateGated, g => g.Features.TickVelocityExcess is { } v && v < e6TveMedian));

            // --- Item 12: trade-sequence comparison, base vs the one promising gate, first 15 trades of the LATE period. ---
            Console.WriteLine("--- Trade sequence comparison (LATE period, first 15 trades): base vs TickVelocityExcess<median gate ---");
            var lateOrdered = lateGated.OrderBy(g => g.Trade.Date).ThenBy(g => g.Trade.BarIndex).Take(15).ToList();
            foreach (var g in lateOrdered)
            {
                var passesGate = g.Features.TickVelocityExcess is { } v && v < e6TveMedian;
                Console.WriteLine($"{g.Trade.Date:yyyy-MM-dd} bar={g.Trade.BarIndex,4} {g.Trade.Side,-4} strike={g.Trade.Strike} gross={g.Trade.GrossPnlLot,7:F1} net={g.Trade.NetPnlLot,9:F1} gatePasses={passesGate}");
            }
        }
        else
        {
            Console.WriteLine("Early/late split skipped: fewer than 4 populated days.");
        }

        Console.WriteLine();
    }

    return 0;
}

// "ma-spread-research" -- 2026-09-22 SMA-vs-EMA option-premium task, Parts 9-14. Collects
// Call AND Put Spread samples (MaSpreadRelationshipAnalyzer, MaSpreadEngine) for one fast/slow/
// MaType combo, prints Call-vs-Put Pearson r (Part 12), quantile-bucketed Spread-vs-forward-return
// (Part 11), a dual Call+Put state split (Part 13, threshold = pooled 70th |Spread| percentile --
// DATA-DERIVED, not invented), and a DTE=0-vs-DTE>0 split (Part 14). Every forward number is a
// RESEARCH LABEL. Never opens a trade.
if (args.Length > 0 && string.Equals(args[0], "ma-spread-research", StringComparison.OrdinalIgnoreCase))
{
    var (msPositional, msNamed) = SplitNamedArgs(args);
    if (msPositional.Length < 5
        || !DateOnly.TryParseExact(msPositional[1], "yyyy-MM-dd", out var msFromDate)
        || !DateOnly.TryParseExact(msPositional[2], "yyyy-MM-dd", out var msToDate)
        || !int.TryParse(msPositional[3], out var msFastBars)
        || !int.TryParse(msPositional[4], out var msSlowBars))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- ma-spread-research <fromDate> <toDate> <fastBars> <slowBars> [barVolumeThreshold=650] [--fasttype=Sma|Ema] [--slowtype=Sma|Ema] [--band=3|5] [--buckets=8] [--dumpstates=true]");
        return 1;
    }

    var msBarVolumeThreshold = msPositional.Length > 5 ? long.Parse(msPositional[5]) : 650L;
    var msFastType = msNamed.TryGetValue("fasttype", out var msFastTypeStr) ? Enum.Parse<MaType>(msFastTypeStr, ignoreCase: true) : MaType.Sma;
    var msSlowType = msNamed.TryGetValue("slowtype", out var msSlowTypeStr) ? Enum.Parse<MaType>(msSlowTypeStr, ignoreCase: true) : MaType.Sma;
    var msBandWidth = msNamed.TryGetValue("band", out var msBandStr) ? (int?)int.Parse(msBandStr) : null;
    var msBuckets = msNamed.TryGetValue("buckets", out var msBucketsStr) ? int.Parse(msBucketsStr) : 8;
    var msZeroDte = new HashSet<DateOnly>(msNamed.TryGetValue("zerodte", out var msZeroDteStr) ? msZeroDteStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd")) : [DateOnly.Parse("2026-09-08"), DateOnly.Parse("2026-09-15"), DateOnly.Parse("2026-09-22")]);
    // 2026-09-22 Experiment 4 addition: per-occurrence detail dump for the Part 13 dual-state split
    // (day/session/DTE concentration audit -- see docs/VOLUME_BAR_FINDINGS.md's "VolContraction
    // day/session/DTE concentration audit" section). Additive only -- default behavior (no flag)
    // is byte-for-byte unchanged from the original Part 13 output.
    var msDumpStates = msNamed.TryGetValue("dumpstates", out var msDumpStatesStr) && bool.Parse(msDumpStatesStr);
    // DTE-per-date table, confirmed directly from real instruments.ExpiryDate data in the original
    // SMA/EMA task's Section 8 (not re-derived here) -- reused exactly as already established.
    var msDteByDate = new Dictionary<DateOnly, int>
    {
        [DateOnly.Parse("2026-09-04")] = 4, [DateOnly.Parse("2026-09-08")] = 0, [DateOnly.Parse("2026-09-09")] = 6,
        [DateOnly.Parse("2026-09-10")] = 5, [DateOnly.Parse("2026-09-11")] = 4, [DateOnly.Parse("2026-09-15")] = 0,
        [DateOnly.Parse("2026-09-16")] = 6, [DateOnly.Parse("2026-09-17")] = 5, [DateOnly.Parse("2026-09-18")] = 4,
        [DateOnly.Parse("2026-09-21")] = 1, [DateOnly.Parse("2026-09-22")] = 0,
    };
    // Session boundary identical to TickActivityAnalyzer.Sample.Session (IST, from bar EndTimestamp/
    // here the joined sample's Timestamp): Open < 10:00, Mid 10:00-13:30, Close >= 13:30.
    static string MsSession(DateTimeOffset t)
    {
        var ist = TimeOnly.FromDateTime(t.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        return ist < new TimeOnly(10, 0) ? "Open(<10:00)" : ist < new TimeOnly(13, 30) ? "Mid(10:00-13:30)" : "Close(>13:30)";
    }

    Console.WriteLine($"=== MA-spread research: fast={msFastBars}({msFastType}) slow={msSlowBars}({msSlowType}) bar={msBarVolumeThreshold}{(msBandWidth is { } msbw ? $" band={msbw}" : " single ATM")} ===");

    var msCallSamples = new List<MaSpreadRelationshipAnalyzer.Sample>();
    var msPutSamples = new List<MaSpreadRelationshipAnalyzer.Sample>();
    for (var date = msFromDate; date <= msToDate; date = date.AddDays(1))
    {
        await using var msSource = new NiftySignalDbContext(tradeSourceOptions);
        await using var msVolumeBars = new VolumeBarDbContext(volumeBarOptions);
        msCallSamples.AddRange(await MaSpreadRelationshipAnalyzer.CollectDayAsync(msSource, msVolumeBars, date, msBarVolumeThreshold, OptionType.Call, msFastBars, msSlowBars, msFastType, msSlowType, msBandWidth, CancellationToken.None, sharedOptionPriceCache));
        await using var msSource2 = new NiftySignalDbContext(tradeSourceOptions);
        await using var msVolumeBars2 = new VolumeBarDbContext(volumeBarOptions);
        msPutSamples.AddRange(await MaSpreadRelationshipAnalyzer.CollectDayAsync(msSource2, msVolumeBars2, date, msBarVolumeThreshold, OptionType.Put, msFastBars, msSlowBars, msFastType, msSlowType, msBandWidth, CancellationToken.None, sharedOptionPriceCache));
    }

    Console.WriteLine($"Call samples: {msCallSamples.Count}, Put samples: {msPutSamples.Count}");

    static double? MsPearson(IEnumerable<(double X, double Y)> pairs)
    {
        var list = pairs.ToList();
        if (list.Count < 3) return null;
        var mx = list.Average(p => p.X); var my = list.Average(p => p.Y);
        var cov = list.Sum(p => (p.X - mx) * (p.Y - my));
        var sx = Math.Sqrt(list.Sum(p => (p.X - mx) * (p.X - mx)));
        var sy = Math.Sqrt(list.Sum(p => (p.Y - my) * (p.Y - my)));
        return sx > 0 && sy > 0 ? cov / (sx * sy) : null;
    }

    // --- Part 12: Call vs Put, own-side spread vs NIFTY forward, at 5/10/20 bars. ---
    void PrintSideCorrelations(string label, List<MaSpreadRelationshipAnalyzer.Sample> samples)
    {
        foreach (var (h, niftyFwd, optFwd) in new (string, Func<MaSpreadRelationshipAnalyzer.Sample, double?>, Func<MaSpreadRelationshipAnalyzer.Sample, double?>)[]
        {
            ("5", s => s.NiftyFwd5, s => s.OptionFwd5), ("10", s => s.NiftyFwd10, s => s.OptionFwd10), ("20", s => s.NiftyFwd20, s => s.OptionFwd20),
        })
        {
            var rNifty = MsPearson(samples.Where(s => niftyFwd(s) is not null).Select(s => (s.Spread, niftyFwd(s)!.Value)));
            var rOpt = MsPearson(samples.Where(s => optFwd(s) is not null).Select(s => (s.Spread, optFwd(s)!.Value)));
            Console.WriteLine($"{label} horizon={h}: r(Spread,NiftyFwd)={rNifty:F4}  r(Spread,OwnOptionFwd)={rOpt:F4}");
        }
    }

    PrintSideCorrelations("Call", msCallSamples);
    PrintSideCorrelations("Put", msPutSamples);

    // --- Part 11: Spread quantile buckets vs NiftyFwd10, per side. ---
    void PrintSpreadBuckets(string label, List<MaSpreadRelationshipAnalyzer.Sample> samples)
    {
        var usable = samples.Where(s => s.NiftyFwd10 is not null).OrderBy(s => s.Spread).ToList();
        if (usable.Count == 0) return;
        Console.WriteLine($"--- {label} Spread buckets (horizon 10) ---");
        Console.WriteLine($"{"Bucket",-24} | {"n",5} | {"MeanNiftyFwd%",13} | {"P(Up)%",7} | {"MeanOwnOptFwd%",14}");
        var n = usable.Count;
        for (var b = 0; b < msBuckets; b++)
        {
            var lo = b * n / msBuckets; var hi = (b + 1) * n / msBuckets;
            if (hi <= lo) continue;
            var slice = usable.GetRange(lo, hi - lo);
            var niftyVals = slice.Select(s => s.NiftyFwd10!.Value).ToList();
            var optVals = slice.Select(s => s.OptionFwd10).Where(v => v is not null).Select(v => v!.Value).ToList();
            var label2 = $"{slice[0].Spread * 100:F2}% to {slice[^1].Spread * 100:F2}%";
            Console.WriteLine($"{label2,-24} | {slice.Count,5} | {niftyVals.Average() * 100,13:F3} | {100.0 * niftyVals.Count(v => v > 0) / niftyVals.Count,7:F1} | {(optVals.Count > 0 ? optVals.Average() * 100 : 0),14:F3}");
        }
    }

    PrintSpreadBuckets("Call", msCallSamples);
    PrintSpreadBuckets("Put", msPutSamples);

    // --- Part 13: dual Call+Put state, threshold = pooled 70th percentile of |Spread| (data-derived). ---
    var msAllAbsSpreads = msCallSamples.Select(s => Math.Abs(s.Spread)).Concat(msPutSamples.Select(s => Math.Abs(s.Spread))).OrderBy(v => v).ToList();
    if (msAllAbsSpreads.Count > 10)
    {
        var msThreshold = msAllAbsSpreads[(int)(msAllAbsSpreads.Count * 0.70)];
        Console.WriteLine($"--- Part 13: dual Call+Put state (|Spread| threshold = pooled 70th pct = {msThreshold * 100:F3}%, data-derived) ---");
        var msJoined = msCallSamples
            .Join(msPutSamples, c => (c.Date, c.BarIndex), p => (p.Date, p.BarIndex), (c, p) => new { c.Date, c.BarIndex, c.Timestamp, CallSpread = c.Spread, PutSpread = p.Spread, NiftyFwd10 = c.NiftyFwd10 })
            .Where(x => x.NiftyFwd10 is not null)
            .ToList();

        void PrintDualState(string label, Func<double, double, bool> predicate)
        {
            var slice = msJoined.Where(x => predicate(x.CallSpread, x.PutSpread)).ToList();
            if (slice.Count == 0) { Console.WriteLine($"{label}: n=0"); return; }
            var fwd = slice.Select(x => x.NiftyFwd10!.Value).ToList();
            Console.WriteLine($"{label}: n={slice.Count} meanFwd10%={fwd.Average() * 100:F3} P(Up)%={100.0 * fwd.Count(v => v > 0) / fwd.Count:F1}");

            if (msDumpStates)
            {
                Console.WriteLine($"  --- {label} per-occurrence detail (Date, BarIndex, Time(IST), Session, DTE, Fwd10%, Outcome) ---");
                foreach (var x in slice.OrderBy(x => x.Date).ThenBy(x => x.BarIndex))
                {
                    var dte = msDteByDate.TryGetValue(x.Date, out var d) ? d.ToString() : "?";
                    var fwd10 = x.NiftyFwd10!.Value;
                    var outcome = fwd10 > 0 ? "Up" : fwd10 < 0 ? "Down" : "Flat";
                    Console.WriteLine($"  {x.Date:yyyy-MM-dd} bar={x.BarIndex,5} t={FormatIst(x.Timestamp)} session={MsSession(x.Timestamp),-16} DTE={dte,2} Fwd10%={fwd10 * 100,7:F3} outcome={outcome}");
                }
            }
        }

        PrintDualState("BullishConfirmation (Call>+thr AND Put<-thr)", (c, p) => c > msThreshold && p < -msThreshold);
        PrintDualState("BearishConfirmation (Call<-thr AND Put>+thr)", (c, p) => c < -msThreshold && p > msThreshold);
        PrintDualState("VolExpansion (both>+thr)", (c, p) => c > msThreshold && p > msThreshold);
        PrintDualState("VolContraction (both<-thr)", (c, p) => c < -msThreshold && p < -msThreshold);
    }

    // --- Part 14: DTE=0 vs DTE>0 split, Call side, horizon 10. ---
    Console.WriteLine("--- Part 14: DTE split (Call side, horizon 10) ---");
    foreach (var (label, slice) in new[] { ("DTE=0", msCallSamples.Where(s => msZeroDte.Contains(s.Date)).ToList()), ("DTE>0", msCallSamples.Where(s => !msZeroDte.Contains(s.Date)).ToList()) })
    {
        var r = MsPearson(slice.Where(s => s.NiftyFwd10 is not null).Select(s => (s.Spread, s.NiftyFwd10!.Value)));
        Console.WriteLine($"{label}: n={slice.Count} r(Spread,NiftyFwd10)={r:F4}");
    }

    return 0;
}

// "callput-diff-research" -- Experiment 2, 2026-09-22 "Call/Put premium momentum -> Nifty
// direction" task (see docs/VOLUME_BAR_FINDINGS.md's dated section). Reuses
// MaSpreadRelationshipAnalyzer.CollectDayAsync (same as ma-spread-research) for Call and Put
// samples at ONE fast/slow window pair, for BOTH EMA/EMA and SMA/SMA, and adds the Call-minus-Put
// combined signal (CallPutDiffSignal.Combine). Prints the full feature x horizon x DTE-split x
// MA-type correlation matrix plus a quantile-bucket table (horizon=10, pooled across DTE) for
// each feature. Every forward number is a RESEARCH LABEL. Never opens a trade.
if (args.Length > 0 && string.Equals(args[0], "callput-diff-research", StringComparison.OrdinalIgnoreCase))
{
    var (cpPositional, cpNamed) = SplitNamedArgs(args);
    if (cpPositional.Length < 5
        || !DateOnly.TryParseExact(cpPositional[1], "yyyy-MM-dd", out var cpFromDate)
        || !DateOnly.TryParseExact(cpPositional[2], "yyyy-MM-dd", out var cpToDate)
        || !int.TryParse(cpPositional[3], out var cpFastBars)
        || !int.TryParse(cpPositional[4], out var cpSlowBars))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- callput-diff-research <fromDate> <toDate> <fastBars> <slowBars> [barVolumeThreshold=650] [--band=3] [--buckets=8]");
        return 1;
    }

    var cpBarVolumeThreshold = cpPositional.Length > 5 ? long.Parse(cpPositional[5]) : 650L;
    var cpBandWidth = cpNamed.TryGetValue("band", out var cpBandStr) ? (int?)int.Parse(cpBandStr) : 3;
    var cpBuckets = cpNamed.TryGetValue("buckets", out var cpBucketsStr) ? int.Parse(cpBucketsStr) : 8;
    var cpZeroDte = new HashSet<DateOnly>(cpNamed.TryGetValue("zerodte", out var cpZeroDteStr)
        ? cpZeroDteStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd"))
        : [DateOnly.Parse("2026-09-08"), DateOnly.Parse("2026-09-15"), DateOnly.Parse("2026-09-22")]);

    Console.WriteLine($"=== Call/Put diff research: fast={cpFastBars} slow={cpSlowBars} bar={cpBarVolumeThreshold} band={cpBandWidth} ===");

    static double? CpPearson(IEnumerable<(double X, double Y)> pairs)
    {
        var list = pairs.ToList();
        if (list.Count < 3) return null;
        var mx = list.Average(p => p.X); var my = list.Average(p => p.Y);
        var cov = list.Sum(p => (p.X - mx) * (p.Y - my));
        var sx = Math.Sqrt(list.Sum(p => (p.X - mx) * (p.X - mx)));
        var sy = Math.Sqrt(list.Sum(p => (p.Y - my) * (p.Y - my)));
        return sx > 0 && sy > 0 ? cov / (sx * sy) : null;
    }

    var cpBestAbsR = 0.0;
    var cpBestLabel = "";
    List<(double Spread, double? NiftyFwd10)>? cpBestBucketSource = null;

    foreach (var (cpFastType, cpSlowType) in new[] { (MaType.Ema, MaType.Ema), (MaType.Sma, MaType.Sma) })
    {
        var cpCallSamples = new List<MaSpreadRelationshipAnalyzer.Sample>();
        var cpPutSamples = new List<MaSpreadRelationshipAnalyzer.Sample>();
        for (var date = cpFromDate; date <= cpToDate; date = date.AddDays(1))
        {
            await using var cpSource = new NiftySignalDbContext(tradeSourceOptions);
            await using var cpVolumeBars = new VolumeBarDbContext(volumeBarOptions);
            cpCallSamples.AddRange(await MaSpreadRelationshipAnalyzer.CollectDayAsync(cpSource, cpVolumeBars, date, cpBarVolumeThreshold, OptionType.Call, cpFastBars, cpSlowBars, cpFastType, cpSlowType, cpBandWidth, CancellationToken.None, sharedOptionPriceCache));
            await using var cpSource2 = new NiftySignalDbContext(tradeSourceOptions);
            await using var cpVolumeBars2 = new VolumeBarDbContext(volumeBarOptions);
            cpPutSamples.AddRange(await MaSpreadRelationshipAnalyzer.CollectDayAsync(cpSource2, cpVolumeBars2, date, cpBarVolumeThreshold, OptionType.Put, cpFastBars, cpSlowBars, cpFastType, cpSlowType, cpBandWidth, CancellationToken.None, sharedOptionPriceCache));
        }

        var cpDiffRows = CallPutDiffSignal.Combine(cpCallSamples, cpPutSamples);
        Console.WriteLine($"--- MaType={cpFastType}/{cpSlowType}  Call n={cpCallSamples.Count}  Put n={cpPutSamples.Count}  Diff n={cpDiffRows.Count} ---");

        foreach (var dteLabel in new[] { "ALL", "DTE=0", "DTE>0" })
        {
            bool DteFilter(DateOnly d) => dteLabel switch
            {
                "DTE=0" => cpZeroDte.Contains(d),
                "DTE>0" => !cpZeroDte.Contains(d),
                _ => true,
            };

            var callSlice = cpCallSamples.Where(s => DteFilter(s.Date)).ToList();
            var putSlice = cpPutSamples.Where(s => DteFilter(s.Date)).ToList();
            var diffSlice = cpDiffRows.Where(r => DteFilter(r.Date)).ToList();

            foreach (var (hLabel, callFwd, putFwd, diffFwd) in new (string, Func<MaSpreadRelationshipAnalyzer.Sample, double?>, Func<MaSpreadRelationshipAnalyzer.Sample, double?>, Func<CallPutDiffSignal.Row, double?>)[]
            {
                ("5", s => s.NiftyFwd5, s => s.NiftyFwd5, r => r.NiftyFwd5),
                ("10", s => s.NiftyFwd10, s => s.NiftyFwd10, r => r.NiftyFwd10),
                ("20", s => s.NiftyFwd20, s => s.NiftyFwd20, r => r.NiftyFwd20),
            })
            {
                var rCall = CpPearson(callSlice.Where(s => callFwd(s) is not null).Select(s => (s.Spread, callFwd(s)!.Value)));
                var rPut = CpPearson(putSlice.Where(s => putFwd(s) is not null).Select(s => (s.Spread, putFwd(s)!.Value)));
                var rDiff = CpPearson(diffSlice.Where(r => diffFwd(r) is not null).Select(r => (r.Diff, diffFwd(r)!.Value)));
                Console.WriteLine($"{cpFastType}/{cpSlowType} {dteLabel,-6} h={hLabel,-2} n(Call/Put/Diff)={callSlice.Count}/{putSlice.Count}/{diffSlice.Count}  r(Call)={rCall:F4}  r(Put)={rPut:F4}  r(Diff)={rDiff:F4}");

                foreach (var (name, r, source) in new (string, double?, List<(double, double?)>)[]
                {
                    ("Call", rCall, callSlice.Select(s => (s.Spread, callFwd(s))).ToList()),
                    ("Put", rPut, putSlice.Select(s => (s.Spread, putFwd(s))).ToList()),
                    ("Diff", rDiff, diffSlice.Select(r2 => (r2.Diff, diffFwd(r2))).ToList()),
                })
                {
                    if (r is { } rv && Math.Abs(rv) > cpBestAbsR && dteLabel == "ALL" && hLabel == "10")
                    {
                        cpBestAbsR = Math.Abs(rv);
                        cpBestLabel = $"{name} ({cpFastType}/{cpSlowType}, h=10, ALL)";
                        cpBestBucketSource = source;
                    }
                }
            }
        }

        // Quantile-bucket table (horizon=10, pooled across DTE) for each feature.
        void PrintBuckets(string label, List<(double Spread, double? NiftyFwd10)> data)
        {
            var usable = data.Where(x => x.NiftyFwd10 is not null).OrderBy(x => x.Spread).ToList();
            if (usable.Count == 0) return;
            Console.WriteLine($"--- {cpFastType}/{cpSlowType} {label} buckets (horizon 10, pooled DTE) ---");
            Console.WriteLine($"{"Bucket",-24} | {"n",6} | {"MeanNiftyFwd%",13} | {"P(Up)%",7}");
            var n = usable.Count;
            for (var b = 0; b < cpBuckets; b++)
            {
                var lo = b * n / cpBuckets; var hi = (b + 1) * n / cpBuckets;
                if (hi <= lo) continue;
                var slice = usable.GetRange(lo, hi - lo);
                var vals = slice.Select(x => x.NiftyFwd10!.Value).ToList();
                var bLabel = $"{slice[0].Spread * 100:F2}% to {slice[^1].Spread * 100:F2}%";
                Console.WriteLine($"{bLabel,-24} | {slice.Count,6} | {vals.Average() * 100,13:F3} | {100.0 * vals.Count(v => v > 0) / vals.Count,7:F1}");
            }
        }

        PrintBuckets("Call", cpCallSamples.Select(s => (s.Spread, s.NiftyFwd10)).ToList());
        PrintBuckets("Put", cpPutSamples.Select(s => (s.Spread, s.NiftyFwd10)).ToList());
        PrintBuckets("Diff", cpDiffRows.Select(r => (r.Diff, r.NiftyFwd10)).ToList());
    }

    Console.WriteLine($"=== Strongest |r| at h=10, ALL days: {cpBestLabel} = {cpBestAbsR:F4} ===");

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "crossover-calibrate", StringComparison.OrdinalIgnoreCase))
{
    var (ccPositional, ccNamed) = SplitNamedArgs(args);
    if (ccPositional.Length < 3
        || !DateOnly.TryParseExact(ccPositional[1], "yyyy-MM-dd", out var ccFromDate)
        || !DateOnly.TryParseExact(ccPositional[2], "yyyy-MM-dd", out var ccToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- crossover-calibrate <fromDate> <toDate> [barVolumeThreshold=2600] [fastOptions=3,4,6] [slowOptions=10,12,16,20] [thresholdOptions=1,2,3,5] [targetTradesPerDayMin=7] [targetTradesPerDayMax=20] [--metric=SessionGatedDepthDurationConfirmed|OptionsScoreThreeWaySwitchMaxPainConfirmed] [--band=5]");
        return 1;
    }

    var ccBarVolumeThreshold = ccPositional.Length > 3 ? long.Parse(ccPositional[3]) : 2600L;
    var fastOptions = ccPositional.Length > 4 ? ccPositional[4].Split(',').Select(int.Parse).ToArray() : [3, 4, 6];
    var slowOptions = ccPositional.Length > 5 ? ccPositional[5].Split(',').Select(int.Parse).ToArray() : [10, 12, 16, 20];
    var thresholdOptions = ccPositional.Length > 6 ? ccPositional[6].Split(',').Select(double.Parse).ToArray() : [1, 2, 3, 5];
    var ccTargetMin = ccPositional.Length > 7 ? double.Parse(ccPositional[7]) : 7.0;
    var ccTargetMax = ccPositional.Length > 8 ? double.Parse(ccPositional[8]) : 20.0;
    var ccScoreMetric = ccNamed.TryGetValue("metric", out var ccMetricStr)
        ? Enum.Parse<VolumeBarMetric>(ccMetricStr, ignoreCase: true)
        : VolumeBarMetric.SessionGatedDepthDurationConfirmed;
    var ccBandWidth = ccNamed.TryGetValue("band", out var ccBandStr) ? int.Parse(ccBandStr) : 5;

    Console.WriteLine($"=== Crossover calibration sweep, metric={ccScoreMetric}, barThreshold={ccBarVolumeThreshold}, target {ccTargetMin}-{ccTargetMax} trades/day ===");
    Console.WriteLine($"{"Fast",5} | {"Slow",5} | {"Thresh",6} | {"Trades",7} | {"Trades/Day",10} | {"WinRate",8} | {"NetPts",9} | In target?");
    foreach (var fast in fastOptions)
    {
        foreach (var slow in slowOptions)
        {
            if (fast >= slow)
            {
                continue;
            }

            foreach (var threshold in thresholdOptions)
            {
                var trades = new List<VolumeBarTrade>();
                var dayCount = 0;
                for (var date = ccFromDate; date <= ccToDate; date = date.AddDays(1))
                {
                    await using var source = new NiftySignalDbContext(tradeSourceOptions);
                    await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
                    var dayTrades = await TradeSimulator.SimulateCrossoverDayAsync(source, volumeBars, date, ccBarVolumeThreshold, fast, slow, threshold, CancellationToken.None, sharedOptionPriceCache, ccScoreMetric, ccBandWidth);
                    if (dayTrades.Count == 0 && !await volumeBars.VolumeBars.AnyAsync(b => b.AsOfDate == date && b.BarVolumeThreshold == ccBarVolumeThreshold))
                    {
                        continue;
                    }

                    dayCount++;
                    trades.AddRange(dayTrades);
                }

                if (dayCount == 0)
                {
                    continue;
                }

                var winRate = trades.Count > 0 ? 100.0 * trades.Count(t => t.NetPnlPoints > 0) / trades.Count : 0;
                var net = trades.Sum(t => t.NetPnlPoints);
                var tradesPerDay = trades.Count / (double)dayCount;
                var inTarget = tradesPerDay >= ccTargetMin && tradesPerDay <= ccTargetMax;
                Console.WriteLine($"{fast,5} | {slow,5} | {threshold,6:F0} | {trades.Count,7} | {tradesPerDay,10:F1} | {winRate,7:F1}% | {net,9:F2} |{(inTarget ? "  <-- TARGET" : "")}");
            }
        }
    }

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "sanity-oi-raw", StringComparison.OrdinalIgnoreCase))
{
    var rawDate = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
    await using var source = new NiftySignalDbContext(tradeSourceOptions);
    var strike = decimal.Parse(args[2]);
    var side = Enum.Parse<OptionType>(args[3], ignoreCase: true);
    // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
    // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
    var instrument = await source.Instruments
        .Where(i => i.AsOfDate == rawDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.StrikePrice == strike && i.OptionType == side && i.Underlying == "NIFTY")
        .OrderBy(i => i.ExpiryDate)
        .FirstAsync();
    var token = instrument.Token;
    Console.WriteLine($"token={token}, expiry={instrument.ExpiryDate:yyyy-MM-dd}");
    var dayStart = new DateTimeOffset(rawDate.ToDateTime(new TimeOnly(3, 45)), TimeSpan.Zero);
    var dayEnd = dayStart.AddHours(6.25);
    var ticks = await source.Ticks.AsNoTracking()
        .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd)
        .OrderBy(t => t.ExchangeTimestamp)
        .Select(t => new { t.ExchangeTimestamp, t.OpenInterest })
        .ToListAsync();
    Console.WriteLine($"totalTicks={ticks.Count}, ticksWithOi={ticks.Count(t => t.OpenInterest != null)}, distinctOiValues={ticks.Select(t => t.OpenInterest).Distinct().Count()}");
    foreach (var g in ticks.Where(t => t.OpenInterest != null).GroupBy(t => t.OpenInterest).Take(10))
    {
        Console.WriteLine($"  OI={g.Key}, firstSeen={g.First().ExchangeTimestamp:HH:mm:ss}, count={g.Count()}");
    }
    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "sanity-skew25delta", StringComparison.OrdinalIgnoreCase))
{
    var sanityDate = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
    var sanityThreshold = long.Parse(args[2]);
    await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
    var rows = await volumeBars.OptionSkew25DeltaBars
        .Where(b => b.AsOfDate == sanityDate && b.BarVolumeThreshold == sanityThreshold)
        .ToListAsync();
    Console.WriteLine($"rows={rows.Count}");
    Console.WriteLine($"nullSkewRatio={rows.Count(r => r.SkewRatio is null)}");
    var ratios = rows.Where(r => r.SkewRatio is not null).Select(r => r.SkewRatio!.Value).ToList();
    Console.WriteLine($"minRatio={(ratios.Count > 0 ? ratios.Min() : 0):F4}, maxRatio={(ratios.Count > 0 ? ratios.Max() : 0):F4}, avgRatio={(ratios.Count > 0 ? ratios.Average() : 0):F4}");
    var callIvs = rows.Where(r => r.Call25DeltaIv is not null).Select(r => r.Call25DeltaIv!.Value).ToList();
    var putIvs = rows.Where(r => r.Put25DeltaIv is not null).Select(r => r.Put25DeltaIv!.Value).ToList();
    Console.WriteLine($"callIv range={(callIvs.Count > 0 ? callIvs.Min() : 0):F3}-{(callIvs.Count > 0 ? callIvs.Max() : 0):F3}, putIv range={(putIvs.Count > 0 ? putIvs.Min() : 0):F3}-{(putIvs.Count > 0 ? putIvs.Max() : 0):F3}");
    var distinctCallStrikes = rows.Where(r => r.Call25DeltaStrike is not null).Select(r => r.Call25DeltaStrike!.Value).Distinct().Count();
    var distinctPutStrikes = rows.Where(r => r.Put25DeltaStrike is not null).Select(r => r.Put25DeltaStrike!.Value).Distinct().Count();
    Console.WriteLine($"distinctCall25Strikes={distinctCallStrikes}, distinctPut25Strikes={distinctPutStrikes}");
    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "sanity-maxpain", StringComparison.OrdinalIgnoreCase))
{
    var sanityDate = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
    var sanityThreshold = long.Parse(args[2]);
    await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
    var rows = await volumeBars.OptionMaxPainBars
        .Where(b => b.AsOfDate == sanityDate && b.BarVolumeThreshold == sanityThreshold)
        .Join(volumeBars.VolumeBars.Where(v => v.AsOfDate == sanityDate && v.BarVolumeThreshold == sanityThreshold), b => b.BarIndex, v => v.BarIndex, (b, v) => new { b.MaxPainStrike, b.HighestOiStrike, v.ClosePrice })
        .ToListAsync();
    Console.WriteLine($"rows={rows.Count}");
    Console.WriteLine($"nullMaxPain={rows.Count(r => r.MaxPainStrike is null)}, nullHighestOi={rows.Count(r => r.HighestOiStrike is null)}");
    var mpStrikes = rows.Where(r => r.MaxPainStrike is not null).Select(r => r.MaxPainStrike!.Value).Distinct().ToList();
    var oiStrikes = rows.Where(r => r.HighestOiStrike is not null).Select(r => r.HighestOiStrike!.Value).Distinct().ToList();
    Console.WriteLine($"distinctMaxPainStrikes={mpStrikes.Count} ({string.Join(",", mpStrikes.OrderBy(s => s))})");
    Console.WriteLine($"distinctHighestOiStrikes={oiStrikes.Count} ({string.Join(",", oiStrikes.OrderBy(s => s))})");
    var dists = rows.Where(r => r.MaxPainStrike is not null).Select(r => r.ClosePrice - r.MaxPainStrike!.Value).ToList();
    Console.WriteLine($"distanceToMaxPain range={dists.Min():F0} to {dists.Max():F0}, avg={dists.Average():F0}");
    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "sanity-options-oi", StringComparison.OrdinalIgnoreCase))
{
    var sanityDate = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
    var sanityThreshold = long.Parse(args[2]);
    await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
    var rows = await volumeBars.OptionOiBars
        .Where(b => b.AsOfDate == sanityDate && b.BarVolumeThreshold == sanityThreshold)
        .ToListAsync();
    Console.WriteLine($"rows={rows.Count}");
    Console.WriteLine($"nullBuildup={rows.Count(r => r.BuildupNet is null)}");
    Console.WriteLine($"avgCallOiChangeNotional={rows.Average(r => r.CallOiChangeNotional):F0}");
    Console.WriteLine($"avgPutOiChangeNotional={rows.Average(r => r.PutOiChangeNotional):F0}");
    Console.WriteLine($"nonZeroCallFlow={rows.Count(r => r.CallOiChangeNotional != 0)}, nonZeroPutFlow={rows.Count(r => r.PutOiChangeNotional != 0)}");
    var buildupVals = rows.Where(r => r.BuildupNet is not null).Select(r => r.BuildupNet!.Value).ToList();
    Console.WriteLine($"buildupNonNullCount={buildupVals.Count}, nonZero={buildupVals.Count(v => v != 0.0)}");
    Console.WriteLine($"minBuildup={(buildupVals.Count > 0 ? buildupVals.Min() : 0):F2}, maxBuildup={(buildupVals.Count > 0 ? buildupVals.Max() : 0):F2}, avgBuildup={(buildupVals.Count > 0 ? buildupVals.Average() : 0):F2}");
    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "trade", StringComparison.OrdinalIgnoreCase))
{
    var (tradePositional, tradeNamed) = SplitNamedArgs(args);
    if (tradePositional.Length < 4
        || !DateOnly.TryParseExact(tradePositional[1], "yyyy-MM-dd", out var tradeFromDate)
        || !DateOnly.TryParseExact(tradePositional[2], "yyyy-MM-dd", out var tradeToDate)
        || !Enum.TryParse<VolumeBarMetric>(tradePositional[3], ignoreCase: true, out var metric))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- trade <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> <metric> [entryPercentile] [trendWindowBars] [barVolumeThreshold] [--stop=N] [--rolling=N] [--band=N]");
        Console.Error.WriteLine($"  <metric> is one of: {string.Join(", ", Enum.GetNames<VolumeBarMetric>())}");
        Console.Error.WriteLine("  --stop=30 means exit if the option premium falls 30% below entry -- omit for no stop (default, original behavior).");
        Console.Error.WriteLine("  --rolling=650 -- when set, barVolumeThreshold is built as a ROLLING window of this many-sized sub-bars (must divide barVolumeThreshold exactly) instead of reading a pre-populated fixed bar; omit for the original fixed-bar behavior.");
        Console.Error.WriteLine("  --band=5 only used by the 3 Phase-2 depth metrics -- strikes in the band (3 = ATM+/-1, the default; 5 = ATM+/-2). Must match a value already populated via populate-options-depth.");
        Console.Error.WriteLine("  These 3 are named flags (can appear anywhere, in any order) rather than positional -- PowerShell silently drops empty-string \"\" placeholder args, which used to corrupt runs that skipped one of these. Named flags avoid that entirely.");
        return 1;
    }

    var entryPercentile = tradePositional.Length > 4 ? double.Parse(tradePositional[4]) : 90.0;
    var trendWindowBars = tradePositional.Length > 5 ? int.Parse(tradePositional[5]) : 15;
    var tradeThreshold = tradePositional.Length > 6 ? long.Parse(tradePositional[6]) : 1300L;
    var stopLossPercent = tradeNamed.TryGetValue("stop", out var stopStr) ? (decimal?)(decimal.Parse(stopStr) / 100m) : null;
    var rollingSubBarThreshold = tradeNamed.TryGetValue("rolling", out var rollingStr) ? (long?)long.Parse(rollingStr) : null;
    var tradeDepthBandWidth = tradeNamed.TryGetValue("band", out var bandStr) ? int.Parse(bandStr) : OptionDepthPopulator.DefaultBandWidth;
    var tradeSwitchTime = tradeNamed.TryGetValue("switchtime", out var switchStr) ? (TimeSpan?)TimeSpan.Parse(switchStr) : null;
    var tradeEntryStart = tradeNamed.TryGetValue("entrystart", out var entryStartStr) ? (TimeSpan?)TimeSpan.Parse(entryStartStr) : null;
    // 2026-09-22, Open-phase Put edge deep dive -- counterpart to --entrystart=, lets a run be
    // gated to an Open-only window (--entrystart=09:30 --entryend=10:00). Null by default (no-op,
    // unchanged EntryWindowEnd behavior for every existing use).
    var tradeEntryEnd = tradeNamed.TryGetValue("entryend", out var entryEndStr) ? (TimeSpan?)TimeSpan.Parse(entryEndStr) : null;
    // 2026-09-21, whipsaw-reduction experiment -- only meaningful for
    // OptionsScoreThreeWaySwitchMaxPainConfirmedSmoothed/...MinHold respectively; no-op for every
    // other metric.
    var tradeSmoothBars = tradeNamed.TryGetValue("smoothbars", out var smoothBarsStr) ? (int?)int.Parse(smoothBarsStr) : null;
    var tradeMinHoldMinutes = tradeNamed.TryGetValue("minhold", out var minHoldStr) ? (double?)double.Parse(minHoldStr) : null;
    // 2026-09-21, user's own explicit entry-premium band (e.g. --minprice=100 --maxprice=150) --
    // a bar whose ATM premium falls outside is simply skipped, no trade that bar. Both null by
    // default (no filter, unchanged locked behavior).
    var tradeMinEntryPrice = tradeNamed.TryGetValue("minprice", out var minPriceStr) ? (decimal?)decimal.Parse(minPriceStr) : null;
    var tradeMaxEntryPrice = tradeNamed.TryGetValue("maxprice", out var maxPriceStr) ? (decimal?)decimal.Parse(maxPriceStr) : null;
    // 2026-09-21, backtest-only risk-rule sweep (old live engine's ExitRuleEvaluator mechanics,
    // never before tested against either strategy now live) -- all 3 off by default (null), same
    // named-flag convention as --stop=. --tp1pct=15 --tp1frac=50 means "book 50% of quantity once
    // premium is up 15% from entry, trail the remaining leg's stop to breakeven" (see
    // TradeSimulator.RiskRuleState's own doc comment). --dailyloss=20 means "stop taking new
    // entries once today's realized P&L crosses -20% of the reference capital documented on
    // RiskRuleState" -- open positions still exit normally.
    var tradeTp1ProfitPct = tradeNamed.TryGetValue("tp1pct", out var tp1PctStr) ? (decimal?)(decimal.Parse(tp1PctStr) / 100m) : null;
    var tradeTp1Fraction = tradeNamed.TryGetValue("tp1frac", out var tp1FracStr) ? (decimal?)(decimal.Parse(tp1FracStr) / 100m) : null;
    var tradeDailyLossCapPct = tradeNamed.TryGetValue("dailyloss", out var dailyLossStr) ? (decimal?)decimal.Parse(dailyLossStr) : null;
    // 2026-09-22, cross-session rolling-baseline research task -- only meaningful for
    // OptionsScoreThreeWaySwitchMaxPainConfirmedRollingBoundedTransform/...ScoreEma; no-op for
    // every other metric. Defaults to 3 prior populated trading days when omitted.
    var tradeRollingWindowDays = tradeNamed.TryGetValue("rollingdays", out var rollingDaysStr) ? (int?)int.Parse(rollingDaysStr) : null;

    Console.WriteLine($"=== Volume-bar trade simulation: metric={metric}, entry-percentile>={entryPercentile}, bar-threshold={tradeThreshold}, stopLoss={(stopLossPercent is { } slp ? $"{slp:P0}" : "none")}, rolling={(rollingSubBarThreshold is { } rsbt ? $"{rsbt}-wide" : "no")}, depthBandWidth={tradeDepthBandWidth}{(tradeSmoothBars is { } tsb ? $", smoothingWindowBars={tsb}" : "")}{(tradeMinHoldMinutes is { } tmh ? $", minHoldMinutes={tmh}" : "")}{(tradeMinEntryPrice is not null || tradeMaxEntryPrice is not null ? $", entryPriceBand=[{tradeMinEntryPrice?.ToString() ?? "-inf"},{tradeMaxEntryPrice?.ToString() ?? "+inf"}]" : "")}{(tradeTp1ProfitPct is { } tp1p ? $", tp1={tp1p:P0}@{tradeTp1Fraction:P0}" : "")}{(tradeDailyLossCapPct is { } dlc ? $", dailyLossCap={dlc}%" : "")}{(tradeRollingWindowDays is { } trwd ? $", rollingWindowDays={trwd}" : "")} ===");
    var allTrades = new List<VolumeBarTrade>();
    for (var date = tradeFromDate; date <= tradeToDate; date = date.AddDays(1))
    {
        var dayTrades = await RunRangeAsync(date, date, metric, entryPercentile, trendWindowBars, tradeThreshold, stopLossPercent, rollingSubBarThreshold, tradeDepthBandWidth, tradeSwitchTime, tradeEntryStart, sessionWeightsOnFutures: null, tradeSmoothBars, tradeMinHoldMinutes, tradeMinEntryPrice, tradeMaxEntryPrice, tradeTp1ProfitPct, tradeTp1Fraction, tradeDailyLossCapPct, tradeEntryEnd, tradeRollingWindowDays);
        if (dayTrades.Count == 0)
        {
            continue;
        }

        allTrades.AddRange(dayTrades);
        var dayWinRate = 100.0 * dayTrades.Count(t => t.NetPnlPoints > 0) / dayTrades.Count;
        var dayNet = dayTrades.Sum(t => t.NetPnlPoints);
        Console.WriteLine($"{date:yyyy-MM-dd}: {dayTrades.Count} trades, {dayWinRate:F1}% win rate, net {dayNet:F2} pts");
        foreach (var t in dayTrades)
        {
            var side = t.Side == OptionType.Call ? "Call" : "Put";
            Console.WriteLine($"    {FormatIst(t.EntryTime)} Long {side}@{t.StrikePrice} entry={t.EntryPrice:F2} score={t.EntryScore:F1} -> {FormatIst(t.ExitTime)} exit={t.ExitPrice:F2} ({t.ExitReason}) netPnl={t.NetPnlPoints:F2} ({t.NetPnlPercent:F1}%)");
        }
    }

    if (allTrades.Count > 0)
    {
        var winRate = 100.0 * allTrades.Count(t => t.NetPnlPoints > 0) / allTrades.Count;
        var net = allTrades.Sum(t => t.NetPnlPoints);
        var netPercent = allTrades.Sum(t => t.NetPnlPercent);
        Console.WriteLine($"--- {metric} total: {allTrades.Count} trades, {winRate:F1}% win rate, net {net:F2} pts ({netPercent:F1}%), avgPerTrade={netPercent / allTrades.Count:F1}% ---");
    }
    else
    {
        Console.WriteLine("No trades fired across the requested range -- try a lower entryPercentile.");
    }

    return 0;
}

// 2026-09-22, MAE/MFE analytics task (docs/VOLUME_BAR_FINDINGS.md's own dated section): reruns
// either the "trade" or "crossover" simulation to get the exact same trade list those commands
// would produce (never reimplements trade generation), then for each trade reconstructs its
// traded instrument's Token (VolumeBarTrade itself has no Token -- see TradeSimulator.cs's own
// doc comment on why) by re-querying the day's option chain the same way
// AtmStrikeSelector/PickStrikeInBandAsync already do: AsOfDate + StrikePrice + OptionType
// uniquely identifies the instrument, since this codebase's convention is one expiry in play per
// day (verified against SimulateDayAsync/SimulateCrossoverDayAsync's own single `nearestExpiry`
// chain-build). Loads that instrument's real tick price path for exactly the EntryTime..ExitTime
// window (OptionPriceSeries.LoadAsync, same Ticks table/ordering convention every other price
// lookup here uses) and computes MAE/MFE via the separately-tested MaeMfeCalculator.
//   dotnet run --project NiftySignal.VolumeBarData -- mae-mfe trade <fromDate> <toDate> <metric> [entryPercentile] [trendWindowBars] [barVolumeThreshold] [--band=] [--minprice=] [--maxprice=] ...
//   dotnet run --project NiftySignal.VolumeBarData -- mae-mfe crossover <fromDate> <toDate> [fastBars] [slowBars] [thresholdPoints] [barVolumeThreshold] [--metric=] [--band=] [--minprice=] [--maxprice=] ...
if (args.Length > 0 && string.Equals(args[0], "mae-mfe", StringComparison.OrdinalIgnoreCase))
{
    var (mmPositional, mmNamed) = SplitNamedArgs(args);
    if (mmPositional.Length < 2
        || (!string.Equals(mmPositional[1], "trade", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mmPositional[1], "crossover", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mmPositional[1], "price-crossover", StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- mae-mfe trade <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> <metric> [entryPercentile] [trendWindowBars] [barVolumeThreshold] [--band=N] [--minprice=] [--maxprice=]");
        Console.Error.WriteLine("   or: dotnet run --project NiftySignal.VolumeBarData -- mae-mfe crossover <fromDate> <toDate> [fastBars=4] [slowBars=12] [thresholdPoints=2] [barVolumeThreshold=2600] [--metric=] [--band=5] [--minprice=] [--maxprice=]");
        Console.Error.WriteLine("   or: dotnet run --project NiftySignal.VolumeBarData -- mae-mfe price-crossover <fromDate> <toDate> <side:Call|Put|DualAgreement|DualAgreementBothExit> [fastBars=3] [slowBars=10] [thresholdPct=1.0] [barVolumeThreshold=2600] [--band=3|5] [--bandmode=Symmetric|ItmSide|OtmSide] [--putfast=] [--putslow=] [--fasttype=Sma|Ema] [--slowtype=Sma|Ema] [--minprice=] [--maxprice=]");
        return 1;
    }

    var mmMode = mmPositional[1].ToLowerInvariant();
    var mmSub = mmPositional.Skip(1).ToArray(); // mmSub[0] = "trade"/"crossover" (mirrors that command's own positional[0]), mmSub[1..] = the rest, unchanged.

    List<VolumeBarTrade> mmTrades;
    if (mmMode == "trade")
    {
        if (mmSub.Length < 4
            || !DateOnly.TryParseExact(mmSub[1], "yyyy-MM-dd", out var mmFromDate)
            || !DateOnly.TryParseExact(mmSub[2], "yyyy-MM-dd", out var mmToDate)
            || !Enum.TryParse<VolumeBarMetric>(mmSub[3], ignoreCase: true, out var mmMetric))
        {
            Console.Error.WriteLine("Bad mae-mfe trade arguments -- see usage above.");
            return 1;
        }

        var mmEntryPercentile = mmSub.Length > 4 ? double.Parse(mmSub[4]) : 90.0;
        var mmTrendWindowBars = mmSub.Length > 5 ? int.Parse(mmSub[5]) : 15;
        var mmThreshold = mmSub.Length > 6 ? long.Parse(mmSub[6]) : 1300L;
        var mmStopLossPercent = mmNamed.TryGetValue("stop", out var mmStopStr) ? (decimal?)(decimal.Parse(mmStopStr) / 100m) : null;
        var mmRolling = mmNamed.TryGetValue("rolling", out var mmRollingStr) ? (long?)long.Parse(mmRollingStr) : null;
        var mmBand = mmNamed.TryGetValue("band", out var mmBandStr) ? int.Parse(mmBandStr) : OptionDepthPopulator.DefaultBandWidth;
        var mmSwitchTime = mmNamed.TryGetValue("switchtime", out var mmSwitchStr) ? (TimeSpan?)TimeSpan.Parse(mmSwitchStr) : null;
        var mmEntryStart = mmNamed.TryGetValue("entrystart", out var mmEntryStartStr) ? (TimeSpan?)TimeSpan.Parse(mmEntryStartStr) : null;
        var mmEntryEnd = mmNamed.TryGetValue("entryend", out var mmEntryEndStr) ? (TimeSpan?)TimeSpan.Parse(mmEntryEndStr) : null;
        var mmSmoothBars = mmNamed.TryGetValue("smoothbars", out var mmSmoothBarsStr) ? (int?)int.Parse(mmSmoothBarsStr) : null;
        var mmMinHold = mmNamed.TryGetValue("minhold", out var mmMinHoldStr) ? (double?)double.Parse(mmMinHoldStr) : null;
        var mmMinPrice = mmNamed.TryGetValue("minprice", out var mmMinPriceStr) ? (decimal?)decimal.Parse(mmMinPriceStr) : null;
        var mmMaxPrice = mmNamed.TryGetValue("maxprice", out var mmMaxPriceStr) ? (decimal?)decimal.Parse(mmMaxPriceStr) : null;
        var mmTp1Pct = mmNamed.TryGetValue("tp1pct", out var mmTp1PctStr) ? (decimal?)(decimal.Parse(mmTp1PctStr) / 100m) : null;
        var mmTp1Frac = mmNamed.TryGetValue("tp1frac", out var mmTp1FracStr) ? (decimal?)decimal.Parse(mmTp1FracStr) / 100m : null;
        var mmDailyLoss = mmNamed.TryGetValue("dailyloss", out var mmDailyLossStr) ? (decimal?)decimal.Parse(mmDailyLossStr) : null;
        var mmRollingWindowDays = mmNamed.TryGetValue("rollingdays", out var mmRollingDaysStr) ? (int?)int.Parse(mmRollingDaysStr) : null;

        Console.WriteLine($"=== mae-mfe trade: metric={mmMetric}, entry-percentile>={mmEntryPercentile}, bar-threshold={mmThreshold}, band={mmBand}{(mmMinPrice is not null || mmMaxPrice is not null ? $", entryPriceBand=[{mmMinPrice?.ToString() ?? "-inf"},{mmMaxPrice?.ToString() ?? "+inf"}]" : "")}{(mmRollingWindowDays is { } mmrwd ? $", rollingWindowDays={mmrwd}" : "")} ===");
        mmTrades = await RunRangeAsync(mmFromDate, mmToDate, mmMetric, mmEntryPercentile, mmTrendWindowBars, mmThreshold, mmStopLossPercent, mmRolling, mmBand, mmSwitchTime, mmEntryStart, sessionWeightsOnFutures: null, mmSmoothBars, mmMinHold, mmMinPrice, mmMaxPrice, mmTp1Pct, mmTp1Frac, mmDailyLoss, mmEntryEnd, mmRollingWindowDays);
    }
    else if (mmMode == "crossover")
    {
        if (mmSub.Length < 3
            || !DateOnly.TryParseExact(mmSub[1], "yyyy-MM-dd", out var mmFromDate)
            || !DateOnly.TryParseExact(mmSub[2], "yyyy-MM-dd", out var mmToDate))
        {
            Console.Error.WriteLine("Bad mae-mfe crossover arguments -- see usage above.");
            return 1;
        }

        var mmFastBars = mmSub.Length > 3 ? int.Parse(mmSub[3]) : 4;
        var mmSlowBars = mmSub.Length > 4 ? int.Parse(mmSub[4]) : 12;
        var mmThresholdPoints = mmSub.Length > 5 ? double.Parse(mmSub[5]) : 2.0;
        var mmBarVolumeThreshold = mmSub.Length > 6 ? long.Parse(mmSub[6]) : 2600L;
        var mmScoreMetric = mmNamed.TryGetValue("metric", out var mmMetricStr) ? Enum.Parse<VolumeBarMetric>(mmMetricStr, ignoreCase: true) : VolumeBarMetric.SessionGatedDepthDurationConfirmed;
        var mmBandWidth = mmNamed.TryGetValue("band", out var mmBandStr) ? int.Parse(mmBandStr) : 5;
        var mmMinPrice = mmNamed.TryGetValue("minprice", out var mmMinPriceStr) ? (decimal?)decimal.Parse(mmMinPriceStr) : null;
        var mmMaxPrice = mmNamed.TryGetValue("maxprice", out var mmMaxPriceStr) ? (decimal?)decimal.Parse(mmMaxPriceStr) : null;
        var mmStopLossPercent = mmNamed.TryGetValue("stop", out var mmStopStr) ? (decimal?)(decimal.Parse(mmStopStr) / 100m) : null;
        var mmTp1Pct = mmNamed.TryGetValue("tp1pct", out var mmTp1PctStr) ? (decimal?)(decimal.Parse(mmTp1PctStr) / 100m) : null;
        var mmTp1Frac = mmNamed.TryGetValue("tp1frac", out var mmTp1FracStr) ? (decimal?)decimal.Parse(mmTp1FracStr) / 100m : null;
        var mmDailyLoss = mmNamed.TryGetValue("dailyloss", out var mmDailyLossStr) ? (decimal?)decimal.Parse(mmDailyLossStr) : null;

        Console.WriteLine($"=== mae-mfe crossover: metric={mmScoreMetric} fast={mmFastBars} slow={mmSlowBars} thresholdPoints={mmThresholdPoints} barThreshold={mmBarVolumeThreshold}{(mmMinPrice is not null || mmMaxPrice is not null ? $", entryPriceBand=[{mmMinPrice?.ToString() ?? "-inf"},{mmMaxPrice?.ToString() ?? "+inf"}]" : "")} ===");
        mmTrades = [];
        for (var date = mmFromDate; date <= mmToDate; date = date.AddDays(1))
        {
            await using var mmSource = new NiftySignalDbContext(tradeSourceOptions);
            await using var mmVolumeBars = new VolumeBarDbContext(volumeBarOptions);
            mmTrades.AddRange(await TradeSimulator.SimulateCrossoverDayAsync(mmSource, mmVolumeBars, date, mmBarVolumeThreshold, mmFastBars, mmSlowBars, mmThresholdPoints, CancellationToken.None, sharedOptionPriceCache, mmScoreMetric, mmBandWidth, optionsSwitchTime: null, mmMinPrice, mmMaxPrice, mmStopLossPercent, mmTp1Pct, mmTp1Frac, mmDailyLoss));
        }
    }
    else
    {
        if (mmSub.Length < 4
            || !DateOnly.TryParseExact(mmSub[1], "yyyy-MM-dd", out var mmFromDate)
            || !DateOnly.TryParseExact(mmSub[2], "yyyy-MM-dd", out var mmToDate)
            || !Enum.TryParse<PriceCrossoverSide>(mmSub[3], ignoreCase: true, out var mmPcSide))
        {
            Console.Error.WriteLine("Bad mae-mfe price-crossover arguments -- see usage above.");
            return 1;
        }

        var mmPcFastBars = mmSub.Length > 4 ? int.Parse(mmSub[4]) : 3;
        var mmPcSlowBars = mmSub.Length > 5 ? int.Parse(mmSub[5]) : 10;
        var mmPcThresholdPct = mmSub.Length > 6 ? double.Parse(mmSub[6]) : 1.0;
        var mmPcBarVolumeThreshold = mmSub.Length > 7 ? long.Parse(mmSub[7]) : 2600L;
        var mmPcBandWidth = mmNamed.TryGetValue("band", out var mmPcBandStr) ? (int?)int.Parse(mmPcBandStr) : null;
        var mmPcPutFastBars = mmNamed.TryGetValue("putfast", out var mmPcPutFastStr) ? (int?)int.Parse(mmPcPutFastStr) : null;
        var mmPcPutSlowBars = mmNamed.TryGetValue("putslow", out var mmPcPutSlowStr) ? (int?)int.Parse(mmPcPutSlowStr) : null;
        var mmPcFastType = mmNamed.TryGetValue("fasttype", out var mmPcFastTypeStr) ? Enum.Parse<MaType>(mmPcFastTypeStr, ignoreCase: true) : MaType.Sma;
        var mmPcSlowType = mmNamed.TryGetValue("slowtype", out var mmPcSlowTypeStr) ? Enum.Parse<MaType>(mmPcSlowTypeStr, ignoreCase: true) : MaType.Sma;
        var mmPcMinPrice = mmNamed.TryGetValue("minprice", out var mmPcMinPriceStr) ? (decimal?)decimal.Parse(mmPcMinPriceStr) : null;
        var mmPcMaxPrice = mmNamed.TryGetValue("maxprice", out var mmPcMaxPriceStr) ? (decimal?)decimal.Parse(mmPcMaxPriceStr) : null;
        var mmPcBandComposition = mmNamed.TryGetValue("bandmode", out var mmPcBandModeStr) ? Enum.Parse<PriceBandComposition>(mmPcBandModeStr, ignoreCase: true) : PriceBandComposition.Symmetric;
        var mmPcEntryStart = mmNamed.TryGetValue("entrystart", out var mmPcEntryStartStr) ? (TimeSpan?)TimeSpan.Parse(mmPcEntryStartStr) : null;
        var mmPcEntryEnd = mmNamed.TryGetValue("entryend", out var mmPcEntryEndStr) ? (TimeSpan?)TimeSpan.Parse(mmPcEntryEndStr) : null;

        Console.WriteLine($"=== mae-mfe price-crossover: side={mmPcSide} fast={mmPcFastBars}({mmPcFastType}) slow={mmPcSlowBars}({mmPcSlowType}) thresholdPct={mmPcThresholdPct} barThreshold={mmPcBarVolumeThreshold}{(mmPcBandWidth is { } mmpcbw ? $", band={mmpcbw}({mmPcBandComposition})" : "")}{(mmPcMinPrice is not null || mmPcMaxPrice is not null ? $", entryPriceBand=[{mmPcMinPrice?.ToString() ?? "-inf"},{mmPcMaxPrice?.ToString() ?? "+inf"}]" : "")} ===");
        mmTrades = [];
        for (var date = mmFromDate; date <= mmToDate; date = date.AddDays(1))
        {
            await using var mmSource = new NiftySignalDbContext(tradeSourceOptions);
            await using var mmVolumeBars = new VolumeBarDbContext(volumeBarOptions);
            mmTrades.AddRange(await TradeSimulator.SimulatePriceCrossoverDayAsync(mmSource, mmVolumeBars, date, mmPcBarVolumeThreshold, mmPcSide, mmPcFastBars, mmPcSlowBars, mmPcThresholdPct / 100.0, CancellationToken.None, sharedOptionPriceCache, mmPcBandWidth, mmPcPutFastBars, mmPcPutSlowBars, mmPcFastType, mmPcSlowType, mmPcMinPrice, mmPcMaxPrice, mmPcBandComposition, mmPcEntryStart, mmPcEntryEnd));
        }
    }

    if (mmTrades.Count == 0)
    {
        Console.WriteLine("No trades fired across the requested range -- nothing to analyze.");
        return 0;
    }

    var mmWinRate = 100.0 * mmTrades.Count(t => t.NetPnlPoints > 0) / mmTrades.Count;
    var mmNet = mmTrades.Sum(t => t.NetPnlPoints);
    Console.WriteLine($"--- {mmMode} total: {mmTrades.Count} trades, {mmWinRate:F1}% win rate, net {mmNet:F2} pts (reproduction check -- compare against the known baseline before trusting the MAE/MFE numbers below) ---");
    Console.WriteLine();

    // Token reconstruction: cache each day's option chain once (many trades share a day), same
    // AsOfDate+ExpiryDate!=null query SimulateDayAsync/SimulateCrossoverDayAsync build their own
    // `chain` from -- a trade's StrikePrice+Side is then unique within that one day's chain.
    await using var mmAnalysisSource = new NiftySignalDbContext(tradeSourceOptions);
    var mmChainCache = new Dictionary<DateOnly, List<NiftySignal.Domain.Entities.Instrument>>();
    async Task<List<NiftySignal.Domain.Entities.Instrument>> GetChainAsync(DateOnly date)
    {
        if (mmChainCache.TryGetValue(date, out var cached))
        {
            return cached;
        }

        // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
        // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
        var options = await mmAnalysisSource.Instruments
            .Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .ToListAsync();
        mmChainCache[date] = options;
        return options;
    }

    Console.WriteLine($"{"EntryTime",10} | {"Strike",7} | {"Side",4} | {"Entry",8} | {"Exit",8} | {"NetPnl",8} | {"MAE",8} | {"MFE",8} | {"MAE%",7} | {"MFE%",7}");

    var mmMaePoints = new List<decimal>();
    var mmMfePoints = new List<decimal>();
    var mmMaePercents = new List<decimal>();
    var mmMfePercents = new List<decimal>();
    var mmRecoveredCount = 0;
    var mmGaveBackCount = 0;
    var mmMissingTokenCount = 0;

    foreach (var t in mmTrades)
    {
        // Ticks are stored UTC throughout this project (Program.cs's own top-of-file comment) --
        // EntryTime's UTC date is the same AsOfDate the day's chain was built for.
        var tradeDate = DateOnly.FromDateTime(t.EntryTime.UtcDateTime);
        var chain = await GetChainAsync(tradeDate);
        var instrument = chain.FirstOrDefault(i => i.StrikePrice == t.StrikePrice && i.OptionType == t.Side);
        if (instrument is null)
        {
            mmMissingTokenCount++;
            Console.WriteLine($"    (no instrument match for {FormatIst(t.EntryTime)} strike={t.StrikePrice} side={t.Side} on {tradeDate:yyyy-MM-dd} -- skipped)");
            continue;
        }

        var series = await OptionPriceSeries.LoadAsync(mmAnalysisSource, instrument.Token, t.EntryTime, t.ExitTime, CancellationToken.None);
        var prices = series.AllPrices.Select(p => p.Price).ToList();
        var result = MaeMfeCalculator.Compute(t.EntryPrice, prices);

        mmMaePoints.Add(result.MaePoints);
        mmMfePoints.Add(result.MfePoints);
        mmMaePercents.Add(result.MaePercent);
        mmMfePercents.Add(result.MfePercent);

        var finalLossMagnitude = Math.Max(0m, -t.NetPnlPoints);
        var finalGainMagnitude = Math.Max(0m, t.NetPnlPoints);
        if (result.MaePoints > finalLossMagnitude)
        {
            mmRecoveredCount++;
        }
        if (result.MfePoints > finalGainMagnitude)
        {
            mmGaveBackCount++;
        }

        var side = t.Side == OptionType.Call ? "Call" : "Put";
        Console.WriteLine($"{FormatIst(t.EntryTime),10} | {t.StrikePrice,7} | {side,4} | {t.EntryPrice,8:F2} | {t.ExitPrice,8:F2} | {t.NetPnlPoints,8:F2} | {result.MaePoints,8:F2} | {result.MfePoints,8:F2} | {result.MaePercent,7:F2} | {result.MfePercent,7:F2}");
    }

    if (mmMaePoints.Count > 0)
    {
        var mmMaePointsSorted = mmMaePoints.OrderBy(x => x).ToList();
        var mmMfePointsSorted = mmMfePoints.OrderBy(x => x).ToList();
        var mmMaePercentsSorted = mmMaePercents.OrderBy(x => x).ToList();
        var mmMfePercentsSorted = mmMfePercents.OrderBy(x => x).ToList();
        Console.WriteLine();
        Console.WriteLine("--- MAE/MFE summary ---");
        Console.WriteLine($"Trades analyzed: {mmMaePoints.Count}{(mmMissingTokenCount > 0 ? $" ({mmMissingTokenCount} skipped -- no instrument match)" : "")}");
        Console.WriteLine($"MAE (points): avg={mmMaePoints.Average():F2}, median={mmMaePointsSorted[mmMaePointsSorted.Count / 2]:F2}, worst={mmMaePointsSorted[^1]:F2}");
        Console.WriteLine($"MFE (points): avg={mmMfePoints.Average():F2}, median={mmMfePointsSorted[mmMfePointsSorted.Count / 2]:F2}, best={mmMfePointsSorted[^1]:F2}");
        Console.WriteLine($"MAE (%): avg={mmMaePercents.Average():F2}%, median={mmMaePercentsSorted[mmMaePercentsSorted.Count / 2]:F2}%, worst={mmMaePercentsSorted[^1]:F2}%");
        Console.WriteLine($"MFE (%): avg={mmMfePercents.Average():F2}%, median={mmMfePercentsSorted[mmMfePercentsSorted.Count / 2]:F2}%, best={mmMfePercentsSorted[^1]:F2}%");
        Console.WriteLine($"Trades where MAE exceeded the final loss magnitude (recovered from a worse drawdown): {mmRecoveredCount}/{mmMaePoints.Count}");
        Console.WriteLine($"Trades where MFE exceeded the final gain (gave back profit): {mmGaveBackCount}/{mmMaePoints.Count}");
    }

    return 0;
}

if (args.Length > 0 && string.Equals(args[0], "calibrate", StringComparison.OrdinalIgnoreCase))
{
    var (calPositional, calNamed) = SplitNamedArgs(args);
    if (calPositional.Length < 4
        || !DateOnly.TryParseExact(calPositional[1], "yyyy-MM-dd", out var calFromDate)
        || !DateOnly.TryParseExact(calPositional[2], "yyyy-MM-dd", out var calToDate)
        || !Enum.TryParse<VolumeBarMetric>(calPositional[3], ignoreCase: true, out var calMetric))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- calibrate <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> <metric> [barVolumeThresholds, comma-separated] [--rolling=N] [--exclude=date,date] [--band=N]");
        Console.Error.WriteLine($"  <metric> is one of: {string.Join(", ", Enum.GetNames<VolumeBarMetric>())}");
        Console.Error.WriteLine("  --rolling=650 -- when set, each barVolumeThreshold is built as a ROLLING window of this many-sized sub-bars (each must divide evenly) instead of a pre-populated fixed bar.");
        Console.Error.WriteLine("  --exclude=2026-09-08,2026-09-15 -- skip these specific days entirely (e.g. to isolate non-0-DTE days that aren't at the edges of the date range).");
        Console.Error.WriteLine("  --band=5 only used by the 3 Phase-2 depth metrics -- strikes in the band (3 = ATM+/-1, the default; 5 = ATM+/-2). Must match a value already populated via populate-options-depth.");
        Console.Error.WriteLine("  These 3 are named flags (can appear anywhere, in any order) rather than positional -- PowerShell silently drops empty-string \"\" placeholder args, which used to corrupt runs that skipped one of these. Named flags avoid that entirely.");
        return 1;
    }

    var calThresholds = calPositional.Length > 4
        ? calPositional[4].Split(',').Select(long.Parse).ToArray()
        : [650L, 1300L, 2600L];
    var calRollingSubBarThreshold = calNamed.TryGetValue("rolling", out var calRollingStr)
        ? (long?)long.Parse(calRollingStr)
        : null;
    var calExcludeDates = calNamed.TryGetValue("exclude", out var calExcludeStr)
        ? calExcludeStr.Split(',').Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd")).ToHashSet()
        : [];
    var calDepthBandWidth = calNamed.TryGetValue("band", out var calBandStr) ? int.Parse(calBandStr) : OptionDepthPopulator.DefaultBandWidth;
    var calSwitchTime = calNamed.TryGetValue("switchtime", out var calSwitchStr) ? (TimeSpan?)TimeSpan.Parse(calSwitchStr) : null;
    var calEntryStart = calNamed.TryGetValue("entrystart", out var calEntryStartStr) ? (TimeSpan?)TimeSpan.Parse(calEntryStartStr) : null;
    var calEntryEnd = calNamed.TryGetValue("entryend", out var calEntryEndStr) ? (TimeSpan?)TimeSpan.Parse(calEntryEndStr) : null;
    // 2026-09-20, item 13 follow-up: only meaningful for FinalScoreSessionWeighted -- weight-on-FuturesScore
    // per session phase (OptionsScore always gets 1 minus it), overriding the 0.7/0.5/0.3 default so the
    // 3 phase weights can be swept via calibrate without recompiling.
    var calWOpen = calNamed.TryGetValue("wopen", out var calWOpenStr) ? double.Parse(calWOpenStr) : (double?)null;
    var calWMid = calNamed.TryGetValue("wmid", out var calWMidStr) ? double.Parse(calWMidStr) : (double?)null;
    var calWClose = calNamed.TryGetValue("wclose", out var calWCloseStr) ? double.Parse(calWCloseStr) : (double?)null;
    (double Open, double Mid, double Close)? calSessionWeights = calWOpen is not null || calWMid is not null || calWClose is not null
        ? (calWOpen ?? 0.7, calWMid ?? 0.5, calWClose ?? 0.3)
        : null;
    // 2026-09-21, whipsaw-reduction experiment -- only meaningful for
    // OptionsScoreThreeWaySwitchMaxPainConfirmedSmoothed/...MinHold respectively; no-op for every
    // other metric.
    var calSmoothBars = calNamed.TryGetValue("smoothbars", out var calSmoothBarsStr) ? (int?)int.Parse(calSmoothBarsStr) : null;
    var calMinHoldMinutes = calNamed.TryGetValue("minhold", out var calMinHoldStr) ? (double?)double.Parse(calMinHoldStr) : null;
    double[] percentiles = [75, 80, 85, 90, 93, 95, 97, 99];

    Console.WriteLine($"=== Calibration sweep: metric={calMetric}, {calFromDate:yyyy-MM-dd}..{calToDate:yyyy-MM-dd}, target 7-20 trades/day{(calRollingSubBarThreshold is { } crsbt ? $", rolling {crsbt}-wide sub-bars" : "")}{(calExcludeDates.Count > 0 ? $", excluding {string.Join(",", calExcludeDates)}" : "")} ===");
    Console.WriteLine($"{"BarThreshold",13} | {"Percentile",10} | {"Trades",7} | {"TradingDays",11} | {"Trades/Day",10} | {"WinRate",8} | {"NetPts",9} | In target?");
    foreach (var threshold in calThresholds)
    {
        // Real TRADING days for THIS threshold, not the calendar span -- 04-16 Sep spans 13
        // calendar days but only 7 have any future tick data at all (weekends/gaps silently
        // produce zero bars, not zero trades, and dividing by a padded denominator would
        // understate every trades/day figure below by roughly 13/7 =~ 1.86x). In rolling mode
        // there are no pre-populated rows at the logical threshold -- count against the
        // underlying sub-bar threshold instead, which is what's actually populated. Excluded
        // dates are dropped from both the day count and the trades below, same list either way.
        var tradingDaysThreshold = calRollingSubBarThreshold ?? threshold;
        await using var tradingDaysDb = new VolumeBarDbContext(volumeBarOptions);
        var tradingDayCount = await tradingDaysDb.VolumeBars
            .Where(b => b.AsOfDate >= calFromDate && b.AsOfDate <= calToDate && b.BarVolumeThreshold == tradingDaysThreshold)
            .Select(b => b.AsOfDate)
            .Distinct()
            .ToListAsync();
        var tradingDayCountFiltered = tradingDayCount.Count(d => !calExcludeDates.Contains(d));

        if (tradingDayCountFiltered == 0)
        {
            Console.WriteLine($"{threshold,13} | (no populated trading days in this range at this threshold -- populate it first)");
            continue;
        }

        foreach (var percentile in percentiles)
        {
            var trades = new List<VolumeBarTrade>();
            for (var date = calFromDate; date <= calToDate; date = date.AddDays(1))
            {
                if (calExcludeDates.Contains(date))
                {
                    continue;
                }

                trades.AddRange(await RunRangeAsync(date, date, calMetric, percentile, 15, threshold, stopLossPercent: null, rollingSubBarThreshold: calRollingSubBarThreshold, depthBandWidth: calDepthBandWidth, optionsSwitchTime: calSwitchTime, entryWindowStartOverride: calEntryStart, sessionWeightsOnFutures: calSessionWeights, smoothingWindowBars: calSmoothBars, minHoldMinutes: calMinHoldMinutes, entryWindowEndOverride: calEntryEnd));
            }

            var tradesPerDay = trades.Count / (double)tradingDayCountFiltered;
            var winRate = trades.Count > 0 ? 100.0 * trades.Count(t => t.NetPnlPoints > 0) / trades.Count : 0;
            var net = trades.Sum(t => t.NetPnlPoints);
            var inTarget = tradesPerDay is >= 7 and <= 20;
            Console.WriteLine($"{threshold,13} | {percentile,10:F0} | {trades.Count,7} | {tradingDayCountFiltered,11} | {tradesPerDay,10:F1} | {winRate,7:F1}% | {net,9:F2} |{(inTarget ? "  <-- TARGET" : "")}");
        }
    }

    return 0;
}

// 2026-09-23, 0-DTE volume-candle research spec (see the approved plan, "0-DTE Volume-Candle
// Research Framework -- Phase 1 / Experiment 1"). Futures-volume-event-bar clock (excess-volume
// carry, spec 4.1), synchronized 0-DTE CE+PE bars for every available strike (spec 6-8), dynamic
// ATM signal (spec 14), fast=3/slow=10 crossover baseline (spec 17/52). In-memory only, no new
// database table -- same convention TimeBasedBarBuilder/PerStrikeCadenceSimulator already use.
async Task<(List<Instrument> Chain, List<FutureEventBar> FutureBars, List<SynchronizedOptionEventBar> OptionBars)> LoadVc0DteDayAsync(
    DateOnly date, long threshold)
{
    await using var vcSource = new NiftySignalDbContext(tradeSourceOptions);
    var futureBars = await FutureEventBarBuilder.BuildDayAsync(vcSource, date, threshold, CancellationToken.None);
    var optionBars = await SynchronizedOptionBarBuilder.BuildDayAsync(vcSource, date, futureBars, CancellationToken.None);
    var chain = await vcSource.Instruments
        .Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate == date && i.Underlying == "NIFTY")
        .ToListAsync();
    return (chain, futureBars, optionBars);
}

// "vc0dte-populate" -- data-quality/validation pass (spec 41-42): builds both bar layers for a
// date range and reports per-day counts, never persisting anything.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-populate <fromDate> <toDate> [--threshold=1300]
if (args.Length > 0 && string.Equals(args[0], "vc0dte-populate", StringComparison.OrdinalIgnoreCase))
{
    var (vpPositional, vpNamed) = SplitNamedArgs(args);
    if (vpPositional.Length < 3
        || !DateOnly.TryParseExact(vpPositional[1], "yyyy-MM-dd", out var vpFromDate)
        || !DateOnly.TryParseExact(vpPositional[2], "yyyy-MM-dd", out var vpToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-populate <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--threshold=1300]");
        return 1;
    }

    var vpThreshold = vpNamed.TryGetValue("threshold", out var vpThresholdStr) ? long.Parse(vpThresholdStr) : 1300L;

    for (var date = vpFromDate; date <= vpToDate; date = date.AddDays(1))
    {
        var (vpChain, vpFutureBars, vpOptionBars) = await LoadVc0DteDayAsync(date, vpThreshold);
        if (vpFutureBars.Count == 0)
        {
            Console.WriteLine($"{date:yyyy-MM-dd}: no future tick data -- skipped.");
            continue;
        }

        var vpDistinctStrikes = vpChain.Select(i => i.StrikePrice).Distinct().Count();
        var vpMissing = vpOptionBars.Count(b => b.MissingData);
        var vpFinalPartial = vpFutureBars.Count(b => b.IsFinalPartialBar);
        Console.WriteLine($"{date:yyyy-MM-dd}: {vpFutureBars.Count} future event bars ({vpFinalPartial} final-partial), {vpDistinctStrikes} distinct 0-DTE strikes, {vpOptionBars.Count} option bars, {vpMissing} missing ({100.0 * vpMissing / Math.Max(1, vpOptionBars.Count):F1}%).");
    }

    return 0;
}

// "vc0dte-trace" -- raw-tick trace mode (spec 43): dumps every future event bar up to and
// including the requested one, plus the dynamic-ATM Call/Put fast/slow MA state and any
// crossing, so a result can be hand-verified against raw numbers before trusting anything built
// on top of this layer.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-trace <date> <eventIndex> [--threshold=1300] [--fast=3] [--slow=10]
if (args.Length > 0 && string.Equals(args[0], "vc0dte-trace", StringComparison.OrdinalIgnoreCase))
{
    var (vtPositional, vtNamed) = SplitNamedArgs(args);
    if (vtPositional.Length < 3
        || !DateOnly.TryParseExact(vtPositional[1], "yyyy-MM-dd", out var vtDate)
        || !int.TryParse(vtPositional[2], out var vtEventIndex))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-trace <date:yyyy-MM-dd> <eventIndex> [--threshold=1300] [--fast=3] [--slow=10]");
        return 1;
    }

    var vtThreshold = vtNamed.TryGetValue("threshold", out var vtThresholdStr) ? long.Parse(vtThresholdStr) : 1300L;
    var vtFastBars = vtNamed.TryGetValue("fast", out var vtFastStr) ? int.Parse(vtFastStr) : 3;
    var vtSlowBars = vtNamed.TryGetValue("slow", out var vtSlowStr) ? int.Parse(vtSlowStr) : 10;

    var (vtChain, vtFutureBars, vtOptionBars) = await LoadVc0DteDayAsync(vtDate, vtThreshold);
    if (vtFutureBars.Count == 0)
    {
        Console.Error.WriteLine($"No future tick data for {vtDate:yyyy-MM-dd}.");
        return 1;
    }
    if (vtEventIndex < 0 || vtEventIndex >= vtFutureBars.Count)
    {
        Console.Error.WriteLine($"eventIndex must be in [0,{vtFutureBars.Count - 1}] for this date.");
        return 1;
    }

    var vtBarsByToken = vtOptionBars.GroupBy(b => b.Token).ToDictionary(g => g.Key, g => g.OrderBy(b => b.EventId).ToList());
    var vtCallChain = vtChain.Where(c => c.OptionType == OptionType.Call).ToList();
    var vtPutChain = vtChain.Where(c => c.OptionType == OptionType.Put).ToList();
    var vtCallEngine = new PriceCrossoverEngine(vtFastBars, vtSlowBars);
    var vtPutEngine = new PriceCrossoverEngine(vtFastBars, vtSlowBars);
    string? vtLastCallAtm = null, vtLastPutAtm = null;

    Console.WriteLine($"=== vc0dte-trace: {vtDate:yyyy-MM-dd}, threshold={vtThreshold}, fast={vtFastBars}, slow={vtSlowBars} ===");
    Console.WriteLine($"{"Ev",3} {"TimeIST",12} {"FutClose",9} {"FutVol",7} {"CallAtm",7} {"CallAvg",8} {"CallFast",8} {"CallSlow",8} {"C",1} {"PutAtm",7} {"PutAvg",8} {"PutFast",8} {"PutSlow",8} {"P",1}");

    for (var e = 0; e <= vtEventIndex; e++)
    {
        var bar = vtFutureBars[e];
        var vtCallAtm = EventBarStrikeSelector.PickDynamicAtm(vtCallChain, OptionType.Call, bar.Close);
        var vtPutAtm = EventBarStrikeSelector.PickDynamicAtm(vtPutChain, OptionType.Put, bar.Close);

        if (vtCallAtm is not null && vtLastCallAtm is not null && vtLastCallAtm != vtCallAtm.Token)
        {
            vtCallEngine.Reset();
        }
        if (vtCallAtm is not null)
        {
            vtLastCallAtm = vtCallAtm.Token;
        }
        if (vtPutAtm is not null && vtLastPutAtm is not null && vtLastPutAtm != vtPutAtm.Token)
        {
            vtPutEngine.Reset();
        }
        if (vtPutAtm is not null)
        {
            vtLastPutAtm = vtPutAtm.Token;
        }

        var vtCallBar = vtCallAtm is not null && vtBarsByToken.TryGetValue(vtCallAtm.Token, out var vtCallBars) ? vtCallBars.FirstOrDefault(b => b.EventId == e) : null;
        var vtPutBar = vtPutAtm is not null && vtBarsByToken.TryGetValue(vtPutAtm.Token, out var vtPutBars) ? vtPutBars.FirstOrDefault(b => b.EventId == e) : null;
        var vtCallStep = vtCallEngine.Observe(vtCallBar?.AverageLtp is { } cavg ? (double)cavg : null, 0.0);
        var vtPutStep = vtPutEngine.Observe(vtPutBar?.AverageLtp is { } pavg ? (double)pavg : null, 0.0);

        Console.WriteLine($"{e,3} {FormatIst(bar.EndTimestamp),12} {bar.Close,9:F2} {bar.Volume,7} {(vtCallAtm?.StrikePrice?.ToString("F0") ?? "--"),7} {(vtCallBar?.AverageLtp?.ToString("F2") ?? "--"),8} {(vtCallStep.FastMa?.ToString("F2") ?? "--"),8} {(vtCallStep.SlowMa?.ToString("F2") ?? "--"),8} {(vtCallStep.CrossedUp ? "U" : vtCallStep.CrossedDown ? "D" : ""),1} {(vtPutAtm?.StrikePrice?.ToString("F0") ?? "--"),7} {(vtPutBar?.AverageLtp?.ToString("F2") ?? "--"),8} {(vtPutStep.FastMa?.ToString("F2") ?? "--"),8} {(vtPutStep.SlowMa?.ToString("F2") ?? "--"),8} {(vtPutStep.CrossedUp ? "U" : vtPutStep.CrossedDown ? "D" : ""),1}");
    }

    Console.WriteLine();
    Console.WriteLine($"Futures event #{vtEventIndex}: Start={FormatIst(vtFutureBars[vtEventIndex].StartTimestamp)} End={FormatIst(vtFutureBars[vtEventIndex].EndTimestamp)} Volume={vtFutureBars[vtEventIndex].Volume} (threshold={vtThreshold}, excess carried to next bar={vtFutureBars[vtEventIndex].Volume - vtThreshold}) TickCount={vtFutureBars[vtEventIndex].TickCount} IsFinalPartialBar={vtFutureBars[vtEventIndex].IsFinalPartialBar}");

    return 0;
}

// "vc0dte-behavior" -- Experiment 1's PRIMARY research output (spec 21-23, 40, 45): forward
// returns/MFE-MAE for every dynamic-ATM Call/Put crossover, pinned to the signalling contract.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-behavior <fromDate> <toDate> [--threshold=1300] [--fast=3] [--slow=10] [--out=path.csv]
if (args.Length > 0 && string.Equals(args[0], "vc0dte-behavior", StringComparison.OrdinalIgnoreCase))
{
    var (vbPositional, vbNamed) = SplitNamedArgs(args);
    if (vbPositional.Length < 3
        || !DateOnly.TryParseExact(vbPositional[1], "yyyy-MM-dd", out var vbFromDate)
        || !DateOnly.TryParseExact(vbPositional[2], "yyyy-MM-dd", out var vbToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-behavior <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--threshold=1300] [--fast=3] [--slow=10] [--out=path.csv]");
        return 1;
    }

    var vbThreshold = vbNamed.TryGetValue("threshold", out var vbThresholdStr) ? long.Parse(vbThresholdStr) : 1300L;
    var vbFastBars = vbNamed.TryGetValue("fast", out var vbFastStr) ? int.Parse(vbFastStr) : 3;
    var vbSlowBars = vbNamed.TryGetValue("slow", out var vbSlowStr) ? int.Parse(vbSlowStr) : 10;
    var vbOutPath = vbNamed.TryGetValue("out", out var vbOutStr) ? vbOutStr : $"vc0dte-behavior-{vbFromDate:yyyy-MM-dd}-{vbToDate:yyyy-MM-dd}.csv";

    var vbAllRows = new List<Vc0DteBehaviorRecorder.ObservationRow>();
    for (var date = vbFromDate; date <= vbToDate; date = date.AddDays(1))
    {
        var (vbChain, vbFutureBars, vbOptionBars) = await LoadVc0DteDayAsync(date, vbThreshold);
        if (vbFutureBars.Count == 0)
        {
            continue;
        }

        await using var vbSource = new NiftySignalDbContext(tradeSourceOptions);
        var vbDayRows = await Vc0DteBehaviorRecorder.RecordAsync(vbSource, date, vbChain, vbFutureBars, vbOptionBars, CancellationToken.None, vbFastBars, vbSlowBars);
        Console.WriteLine($"{date:yyyy-MM-dd}: {vbDayRows.Count} crossover observations.");
        vbAllRows.AddRange(vbDayRows);
    }

    Console.WriteLine($"--- TOTAL: {vbAllRows.Count} observations across {(vbToDate.DayNumber - vbFromDate.DayNumber) + 1} calendar day(s) ---");
    Console.WriteLine();
    Console.WriteLine("Behaviour by signal type (BEFORE any trading result -- spec section 45, do not read the trading result back into this table):");
    Console.WriteLine($"{"Signal",18} {"Count",6} {"AvgFwd1",8} {"AvgFwd2",8} {"AvgFwd3",8} {"AvgFwd5",8} {"AvgFwd10",9} {"AvgMFE%",8} {"AvgMAE%",8}");
    foreach (var vbSummary in Vc0DteBehaviorSummary.Summarize(vbAllRows))
    {
        Console.WriteLine($"{vbSummary.OptionType + " " + vbSummary.SignalDirection,18} {vbSummary.Count,6} {vbSummary.AvgForwardReturn1?.ToString("F3") ?? "--",8} {vbSummary.AvgForwardReturn2?.ToString("F3") ?? "--",8} {vbSummary.AvgForwardReturn3?.ToString("F3") ?? "--",8} {vbSummary.AvgForwardReturn5?.ToString("F3") ?? "--",8} {vbSummary.AvgForwardReturn10?.ToString("F3") ?? "--",9} {vbSummary.AvgMaximumFavorableMovePercent,8:F3} {vbSummary.AvgMaximumAdverseMovePercent,8:F3}");
    }
    Console.WriteLine("(CE CrossDown is NOT a PE buy signal -- these four rows are always kept separate, never merged.)");
    Console.WriteLine();

    Vc0DteBehaviorRecorder.WriteCsv(vbAllRows, vbOutPath, TimeSpan.FromHours(5.5));
    Console.WriteLine($"CSV written to: {Path.GetFullPath(vbOutPath)}");

    return 0;
}

// "vc0dte-simulate" -- first trading-simulation pass (spec 24-39 + user's explicit rules: warm-up
// gating, [100,150] entry band, no new entries after 15:00 IST, forced close at 15:15 IST).
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-simulate <fromDate> <toDate> [--threshold=1300] [--fast=3] [--slow=10] [--minprice=100] [--maxprice=150] [--out=trades.csv] [--rejected=rejected.csv]
if (args.Length > 0 && string.Equals(args[0], "vc0dte-simulate", StringComparison.OrdinalIgnoreCase))
{
    var (vsPositional, vsNamed) = SplitNamedArgs(args);
    if (vsPositional.Length < 3
        || !DateOnly.TryParseExact(vsPositional[1], "yyyy-MM-dd", out var vsFromDate)
        || !DateOnly.TryParseExact(vsPositional[2], "yyyy-MM-dd", out var vsToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-simulate <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--threshold=1300] [--fast=3] [--slow=10] [--minprice=100] [--maxprice=150] [--out=trades.csv] [--rejected=rejected.csv]");
        return 1;
    }

    var vsThreshold = vsNamed.TryGetValue("threshold", out var vsThresholdStr) ? long.Parse(vsThresholdStr) : 1300L;
    var vsFastBars = vsNamed.TryGetValue("fast", out var vsFastStr) ? int.Parse(vsFastStr) : 3;
    var vsSlowBars = vsNamed.TryGetValue("slow", out var vsSlowStr) ? int.Parse(vsSlowStr) : 10;
    var vsMinPrice = vsNamed.TryGetValue("minprice", out var vsMinPriceStr) ? decimal.Parse(vsMinPriceStr) : 100m;
    var vsMaxPrice = vsNamed.TryGetValue("maxprice", out var vsMaxPriceStr) ? decimal.Parse(vsMaxPriceStr) : 150m;
    var vsOutPath = vsNamed.TryGetValue("out", out var vsOutStr) ? vsOutStr : $"vc0dte-trades-{vsFromDate:yyyy-MM-dd}-{vsToDate:yyyy-MM-dd}.csv";
    var vsRejectedPath = vsNamed.TryGetValue("rejected", out var vsRejectedStr) ? vsRejectedStr : $"vc0dte-rejected-{vsFromDate:yyyy-MM-dd}-{vsToDate:yyyy-MM-dd}.csv";

    var vsAllTrades = new List<Vc0DteTradeSimulator.TradeRow>();
    var vsAllRejected = new List<Vc0DteTradeSimulator.RejectedSignalRow>();
    var vsFunnels = new List<(DateOnly Date, Vc0DteTradeSimulator.SignalFunnel Funnel)>();
    for (var date = vsFromDate; date <= vsToDate; date = date.AddDays(1))
    {
        var (vsChain, vsFutureBars, vsOptionBars) = await LoadVc0DteDayAsync(date, vsThreshold);
        if (vsFutureBars.Count == 0)
        {
            continue;
        }

        await using var vsSource = new NiftySignalDbContext(tradeSourceOptions);
        var vsResult = await Vc0DteTradeSimulator.SimulateDayAsync(vsSource, date, vsChain, vsFutureBars, vsOptionBars, CancellationToken.None, vsFastBars, vsSlowBars, minEntryPrice: vsMinPrice, maxEntryPrice: vsMaxPrice);
        var vsDayNet = vsResult.Trades.Sum(t => t.NetPnl);
        Console.WriteLine($"{date:yyyy-MM-dd}: {vsResult.Trades.Count} trades, net {vsDayNet:F2}, {vsResult.RejectedSignals.Count} rejected signals.");
        vsAllTrades.AddRange(vsResult.Trades);
        vsAllRejected.AddRange(vsResult.RejectedSignals);
        vsFunnels.Add((date, vsResult.Funnel));
    }

    var vsTotalNet = vsAllTrades.Sum(t => t.NetPnl);
    var vsWinRate = vsAllTrades.Count > 0 ? 100.0 * vsAllTrades.Count(t => t.NetPnl > 0) / vsAllTrades.Count : 0;
    Console.WriteLine($"--- TOTAL: {vsAllTrades.Count} trades, {vsWinRate:F1}% win rate, net {vsTotalNet:F2} ---");
    Console.WriteLine();
    Console.WriteLine("Signal funnel -- \"explain the trade count\" (user's own explicit diagnostic request), per day:");
    foreach (var (vsFunnelDate, vsFunnel) in vsFunnels)
    {
        Console.WriteLine($"{vsFunnelDate:yyyy-MM-dd}: TotalCrossoverSignals={vsFunnel.TotalCrossoverSignals} (CE up={vsFunnel.CeCrossUp} down={vsFunnel.CeCrossDown}, PE up={vsFunnel.PeCrossUp} down={vsFunnel.PeCrossDown}), WhileWarmingUp={vsFunnel.SignalsWhileWarmingUp} (structurally always 0), ConsideredWhileFlat={vsFunnel.EntrySignalsConsideredWhileFlat}, IgnoredPositionOpen={vsFunnel.EntrySignalsIgnoredPositionAlreadyOpen}, Rejected[NoStrikeInBand={vsFunnel.RejectedNoStrikeInBand} NoExecutableTick={vsFunnel.RejectedNoExecutableTick} OutsideSession={vsFunnel.RejectedOutsideSession}], TradesOpened={vsFunnel.TradesOpened} (CE={vsFunnel.CeTrades} PE={vsFunnel.PeTrades}), ReversalExits={vsFunnel.ReversalExits}, ForcedExits={vsFunnel.ForcedExits}, TradesPerHour={vsFunnel.TradesPerHour:F2}");
    }
    Console.WriteLine();

    Vc0DteTradeSimulator.WriteTradesCsv(vsAllTrades, vsOutPath, TimeSpan.FromHours(5.5));
    Vc0DteTradeSimulator.WriteRejectedCsv(vsAllRejected, vsRejectedPath, TimeSpan.FromHours(5.5));
    Console.WriteLine($"Trades CSV written to: {Path.GetFullPath(vsOutPath)}");
    Console.WriteLine($"Rejected-signals CSV written to: {Path.GetFullPath(vsRejectedPath)}");

    return 0;
}

// "vc0dte-tick-trace" -- forensic verification (user's own explicit request, after the first
// 87-trade/1591-bar result): raw per-tick accounting around a specific futures event, so the
// cumulative-volume delta and excess-carry arithmetic can be read off directly and hand-verified,
// never trusted from the aggregate FutureEventBar alone.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-tick-trace <date> <eventIndex> [--threshold=1300] [--context=5]
if (args.Length > 0 && string.Equals(args[0], "vc0dte-tick-trace", StringComparison.OrdinalIgnoreCase))
{
    var (ttPositional, ttNamed) = SplitNamedArgs(args);
    if (ttPositional.Length < 3
        || !DateOnly.TryParseExact(ttPositional[1], "yyyy-MM-dd", out var ttDate)
        || !int.TryParse(ttPositional[2], out var ttEventIndex))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-tick-trace <date:yyyy-MM-dd> <eventIndex> [--threshold=1300] [--context=5]");
        return 1;
    }

    var ttThreshold = ttNamed.TryGetValue("threshold", out var ttThresholdStr) ? long.Parse(ttThresholdStr) : 1300L;
    var ttContext = ttNamed.TryGetValue("context", out var ttContextStr) ? int.Parse(ttContextStr) : 5;

    await using var ttSource = new NiftySignalDbContext(tradeSourceOptions);
    var (ttBars, ttTrace) = await FutureEventBarBuilder.BuildDayWithTraceAsync(ttSource, ttDate, ttThreshold, CancellationToken.None);
    if (ttBars.Count == 0)
    {
        Console.Error.WriteLine($"No future tick data for {ttDate:yyyy-MM-dd}.");
        return 1;
    }
    if (ttEventIndex < 0 || ttEventIndex >= ttBars.Count)
    {
        Console.Error.WriteLine($"eventIndex must be in [0,{ttBars.Count - 1}] for this date.");
        return 1;
    }

    // Every tick belonging to this event, plus a few ticks of context from the PRECEDING event
    // (so a threshold crossing right at the boundary, and the excess it carries forward, is
    // visible in one table).
    var ttRelevant = ttTrace.Where(r => r.EventId == ttEventIndex).ToList();
    var ttPrecedingContext = ttTrace.Where(r => r.EventId == ttEventIndex - 1).TakeLast(ttContext).ToList();
    var ttRows = ttPrecedingContext.Concat(ttRelevant).ToList();

    Console.WriteLine($"=== vc0dte-tick-trace: {ttDate:yyyy-MM-dd}, event #{ttEventIndex}, threshold={ttThreshold} ===");
    Console.WriteLine($"{"TimeIST",13} {"PrevCumVol",10} {"CurCumVol",10} {"Delta",7} {"AccumBefore",11} {"Threshold",9} {"AccumAfter",10} {"EventId",7} {"ClosesBar",9} {"Excess",7}");
    foreach (var row in ttRows)
    {
        Console.WriteLine($"{FormatIst(row.Timestamp),13} {(row.PreviousCumulativeVolume?.ToString() ?? "--"),10} {row.CurrentCumulativeVolume,10} {row.Delta,7} {row.AccumulatorBefore,11} {row.Threshold,9} {row.AccumulatorAfter,10} {row.EventId,7} {(row.ClosesBar ? "Y" : ""),9} {(row.Excess?.ToString() ?? ""),7}");
    }

    Console.WriteLine();
    var ttBar = ttBars[ttEventIndex];
    Console.WriteLine($"Event #{ttEventIndex} bar: Start={FormatIst(ttBar.StartTimestamp)} End={FormatIst(ttBar.EndTimestamp)} Volume={ttBar.Volume} TickCount={ttBar.TickCount} IsFinalPartialBar={ttBar.IsFinalPartialBar}");
    Console.WriteLine("Read this table's own AccumBefore/Delta/Threshold/AccumAfter/Excess columns to hand-verify: a bar never gets a split tick (ClosesBar=Y always uses the WHOLE tick's own delta), but the bar's own Volume can exceed the threshold, and the amount past it (Excess) becomes the NEXT event's own AccumBefore on its first tick -- confirm that directly by comparing this event's own final AccumAfter/Excess to the first row of the next event's own AccumBefore (run this command again with eventIndex+1).");

    return 0;
}

// "vc0dte-duration-stats" -- forensic verification (user's own explicit request): what temporal
// scale does a 1300-contract event bar actually span? A 3-bar fast MA over sub-second bars is not
// the same signal as a 3-bar fast MA over 5-minute bars -- measured directly here, not assumed.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-duration-stats <fromDate> <toDate> [--threshold=1300]
if (args.Length > 0 && string.Equals(args[0], "vc0dte-duration-stats", StringComparison.OrdinalIgnoreCase))
{
    var (dsPositional2, dsNamed2) = SplitNamedArgs(args);
    if (dsPositional2.Length < 3
        || !DateOnly.TryParseExact(dsPositional2[1], "yyyy-MM-dd", out var dsFromDate2)
        || !DateOnly.TryParseExact(dsPositional2[2], "yyyy-MM-dd", out var dsToDate2))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-duration-stats <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--threshold=1300]");
        return 1;
    }

    var dsThreshold2 = dsNamed2.TryGetValue("threshold", out var dsThresholdStr2) ? long.Parse(dsThresholdStr2) : 1300L;
    var dsAllBars = new List<FutureEventBar>();
    for (var date = dsFromDate2; date <= dsToDate2; date = date.AddDays(1))
    {
        await using var dsSource2 = new NiftySignalDbContext(tradeSourceOptions);
        var dsDayBars = await FutureEventBarBuilder.BuildDayAsync(dsSource2, date, dsThreshold2, CancellationToken.None);
        if (dsDayBars.Count == 0)
        {
            continue;
        }
        Console.WriteLine($"{date:yyyy-MM-dd}: {dsDayBars.Count} event bars.");
        dsAllBars.AddRange(dsDayBars);
    }

    var dsStats = EventBarDurationStats.Compute(dsAllBars);
    Console.WriteLine();
    Console.WriteLine($"=== Event bar duration distribution, threshold={dsThreshold2}, {dsStats.BarCount} bars (final-partial bars excluded) ===");
    Console.WriteLine($"Min={dsStats.MinMs:F0}ms P1={dsStats.P1Ms:F0}ms P5={dsStats.P5Ms:F0}ms P10={dsStats.P10Ms:F0}ms P25={dsStats.P25Ms:F0}ms Median={dsStats.MedianMs:F0}ms P75={dsStats.P75Ms:F0}ms P90={dsStats.P90Ms:F0}ms P95={dsStats.P95Ms:F0}ms P99={dsStats.P99Ms:F0}ms Max={dsStats.MaxMs:F0}ms");
    Console.WriteLine();
    Console.WriteLine("Histogram:");
    foreach (var bucket in dsStats.Histogram)
    {
        Console.WriteLine($"  {bucket.Label,-10} {bucket.Count,6} ({(dsStats.BarCount > 0 ? 100.0 * bucket.Count / dsStats.BarCount : 0),5:F1}%)");
    }

    return 0;
}

// "vc0dte-multiday-behavior" -- 2026-09-23 multi-day behaviour validation (user's own explicit
// instruction: FROZEN Experiment 1 configuration, no parameter flags on this command at all --
// 1300/dynamic-ATM/average-LTP/3-10 SMA/0-DTE are hardcoded, not overridable, so this command
// cannot be used to accidentally sweep them). Auto-detects every complete 0-DTE day in the given
// range (future data present AND at least one option instrument whose ExpiryDate == that date),
// explicitly listing every excluded date with its concrete reason (spec point 2 -- never silently
// skip). Produces the pooled, day-by-day, session-bucket, and event-speed-bucket behavioural
// tables (points 3, 7, 8, 9), the underlying-vs-option/lead-lag columns (points 5-6, already on
// every row via the extended ObservationRow), the missing/stale audit (point 12), and (secondary
// only, point 10) a continuity run of the existing trade simulator.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-multiday-behavior <fromDate> <toDate> [--out=path.csv]
if (args.Length > 0 && string.Equals(args[0], "vc0dte-multiday-behavior", StringComparison.OrdinalIgnoreCase))
{
    const long mdThreshold = 1300L;
    const int mdFastBars = 3;
    const int mdSlowBars = 10;
    var istOffset = TimeSpan.FromHours(5.5);

    var (mdPositional, mdNamed) = SplitNamedArgs(args);
    if (mdPositional.Length < 3
        || !DateOnly.TryParseExact(mdPositional[1], "yyyy-MM-dd", out var mdFromDate)
        || !DateOnly.TryParseExact(mdPositional[2], "yyyy-MM-dd", out var mdToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-multiday-behavior <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--out=path.csv]");
        return 1;
    }
    var mdOutPath = mdNamed.TryGetValue("out", out var mdOutStr) ? mdOutStr : $"vc0dte-multiday-behavior-{mdFromDate:yyyy-MM-dd}-{mdToDate:yyyy-MM-dd}.csv";

    Console.WriteLine($"=== vc0dte-multiday-behavior: FROZEN Experiment 1 config -- threshold={mdThreshold}, dynamic ATM, average LTP, fast={mdFastBars}/slow={mdSlowBars} SMA, 0-DTE only ===");
    Console.WriteLine();
    Console.WriteLine("Date-by-date inclusion (spec point 2 -- every exclusion stated, none silent):");

    var mdIncludedDates = new List<DateOnly>();
    var mdAllRows = new List<Vc0DteBehaviorRecorder.ObservationRow>();
    var mdAllOptionBars = new List<SynchronizedOptionEventBar>();
    var mdSimTrades = new List<Vc0DteTradeSimulator.TradeRow>();
    var mdSimFunnels = new List<(DateOnly Date, Vc0DteTradeSimulator.SignalFunnel Funnel)>();

    for (var date = mdFromDate; date <= mdToDate; date = date.AddDays(1))
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            Console.WriteLine($"  {date:yyyy-MM-dd} ({date.DayOfWeek}): EXCLUDED -- weekend, no trading session.");
            continue;
        }

        var (mdChain, mdFutureBars, mdOptionBars) = await LoadVc0DteDayAsync(date, mdThreshold);
        if (mdFutureBars.Count == 0)
        {
            Console.WriteLine($"  {date:yyyy-MM-dd} ({date.DayOfWeek}): EXCLUDED -- no futures tick data available in this dataset for this date.");
            continue;
        }
        if (mdChain.Count == 0)
        {
            Console.WriteLine($"  {date:yyyy-MM-dd} ({date.DayOfWeek}): EXCLUDED -- futures data present, but no option instrument has ExpiryDate == this date (not a 0-DTE expiry day).");
            continue;
        }

        await using var mdSource = new NiftySignalDbContext(tradeSourceOptions);
        var mdDayRows = await Vc0DteBehaviorRecorder.RecordAsync(mdSource, date, mdChain, mdFutureBars, mdOptionBars, CancellationToken.None, mdFastBars, mdSlowBars);
        Console.WriteLine($"  {date:yyyy-MM-dd} ({date.DayOfWeek}): INCLUDED -- {mdFutureBars.Count} future event bars, {mdChain.Select(c => c.StrikePrice).Distinct().Count()} distinct 0-DTE strikes, {mdOptionBars.Count} option bars, {mdDayRows.Count} crossover observations.");
        mdIncludedDates.Add(date);
        mdAllRows.AddRange(mdDayRows);
        mdAllOptionBars.AddRange(mdOptionBars);

        await using var mdSimSource = new NiftySignalDbContext(tradeSourceOptions);
        var mdSimResult = await Vc0DteTradeSimulator.SimulateDayAsync(mdSimSource, date, mdChain, mdFutureBars, mdOptionBars, CancellationToken.None, mdFastBars, mdSlowBars);
        mdSimTrades.AddRange(mdSimResult.Trades);
        mdSimFunnels.Add((date, mdSimResult.Funnel));
    }

    Console.WriteLine();
    Console.WriteLine($"--- {mdIncludedDates.Count} complete 0-DTE day(s) included: {string.Join(", ", mdIncludedDates.Select(d => d.ToString("yyyy-MM-dd")))} ---");
    Console.WriteLine($"--- TOTAL: {mdAllRows.Count} crossover observations pooled across {mdIncludedDates.Count} day(s) ---");
    Console.WriteLine();

    Console.WriteLine("### Point 3 -- core behavioural table (pooled across all included days) ###");
    PrintSummaryTable(Vc0DteBehaviorSummary.Summarize(mdAllRows));
    Console.WriteLine();

    Console.WriteLine("### Point 7 -- day-by-day robustness (per date, per signal type) ###");
    PrintSummaryTable(Vc0DteBehaviorSummary.SummarizeByDay(mdAllRows), includeDate: true);
    Console.WriteLine();
    PrintDayRobustnessVerdict(mdAllRows);
    Console.WriteLine();

    Console.WriteLine("### Point 8 -- session-bucket analysis (descriptive only) ###");
    foreach (var (bucket, stats) in Vc0DteBehaviorSummary.SummarizeBySessionBucket(mdAllRows, istOffset))
    {
        Console.WriteLine($"  [{bucket}] {stats.OptionType} {stats.SignalDirection}: n={stats.Count} AvgFwd1={Fmt(stats.AvgForwardReturn1)} AvgFwd3={Fmt(stats.AvgForwardReturn3)} AvgMFE%={stats.AvgMaximumFavorableMovePercent:F2} AvgMAE%={stats.AvgMaximumAdverseMovePercent:F2}");
    }
    Console.WriteLine();

    Console.WriteLine("### Point 9 -- event-speed dependence (quartiles of THIS dataset's own slow-window duration) ###");
    var (mdQ1, mdQ3) = Vc0DteBehaviorSummary.ComputeSlowWindowQuartiles(mdAllRows);
    Console.WriteLine($"  Q1(slow-window duration)={mdQ1:F0}ms, Q3={mdQ3:F0}ms (descriptive quartiles from the actual data, not invented thresholds)");
    foreach (var (bucket, stats) in Vc0DteBehaviorSummary.SummarizeBySpeedBucket(mdAllRows, mdQ1, mdQ3))
    {
        Console.WriteLine($"  [{bucket}] {stats.OptionType} {stats.SignalDirection}: n={stats.Count} AvgFwd1={Fmt(stats.AvgForwardReturn1)} AvgFwd3={Fmt(stats.AvgForwardReturn3)} AvgMFE%={stats.AvgMaximumFavorableMovePercent:F2} AvgMAE%={stats.AvgMaximumAdverseMovePercent:F2}");
    }
    Console.WriteLine();

    Console.WriteLine("### Point 12 -- missing/stale option-bar audit ###");
    var mdAudit = OptionBarQualityAudit.Compute(mdAllOptionBars);
    Console.WriteLine($"  TOTAL: {mdAudit.TotalBars} bars, {mdAudit.MissingCount} missing ({mdAudit.MissingPercent:F1}%), {mdAudit.StaleCount} stale ({mdAudit.StalePercent:F1}%)");
    foreach (var day in mdAudit.ByDay)
    {
        Console.WriteLine($"    {day.TradingDate:yyyy-MM-dd}: {day.TotalBars} bars, {day.MissingCount} missing ({(day.TotalBars > 0 ? 100.0 * day.MissingCount / day.TotalBars : 0):F1}%), {day.StaleCount} stale");
    }
    Console.WriteLine();

    Console.WriteLine("### Point 10 -- trading simulation summary (SECONDARY ONLY -- not evidence of behavioural edge) ###");
    var mdSimNet = mdSimTrades.Sum(t => t.NetPnl);
    var mdSimWinRate = mdSimTrades.Count > 0 ? 100.0 * mdSimTrades.Count(t => t.NetPnl > 0) / mdSimTrades.Count : 0;
    Console.WriteLine($"  {mdSimTrades.Count} trades pooled, {mdSimWinRate:F1}% win rate, net {mdSimNet:F2}");
    foreach (var (date, funnel) in mdSimFunnels)
    {
        var dayNet = mdSimTrades.Where(t => t.TradingDate == date).Sum(t => t.NetPnl);
        Console.WriteLine($"    {date:yyyy-MM-dd}: {funnel.TradesOpened} trades, net {dayNet:F2}, {funnel.TotalCrossoverSignals} total crossovers, {funnel.TradesPerHour:F2} trades/hour");
    }

    Vc0DteBehaviorRecorder.WriteCsv(mdAllRows, mdOutPath, istOffset);
    Console.WriteLine();
    Console.WriteLine($"Full observation CSV written to: {Path.GetFullPath(mdOutPath)}");

    return 0;

    static string Fmt(decimal? v) => v?.ToString("F3") ?? "--";

    void PrintSummaryTable(List<Vc0DteBehaviorSummary.SummaryRow> summaryRows, bool includeDate = false)
    {
        foreach (var s in summaryRows)
        {
            Console.WriteLine($"  {s.OptionType,-4} {s.SignalDirection,-8} n={s.Count,4} days={s.DayCount,2} | "
                + $"Fwd1(avg/med)={Fmt(s.AvgForwardReturn1)}/{Fmt(s.MedianForwardReturn1)} Fwd2={Fmt(s.AvgForwardReturn2)}/{Fmt(s.MedianForwardReturn2)} Fwd3={Fmt(s.AvgForwardReturn3)}/{Fmt(s.MedianForwardReturn3)} Fwd5={Fmt(s.AvgForwardReturn5)}/{Fmt(s.MedianForwardReturn5)} Fwd10={Fmt(s.AvgForwardReturn10)}/{Fmt(s.MedianForwardReturn10)} | "
                + $"MFE%(avg/med)={s.AvgMaximumFavorableMovePercent:F2}/{s.MedianMaximumFavorableMovePercent:F2} MAE%={s.AvgMaximumAdverseMovePercent:F2}/{s.MedianMaximumAdverseMovePercent:F2} | "
                + $"MFEabs={s.AvgMaximumFavorableMoveAbs:F2} MAEabs={s.AvgMaximumAdverseMoveAbs:F2} | "
                + $"BarsToMFE={s.AvgEventBarsToMaximumFavorable:F1} BarsToMAE={s.AvgEventBarsToMaximumAdverse:F1} | "
                + $"FutFwd1={Fmt(s.AvgFutureForwardReturnPercent1)} FutFwd3={Fmt(s.AvgFutureForwardReturnPercent3)} | "
                + $"PreOpt3={Fmt(s.AvgPreSignalOptionMovePercent3)} PreFut3={Fmt(s.AvgPreSignalFutureMovePercent3)}");
        }
    }

    void PrintDayRobustnessVerdict(List<Vc0DteBehaviorRecorder.ObservationRow> rows)
    {
        Console.WriteLine("  Per-signal-type day count classification (Fwd3, the horizon point 7 names) -- NOT a robustness claim by itself, just the raw day split:");
        foreach (var group in rows.GroupBy(r => (r.OptionType, r.SignalDirection)).OrderBy(g => g.Key.OptionType).ThenBy(g => g.Key.SignalDirection))
        {
            var byDay = group.GroupBy(r => r.TradingDate)
                .Select(g => g.Where(r => r.ForwardReturn3 is not null).Select(r => r.ForwardReturn3!.Value).DefaultIfEmpty().Average())
                .ToList();
            var positive = byDay.Count(d => d > 0.5m);
            var negative = byDay.Count(d => d < -0.5m);
            var flat = byDay.Count - positive - negative;
            var avgOfDays = byDay.Count > 0 ? byDay.Average() : 0m;
            var medianOfDays = byDay.Count > 0 ? byDay.Order().ElementAt(byDay.Count / 2) : 0m;
            Console.WriteLine($"    {group.Key.OptionType,-4} {group.Key.SignalDirection,-8}: {byDay.Count} day(s) -- positive={positive} negative={negative} flat(+/-0.5%)={flat}, avgOfDailyAvgFwd3={avgOfDays:F3}%, medianOfDailyAvgFwd3={medianOfDays:F3}%");
        }
    }
}

// "vc0dte-relationship" -- 2026-09-23 descriptive research layer (SEPARATE from, and does not
// modify, Experiment 1's own control results): futures/spot/CE/PE relationship analysis. FROZEN
// Experiment 1 configuration hardcoded, no override flags -- same discipline
// "vc0dte-multiday-behavior" already established. Reuses LoadVc0DteDayAsync and
// Vc0DteBehaviorRecorder.RecordAsync completely unchanged; only NEW types
// (UnderlyingOptionRelationshipRecorder/Summary) are added.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship <fromDate> <toDate> --out=path.csv
if (args.Length > 0 && string.Equals(args[0], "vc0dte-relationship", StringComparison.OrdinalIgnoreCase))
{
    const long relThreshold = 1300L;
    var relIstOffset = TimeSpan.FromHours(5.5);

    var (relPositional, relNamed) = SplitNamedArgs(args);
    if (relPositional.Length < 3
        || !DateOnly.TryParseExact(relPositional[1], "yyyy-MM-dd", out var relFromDate)
        || !DateOnly.TryParseExact(relPositional[2], "yyyy-MM-dd", out var relToDate)
        || !relNamed.TryGetValue("out", out var relOutPath))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> --out=path.csv");
        return 1;
    }

    Console.WriteLine("=== vc0dte-relationship: descriptive research layer, FROZEN Experiment 1 config (threshold=1300, dynamic ATM, average LTP, fast=3/slow=10 SMA, 0-DTE only) -- DIAGNOSTIC ONLY, no trading logic ===");
    Console.WriteLine();
    Console.WriteLine("Date-by-date inclusion:");

    var relIncludedDates = new List<DateOnly>();
    var relAllRows = new List<RelationshipObservation>();
    var relRowsByDay = new Dictionary<DateOnly, List<RelationshipObservation>>();
    var relAllCrossovers = new List<Vc0DteBehaviorRecorder.ObservationRow>();
    var relSpotAvailableDays = new List<DateOnly>();

    for (var date = relFromDate; date <= relToDate; date = date.AddDays(1))
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            Console.WriteLine($"  {date:yyyy-MM-dd} ({date.DayOfWeek}): EXCLUDED -- weekend, no trading session.");
            continue;
        }

        var (relChain, relFutureBars, relOptionBars) = await LoadVc0DteDayAsync(date, relThreshold);
        if (relFutureBars.Count == 0)
        {
            Console.WriteLine($"  {date:yyyy-MM-dd} ({date.DayOfWeek}): EXCLUDED -- no futures tick data available in this dataset for this date.");
            continue;
        }
        if (relChain.Count == 0)
        {
            Console.WriteLine($"  {date:yyyy-MM-dd} ({date.DayOfWeek}): EXCLUDED -- futures data present, but no option instrument has ExpiryDate == this date (not a 0-DTE expiry day).");
            continue;
        }

        await using var relSource = new NiftySignalDbContext(tradeSourceOptions);
        var relDayRows = await UnderlyingOptionRelationshipRecorder.RecordAsync(relSource, date, relChain, relFutureBars, relOptionBars, CancellationToken.None);
        var relSpotAvailable = relDayRows.Count > 0 && relDayRows[0].SpotAvailable;
        if (relSpotAvailable)
        {
            relSpotAvailableDays.Add(date);
        }

        await using var relCrossoverSource = new NiftySignalDbContext(tradeSourceOptions);
        var relDayCrossovers = await Vc0DteBehaviorRecorder.RecordAsync(relCrossoverSource, date, relChain, relFutureBars, relOptionBars, CancellationToken.None);

        Console.WriteLine($"  {date:yyyy-MM-dd} ({date.DayOfWeek}): INCLUDED -- {relDayRows.Count} relationship observations, spot available={relSpotAvailable}, {relDayCrossovers.Count} Experiment-1 crossovers (unmodified, reused).");
        relIncludedDates.Add(date);
        relAllRows.AddRange(relDayRows);
        relRowsByDay[date] = relDayRows;
        relAllCrossovers.AddRange(relDayCrossovers);
    }

    Console.WriteLine();
    Console.WriteLine($"--- {relIncludedDates.Count} day(s) included: {string.Join(", ", relIncludedDates.Select(d => d.ToString("yyyy-MM-dd")))} ---");
    Console.WriteLine($"--- Spot (NIFTY Index) data available on: {(relSpotAvailableDays.Count > 0 ? string.Join(", ", relSpotAvailableDays.Select(d => d.ToString("yyyy-MM-dd"))) : "NONE -- spot-prefixed fields are unavailable for every included day; futures/CE/PE analysis proceeds without them")} ---");
    Console.WriteLine($"--- TOTAL: {relAllRows.Count} relationship observations ---");
    Console.WriteLine();

    Console.WriteLine("### Relationship category counts (deterministic, descriptive-only labels) ###");
    foreach (var cat in UnderlyingOptionRelationshipSummary.CountCategories(relAllRows))
    {
        Console.WriteLine($"  {cat.Category,-32} {cat.Count,6} ({100.0 * cat.Count / Math.Max(1, relAllRows.Count):F1}%)");
    }
    Console.WriteLine();

    Console.WriteLine("### Point 11 -- user's hypothesis patterns (1-event-bar direction, exact zero/non-zero classification) ###");
    void ReportPattern(string label, RelationshipDirection und, RelationshipDirection ce, RelationshipDirection pe)
    {
        var matches = UnderlyingOptionRelationshipSummary.FindPattern(relAllRows, und, ce, pe);
        var days = matches.Select(m => m.TradingDate).Distinct().Count();
        Console.WriteLine($"  [{label}] n={matches.Count}, days={days}");
        if (matches.Count == 0)
        {
            return;
        }
        var avgFut = matches.Average(m => m.FuturesChange1 ?? 0m);
        var medFut = matches.Select(m => m.FuturesChange1 ?? 0m).Order().ElementAt(matches.Count / 2);
        var avgCe = matches.Average(m => m.CeChange1 ?? 0m);
        var avgPe = matches.Average(m => m.PeChange1 ?? 0m);
        var staleOrTransition = matches.Count(m => m.CeContractTransition || m.PeContractTransition || m.CeIsStale || m.PeIsStale);
        Console.WriteLine($"    avgFuturesChange1={avgFut:F3} medFuturesChange1={medFut:F3} avgCeChange1={avgCe:F3} avgPeChange1={avgPe:F3} avgCeMinusPe={avgCe - avgPe:F3} staleOrTransitionCount={staleOrTransition}");
        Console.WriteLine("    Observed pattern only -- NOT labeled 'trend ending'/'reversal'/'demand shift'. Possible interpretation: consistent with a hypothesis of that kind. What the data cannot establish: causality, or whether this generalizes beyond the included days.");
    }
    ReportPattern("Underlying rising, CE weakening, PE strengthening", RelationshipDirection.Up, RelationshipDirection.Down, RelationshipDirection.Up);
    ReportPattern("Underlying falling, CE strengthening, PE weakening", RelationshipDirection.Down, RelationshipDirection.Up, RelationshipDirection.Down);
    Console.WriteLine();

    Console.WriteLine("### Point 12 -- pre/post crossover context (avg %% change; '--' = no valid same-contract/available observations) ###");
    var relContext = UnderlyingOptionRelationshipSummary.AttachCrossoverContext(relAllCrossovers, relRowsByDay);
    string FmtPct(IEnumerable<UnderlyingOptionRelationshipSummary.SeriesChange> changes)
    {
        var real = changes.Where(c => c.PercentChange is not null).Select(c => c.PercentChange!.Value).ToList();
        return real.Count > 0 ? real.Average().ToString("F3") : "--";
    }
    foreach (var group in relContext.GroupBy(c => (c.OptionType, c.SignalDirection)).OrderBy(g => g.Key.OptionType).ThenBy(g => g.Key.SignalDirection))
    {
        var g = group.ToList();
        Console.WriteLine($"  {group.Key.OptionType} {group.Key.SignalDirection} (n={g.Count}):");
        Console.WriteLine($"    Futures: Pre3={FmtPct(g.Select(x => x.PreFutures3))} Pre5={FmtPct(g.Select(x => x.PreFutures5))} Pre10={FmtPct(g.Select(x => x.PreFutures10))} | Post1={FmtPct(g.Select(x => x.PostFutures1))} Post3={FmtPct(g.Select(x => x.PostFutures3))} Post5={FmtPct(g.Select(x => x.PostFutures5))} Post10={FmtPct(g.Select(x => x.PostFutures10))}");
        Console.WriteLine($"    CE:      Pre3={FmtPct(g.Select(x => x.PreCe3))} Pre5={FmtPct(g.Select(x => x.PreCe5))} Pre10={FmtPct(g.Select(x => x.PreCe10))} | Post1={FmtPct(g.Select(x => x.PostCe1))} Post3={FmtPct(g.Select(x => x.PostCe3))} Post5={FmtPct(g.Select(x => x.PostCe5))} Post10={FmtPct(g.Select(x => x.PostCe10))}");
        Console.WriteLine($"    PE:      Pre3={FmtPct(g.Select(x => x.PrePe3))} Pre5={FmtPct(g.Select(x => x.PrePe5))} Pre10={FmtPct(g.Select(x => x.PrePe10))} | Post1={FmtPct(g.Select(x => x.PostPe1))} Post3={FmtPct(g.Select(x => x.PostPe3))} Post5={FmtPct(g.Select(x => x.PostPe5))} Post10={FmtPct(g.Select(x => x.PostPe10))}");
    }
    Console.WriteLine();

    Console.WriteLine("### Day-by-day / contract-transition / missing-data audit ###");
    foreach (var day in UnderlyingOptionRelationshipSummary.AuditByDay(relAllRows))
    {
        Console.WriteLine($"  {day.TradingDate:yyyy-MM-dd}: events={day.EventCount} futuresAvail={day.FuturesAvailable} spotAvail={day.SpotAvailable} ceAvail={day.CeAvailable} peAvail={day.PeAvailable} ceTransitions={day.CeContractTransitions} peTransitions={day.PeContractTransitions}");
    }
    Console.WriteLine();

    Console.WriteLine("### Session-bucket breakdown (descriptive only, no filters applied) ###");
    foreach (var bucketGroup in relAllRows.GroupBy(r => Vc0DteBehaviorSummary.SessionBucket(r.StartTimestamp, relIstOffset)).OrderBy(g => g.Key))
    {
        var withCat = bucketGroup.GroupBy(r => r.RelationshipCategory).OrderByDescending(g => g.Count()).Take(3);
        Console.WriteLine($"  [{bucketGroup.Key}] n={bucketGroup.Count()}, top categories: {string.Join(", ", withCat.Select(g => $"{g.Key}={g.Count()}"))}");
    }

    UnderlyingOptionRelationshipRecorder.WriteCsv(relAllRows, relOutPath, relIstOffset);
    Console.WriteLine();
    Console.WriteLine($"Full relationship-observation CSV written to: {Path.GetFullPath(relOutPath)}");

    return 0;
}

// "vc0dte-relationship-forward" -- 2026-09-23 forward validation of the Underlying-CE-PE
// relationship patterns (SEPARATE command, keeps "vc0dte-relationship" itself frozen). FROZEN
// Experiment 1 + relationship-layer configuration, no override flags. Reuses
// UnderlyingOptionRelationshipRecorder.RecordAsync AND UnderlyingOptionRelationshipSummary's
// Compute*Change functions completely unchanged -- all same-contract/missing/stale rejection
// already built and tested there. Only NEW pieces: ForwardValidationAnalysis's metrics/
// classification/bootstrap helpers, and this command's own orchestration.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-forward <fromDate> <toDate> --out=path.csv
if (args.Length > 0 && string.Equals(args[0], "vc0dte-relationship-forward", StringComparison.OrdinalIgnoreCase))
{
    const long fwThreshold = 1300L;
    var fwIstOffset = TimeSpan.FromHours(5.5);
    int[] fwHorizons = [1, 3, 5, 10];

    var (fwPositional, fwNamed) = SplitNamedArgs(args);
    if (fwPositional.Length < 3
        || !DateOnly.TryParseExact(fwPositional[1], "yyyy-MM-dd", out var fwFromDate)
        || !DateOnly.TryParseExact(fwPositional[2], "yyyy-MM-dd", out var fwToDate)
        || !fwNamed.TryGetValue("out", out var fwOutPath))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-forward <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> --out=path.csv");
        return 1;
    }

    Console.WriteLine("=== vc0dte-relationship-forward: FROZEN config, DIAGNOSTIC ONLY -- forward validation of the 4 relationship states, not a trading signal ===");
    Console.WriteLine();

    // Per-day row lists MUST stay separate (EventId is day-relative) -- ComputeXChange indexes
    // into ONE day's own list, never a pooled cross-day list.
    var fwDayRows = new List<(DateOnly Date, List<RelationshipObservation> Rows)>();
    for (var date = fwFromDate; date <= fwToDate; date = date.AddDays(1))
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
        var (fwChain, fwFutureBars, fwOptionBars) = await LoadVc0DteDayAsync(date, fwThreshold);
        if (fwFutureBars.Count == 0 || fwChain.Count == 0) { continue; }

        await using var fwSource = new NiftySignalDbContext(tradeSourceOptions);
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(fwSource, date, fwChain, fwFutureBars, fwOptionBars, CancellationToken.None);
        fwDayRows.Add((date, rows));
        Console.WriteLine($"  {date:yyyy-MM-dd}: {rows.Count} relationship observations loaded (reused, unmodified recorder).");
    }
    Console.WriteLine($"--- {fwDayRows.Count} day(s): {string.Join(", ", fwDayRows.Select(d => d.Date.ToString("yyyy-MM-dd")))} ---");
    Console.WriteLine();

    // One entry per state-classified base observation T (Population A/B -- see docs; the two
    // populations coincide in this codebase because IsStale implies MissingData by construction,
    // so a state-classified row is already guaranteed non-stale/non-missing/non-transition at T).
    var stateObs = fwDayRows
        .SelectMany(d => d.Rows.Select(r => (d.Date, d.Rows, Row: r)))
        .Where(x => ForwardValidationAnalysis.AllFourStates.Contains(x.Row.RelationshipCategory))
        .ToList();

    Console.WriteLine($"Population A/B (state-classified, clean at T): {stateObs.Count} of {fwDayRows.Sum(d => d.Rows.Count)} total observations.");
    foreach (var state in ForwardValidationAnalysis.AllFourStates)
    {
        Console.WriteLine($"  {state}: {stateObs.Count(x => x.Row.RelationshipCategory == state)}");
    }
    Console.WriteLine();

    // ---- Core per-state x per-horizon forward metrics (section C/D/E: four states, Pattern A/B are State2/State4) ----
    Console.WriteLine("### C/D/E -- Four relationship states x forward horizons (Population C coverage = N shown per metric) ###");
    foreach (var state in ForwardValidationAnalysis.AllFourStates)
    {
        var members = stateObs.Where(x => x.Row.RelationshipCategory == state).ToList();
        Console.WriteLine($"[{state}] (n={members.Count}, {members.Select(m => m.Date).Distinct().Count()} day(s))");
        foreach (var h in fwHorizons)
        {
            var futAbs = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).AbsoluteChange).ToList();
            var futPct = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange).ToList();
            var ceAbs = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h).AbsoluteChange).ToList();
            var cePct = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h).PercentChange).ToList();
            var peAbs = members.Select(m => UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h).AbsoluteChange).ToList();
            var pePct = members.Select(m => UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h).PercentChange).ToList();

            var futAbsM = ForwardValidationAnalysis.ComputeForwardMetrics(futAbs);
            var futPctM = ForwardValidationAnalysis.ComputeForwardMetrics(futPct);
            var ceAbsM = ForwardValidationAnalysis.ComputeForwardMetrics(ceAbs);
            var cePctM = ForwardValidationAnalysis.ComputeForwardMetrics(cePct);
            var peAbsM = ForwardValidationAnalysis.ComputeForwardMetrics(peAbs);
            var pePctM = ForwardValidationAnalysis.ComputeForwardMetrics(pePct);

            var ceMinusPePct = members.Zip(cePct, peAbs).Select((_, i) => cePct[i] is { } c && pePct[i] is { } p ? c - p : (decimal?)null).ToList();
            var ceMinusPeM = ForwardValidationAnalysis.ComputeForwardMetrics(ceMinusPePct);

            var sameDir = members.Select((m, i) => ForwardValidationAnalysis.ClassifyForwardDirection(m.Row.FuturesDirection1, futAbs[i])).ToList();
            var sameDirPct = 100.0 * sameDir.Count(s => s == "SameDirection") / Math.Max(1, sameDir.Count);
            var oppDirPct = 100.0 * sameDir.Count(s => s == "OppositeDirection") / Math.Max(1, sameDir.Count);

            Console.WriteLine($"  +{h,2}: Fut[N={futPctM.N,4} mean={Fmt(futPctM.Mean)}% med={Fmt(futPctM.Median)}% pos={futPctM.PositivePct:F1}% neg={futPctM.NegativePct:F1}% absRs={Fmt(futAbsM.Mean)}] "
                + $"CE[N={cePctM.N,4} mean={Fmt(cePctM.Mean)}% med={Fmt(cePctM.Median)}% pos={cePctM.PositivePct:F1}% absRs={Fmt(ceAbsM.Mean)}] "
                + $"PE[N={pePctM.N,4} mean={Fmt(pePctM.Mean)}% med={Fmt(pePctM.Median)}% pos={pePctM.PositivePct:F1}% absRs={Fmt(peAbsM.Mean)}] "
                + $"CE-PE%[mean={Fmt(ceMinusPeM.Mean)} med={Fmt(ceMinusPeM.Median)}] SameDir={sameDirPct:F1}% OppDir={oppDirPct:F1}%");
        }
        Console.WriteLine();
    }

    // ---- Section 8: Pattern A/B directional cross-tab (4 named cases) ----
    Console.WriteLine("### 8 -- Pattern A/B: forward Nifty/CE/PE direction cross-tab ###");
    foreach (var (label, state) in new[] { ("Pattern A (UnderlyingUp_CEDown_PEUp)", ForwardValidationAnalysis.State2_BullishDivergence_PatternA), ("Pattern B (UnderlyingDown_CEUp_PEDown)", ForwardValidationAnalysis.State4_BearishDivergence_PatternB) })
    {
        var members = stateObs.Where(x => x.Row.RelationshipCategory == state).ToList();
        Console.WriteLine($"[{label}] n={members.Count}");
        foreach (var h in fwHorizons)
        {
            var cases = members.Select(m =>
            {
                var futDir = RelationshipDirectionExtensions.Classify(UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).AbsoluteChange);
                var ceDir = RelationshipDirectionExtensions.Classify(UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h).AbsoluteChange);
                var peDir = RelationshipDirectionExtensions.Classify(UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h).AbsoluteChange);
                return (futDir, ceDir, peDir) switch
                {
                    (RelationshipDirection.Up, RelationshipDirection.Up, RelationshipDirection.Down) => "CaseA(NiftyUp_CEUp_PEDown)",
                    (RelationshipDirection.Up, RelationshipDirection.Down, RelationshipDirection.Up) => "CaseB(NiftyUp_CEDown_PEUp)",
                    (RelationshipDirection.Down, RelationshipDirection.Up, RelationshipDirection.Down) => "CaseC(NiftyDown_CEUp_PEDown)",
                    (RelationshipDirection.Down, RelationshipDirection.Down, RelationshipDirection.Up) => "CaseD(NiftyDown_CEDown_PEUp)",
                    _ when futDir == RelationshipDirection.Unavailable || ceDir == RelationshipDirection.Unavailable || peDir == RelationshipDirection.Unavailable => "Unavailable",
                    _ => "Other",
                };
            }).ToList();
            var total = cases.Count;
            var summary = cases.GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()} ({100.0 * g.Count() / Math.Max(1, total):F1}%)");
            Console.WriteLine($"  +{h,2}: {string.Join(", ", summary)}");
        }
        Console.WriteLine();
    }

    // ---- Section F: Futures vs Spot ----
    Console.WriteLine("### F -- Futures vs Spot, per state x horizon ###");
    foreach (var state in ForwardValidationAnalysis.AllFourStates)
    {
        var members = stateObs.Where(x => x.Row.RelationshipCategory == state).ToList();
        Console.WriteLine($"[{state}]");
        foreach (var h in fwHorizons)
        {
            var futChanges = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h)).ToList();
            var spotChanges = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeSpotChange(m.Rows, m.Row.EventId, h)).ToList();
            var pairwise = futChanges.Zip(spotChanges, (f, s) => ForwardValidationAnalysis.ClassifyPairwiseDirection(
                RelationshipDirectionExtensions.Classify(f.AbsoluteChange), RelationshipDirectionExtensions.Classify(s.AbsoluteChange))).ToList();
            var n = pairwise.Count(p => p != "Unavailable");
            var futM = ForwardValidationAnalysis.ComputeForwardMetrics(futChanges.Select(f => f.PercentChange));
            var spotM = ForwardValidationAnalysis.ComputeForwardMetrics(spotChanges.Select(s => s.PercentChange));
            Console.WriteLine($"  +{h,2}: n(both avail)={n} Same={100.0 * pairwise.Count(p => p == "SameDirection") / Math.Max(1, n):F1}% Opp={100.0 * pairwise.Count(p => p == "OppositeDirection") / Math.Max(1, n):F1}% BothFlat={100.0 * pairwise.Count(p => p == "BothFlat") / Math.Max(1, n):F1}% | FutMean={Fmt(futM.Mean)}% FutMed={Fmt(futM.Median)}% SpotMean={Fmt(spotM.Mean)}% SpotMed={Fmt(spotM.Median)}%");
        }
        Console.WriteLine();
    }

    // ---- Section J: Day-by-day (horizon=3 representative, per user's own "at minimum" list) ----
    Console.WriteLine("### J -- Day-by-day stability (horizon=+3, representative) ###");
    foreach (var state in ForwardValidationAnalysis.AllFourStates)
    {
        Console.WriteLine($"[{state}]");
        foreach (var (date, rows) in fwDayRows)
        {
            var members = stateObs.Where(x => x.Date == date && x.Row.RelationshipCategory == state).ToList();
            var futPct = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, m.Row.EventId, 3).PercentChange).ToList();
            var cePct = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, m.Row.EventId, 3).PercentChange).ToList();
            var pePct = members.Select(m => UnderlyingOptionRelationshipSummary.ComputePeChange(rows, m.Row.EventId, 3).PercentChange).ToList();
            var futM = ForwardValidationAnalysis.ComputeForwardMetrics(futPct);
            var ceM = ForwardValidationAnalysis.ComputeForwardMetrics(cePct);
            var peM = ForwardValidationAnalysis.ComputeForwardMetrics(pePct);
            Console.WriteLine($"    {date:yyyy-MM-dd}: n={members.Count} FutMed={Fmt(futM.Median)}% pos={futM.PositivePct:F1}% neg={futM.NegativePct:F1}% | CeMed={Fmt(ceM.Median)}% PeMed={Fmt(peM.Median)}%");
        }
        var all = stateObs.Where(x => x.Row.RelationshipCategory == state).ToList();
        var allFutPct = all.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, 3).PercentChange).ToList();
        var allCePct = all.Select(m => UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, 3).PercentChange).ToList();
        var allPePct = all.Select(m => UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, 3).PercentChange).ToList();
        var allFutM = ForwardValidationAnalysis.ComputeForwardMetrics(allFutPct);
        var allCeM = ForwardValidationAnalysis.ComputeForwardMetrics(allCePct);
        var allPeM = ForwardValidationAnalysis.ComputeForwardMetrics(allPePct);
        Console.WriteLine($"    POOLED  : n={all.Count} FutMed={Fmt(allFutM.Median)}% pos={allFutM.PositivePct:F1}% neg={allFutM.NegativePct:F1}% | CeMed={Fmt(allCeM.Median)}% PeMed={Fmt(allPeM.Median)}%");
        Console.WriteLine();
    }

    // ---- Section I: Session stability (horizon=3 representative) ----
    Console.WriteLine("### I -- Session stability (horizon=+3, representative; descriptive only, no filter) ###");
    foreach (var state in ForwardValidationAnalysis.AllFourStates)
    {
        Console.WriteLine($"[{state}]");
        foreach (var bucketGroup in stateObs.Where(x => x.Row.RelationshipCategory == state)
            .GroupBy(x => Vc0DteBehaviorSummary.SessionBucket(x.Row.StartTimestamp, fwIstOffset)).OrderBy(g => g.Key))
        {
            var members = bucketGroup.ToList();
            var futPct = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, 3).PercentChange).ToList();
            var futM = ForwardValidationAnalysis.ComputeForwardMetrics(futPct);
            Console.WriteLine($"    [{bucketGroup.Key}] n={members.Count} FutMedFwd3={Fmt(futM.Median)}% pos={futM.PositivePct:F1}% neg={futM.NegativePct:F1}%");
        }
        Console.WriteLine();
    }

    // ---- Section K: Low-underlying-movement analysis (terciles of |FuturesChange1|, section 14) ----
    Console.WriteLine("### K -- Low-underlying-movement analysis (terciles of pre-event |futures 1-bar %change|, descriptive quantile per spec section 14) ###");
    var allWithPreMove = fwDayRows.SelectMany(d => d.Rows.Where(r => r.FuturesChange1 is not null && r.FuturesClose != 0)
        .Select(r => (r, absPct: Math.Abs(r.FuturesChange1!.Value / (r.FuturesClose - r.FuturesChange1!.Value) * 100m)))).ToList();
    var (low33, high67) = ForwardValidationAnalysis.ComputeTerciles(allWithPreMove.Select(x => x.absPct).ToList());
    Console.WriteLine($"  Terciles of |pre-event futures 1-bar %change| across all {allWithPreMove.Count} observations with a valid reading: Low33={low33:F4}%, High67={high67:F4}%");
    foreach (var (label, predicate) in new (string, Func<decimal, bool>)[]
        { ("Low (bottom third)", v => v <= low33), ("Normal (middle third)", v => v > low33 && v < high67), ("High (top third)", v => v >= high67) })
    {
        var tierRows = allWithPreMove.Where(x => predicate(x.absPct)).Select(x => x.r).Where(r => ForwardValidationAnalysis.AllFourStates.Contains(r.RelationshipCategory)).ToList();
        var ceAbsMove = ForwardValidationAnalysis.ComputeForwardMetrics(tierRows.Select(r => r.CeChange1));
        var peAbsMove = ForwardValidationAnalysis.ComputeForwardMetrics(tierRows.Select(r => r.PeChange1));
        Console.WriteLine($"  [{label}] state-classified n={tierRows.Count}: CE 1-bar Rs move mean={Fmt(ceAbsMove.Mean)} meanAbs={Fmt(ceAbsMove.MeanAbsolute)} | PE 1-bar Rs move mean={Fmt(peAbsMove.Mean)} meanAbs={Fmt(peAbsMove.MeanAbsolute)}");
    }
    Console.WriteLine();

    // ---- Section: bootstrap 95% CI (descriptive uncertainty only, horizon=3 representative) ----
    Console.WriteLine("### Bootstrap 95% CI for the median (seed=42, 1000 resamples, horizon=+3) -- descriptive uncertainty ONLY, not a significance test. THREE DAYS IS AN INSUFFICIENT SAMPLE FOR STRONG INFERENCE. ###");
    foreach (var state in ForwardValidationAnalysis.AllFourStates)
    {
        var members = stateObs.Where(x => x.Row.RelationshipCategory == state).ToList();
        var futPct = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, 3).PercentChange).Where(v => v is not null).Select(v => v!.Value).ToList();
        var cePct = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, 3).PercentChange).Where(v => v is not null).Select(v => v!.Value).ToList();
        var pePct = members.Select(m => UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, 3).PercentChange).Where(v => v is not null).Select(v => v!.Value).ToList();
        var (futLo, futHi) = ForwardValidationAnalysis.BootstrapMedianCi(futPct);
        var (ceLo, ceHi) = ForwardValidationAnalysis.BootstrapMedianCi(cePct);
        var (peLo, peHi) = ForwardValidationAnalysis.BootstrapMedianCi(pePct);
        Console.WriteLine($"  [{state}] Futures median 95% CI=[{futLo:F4}%, {futHi:F4}%] (n={futPct.Count}) | CE=[{ceLo:F3}%, {ceHi:F3}%] (n={cePct.Count}) | PE=[{peLo:F3}%, {peHi:F3}%] (n={pePct.Count})");
    }
    Console.WriteLine();

    // ---- Event-level CSV ----
    using (var writer = new StreamWriter(fwOutPath))
    {
        writer.WriteLine("TradingDate,EventId,TimestampIST,RelationshipState,"
            + "PreFuturesDir,PreCeDir,PrePeDir,"
            + "FutFwd1Pct,FutFwd3Pct,FutFwd5Pct,FutFwd10Pct,"
            + "SpotFwd1Pct,SpotFwd3Pct,SpotFwd5Pct,SpotFwd10Pct,"
            + "CeFwd1Pct,CeFwd3Pct,CeFwd5Pct,CeFwd10Pct,CeFwd1SameContract,CeFwd3SameContract,CeFwd5SameContract,CeFwd10SameContract,"
            + "PeFwd1Pct,PeFwd3Pct,PeFwd5Pct,PeFwd10Pct,PeFwd1SameContract,PeFwd3SameContract,PeFwd5SameContract,PeFwd10SameContract");
        foreach (var (date, rows, row) in stateObs)
        {
            var ce1 = UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, row.EventId, 1);
            var ce3 = UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, row.EventId, 3);
            var ce5 = UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, row.EventId, 5);
            var ce10 = UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, row.EventId, 10);
            var pe1 = UnderlyingOptionRelationshipSummary.ComputePeChange(rows, row.EventId, 1);
            var pe3 = UnderlyingOptionRelationshipSummary.ComputePeChange(rows, row.EventId, 3);
            var pe5 = UnderlyingOptionRelationshipSummary.ComputePeChange(rows, row.EventId, 5);
            var pe10 = UnderlyingOptionRelationshipSummary.ComputePeChange(rows, row.EventId, 10);
            writer.WriteLine(string.Join(',',
                date.ToString("yyyy-MM-dd"), row.EventId, row.StartTimestamp.ToOffset(fwIstOffset).ToString("HH:mm:ss.fff"), row.RelationshipCategory,
                row.FuturesDirection1, row.CeDirection1, row.PeDirection1,
                UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, row.EventId, 1).PercentChange, UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, row.EventId, 3).PercentChange, UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, row.EventId, 5).PercentChange, UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, row.EventId, 10).PercentChange,
                UnderlyingOptionRelationshipSummary.ComputeSpotChange(rows, row.EventId, 1).PercentChange, UnderlyingOptionRelationshipSummary.ComputeSpotChange(rows, row.EventId, 3).PercentChange, UnderlyingOptionRelationshipSummary.ComputeSpotChange(rows, row.EventId, 5).PercentChange, UnderlyingOptionRelationshipSummary.ComputeSpotChange(rows, row.EventId, 10).PercentChange,
                ce1.PercentChange, ce3.PercentChange, ce5.PercentChange, ce10.PercentChange, ce1.SameContract, ce3.SameContract, ce5.SameContract, ce10.SameContract,
                pe1.PercentChange, pe3.PercentChange, pe5.PercentChange, pe10.PercentChange, pe1.SameContract, pe3.SameContract, pe5.SameContract, pe10.SameContract));
        }
    }
    Console.WriteLine($"Event-level forward-validation CSV written to: {Path.GetFullPath(fwOutPath)}");

    return 0;

    static string Fmt(decimal? v) => v?.ToString("F3") ?? "--";
}

// "vc0dte-relationship-forensic" -- 2026-09-23 forensic validation of the Pattern A/B forward-
// futures-direction finding (HARD FREEZE: 1300/0-DTE/dynamic-ATM/3-10/direction-classification/
// existing commands all unchanged; this is a NEW, separate, read-only diagnostic command).
// Reuses UnderlyingOptionRelationshipRecorder.RecordAsync and every UnderlyingOptionRelationshipSummary/
// ForwardValidationAnalysis function completely unchanged.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-forensic <fromDate> <toDate> --out=path.csv
if (args.Length > 0 && string.Equals(args[0], "vc0dte-relationship-forensic", StringComparison.OrdinalIgnoreCase))
{
    const long fzThreshold = 1300L;
    var fzIstOffset = TimeSpan.FromHours(5.5);
    int[] fzHorizons = [1, 3, 5, 10];
    const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    const string PatternB = ForwardValidationAnalysis.State4_BearishDivergence_PatternB;

    var (fzPositional, fzNamed) = SplitNamedArgs(args);
    if (fzPositional.Length < 3
        || !DateOnly.TryParseExact(fzPositional[1], "yyyy-MM-dd", out var fzFromDate)
        || !DateOnly.TryParseExact(fzPositional[2], "yyyy-MM-dd", out var fzToDate)
        || !fzNamed.TryGetValue("out", out var fzOutPath))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-forensic <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> --out=path.csv");
        return 1;
    }

    Console.WriteLine("=== vc0dte-relationship-forensic: HARD FREEZE, read-only forensic audit of Pattern A/B -- no methodology change ===");
    Console.WriteLine();

    decimal? PctChange(decimal? change, decimal endClose) => change is { } c && endClose - c != 0 ? c / (endClose - c) * 100m : null;

    var fzDayRows = new List<(DateOnly Date, List<RelationshipObservation> Rows)>();
    for (var date = fzFromDate; date <= fzToDate; date = date.AddDays(1))
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
        var (fzChain, fzFutureBars, fzOptionBars) = await LoadVc0DteDayAsync(date, fzThreshold);
        if (fzFutureBars.Count == 0 || fzChain.Count == 0) { continue; }
        await using var fzSource = new NiftySignalDbContext(tradeSourceOptions);
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(fzSource, date, fzChain, fzFutureBars, fzOptionBars, CancellationToken.None);
        fzDayRows.Add((date, rows));
        Console.WriteLine($"  {date:yyyy-MM-dd}: {rows.Count} relationship observations loaded (reused, unmodified recorder).");
    }
    Console.WriteLine();

    var patternAObs = fzDayRows.SelectMany(d => d.Rows.Where(r => r.RelationshipCategory == PatternA).Select(r => (d.Date, d.Rows, Row: r))).ToList();
    var patternBObs = fzDayRows.SelectMany(d => d.Rows.Where(r => r.RelationshipCategory == PatternB).Select(r => (d.Date, d.Rows, Row: r))).ToList();
    var allRows = fzDayRows.SelectMany(d => d.Rows.Select(r => (d.Date, d.Rows, Row: r))).ToList();

    // ---- Section 2: raw forensic examples, deterministic first/middle/last per day per pattern ----
    Console.WriteLine("### Section 2 -- raw forensic examples (selection rule: first/middle/last qualifying observation per day, per pattern) ###");
    void PrintExamples(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> members)
    {
        Console.WriteLine($"[{label}]");
        foreach (var day in members.Select(m => m.Date).Distinct())
        {
            var dayMembers = members.Where(m => m.Date == day).OrderBy(m => m.Row.EventId).ToList();
            var picks = new List<(string Tag, (DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row) M)>();
            if (dayMembers.Count > 0) { picks.Add(("first", dayMembers[0])); }
            if (dayMembers.Count > 2) { picks.Add(("middle", dayMembers[dayMembers.Count / 2])); }
            if (dayMembers.Count > 1) { picks.Add(("last", dayMembers[^1])); }

            foreach (var (tag, m) in picks)
            {
                var t = m.Row.EventId;
                var prev = m.Rows[t - 1];
                Console.WriteLine($"  {day:yyyy-MM-dd} event#{t} ({tag} qualifying obs that day):");
                Console.WriteLine($"    Event window: {m.Row.StartTimestamp.ToOffset(fzIstOffset):HH:mm:ss.fff} -> {m.Row.EndTimestamp.ToOffset(fzIstOffset):HH:mm:ss.fff}");
                Console.WriteLine($"    Futures: T-1={prev.FuturesClose} T={m.Row.FuturesClose} dir={m.Row.FuturesDirection1}");
                Console.WriteLine($"    Spot:    T-1={(prev.SpotClose?.ToString() ?? "--")} T={(m.Row.SpotClose?.ToString() ?? "--")} dir={m.Row.SpotDirection1}");
                Console.WriteLine($"    CE {m.Row.CeToken}: T-1={(prev.CeAverageLtp?.ToString() ?? "--")} T={(m.Row.CeAverageLtp?.ToString() ?? "--")} dir={m.Row.CeDirection1}");
                Console.WriteLine($"    PE {m.Row.PeToken}: T-1={(prev.PeAverageLtp?.ToString() ?? "--")} T={(m.Row.PeAverageLtp?.ToString() ?? "--")} dir={m.Row.PeDirection1}");
                foreach (var h in fzHorizons)
                {
                    var fut = UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, t, h);
                    var spot = UnderlyingOptionRelationshipSummary.ComputeSpotChange(m.Rows, t, h);
                    var ce = UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, t, h);
                    var pe = UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, t, h);
                    var endIdx = t + h;
                    var endRow = endIdx < m.Rows.Count ? m.Rows[endIdx] : null;
                    Console.WriteLine($"    T+{h,2}: Futures={(endRow?.FuturesClose.ToString() ?? "--")} ({Fmt2(fut.PercentChange)}%) Spot={(endRow?.SpotClose?.ToString() ?? "--")} ({Fmt2(spot.PercentChange)}%) CE={(endRow?.CeAverageLtp?.ToString() ?? "--")} ({Fmt2(ce.PercentChange)}%, sameContract={ce.SameContract}) PE={(endRow?.PeAverageLtp?.ToString() ?? "--")} ({Fmt2(pe.PercentChange)}%, sameContract={pe.SameContract})");
                }
                Console.WriteLine();
            }
        }
    }
    PrintExamples("Pattern A (UnderlyingUp_CEDown_PEUp)", patternAObs);
    PrintExamples("Pattern B (UnderlyingDown_CEUp_PEDown)", patternBObs);

    // ---- Section 3: is the effect driven by tiny underlying moves? ----
    Console.WriteLine("### Section 3 -- pre-event and forward underlying (futures) movement distributions ###");
    var globalPreMoves = allRows.Where(x => x.Row.FuturesChange1 is not null)
        .Select(x => Math.Abs(PctChange(x.Row.FuturesChange1, x.Row.FuturesClose) ?? 0m)).ToList();
    var globalPercentiles = ForensicValidationAnalysis.ComputePercentiles(globalPreMoves);
    Console.WriteLine($"  GLOBAL (all {globalPreMoves.Count} obs) |pre-event futures 1-bar %change|: min={globalPercentiles.Min:F4} P10={globalPercentiles.P10:F4} P25={globalPercentiles.P25:F4} med={globalPercentiles.Median:F4} P75={globalPercentiles.P75:F4} P90={globalPercentiles.P90:F4} max={globalPercentiles.Max:F4}");
    foreach (var (label, members) in new[] { ("Pattern A", patternAObs), ("Pattern B", patternBObs) })
    {
        var preMoves = members.Where(m => m.Row.FuturesChange1 is not null).Select(m => Math.Abs(PctChange(m.Row.FuturesChange1, m.Row.FuturesClose) ?? 0m)).ToList();
        var p = ForensicValidationAnalysis.ComputePercentiles(preMoves);
        var zero = preMoves.Count(v => v == 0);
        var belowP25 = preMoves.Count(v => v < globalPercentiles.P25);
        var p25To50 = preMoves.Count(v => v >= globalPercentiles.P25 && v < globalPercentiles.Median);
        var p50To75 = preMoves.Count(v => v >= globalPercentiles.Median && v < globalPercentiles.P75);
        var aboveP75 = preMoves.Count(v => v >= globalPercentiles.P75);
        Console.WriteLine($"  [{label}] pre-event |%change|: min={p.Min:F4} P10={p.P10:F4} P25={p.P25:F4} med={p.Median:F4} P75={p.P75:F4} P90={p.P90:F4} max={p.Max:F4}");
        Console.WriteLine($"    vs GLOBAL quartiles: exactly-zero={100.0 * zero / preMoves.Count:F1}% belowGlobalP25={100.0 * belowP25 / preMoves.Count:F1}% globalP25-50={100.0 * p25To50 / preMoves.Count:F1}% globalP50-75={100.0 * p50To75 / preMoves.Count:F1}% aboveGlobalP75={100.0 * aboveP75 / preMoves.Count:F1}%");
        foreach (var h in fzHorizons)
        {
            var fwdMoves = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange).Where(v => v is not null).Select(v => Math.Abs(v!.Value)).ToList();
            var fp = ForensicValidationAnalysis.ComputePercentiles(fwdMoves);
            Console.WriteLine($"    forward |%change| +{h,2}: min={fp.Min:F4} P25={fp.P25:F4} med={fp.Median:F4} P75={fp.P75:F4} max={fp.Max:F4}");
        }
    }
    Console.WriteLine();

    // ---- Section 4: four-state clean comparison table ----
    Console.WriteLine("### Section 4 -- four-state comparison (no ranking) ###");
    foreach (var h in fzHorizons)
    {
        Console.WriteLine($"  Horizon +{h}:");
        Console.WriteLine("  State                       | Count | MedPreNifty% | MedFwdNifty% | Same% | Opp%");
        foreach (var state in ForwardValidationAnalysis.AllFourStates)
        {
            var members = allRows.Where(x => x.Row.RelationshipCategory == state).ToList();
            var preNifty = members.Select(m => PctChange(m.Row.FuturesChange1, m.Row.FuturesClose)).ToList();
            var fwdNifty = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange).ToList();
            var preM = ForwardValidationAnalysis.ComputeForwardMetrics(preNifty);
            var fwdM = ForwardValidationAnalysis.ComputeForwardMetrics(fwdNifty);
            var dirs = members.Select((m, i) => ForwardValidationAnalysis.ClassifyForwardDirection(m.Row.FuturesDirection1, fwdNifty[i])).ToList();
            var same = 100.0 * dirs.Count(d => d == "SameDirection") / Math.Max(1, dirs.Count);
            var opp = 100.0 * dirs.Count(d => d == "OppositeDirection") / Math.Max(1, dirs.Count);
            Console.WriteLine($"  {state,-27} | {members.Count,5} | {Fmt2(preM.Median),11}% | {Fmt2(fwdM.Median),11}% | {same,4:F1}% | {opp,4:F1}%");
        }
    }
    Console.WriteLine();

    // ---- Section 5: underlying-only baseline ----
    Console.WriteLine("### Section 5 -- underlying-only baseline (regardless of CE/PE) vs Pattern A/B ###");
    foreach (var h in fzHorizons)
    {
        var upOnly = allRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Up).ToList();
        var downOnly = allRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Down).ToList();
        decimal? med(List<(DateOnly, List<RelationshipObservation> Rows, RelationshipObservation Row)> members) =>
            ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange)).Median;
        double negPct(List<(DateOnly, List<RelationshipObservation> Rows, RelationshipObservation Row)> members) =>
            ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange)).NegativePct;
        Console.WriteLine($"  +{h,2}: UnderlyingUpOnly(n={upOnly.Count}) medFwd={Fmt2(med(upOnly))}% neg%={negPct(upOnly):F1}%  ||  PatternA(n={patternAObs.Count}) medFwd={Fmt2(med(patternAObs))}% neg%={negPct(patternAObs):F1}%");
        Console.WriteLine($"       UnderlyingDownOnly(n={downOnly.Count}) medFwd={Fmt2(med(downOnly))}% pos%={100 - negPct(downOnly) - ForwardValidationAnalysis.ComputeForwardMetrics(downOnly.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange)).ZeroPct:F1}%  ||  PatternB(n={patternBObs.Count}) medFwd={Fmt2(med(patternBObs))}%");
    }
    Console.WriteLine();

    // ---- Section 6: CE/PE-only baseline ----
    Console.WriteLine("### Section 6 -- CE/PE-only baseline (regardless of underlying direction) vs Pattern A/B ###");
    var ceDownPeUpAny = allRows.Where(x => x.Row.CeDirection1 == RelationshipDirection.Down && x.Row.PeDirection1 == RelationshipDirection.Up).ToList();
    var ceUpPeDownAny = allRows.Where(x => x.Row.CeDirection1 == RelationshipDirection.Up && x.Row.PeDirection1 == RelationshipDirection.Down).ToList();
    foreach (var h in fzHorizons)
    {
        decimal? med(List<(DateOnly, List<RelationshipObservation> Rows, RelationshipObservation Row)> members) =>
            ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange)).Median;
        Console.WriteLine($"  +{h,2}: CEDown+PEUp(any underlying, n={ceDownPeUpAny.Count}) medFwdNifty={Fmt2(med(ceDownPeUpAny))}%  vs  PatternA(underlying-up-required, n={patternAObs.Count}) medFwdNifty={Fmt2(med(patternAObs))}%");
        Console.WriteLine($"       CEUp+PEDown(any underlying, n={ceUpPeDownAny.Count}) medFwdNifty={Fmt2(med(ceUpPeDownAny))}%  vs  PatternB(underlying-down-required, n={patternBObs.Count}) medFwdNifty={Fmt2(med(patternBObs))}%");
    }
    Console.WriteLine();

    // ---- Section 8: ATM/contract-transition audit ----
    Console.WriteLine("### Section 8 -- ATM/contract-transition audit (is the effect concentrated around transitions?) ###");
    foreach (var (label, members) in new[] { ("Pattern A", patternAObs), ("Pattern B", patternBObs) })
    {
        var transitionedAfter = members.Where(m => m.Row.EventId + 1 < m.Rows.Count && (m.Rows[m.Row.EventId + 1].CeContractTransition || m.Rows[m.Row.EventId + 1].PeContractTransition)).ToList();
        var noTransitionAfter = members.Where(m => m.Row.EventId + 1 < m.Rows.Count && !m.Rows[m.Row.EventId + 1].CeContractTransition && !m.Rows[m.Row.EventId + 1].PeContractTransition).ToList();
        Console.WriteLine($"  [{label}] n={members.Count}, ATM transitioned at T+1: {transitionedAfter.Count} ({100.0 * transitionedAfter.Count / Math.Max(1, members.Count):F1}%)");
        foreach (var h in fzHorizons)
        {
            var medTrans = ForwardValidationAnalysis.ComputeForwardMetrics(transitionedAfter.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange)).Median;
            var medNoTrans = ForwardValidationAnalysis.ComputeForwardMetrics(noTransitionAfter.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange)).Median;
            Console.WriteLine($"    +{h,2}: medFwdNifty% | transitionedAtT+1(n={transitionedAfter.Count})={Fmt2(medTrans)}%  vs  noTransition(n={noTransitionAfter.Count})={Fmt2(medNoTrans)}%");
        }
    }
    Console.WriteLine();

    // ---- Section 9: session stability with counts ----
    Console.WriteLine("### Section 9 -- session stability WITH sample sizes (n<30 flagged LOW SAMPLE) ###");
    foreach (var (label, members) in new[] { ("Pattern A", patternAObs), ("Pattern B", patternBObs) })
    {
        Console.WriteLine($"[{label}]");
        foreach (var bucketGroup in members.GroupBy(m => Vc0DteBehaviorSummary.SessionBucket(m.Row.StartTimestamp, fzIstOffset)).OrderBy(g => g.Key))
        {
            var bm = bucketGroup.ToList();
            var flag = bm.Count < 30 ? " [LOW SAMPLE]" : "";
            var preMed = ForwardValidationAnalysis.ComputeForwardMetrics(bm.Select(m => PctChange(m.Row.FuturesChange1, m.Row.FuturesClose))).Median;
            var line = $"    [{bucketGroup.Key}] n={bm.Count}{flag} medPreNifty={Fmt2(preMed)}%";
            foreach (var h in fzHorizons)
            {
                var fwd = bm.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange).ToList();
                var m2 = ForwardValidationAnalysis.ComputeForwardMetrics(fwd);
                var dirs = bm.Select((m, i) => ForwardValidationAnalysis.ClassifyForwardDirection(m.Row.FuturesDirection1, fwd[i])).ToList();
                var opp = 100.0 * dirs.Count(d => d == "OppositeDirection") / Math.Max(1, dirs.Count);
                line += $" | Fwd+{h}={Fmt2(m2.Median)}%(opp={opp:F0}%)";
            }
            Console.WriteLine(line);
        }
    }
    Console.WriteLine();

    // ---- Section 10: distribution robustness ----
    Console.WriteLine("### Section 10 -- distribution robustness (does the median agree with the mean, or is it a few large moves?) ###");
    foreach (var (label, members) in new[] { ("Pattern A", patternAObs), ("Pattern B", patternBObs) })
    {
        Console.WriteLine($"[{label}]");
        foreach (var h in fzHorizons)
        {
            var values = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange).Where(v => v is not null).Select(v => v!.Value).ToList();
            if (values.Count == 0) { continue; }
            var p = ForensicValidationAnalysis.ComputePercentiles(values);
            var mean = values.Average();
            var c1 = ForensicValidationAnalysis.ContributionOfLargestToTotal(values, 0.01);
            var c5 = ForensicValidationAnalysis.ContributionOfLargestToTotal(values, 0.05);
            var c10 = ForensicValidationAnalysis.ContributionOfLargestToTotal(values, 0.10);
            Console.WriteLine($"    +{h,2}: n={p.N} mean={mean:F4}% med={p.Median:F4}% P10={p.P10:F4}% P25={p.P25:F4}% P75={p.P75:F4}% P90={p.P90:F4}% maxPos={p.Max:F4}% maxNeg={p.Min:F4}% | top1%contrib={c1:F1}% top5%={c5:F1}% top10%={c10:F1}%");
        }
    }
    Console.WriteLine();

    // ---- Section 11: permutation sanity check ----
    Console.WriteLine("### Section 11 -- permutation sanity check (seed=42, 1000 permutations, descriptive only -- NOT a significance test) ###");
    foreach (var (label, members) in new[] { ("Pattern A", patternAObs), ("Pattern B", patternBObs) })
    {
        foreach (var h in fzHorizons)
        {
            var population = allRows.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange).Where(v => v is not null).Select(v => v!.Value).ToList();
            var observedValues = members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange).Where(v => v is not null).Select(v => v!.Value).ToList();
            if (observedValues.Count == 0) { continue; }
            var observedMedian = ForensicValidationAnalysis.ComputePercentiles(observedValues).Median;
            var distribution = ForensicValidationAnalysis.PermutationMedianDistribution(population, observedValues.Count, seed: 42, permutations: 1000);
            var rank = ForensicValidationAnalysis.PercentileRankOf(distribution, observedMedian);
            Console.WriteLine($"  [{label}] +{h,2}: observed median={observedMedian:F4}% (n={observedValues.Count}) falls at percentile {rank:F1} of {distribution.Count} random same-sized draws from the full {population.Count}-observation population (permutation range [{distribution[0]:F4}%, {distribution[^1]:F4}%])");
        }
    }
    Console.WriteLine();

    // ---- Section 13: multi-day availability scan (cheap, no bar-building, no methodology change) ----
    Console.WriteLine("### Section 13 -- multi-day 0-DTE availability scan (cheap existence check only, same inclusion criteria as every other command) ###");
    var fzScanFrom = new DateOnly(2026, 8, 1);
    var fzScanTo = new DateOnly(2026, 10, 31);
    var fzEligibleCount = 0;
    await using (var fzScanSource = new NiftySignalDbContext(tradeSourceOptions))
    {
        for (var date = fzScanFrom; date <= fzScanTo; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
            var hasFutures = await fzScanSource.Instruments.AnyAsync(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Future && i.Underlying == "NIFTY");
            if (!hasFutures)
            {
                continue; // don't print every non-trading/no-data day -- would be hundreds of lines; summarized in the eligible count below instead.
            }
            var hasSpot = await fzScanSource.Instruments.AnyAsync(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Index && i.Underlying == "NIFTY");
            var hasZeroDte = await fzScanSource.Instruments.AnyAsync(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate == date && i.Underlying == "NIFTY");
            var eligible = hasZeroDte; // same criterion LoadVc0DteDayAsync's own callers already use.
            if (eligible) { fzEligibleCount++; }
            Console.WriteLine($"  {date:yyyy-MM-dd} ({date.DayOfWeek}): hasFutures=True hasSpot={hasSpot} hasZeroDteChain={hasZeroDte} eligible={eligible}{(eligible ? "" : " reason=not a 0-DTE expiry day")}");
        }
    }
    Console.WriteLine($"--- {fzEligibleCount} total 0-DTE-eligible day(s) found in {fzScanFrom:yyyy-MM-dd}..{fzScanTo:yyyy-MM-dd} (3 of which were used in this and prior analyses) ---");
    Console.WriteLine("--- The existing frozen vc0dte-relationship-forward command can process any of these directly by date range, with NO methodology change. ---");
    Console.WriteLine();

    // ---- Event-level forensic CSV (Pattern A/B only, with transition/ATM audit columns) ----
    using (var writer = new StreamWriter(fzOutPath))
    {
        writer.WriteLine("TradingDate,EventId,Pattern,FuturesT,FuturesTMinus1,CeToken,CeT,CeTMinus1,PeToken,PeT,PeTMinus1,"
            + "CeTransitionAtTPlus1,PeTransitionAtTPlus1,FutFwd1Pct,FutFwd3Pct,FutFwd5Pct,FutFwd10Pct");
        foreach (var (date, rows, row) in patternAObs.Concat(patternBObs))
        {
            var prev = rows[row.EventId - 1];
            var next = row.EventId + 1 < rows.Count ? rows[row.EventId + 1] : null;
            writer.WriteLine(string.Join(',',
                date.ToString("yyyy-MM-dd"), row.EventId, row.RelationshipCategory == PatternA ? "A" : "B",
                row.FuturesClose, prev.FuturesClose, row.CeToken, row.CeAverageLtp, prev.CeAverageLtp, row.PeToken, row.PeAverageLtp, prev.PeAverageLtp,
                next?.CeContractTransition ?? false, next?.PeContractTransition ?? false,
                UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, row.EventId, 1).PercentChange,
                UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, row.EventId, 3).PercentChange,
                UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, row.EventId, 5).PercentChange,
                UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, row.EventId, 10).PercentChange));
        }
    }
    Console.WriteLine($"Forensic event-level CSV (Pattern A/B only) written to: {Path.GetFullPath(fzOutPath)}");

    return 0;

    static string Fmt2(decimal? v) => v?.ToString("F4") ?? "--";
}

// "vc0dte-relationship-episodes" -- 2026-09-23 episode-level/cluster-level validation (HARD
// FREEZE: same as vc0dte-relationship-forensic -- 1300/0-DTE/dynamic-ATM/3-10/classification/
// forward-return methodology all unchanged; new, additive, read-only command only). Reuses
// UnderlyingOptionRelationshipRecorder.RecordAsync, Vc0DteBehaviorRecorder.RecordAsync, every
// UnderlyingOptionRelationshipSummary.Compute*Change function, and
// ForensicValidationAnalysis.PermutationMedianDistribution completely unchanged.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-episodes <fromDate> <toDate> --out=path.csv
if (args.Length > 0 && string.Equals(args[0], "vc0dte-relationship-episodes", StringComparison.OrdinalIgnoreCase))
{
    const long epThreshold = 1300L;
    var epIstOffset = TimeSpan.FromHours(5.5);
    int[] epHorizons = [1, 3, 5, 10];
    const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    const string PatternB = ForwardValidationAnalysis.State4_BearishDivergence_PatternB;

    var (epPositional, epNamed) = SplitNamedArgs(args);
    if (epPositional.Length < 3
        || !DateOnly.TryParseExact(epPositional[1], "yyyy-MM-dd", out var epFromDate)
        || !DateOnly.TryParseExact(epPositional[2], "yyyy-MM-dd", out var epToDate)
        || !epNamed.TryGetValue("out", out var epOutPath))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-episodes <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> --out=path.csv");
        return 1;
    }

    Console.WriteLine("=== vc0dte-relationship-episodes: HARD FREEZE, episode/cluster-level validation -- one signal point per market episode, not per event bar ===");
    Console.WriteLine();

    string Arrow(RelationshipDirection d) => d switch { RelationshipDirection.Up => "↑", RelationshipDirection.Down => "↓", RelationshipDirection.Flat => "=", _ => "?" };
    static string Fmt3(decimal? v) => v?.ToString("F4") ?? "--";

    var epDayRows = new List<(DateOnly Date, List<RelationshipObservation> Rows, List<Vc0DteBehaviorRecorder.ObservationRow> Crossovers)>();
    for (var date = epFromDate; date <= epToDate; date = date.AddDays(1))
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
        var (epChain, epFutureBars, epOptionBars) = await LoadVc0DteDayAsync(date, epThreshold);
        if (epFutureBars.Count == 0 || epChain.Count == 0) { continue; }
        await using var epSource = new NiftySignalDbContext(tradeSourceOptions);
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(epSource, date, epChain, epFutureBars, epOptionBars, CancellationToken.None);
        await using var epCrossSource = new NiftySignalDbContext(tradeSourceOptions);
        var crossovers = await Vc0DteBehaviorRecorder.RecordAsync(epCrossSource, date, epChain, epFutureBars, epOptionBars, CancellationToken.None);
        epDayRows.Add((date, rows, crossovers));
        Console.WriteLine($"  {date:yyyy-MM-dd}: {rows.Count} relationship observations loaded (reused, unmodified recorder).");
    }
    Console.WriteLine();

    // Episodes for ALL FOUR states -- Pattern A/B are the primary interest, but the other two
    // (confirmation states) feed the episode-level permutation population (section 12).
    var episodesByState = ForwardValidationAnalysis.AllFourStates.ToDictionary(
        state => state,
        state => epDayRows.SelectMany(d => EpisodeAnalysis.DetectEpisodes(d.Date, d.Rows, state, d.Crossovers)).ToList());
    var episodeRowsByState = ForwardValidationAnalysis.AllFourStates.ToDictionary(
        state => state,
        state => episodesByState[state].Select(e => (Episode: e, Rows: epDayRows.Single(d => d.Date == e.TradingDate).Rows)).ToList());

    var patternAEpisodes = episodeRowsByState[PatternA];
    var patternBEpisodes = episodeRowsByState[PatternB];
    var eventLevelA = epDayRows.SelectMany(d => d.Rows.Where(r => r.RelationshipCategory == PatternA)).Count();
    var eventLevelB = epDayRows.SelectMany(d => d.Rows.Where(r => r.RelationshipCategory == PatternB)).Count();

    // ---- Section B: event-level vs episode-level counts ----
    Console.WriteLine("### B -- event-level vs episode-level counts (pseudo-replication check) ###");
    Console.WriteLine($"  Pattern A: {eventLevelA} event-level observations -> {patternAEpisodes.Count} independent episodes ({(double)eventLevelA / Math.Max(1, patternAEpisodes.Count):F1} events/episode on average)");
    Console.WriteLine($"  Pattern B: {eventLevelB} event-level observations -> {patternBEpisodes.Count} independent episodes ({(double)eventLevelB / Math.Max(1, patternBEpisodes.Count):F1} events/episode on average)");
    Console.WriteLine("  Event-level counts are NOT independent-opportunity counts -- one persistent market condition produces many consecutive event rows.");
    Console.WriteLine();

    // ---- Sections C/D: episode-level vs event-level forward results ----
    void PrintComparison(string label, List<(EpisodeAnalysis.Episode Episode, List<RelationshipObservation> Rows)> episodes, string state)
    {
        var eventMembers = epDayRows.SelectMany(d => d.Rows.Where(r => r.RelationshipCategory == state).Select(r => (d.Rows, Row: r))).ToList();
        Console.WriteLine($"[{label}] events={eventMembers.Count}, episodes={episodes.Count}");
        foreach (var h in epHorizons)
        {
            var eventFwd = eventMembers.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange).ToList();
            var episodeFwd = episodes.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Episode.StartEventId, h).PercentChange).ToList();
            var eventM = ForwardValidationAnalysis.ComputeForwardMetrics(eventFwd);
            var episodeM = ForwardValidationAnalysis.ComputeForwardMetrics(episodeFwd);
            var eventDirs = eventMembers.Select((m, i) => ForwardValidationAnalysis.ClassifyForwardDirection(m.Row.FuturesDirection1, eventFwd[i])).ToList();
            var episodeDirs = episodes.Select((m, i) => ForwardValidationAnalysis.ClassifyForwardDirection(m.Episode.Pattern.Contains("Up_CE") ? RelationshipDirection.Up : RelationshipDirection.Down, episodeFwd[i])).ToList();
            Console.WriteLine($"  +{h,2}: EVENT-LEVEL   n={eventM.N,4} mean={Fmt3(eventM.Mean)}% med={Fmt3(eventM.Median)}% pos={eventM.PositivePct:F1}% neg={eventM.NegativePct:F1}% same={100.0 * eventDirs.Count(d => d == "SameDirection") / Math.Max(1, eventDirs.Count):F1}% opp={100.0 * eventDirs.Count(d => d == "OppositeDirection") / Math.Max(1, eventDirs.Count):F1}%");
            Console.WriteLine($"       EPISODE-LEVEL n={episodeM.N,4} mean={Fmt3(episodeM.Mean)}% med={Fmt3(episodeM.Median)}% pos={episodeM.PositivePct:F1}% neg={episodeM.NegativePct:F1}% same={100.0 * episodeDirs.Count(d => d == "SameDirection") / Math.Max(1, episodeDirs.Count):F1}% opp={100.0 * episodeDirs.Count(d => d == "OppositeDirection") / Math.Max(1, episodeDirs.Count):F1}%");
        }
        Console.WriteLine();
    }
    Console.WriteLine("### C -- Pattern A: event-level vs episode-level ###");
    PrintComparison("Pattern A", patternAEpisodes, PatternA);
    Console.WriteLine("### D -- Pattern B: event-level vs episode-level ###");
    PrintComparison("Pattern B", patternBEpisodes, PatternB);

    // ---- Section E: episode duration/persistence ----
    Console.WriteLine("### E -- episode duration/persistence ###");
    foreach (var (label, episodes) in new[] { ("Pattern A", patternAEpisodes), ("Pattern B", patternBEpisodes) })
    {
        var counts = episodes.Select(e => (decimal)e.Episode.EventCount).ToList();
        var durations = episodes.Select(e => (decimal)e.Episode.DurationMs).ToList();
        var countP = ForensicValidationAnalysis.ComputePercentiles(counts);
        var durP = ForensicValidationAnalysis.ComputePercentiles(durations);
        Console.WriteLine($"[{label}] n={episodes.Count} eventCount: med={countP.Median} P25={countP.P25} P75={countP.P75} max={countP.Max} | durationMs: med={durP.Median:F0} mean={durations.Average():F0}");
        foreach (var bucketGroup in episodes.GroupBy(e => EpisodeAnalysis.DurationBucket(e.Episode.EventCount)).OrderBy(g => g.Key == "1 event" ? 0 : g.Key == "2-3 events" ? 1 : g.Key == "4-10 events" ? 2 : 3))
        {
            var members = bucketGroup.ToList();
            var fwd3 = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Episode.StartEventId, 3).PercentChange));
            Console.WriteLine($"  [{bucketGroup.Key}] n={members.Count} medFwdNifty+3={Fmt3(fwd3.Median)}% pos={fwd3.PositivePct:F1}% neg={fwd3.NegativePct:F1}%");
        }
    }
    Console.WriteLine();

    // ---- Section F: first vs later event within episode ----
    Console.WriteLine("### F -- first-event vs later-event behaviour (does the effect exist immediately, or only after persistence?) ###");
    foreach (var (label, episodes) in new[] { ("Pattern A", patternAEpisodes), ("Pattern B", patternBEpisodes) })
    {
        Console.WriteLine($"[{label}]");
        decimal? FwdAt(int offset, List<(EpisodeAnalysis.Episode Episode, List<RelationshipObservation> Rows)> eps, int h) =>
            ForwardValidationAnalysis.ComputeForwardMetrics(eps.Where(e => e.Episode.EventCount > offset)
                .Select(e => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(e.Rows, e.Episode.StartEventId + offset, h).PercentChange)).Median;
        var laterEps = episodes.Where(e => e.Episode.EventCount > 3).ToList();
        decimal? FwdLater(int h) => ForwardValidationAnalysis.ComputeForwardMetrics(laterEps.SelectMany(e =>
            Enumerable.Range(3, e.Episode.EventCount - 3).Select(off => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(e.Rows, e.Episode.StartEventId + off, h).PercentChange))).Median;
        foreach (var h in epHorizons)
        {
            Console.WriteLine($"  +{h,2}: 1st(n={episodes.Count})={Fmt3(FwdAt(0, episodes, h))}% 2nd(n={episodes.Count(e => e.Episode.EventCount > 1)})={Fmt3(FwdAt(1, episodes, h))}% 3rd(n={episodes.Count(e => e.Episode.EventCount > 2)})={Fmt3(FwdAt(2, episodes, h))}% later4+(pooled from {laterEps.Count} episodes)={Fmt3(FwdLater(h))}%");
        }
    }
    Console.WriteLine();

    // ---- Section G: pre-episode movement vs forward movement ----
    Console.WriteLine("### G -- pre-episode underlying movement vs forward movement (possible late-stage divergence context, NOT called a reversal signal) ###");
    foreach (var (label, episodes) in new[] { ("Pattern A", patternAEpisodes), ("Pattern B", patternBEpisodes) })
    {
        foreach (var preHorizon in new[] { 3, 5, 10 })
        {
            var pre = episodes.Where(e => e.Episode.StartEventId - preHorizon >= 0)
                .Select(e => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(e.Rows, e.Episode.StartEventId - preHorizon, preHorizon).PercentChange).ToList();
            var preM = ForwardValidationAnalysis.ComputeForwardMetrics(pre);
            Console.WriteLine($"  [{label}] pre-episode movement over previous {preHorizon} events: n={preM.N} med={Fmt3(preM.Median)}% mean={Fmt3(preM.Mean)}%");
        }
        var fwd10 = ForwardValidationAnalysis.ComputeForwardMetrics(episodes.Select(e => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(e.Rows, e.Episode.StartEventId, 10).PercentChange));
        Console.WriteLine($"  [{label}] (for comparison) forward +10 movement: med={Fmt3(fwd10.Median)}%");
    }
    Console.WriteLine();

    // ---- Section H: day-by-day episode-level results ----
    Console.WriteLine("### H -- day-by-day episode-level results ###");
    foreach (var (label, episodes) in new[] { ("Pattern A", patternAEpisodes), ("Pattern B", patternBEpisodes) })
    {
        Console.WriteLine($"[{label}]");
        var daySignsAt3 = new List<int>();
        foreach (var (date, _, _) in epDayRows)
        {
            var dayEpisodes = episodes.Where(e => e.Episode.TradingDate == date).ToList();
            if (dayEpisodes.Count == 0)
            {
                Console.WriteLine($"    {date:yyyy-MM-dd}: 0 episodes.");
                continue;
            }
            var line = $"    {date:yyyy-MM-dd}: episodes={dayEpisodes.Count}";
            decimal? fwd3Median = null;
            foreach (var h in epHorizons)
            {
                var m = ForwardValidationAnalysis.ComputeForwardMetrics(dayEpisodes.Select(e => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(e.Rows, e.Episode.StartEventId, h).PercentChange));
                if (h == 3) { fwd3Median = m.Median; }
                var dirs = dayEpisodes.Select(e => ForwardValidationAnalysis.ClassifyForwardDirection(label == "Pattern A" ? RelationshipDirection.Up : RelationshipDirection.Down, UnderlyingOptionRelationshipSummary.ComputeFuturesChange(e.Rows, e.Episode.StartEventId, h).PercentChange)).ToList();
                var opp = 100.0 * dirs.Count(d => d == "OppositeDirection") / Math.Max(1, dirs.Count);
                line += $" | +{h}={Fmt3(m.Median)}%(opp={opp:F0}%)";
            }
            Console.WriteLine(line);
            if (fwd3Median is { } fm) { daySignsAt3.Add(Math.Sign(fm)); }
        }
        var expectedSign = label == "Pattern A" ? -1 : 1;
        var sameSignDays = daySignsAt3.Count(s => s == expectedSign);
        Console.WriteLine($"    Days matching pooled sign at +3: {sameSignDays} of {daySignsAt3.Count}. NOT claimed as statistically significant from 3 days.");
    }
    Console.WriteLine();

    // ---- Section I: episode-level permutation sanity check ----
    Console.WriteLine("### I -- episode-level permutation sanity check (seed=42, 1000 permutations; reuses the SAME, unmodified permutation procedure as the event-level check, one value per EPISODE instead of per event) ###");
    var allEpisodeStartFwd = new Dictionary<int, List<decimal>>();
    foreach (var h in epHorizons)
    {
        allEpisodeStartFwd[h] = ForwardValidationAnalysis.AllFourStates
            .SelectMany(state => episodeRowsByState[state].Select(e => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(e.Rows, e.Episode.StartEventId, h).PercentChange))
            .Where(v => v is not null).Select(v => v!.Value).ToList();
    }
    foreach (var (label, episodes) in new[] { ("Pattern A", patternAEpisodes), ("Pattern B", patternBEpisodes) })
    {
        foreach (var h in epHorizons)
        {
            var observed = episodes.Select(e => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(e.Rows, e.Episode.StartEventId, h).PercentChange).Where(v => v is not null).Select(v => v!.Value).ToList();
            if (observed.Count == 0) { continue; }
            var observedMedian = ForensicValidationAnalysis.ComputePercentiles(observed).Median;
            var population = allEpisodeStartFwd[h];
            var distribution = ForensicValidationAnalysis.PermutationMedianDistribution(population, observed.Count, seed: 42, permutations: 1000);
            var rank = ForensicValidationAnalysis.PercentileRankOf(distribution, observedMedian);
            Console.WriteLine($"  [{label}] +{h,2}: observed episode median={observedMedian:F4}% (n={observed.Count} episodes) falls at percentile {rank:F1} of 1000 random same-sized draws from the {population.Count}-episode population (range [{(distribution.Count > 0 ? distribution[0] : 0):F4}%, {(distribution.Count > 0 ? distribution[^1] : 0):F4}%])");
        }
    }
    Console.WriteLine();

    // ---- Section 15: compact event-sequence examples ----
    Console.WriteLine("### Section 15 -- compact event-sequence examples (first/middle/last episode per day per pattern, human inspection only) ###");
    foreach (var (label, state, episodes) in new[] { ("Pattern A", PatternA, patternAEpisodes), ("Pattern B", PatternB, patternBEpisodes) })
    {
        Console.WriteLine($"[{label}]");
        foreach (var (date, rows, _) in epDayRows)
        {
            var dayEpisodes = episodes.Where(e => e.Episode.TradingDate == date).OrderBy(e => e.Episode.StartEventId).ToList();
            if (dayEpisodes.Count == 0) { continue; }
            var picks = new List<(string Tag, (EpisodeAnalysis.Episode Episode, List<RelationshipObservation> Rows) E)> { ("first", dayEpisodes[0]) };
            if (dayEpisodes.Count > 2) { picks.Add(("middle", dayEpisodes[dayEpisodes.Count / 2])); }
            if (dayEpisodes.Count > 1) { picks.Add(("last", dayEpisodes[^1])); }

            foreach (var (tag, e) in picks)
            {
                Console.WriteLine($"  {date:yyyy-MM-dd} episode ({tag}, events {e.Episode.StartEventId}-{e.Episode.EndEventId}, {e.Episode.EventCount} event(s)):");
                Console.WriteLine("    Time         Nifty CE  PE  Relationship");
                for (var idx = e.Episode.StartEventId; idx <= Math.Min(e.Episode.EndEventId + 1, rows.Count - 1); idx++)
                {
                    var r = rows[idx];
                    Console.WriteLine($"    {r.StartTimestamp.ToOffset(epIstOffset):HH:mm:ss}  {Arrow(r.FuturesDirection1),3} {Arrow(r.CeDirection1),3} {Arrow(r.PeDirection1),3}  {r.RelationshipCategory}");
                }
            }
        }
        Console.WriteLine();
    }

    // ---- Episode-level CSV ----
    using (var writer = new StreamWriter(epOutPath))
    {
        writer.WriteLine("TradingDate,EpisodeId,Pattern,StartEventId,EndEventId,StartTimeIST,EndTimeIST,DurationMs,EventCount,"
            + "StartFuturesPrice,EndFuturesPrice,StartSpotPrice,EndSpotPrice,StartCePrice,EndCePrice,StartPePrice,EndPePrice,"
            + "BeganAfterCrossover,CrossoverType,AtmStrike,CeToken,PeToken,AtmTransitionDuringEpisode,"
            + "FutFwd1Pct,FutFwd3Pct,FutFwd5Pct,FutFwd10Pct");
        foreach (var (label, episodes) in new[] { ("A", patternAEpisodes), ("B", patternBEpisodes) })
        {
            foreach (var (episode, rows) in episodes)
            {
                writer.WriteLine(string.Join(',',
                    episode.TradingDate.ToString("yyyy-MM-dd"), episode.EpisodeId, label, episode.StartEventId, episode.EndEventId,
                    episode.StartTimestamp.ToOffset(epIstOffset).ToString("HH:mm:ss.fff"), episode.EndTimestamp.ToOffset(epIstOffset).ToString("HH:mm:ss.fff"),
                    episode.DurationMs, episode.EventCount,
                    episode.StartFuturesPrice, episode.EndFuturesPrice, episode.StartSpotPrice, episode.EndSpotPrice,
                    episode.StartCePrice, episode.EndCePrice, episode.StartPePrice, episode.EndPePrice,
                    episode.BeganAfterCrossover, episode.CrossoverType, episode.AtmStrike, episode.CeToken, episode.PeToken, episode.AtmTransitionDuringEpisode,
                    UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, episode.StartEventId, 1).PercentChange,
                    UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, episode.StartEventId, 3).PercentChange,
                    UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, episode.StartEventId, 5).PercentChange,
                    UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, episode.StartEventId, 10).PercentChange));
            }
        }
    }
    Console.WriteLine($"Episode-level CSV written to: {Path.GetFullPath(epOutPath)}");

    return 0;
}

// "vc0dte-relationship-conditional" -- 2026-09-23 incremental-information / conditional analysis
// (HARD FREEZE: same as every other vc0dte-relationship-* command -- 1300/0-DTE/dynamic-ATM/3-10/
// classification/forward-return methodology all unchanged; new, additive, read-only command).
// Control-group design (confirmed with the user before implementation): for Pattern A/B, the
// control population holds the CONTEMPORANEOUS 1-bar direction constant (Up for A, Down for B),
// excludes data-incomplete/contract-transition rows, and is tercile-matched on |prior-N-event
// movement| using terciles computed WITHIN that same direction-constrained population. Reuses
// UnderlyingOptionRelationshipRecorder.RecordAsync, every UnderlyingOptionRelationshipSummary.Compute*Change
// function, ForwardValidationAnalysis.ComputeForwardMetrics/ComputeTerciles, and
// ForensicValidationAnalysis.ComputePercentiles completely unchanged.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-conditional <fromDate> <toDate> --out=path.csv
if (args.Length > 0 && string.Equals(args[0], "vc0dte-relationship-conditional", StringComparison.OrdinalIgnoreCase))
{
    const long cnThreshold = 1300L;
    int[] cnForwardHorizons = [1, 3, 5, 10];
    int[] cnPriorHorizons = [1, 3, 5, 10];
    int[] cnTercileHorizons = [3, 5, 10]; // N=1 excluded from tercile stratification -- tautological with the pattern's own defining direction (100%/0% split), reported as such in Experiment 1 instead.
    const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    const string PatternB = ForwardValidationAnalysis.State4_BearishDivergence_PatternB;
    string[] excludedCategories = ["OptionDataIncomplete", "ContractTransition"];

    var (cnPositional, cnNamed) = SplitNamedArgs(args);
    if (cnPositional.Length < 3
        || !DateOnly.TryParseExact(cnPositional[1], "yyyy-MM-dd", out var cnFromDate)
        || !DateOnly.TryParseExact(cnPositional[2], "yyyy-MM-dd", out var cnToDate)
        || !cnNamed.TryGetValue("out", out var cnOutPath))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-conditional <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> --out=path.csv");
        return 1;
    }

    Console.WriteLine("=== vc0dte-relationship-conditional: HARD FREEZE, incremental-information/conditional analysis -- does CE/PE divergence add information beyond prior underlying movement? ===");
    Console.WriteLine();
    static string Fmt4(decimal? v) => v?.ToString("F4") ?? "--";

    var cnDayRows = new List<(DateOnly Date, List<RelationshipObservation> Rows)>();
    for (var date = cnFromDate; date <= cnToDate; date = date.AddDays(1))
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
        var (cnChain, cnFutureBars, cnOptionBars) = await LoadVc0DteDayAsync(date, cnThreshold);
        if (cnFutureBars.Count == 0 || cnChain.Count == 0) { continue; }
        await using var cnSource = new NiftySignalDbContext(tradeSourceOptions);
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(cnSource, date, cnChain, cnFutureBars, cnOptionBars, CancellationToken.None);
        cnDayRows.Add((date, rows));
        Console.WriteLine($"  {date:yyyy-MM-dd}: {rows.Count} relationship observations loaded (reused, unmodified recorder).");
    }
    Console.WriteLine();

    var allRows = cnDayRows.SelectMany(d => d.Rows.Select(r => (d.Date, d.Rows, Row: r))).ToList();
    var upPop = allRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Up && !excludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
    var downPop = allRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Down && !excludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
    var patternAObs = upPop.Where(x => x.Row.RelationshipCategory == PatternA).ToList();
    var controlAObs = upPop.Where(x => x.Row.RelationshipCategory != PatternA).ToList();
    var patternBObs = downPop.Where(x => x.Row.RelationshipCategory == PatternB).ToList();
    var controlBObs = downPop.Where(x => x.Row.RelationshipCategory != PatternB).ToList();

    Console.WriteLine($"Population sizes: UpPop(excl. incomplete/transition)={upPop.Count} [PatternA={patternAObs.Count}, ControlA={controlAObs.Count}], DownPop={downPop.Count} [PatternB={patternBObs.Count}, ControlB={controlBObs.Count}]");
    Console.WriteLine();

    decimal? PriorMove(List<(DateOnly, List<RelationshipObservation> Rows, RelationshipObservation Row)> members, int i, int n)
        => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(members[i].Rows, members[i].Row.EventId - n, n).PercentChange;
    decimal? ForwardMove((DateOnly, List<RelationshipObservation> Rows, RelationshipObservation Row) m, int h)
        => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange;

    // ---- Experiment 1: prior underlying movement + forward returns ----
    Console.WriteLine("### Experiment 1 -- prior underlying movement and forward returns (PREVIOUS MOVE -> PATTERN -> FUTURE MOVE) ###");
    foreach (var (label, members) in new[] { ("Pattern A", patternAObs), ("Pattern B", patternBObs) })
    {
        Console.WriteLine($"[{label}] n={members.Count}");
        foreach (var n in cnPriorHorizons)
        {
            var prior = Enumerable.Range(0, members.Count).Select(i => PriorMove(members, i, n)).ToList();
            var m = ForwardValidationAnalysis.ComputeForwardMetrics(prior);
            var note = n == 1 ? " (tautological with the pattern's own defining direction)" : "";
            Console.WriteLine($"  Prior -{n,2}: n={m.N} mean={Fmt4(m.Mean)}% med={Fmt4(m.Median)}% pos={m.PositivePct:F1}% neg={m.NegativePct:F1}%{note}");
        }
        foreach (var h in cnForwardHorizons)
        {
            var fwd = members.Select(m2 => ForwardMove(m2, h)).ToList();
            var m = ForwardValidationAnalysis.ComputeForwardMetrics(fwd);
            Console.WriteLine($"  Fwd   +{h,2}: n={m.N} mean={Fmt4(m.Mean)}% med={Fmt4(m.Median)}% pos={m.PositivePct:F1}% neg={m.NegativePct:F1}%");
        }
        Console.WriteLine();
    }

    // ---- Experiments 2/4: tercile stratification + conditional effect table (Pattern vs Control) ----
    void PrintConditionalTable(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> patternMembers, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> controlMembers, List<(DateOnly, List<RelationshipObservation> Rows, RelationshipObservation Row)> direction)
    {
        foreach (var n in cnTercileHorizons)
        {
            var dirPriorAbs = Enumerable.Range(0, direction.Count).Select(i => PriorMove(direction, i, n)).Where(v => v is not null).Select(v => Math.Abs(v!.Value)).ToList();
            var (low33, high67) = ForwardValidationAnalysis.ComputeTerciles(dirPriorAbs);
            Console.WriteLine($"  [Prior -{n} terciles, computed within the direction-constrained population: n={dirPriorAbs.Count}, Low33={low33:F4}%, High67={high67:F4}%]");

            foreach (var bucket in new[] { "Low", "Mid", "High" })
            {
                List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> Bucketed(List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> src)
                    => Enumerable.Range(0, src.Count).Where(i =>
                    {
                        var p = PriorMove(src, i, n);
                        return p is not null && ConditionalMovementAnalysis.ClassifyTercileBucket(Math.Abs(p.Value), low33, high67) == bucket;
                    }).Select(i => src[i]).ToList();

                var patternBucket = Bucketed(patternMembers);
                var controlBucket = Bucketed(controlMembers);

                var fwdCells = cnForwardHorizons.Select(h =>
                {
                    var pm = ForwardValidationAnalysis.ComputeForwardMetrics(patternBucket.Select(m => ForwardMove(m, h)));
                    var cm = ForwardValidationAnalysis.ComputeForwardMetrics(controlBucket.Select(m => ForwardMove(m, h)));
                    return $"+{h}: {label.Substring(0, 1)}[n={pm.N} med={Fmt4(pm.Median)}%] Ctrl[n={cm.N} med={Fmt4(cm.Median)}%]";
                });
                Console.WriteLine($"    [{bucket}] {string.Join("  ", fwdCells)}");
            }
        }
    }
    Console.WriteLine("### Experiments 2/4 -- tercile-stratified conditional effect table (Pattern vs comparable-movement Control) ###");
    Console.WriteLine("[Pattern A vs Control A -- both drawn from the Up-direction population]");
    PrintConditionalTable("A", patternAObs, controlAObs, upPop);
    Console.WriteLine("[Pattern B vs Control B -- both drawn from the Down-direction population]");
    PrintConditionalTable("B", patternBObs, controlBObs, downPop);
    Console.WriteLine();

    // ---- Experiment 6: day-by-day robustness for the primary N=3 tercile stratification ----
    Console.WriteLine("### Experiment 6 -- day-by-day robustness (prior -3 tercile stratification, +3 forward horizon; only 3 independent days -- no statistical claim made) ###");
    foreach (var (label, patternMembers, controlMembers, direction) in new[] { ("Pattern A", patternAObs, controlAObs, upPop), ("Pattern B", patternBObs, controlBObs, downPop) })
    {
        var dirPriorAbs = Enumerable.Range(0, direction.Count).Select(i => PriorMove(direction, i, 3)).Where(v => v is not null).Select(v => Math.Abs(v!.Value)).ToList();
        var (low33, high67) = ForwardValidationAnalysis.ComputeTerciles(dirPriorAbs);
        Console.WriteLine($"[{label}] (terciles: Low33={low33:F4}%, High67={high67:F4}%)");
        foreach (var (date, _) in cnDayRows)
        {
            var dayPattern = patternMembers.Where(m => m.Date == date).ToList();
            var dayControl = controlMembers.Where(m => m.Date == date).ToList();
            foreach (var bucket in new[] { "Low", "Mid", "High" })
            {
                var pb = Enumerable.Range(0, dayPattern.Count).Where(i => { var p = PriorMove(dayPattern, i, 3); return p is not null && ConditionalMovementAnalysis.ClassifyTercileBucket(Math.Abs(p.Value), low33, high67) == bucket; }).Select(i => dayPattern[i]).ToList();
                var cb = Enumerable.Range(0, dayControl.Count).Where(i => { var p = PriorMove(dayControl, i, 3); return p is not null && ConditionalMovementAnalysis.ClassifyTercileBucket(Math.Abs(p.Value), low33, high67) == bucket; }).Select(i => dayControl[i]).ToList();
                var pm = ForwardValidationAnalysis.ComputeForwardMetrics(pb.Select(m => ForwardMove(m, 3)));
                var cm = ForwardValidationAnalysis.ComputeForwardMetrics(cb.Select(m => ForwardMove(m, 3)));
                Console.WriteLine($"    {date:yyyy-MM-dd} [{bucket}]: Pattern[n={pm.N} med={Fmt4(pm.Median)}%]  Control[n={cm.N} med={Fmt4(cm.Median)}%]");
            }
        }
    }
    Console.WriteLine();

    // ---- Experiment 7: reference the existing (unmodified) episode-level permutation result ----
    Console.WriteLine("### Experiment 7 -- existing permutation result (CONTEXT ONLY, not recomputed, not replaced) ###");
    Console.WriteLine("  See docs/VolumeCandle_0DTE_Findings.md's 'Episode-Level Validation' section, Part I: the episode-level permutation check (seed=42, 1000 permutations, unmodified procedure) placed Pattern A's observed median at percentile 0.0 and Pattern B's at percentile 100.0 of the permutation distribution at every horizon (+1/+3/+5/+10). Reported here as context only -- not recomputed, not replaced, per the task's explicit instruction.");
    Console.WriteLine();

    // ---- CSV: per-observation prior/forward movement for Pattern A/B and their controls ----
    using (var writer = new StreamWriter(cnOutPath))
    {
        writer.WriteLine("TradingDate,EventId,Group,Pattern,"
            + "Prior1Pct,Prior3Pct,Prior5Pct,Prior10Pct,Fwd1Pct,Fwd3Pct,Fwd5Pct,Fwd10Pct");
        foreach (var (label, members) in new[] { ("PatternA", patternAObs), ("ControlA", controlAObs), ("PatternB", patternBObs), ("ControlB", controlBObs) })
        {
            for (var i = 0; i < members.Count; i++)
            {
                var m = members[i];
                writer.WriteLine(string.Join(',',
                    m.Date.ToString("yyyy-MM-dd"), m.Row.EventId, label, m.Row.RelationshipCategory,
                    PriorMove(members, i, 1), PriorMove(members, i, 3), PriorMove(members, i, 5), PriorMove(members, i, 10),
                    ForwardMove(m, 1), ForwardMove(m, 3), ForwardMove(m, 5), ForwardMove(m, 10)));
            }
        }
    }
    Console.WriteLine($"Conditional-analysis CSV written to: {Path.GetFullPath(cnOutPath)}");

    return 0;
}

// "vc0dte-relationship-option-response" -- 2026-09-24 option-response validation (HARD FREEZE:
// same as every other vc0dte-relationship-* command). Behaviour only -- no entries, exits, P&L,
// bid/ask execution, stop loss, target, or threshold tuning anywhere in this command. Reuses
// UnderlyingOptionRelationshipRecorder.RecordAsync, every Compute*Change function,
// ComputeForwardMetrics/ComputeTerciles/ComputePercentiles, ConditionalMovementAnalysis's tercile
// classifier, and Vc0DteBehaviorSummary.SessionBucket completely unchanged. The SAME
// direction-constrained, tercile-matched control-group construction from the conditional-analysis
// task is reused as-is (not redefined).
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-option-response <fromDate> <toDate> --out=path.csv
if (args.Length > 0 && string.Equals(args[0], "vc0dte-relationship-option-response", StringComparison.OrdinalIgnoreCase))
{
    const long orThreshold = 1300L;
    int[] orForwardHorizons = [1, 3, 5, 10];
    int[] orTercileHorizons = [3, 5, 10];
    const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    const string PatternB = ForwardValidationAnalysis.State4_BearishDivergence_PatternB;
    string[] orExcludedCategories = ["OptionDataIncomplete", "ContractTransition"];
    var orIstOffset = TimeSpan.FromHours(5.5);

    var (orPositional, orNamed) = SplitNamedArgs(args);
    if (orPositional.Length < 3
        || !DateOnly.TryParseExact(orPositional[1], "yyyy-MM-dd", out var orFromDate)
        || !DateOnly.TryParseExact(orPositional[2], "yyyy-MM-dd", out var orToDate)
        || !orNamed.TryGetValue("out", out var orOutPath))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-option-response <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> --out=path.csv");
        return 1;
    }

    Console.WriteLine("=== vc0dte-relationship-option-response: HARD FREEZE, BEHAVIOUR ONLY -- does the relationship translate into the directionally appropriate option? No trading simulation. ===");
    Console.WriteLine();
    static string Fmt5(decimal? v) => v?.ToString("F4") ?? "--";

    var orDayRows = new List<(DateOnly Date, List<RelationshipObservation> Rows)>();
    for (var date = orFromDate; date <= orToDate; date = date.AddDays(1))
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
        var (orChain, orFutureBars, orOptionBars) = await LoadVc0DteDayAsync(date, orThreshold);
        if (orFutureBars.Count == 0 || orChain.Count == 0) { continue; }
        await using var orSource = new NiftySignalDbContext(tradeSourceOptions);
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(orSource, date, orChain, orFutureBars, orOptionBars, CancellationToken.None);
        orDayRows.Add((date, rows));
        Console.WriteLine($"  {date:yyyy-MM-dd}: {rows.Count} relationship observations loaded (reused, unmodified recorder).");
    }
    Console.WriteLine();

    var orAllRows = orDayRows.SelectMany(d => d.Rows.Select(r => (d.Date, d.Rows, Row: r))).ToList();
    var orUpPop = orAllRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Up && !orExcludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
    var orDownPop = orAllRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Down && !orExcludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
    var patternAObs = orUpPop.Where(x => x.Row.RelationshipCategory == PatternA).ToList();
    var controlAObs = orUpPop.Where(x => x.Row.RelationshipCategory != PatternA).ToList();
    var patternBObs = orDownPop.Where(x => x.Row.RelationshipCategory == PatternB).ToList();
    var controlBObs = orDownPop.Where(x => x.Row.RelationshipCategory != PatternB).ToList();

    Console.WriteLine($"Population sizes: PatternA={patternAObs.Count} (ControlA={controlAObs.Count}), PatternB={patternBObs.Count} (ControlB={controlBObs.Count})");
    Console.WriteLine();

    decimal? PriorMove(List<(DateOnly, List<RelationshipObservation> Rows, RelationshipObservation Row)> members, int i, int n)
        => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(members[i].Rows, members[i].Row.EventId - n, n).PercentChange;

    // ---- Section 2/8: exclusions + signalling option price level ----
    void ReportExclusionsAndPriceLevel(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> members, bool primaryIsPe)
    {
        var prices = members.Select(m => primaryIsPe ? m.Row.PeAverageLtp : m.Row.CeAverageLtp).Where(p => p is not null).Select(p => p!.Value).ToList();
        var p = ForensicValidationAnalysis.ComputePercentiles(prices);
        Console.WriteLine($"  [{label}] signalling {(primaryIsPe ? "PE" : "CE")} price: n={p.N} median={p.Median:F2} P25={p.P25:F2} P75={p.P75:F2}");
        foreach (var h in orForwardHorizons)
        {
            var changes = members.Select(m => primaryIsPe
                ? UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h)
                : UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h)).ToList();
            var reasons = changes.Select(OptionResponseAnalysis.ClassifyExclusionReason).ToList();
            var counts = reasons.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
            Console.WriteLine($"    +{h,2} coverage: Available={counts.GetValueOrDefault("Available")} ContractTransition={counts.GetValueOrDefault("ContractTransition")} MissingOrStaleData={counts.GetValueOrDefault("MissingOrStaleData")}");
        }
    }
    Console.WriteLine("### Section 2/8 -- exclusions and signalling option price level ###");
    ReportExclusionsAndPriceLevel("Pattern A (primary=PE)", patternAObs, primaryIsPe: true);
    ReportExclusionsAndPriceLevel("Pattern B (primary=CE)", patternBObs, primaryIsPe: false);
    Console.WriteLine();

    // ---- Sections 3/4/6: forward option response, primary + opposite, vs futures ----
    void ReportResponseTable(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> members, bool primaryIsPe)
    {
        Console.WriteLine($"[{label}] n={members.Count}, primary={(primaryIsPe ? "PE" : "CE")}, opposite={(primaryIsPe ? "CE" : "PE")}");
        Console.WriteLine("  Horizon | Futures median% | Primary median% (mean%, pos%/neg%) | Primary median Rs | Opposite median% | Opposite median Rs");
        foreach (var h in orForwardHorizons)
        {
            var fut = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange));
            var primaryPct = members.Select(m => primaryIsPe ? UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h) : UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h)).ToList();
            var oppositePct = members.Select(m => primaryIsPe ? UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h) : UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h)).ToList();
            var primaryM = ForwardValidationAnalysis.ComputeForwardMetrics(primaryPct.Select(c => c.PercentChange));
            var primaryAbsM = ForwardValidationAnalysis.ComputeForwardMetrics(primaryPct.Select(c => c.AbsoluteChange));
            var oppositeM = ForwardValidationAnalysis.ComputeForwardMetrics(oppositePct.Select(c => c.PercentChange));
            var oppositeAbsM = ForwardValidationAnalysis.ComputeForwardMetrics(oppositePct.Select(c => c.AbsoluteChange));
            Console.WriteLine($"  +{h,2}     | {Fmt5(fut.Median)}%          | n={primaryM.N} med={Fmt5(primaryM.Median)}% (mean={Fmt5(primaryM.Mean)}%, pos={primaryM.PositivePct:F1}%/neg={primaryM.NegativePct:F1}%) | Rs{Fmt5(primaryAbsM.Median)} | {Fmt5(oppositeM.Median)}% | Rs{Fmt5(oppositeAbsM.Median)}");
        }
        Console.WriteLine();
    }
    Console.WriteLine("### Sections 3/4/6 -- forward option response (primary + opposite) vs futures ###");
    ReportResponseTable("Pattern A", patternAObs, primaryIsPe: true);
    ReportResponseTable("Pattern B", patternBObs, primaryIsPe: false);

    // ---- Section 5: matched-control comparison, primary option, tercile-stratified ----
    Console.WriteLine("### Section 5 -- matched-control comparison (primary option, tercile-stratified, SAME control construction reused unchanged) ###");
    void PrintControlComparison(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> patternMembers, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> controlMembers, List<(DateOnly, List<RelationshipObservation> Rows, RelationshipObservation Row)> direction, bool primaryIsPe)
    {
        foreach (var n in orTercileHorizons)
        {
            var dirPriorAbs = Enumerable.Range(0, direction.Count).Select(i => PriorMove(direction, i, n)).Where(v => v is not null).Select(v => Math.Abs(v!.Value)).ToList();
            var (low33, high67) = ForwardValidationAnalysis.ComputeTerciles(dirPriorAbs);
            Console.WriteLine($"  [Prior -{n} terciles: Low33={low33:F4}%, High67={high67:F4}%]");
            foreach (var bucket in new[] { "Low", "Mid", "High" })
            {
                List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> Bucketed(List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> src)
                    => Enumerable.Range(0, src.Count).Where(i => { var p = PriorMove(src, i, n); return p is not null && ConditionalMovementAnalysis.ClassifyTercileBucket(Math.Abs(p.Value), low33, high67) == bucket; }).Select(i => src[i]).ToList();

                var patternBucket = Bucketed(patternMembers);
                var controlBucket = Bucketed(controlMembers);
                var cells = orForwardHorizons.Select(h =>
                {
                    var pm = ForwardValidationAnalysis.ComputeForwardMetrics(patternBucket.Select(m => (primaryIsPe ? UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h) : UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h)).PercentChange));
                    var cm = ForwardValidationAnalysis.ComputeForwardMetrics(controlBucket.Select(m => (primaryIsPe ? UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h) : UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h)).PercentChange));
                    return $"+{h}: Pattern[n={pm.N} med={Fmt5(pm.Median)}%] Ctrl[n={cm.N} med={Fmt5(cm.Median)}%]";
                });
                Console.WriteLine($"    [{bucket}] {string.Join("  ", cells)}");
            }
        }
    }
    Console.WriteLine("[Pattern A vs Control A -- primary option PE]");
    PrintControlComparison("A", patternAObs, controlAObs, orUpPop, primaryIsPe: true);
    Console.WriteLine("[Pattern B vs Control B -- primary option CE]");
    PrintControlComparison("B", patternBObs, controlBObs, orDownPop, primaryIsPe: false);
    Console.WriteLine();

    // ---- Section 9: session-bucket breakdown (diagnostic only, 0-DTE decay context) ----
    Console.WriteLine("### Section 9 -- session-bucket breakdown (diagnostic: is this dominated by 0-DTE decay timing?) ###");
    foreach (var (label, members, primaryIsPe) in new[] { ("Pattern A (PE)", patternAObs, true), ("Pattern B (CE)", patternBObs, false) })
    {
        Console.WriteLine($"[{label}]");
        foreach (var bucketGroup in members.GroupBy(m => Vc0DteBehaviorSummary.SessionBucket(m.Row.StartTimestamp, orIstOffset)).OrderBy(g => g.Key))
        {
            var bm = bucketGroup.ToList();
            var m3 = ForwardValidationAnalysis.ComputeForwardMetrics(bm.Select(m => (primaryIsPe ? UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, 3) : UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, 3)).PercentChange));
            Console.WriteLine($"    [{bucketGroup.Key}] n={bm.Count} primaryMedFwd+3={Fmt5(m3.Median)}% pos={m3.PositivePct:F1}% neg={m3.NegativePct:F1}%");
        }
    }
    Console.WriteLine();

    // ---- Section 10: day-by-day ----
    Console.WriteLine("### Section 10 -- day-by-day (only 3 independent sessions -- not treated as independent trading days beyond that) ###");
    foreach (var (label, members, primaryIsPe) in new[] { ("Pattern A (PE)", patternAObs, true), ("Pattern B (CE)", patternBObs, false) })
    {
        Console.WriteLine($"[{label}]");
        foreach (var (date, _) in orDayRows)
        {
            var dm = members.Where(m => m.Date == date).ToList();
            if (dm.Count == 0) { Console.WriteLine($"    {date:yyyy-MM-dd}: 0 observations."); continue; }
            var line = $"    {date:yyyy-MM-dd}: n={dm.Count}";
            foreach (var h in orForwardHorizons)
            {
                var futM = ForwardValidationAnalysis.ComputeForwardMetrics(dm.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange));
                var optM = ForwardValidationAnalysis.ComputeForwardMetrics(dm.Select(m => (primaryIsPe ? UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h) : UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h)).PercentChange));
                line += $" | +{h}: Fut={Fmt5(futM.Median)}% Opt={Fmt5(optM.Median)}%(pos={optM.PositivePct:F0}%)";
            }
            Console.WriteLine(line);
        }
    }
    Console.WriteLine();

    // ---- CSV ----
    using (var writer = new StreamWriter(orOutPath))
    {
        writer.WriteLine("TradingDate,EventId,Group,Pattern,PrimarySide,SignalPrimaryPrice,"
            + "FutFwd1Pct,FutFwd3Pct,FutFwd5Pct,FutFwd10Pct,"
            + "PrimaryFwd1Pct,PrimaryFwd3Pct,PrimaryFwd5Pct,PrimaryFwd10Pct,"
            + "PrimaryFwd1Rs,PrimaryFwd3Rs,PrimaryFwd5Rs,PrimaryFwd10Rs,"
            + "OppositeFwd1Pct,OppositeFwd3Pct,OppositeFwd5Pct,OppositeFwd10Pct");
        foreach (var (label, members, primaryIsPe) in new[] { ("PatternA", patternAObs, true), ("ControlA", controlAObs, true), ("PatternB", patternBObs, false), ("ControlB", controlBObs, false) })
        {
            foreach (var m in members)
            {
                var signalPrice = primaryIsPe ? m.Row.PeAverageLtp : m.Row.CeAverageLtp;
                decimal? FutH(int h) => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange;
                var primary = orForwardHorizons.Select(h => primaryIsPe ? UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h) : UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h)).ToList();
                var opposite = orForwardHorizons.Select(h => primaryIsPe ? UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h) : UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h)).ToList();
                writer.WriteLine(string.Join(',',
                    m.Date.ToString("yyyy-MM-dd"), m.Row.EventId, label, m.Row.RelationshipCategory, primaryIsPe ? "PE" : "CE", signalPrice,
                    FutH(1), FutH(3), FutH(5), FutH(10),
                    primary[0].PercentChange, primary[1].PercentChange, primary[2].PercentChange, primary[3].PercentChange,
                    primary[0].AbsoluteChange, primary[1].AbsoluteChange, primary[2].AbsoluteChange, primary[3].AbsoluteChange,
                    opposite[0].PercentChange, opposite[1].PercentChange, opposite[2].PercentChange, opposite[3].PercentChange));
            }
        }
    }
    Console.WriteLine($"Option-response CSV written to: {Path.GetFullPath(orOutPath)}");

    return 0;
}

// "vc0dte-relationship-decomposition" -- 2026-09-24 option-response decomposition (HARD FREEZE:
// same as every other vc0dte-relationship-* command). Diagnostic/economic decomposition only --
// no IV, no Greeks, no Black-Scholes, no trading simulation. Reuses
// UnderlyingOptionRelationshipRecorder.RecordAsync, every Compute*Change function,
// ComputeForwardMetrics/ComputePercentiles, ConditionalMovementAnalysis's tercile classifier, and
// Vc0DteBehaviorSummary.SessionBucket completely unchanged. Signalling strike is frozen at the
// signal event (RelationshipObservation.AtmStrike at T), never re-derived at a later event.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-decomposition <fromDate> <toDate> --out=path.csv
if (args.Length > 0 && string.Equals(args[0], "vc0dte-relationship-decomposition", StringComparison.OrdinalIgnoreCase))
{
    const long dcThreshold = 1300L;
    int[] dcForwardHorizons = [1, 3, 5, 10];
    const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    const string PatternB = ForwardValidationAnalysis.State4_BearishDivergence_PatternB;
    string[] dcExcludedCategories = ["OptionDataIncomplete", "ContractTransition"];
    var dcIstOffset = TimeSpan.FromHours(5.5);

    var (dcPositional, dcNamed) = SplitNamedArgs(args);
    if (dcPositional.Length < 3
        || !DateOnly.TryParseExact(dcPositional[1], "yyyy-MM-dd", out var dcFromDate)
        || !DateOnly.TryParseExact(dcPositional[2], "yyyy-MM-dd", out var dcToDate)
        || !dcNamed.TryGetValue("out", out var dcOutPath))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-decomposition <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> --out=path.csv");
        return 1;
    }

    Console.WriteLine("=== vc0dte-relationship-decomposition: HARD FREEZE, DIAGNOSTIC ONLY -- intrinsic/extrinsic decomposition, no IV/Greeks/trading simulation ===");
    Console.WriteLine();
    static string Fmt6(decimal? v) => v?.ToString("F4") ?? "--";

    var dcDayRows = new List<(DateOnly Date, List<RelationshipObservation> Rows)>();
    for (var date = dcFromDate; date <= dcToDate; date = date.AddDays(1))
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
        var (dcChain, dcFutureBars, dcOptionBars) = await LoadVc0DteDayAsync(date, dcThreshold);
        if (dcFutureBars.Count == 0 || dcChain.Count == 0) { continue; }
        await using var dcSource = new NiftySignalDbContext(tradeSourceOptions);
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(dcSource, date, dcChain, dcFutureBars, dcOptionBars, CancellationToken.None);
        dcDayRows.Add((date, rows));
        Console.WriteLine($"  {date:yyyy-MM-dd}: {rows.Count} relationship observations loaded (reused, unmodified recorder).");
    }
    Console.WriteLine();

    var dcAllRows = dcDayRows.SelectMany(d => d.Rows.Select(r => (d.Date, d.Rows, Row: r))).ToList();
    var dcUpPop = dcAllRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Up && !dcExcludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
    var dcDownPop = dcAllRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Down && !dcExcludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
    var patternAObs = dcUpPop.Where(x => x.Row.RelationshipCategory == PatternA).ToList();
    var controlAObs = dcUpPop.Where(x => x.Row.RelationshipCategory != PatternA).ToList();
    var patternBObs = dcDownPop.Where(x => x.Row.RelationshipCategory == PatternB).ToList();
    var controlBObs = dcDownPop.Where(x => x.Row.RelationshipCategory != PatternB).ToList();
    Console.WriteLine($"Population sizes: PatternA={patternAObs.Count} (ControlA={controlAObs.Count}), PatternB={patternBObs.Count} (ControlB={controlBObs.Count})");
    Console.WriteLine();

    // One record per (observation, horizon): signal price/intrinsic/extrinsic + forward
    // price/intrinsic/extrinsic for the given side, null when the forward option value can't be
    // measured reliably (missing/stale/contract-transition -- reuses ComputeCeChange/ComputePeChange's
    // own SameContract/availability flags, never re-derived).
    (decimal? OptionChange, decimal? IntrinsicChange, decimal? ExtrinsicChange, decimal? SignalPrice, decimal SignalIntrinsic, decimal SignalExtrinsic)?
        Decompose((DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row) m, int h, OptionType side)
    {
        var price = side == OptionType.Call ? m.Row.CeAverageLtp : m.Row.PeAverageLtp;
        if (price is null)
        {
            return null;
        }
        var strike = m.Row.AtmStrike; // frozen at the signal event -- never re-derived at T+h.
        var signalIntrinsic = OptionValueDecomposition.ComputeIntrinsic(m.Row.FuturesClose, strike, side);
        var signalExtrinsic = OptionValueDecomposition.ComputeExtrinsic(price.Value, signalIntrinsic);

        var change = side == OptionType.Call
            ? UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h)
            : UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h);
        if (change.AbsoluteChange is null)
        {
            return (null, null, null, price, signalIntrinsic, signalExtrinsic);
        }

        var endIdx = m.Row.EventId + h;
        var forwardFutures = m.Rows[endIdx].FuturesClose; // futures is never missing -- always safe to read directly.
        var forwardPrice = price.Value + change.AbsoluteChange.Value;
        var forwardIntrinsic = OptionValueDecomposition.ComputeIntrinsic(forwardFutures, strike, side);
        var forwardExtrinsic = OptionValueDecomposition.ComputeExtrinsic(forwardPrice, forwardIntrinsic);

        return (change.AbsoluteChange, forwardIntrinsic - signalIntrinsic, forwardExtrinsic - signalExtrinsic, price, signalIntrinsic, signalExtrinsic);
    }

    // ---- Sections 3/4: intrinsic/extrinsic decomposition for the directionally appropriate option ----
    void ReportDecomposition(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> members, OptionType side)
    {
        Console.WriteLine($"[{label}] n={members.Count}, side={side}");
        foreach (var h in dcForwardHorizons)
        {
            var results = members.Select(m => Decompose(m, h, side)).Where(r => r is not null).Select(r => r!.Value).ToList();
            var withForward = results.Where(r => r.OptionChange is not null).ToList();
            var optM = ForwardValidationAnalysis.ComputeForwardMetrics(withForward.Select(r => r.OptionChange));
            var intM = ForwardValidationAnalysis.ComputeForwardMetrics(withForward.Select(r => r.IntrinsicChange));
            var extM = ForwardValidationAnalysis.ComputeForwardMetrics(withForward.Select(r => r.ExtrinsicChange));
            // Reconciliation is an exact algebraic identity by construction (see OptionValueDecomposition's
            // own doc comment) -- verified here anyway, on real data, rather than merely asserted.
            var maxDiscrepancy = withForward.Count > 0 ? withForward.Max(r => Math.Abs(r.OptionChange!.Value - (r.IntrinsicChange!.Value + r.ExtrinsicChange!.Value))) : 0m;
            Console.WriteLine($"  +{h,2}: n={optM.N} OptionRs[mean={Fmt6(optM.Mean)} med={Fmt6(optM.Median)}] IntrinsicRs[mean={Fmt6(intM.Mean)} med={Fmt6(intM.Median)}] ExtrinsicRs[mean={Fmt6(extM.Mean)} med={Fmt6(extM.Median)}] maxReconciliationDiscrepancy={maxDiscrepancy}");
        }
        Console.WriteLine();
    }
    Console.WriteLine("### Sections 3/4 -- intrinsic/extrinsic decomposition, directionally appropriate option ###");
    ReportDecomposition("Pattern A -> PE", patternAObs, OptionType.Put);
    ReportDecomposition("Pattern B -> CE", patternBObs, OptionType.Call);
    Console.WriteLine("### (opposite-side option, for completeness) ###");
    ReportDecomposition("Pattern A -> CE (opposite)", patternAObs, OptionType.Call);
    ReportDecomposition("Pattern B -> PE (opposite)", patternBObs, OptionType.Put);

    // ---- Section 5: price-level diagnostics ----
    Console.WriteLine("### Section 5 -- signalling option price-level diagnostics ###");
    void ReportPriceLevel(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> members, OptionType side)
    {
        var prices = members.Select(m => side == OptionType.Call ? m.Row.CeAverageLtp : m.Row.PeAverageLtp).Where(p => p is not null).Select(p => p!.Value).ToList();
        var p = ForensicValidationAnalysis.ComputePercentiles(prices);
        var cheapCount = prices.Count(v => v < 20m);
        Console.WriteLine($"  [{label}] n={p.N} median={p.Median:F2} P25={p.P25:F2} P75={p.P75:F2} min={p.Min:F2} max={p.Max:F2} | <Rs20: {cheapCount} ({100.0 * cheapCount / Math.Max(1, p.N):F1}%)");
    }
    ReportPriceLevel("Pattern A PE", patternAObs, OptionType.Put);
    ReportPriceLevel("Pattern B CE", patternBObs, OptionType.Call);
    Console.WriteLine();

    // ---- Section 6: moneyness diagnostics ----
    Console.WriteLine("### Section 6 -- moneyness diagnostics (signed, futures-strike / strike-futures; unclipped) ###");
    void ReportMoneyness(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> members, OptionType side)
    {
        var moneyness = members.Select(m => OptionValueDecomposition.ComputeMoneyness(m.Row.FuturesClose, m.Row.AtmStrike, side)).ToList();
        var p = ForensicValidationAnalysis.ComputePercentiles(moneyness);
        Console.WriteLine($"  [{label}] n={p.N} median={p.Median:F2} P25={p.P25:F2} P75={p.P75:F2} min={p.Min:F2} max={p.Max:F2}");
    }
    ReportMoneyness("Pattern A PE", patternAObs, OptionType.Put);
    ReportMoneyness("Pattern B CE", patternBObs, OptionType.Call);
    Console.WriteLine();

    // ---- Section 7: underlying vs intrinsic vs extrinsic vs total option (the key diagnostic) ----
    Console.WriteLine("### Section 7 -- KEY DIAGNOSTIC: underlying vs intrinsic vs extrinsic vs total option movement ###");
    Console.WriteLine("Pattern | Horizon | Futures med% | Intrinsic medRs | Extrinsic medRs | Total Option medRs");
    void ReportKeyDiagnostic(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> members, OptionType side)
    {
        foreach (var h in dcForwardHorizons)
        {
            var futM = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange));
            var results = members.Select(m => Decompose(m, h, side)).Where(r => r is not null).Select(r => r!.Value).Where(r => r.OptionChange is not null).ToList();
            var intM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.IntrinsicChange));
            var extM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.ExtrinsicChange));
            var optM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.OptionChange));
            Console.WriteLine($"{label,-8}| +{h,2}     | {Fmt6(futM.Median)}%      | {Fmt6(intM.Median)}          | {Fmt6(extM.Median)}          | {Fmt6(optM.Median)}");
        }
    }
    ReportKeyDiagnostic("A->PE", patternAObs, OptionType.Put);
    ReportKeyDiagnostic("B->CE", patternBObs, OptionType.Call);
    Console.WriteLine();

    // ---- Section 8: session-bucket diagnostics ----
    Console.WriteLine("### Section 8 -- session-bucket diagnostics (total/intrinsic/extrinsic change, +3 horizon representative) ###");
    void ReportSessionBuckets(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> members, OptionType side)
    {
        Console.WriteLine($"[{label}]");
        foreach (var bucketGroup in members.GroupBy(m => Vc0DteBehaviorSummary.SessionBucket(m.Row.StartTimestamp, dcIstOffset)).OrderBy(g => g.Key))
        {
            var bm = bucketGroup.ToList();
            var results = bm.Select(m => Decompose(m, 3, side)).Where(r => r is not null).Select(r => r!.Value).Where(r => r.OptionChange is not null).ToList();
            var intM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.IntrinsicChange));
            var extM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.ExtrinsicChange));
            var optM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.OptionChange));
            Console.WriteLine($"    [{bucketGroup.Key}] n={optM.N} TotalRs={Fmt6(optM.Median)} IntrinsicRs={Fmt6(intM.Median)} ExtrinsicRs={Fmt6(extM.Median)}");
        }
    }
    ReportSessionBuckets("Pattern A -> PE", patternAObs, OptionType.Put);
    ReportSessionBuckets("Pattern B -> CE", patternBObs, OptionType.Call);
    Console.WriteLine();

    // ---- Section 9: matched-control comparison ----
    Console.WriteLine("### Section 9 -- matched-control comparison (SAME control populations reused unchanged, no new definition) ###");
    void ReportControlComparison(string label, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> patternMembers, List<(DateOnly Date, List<RelationshipObservation> Rows, RelationshipObservation Row)> controlMembers, OptionType side)
    {
        Console.WriteLine($"[{label}]");
        foreach (var h in dcForwardHorizons)
        {
            var pResults = patternMembers.Select(m => Decompose(m, h, side)).Where(r => r is not null).Select(r => r!.Value).Where(r => r.OptionChange is not null).ToList();
            var cResults = controlMembers.Select(m => Decompose(m, h, side)).Where(r => r is not null).Select(r => r!.Value).Where(r => r.OptionChange is not null).ToList();
            var pOpt = ForwardValidationAnalysis.ComputeForwardMetrics(pResults.Select(r => r.OptionChange));
            var cOpt = ForwardValidationAnalysis.ComputeForwardMetrics(cResults.Select(r => r.OptionChange));
            var pInt = ForwardValidationAnalysis.ComputeForwardMetrics(pResults.Select(r => r.IntrinsicChange));
            var cInt = ForwardValidationAnalysis.ComputeForwardMetrics(cResults.Select(r => r.IntrinsicChange));
            var pExt = ForwardValidationAnalysis.ComputeForwardMetrics(pResults.Select(r => r.ExtrinsicChange));
            var cExt = ForwardValidationAnalysis.ComputeForwardMetrics(cResults.Select(r => r.ExtrinsicChange));
            Console.WriteLine($"  +{h,2}: Total Pattern[n={pOpt.N} med={Fmt6(pOpt.Median)}] Ctrl[n={cOpt.N} med={Fmt6(cOpt.Median)}] | Intrinsic Pattern[med={Fmt6(pInt.Median)}] Ctrl[med={Fmt6(cInt.Median)}] | Extrinsic Pattern[med={Fmt6(pExt.Median)}] Ctrl[med={Fmt6(cExt.Median)}]");
        }
    }
    ReportControlComparison("Pattern A -> PE vs Control A -> PE", patternAObs, controlAObs, OptionType.Put);
    ReportControlComparison("Pattern B -> CE vs Control B -> CE", patternBObs, controlBObs, OptionType.Call);
    Console.WriteLine();

    // ---- CSV ----
    using (var writer = new StreamWriter(dcOutPath))
    {
        writer.WriteLine("TradingDate,EventId,Pattern,Side,SignalPrice,SignalIntrinsic,SignalExtrinsic,SignalMoneyness,"
            + "OptionChange1,IntrinsicChange1,ExtrinsicChange1,OptionChange3,IntrinsicChange3,ExtrinsicChange3,"
            + "OptionChange5,IntrinsicChange5,ExtrinsicChange5,OptionChange10,IntrinsicChange10,ExtrinsicChange10");
        foreach (var (label, members, side) in new[] { ("PatternA", patternAObs, OptionType.Put), ("PatternB", patternBObs, OptionType.Call) })
        {
            foreach (var m in members)
            {
                var moneyness = OptionValueDecomposition.ComputeMoneyness(m.Row.FuturesClose, m.Row.AtmStrike, side);
                var d1 = Decompose(m, 1, side); var d3 = Decompose(m, 3, side); var d5 = Decompose(m, 5, side); var d10 = Decompose(m, 10, side);
                writer.WriteLine(string.Join(',',
                    m.Date.ToString("yyyy-MM-dd"), m.Row.EventId, label, side,
                    d1?.SignalPrice, d1?.SignalIntrinsic, d1?.SignalExtrinsic, moneyness,
                    d1?.OptionChange, d1?.IntrinsicChange, d1?.ExtrinsicChange,
                    d3?.OptionChange, d3?.IntrinsicChange, d3?.ExtrinsicChange,
                    d5?.OptionChange, d5?.IntrinsicChange, d5?.ExtrinsicChange,
                    d10?.OptionChange, d10?.IntrinsicChange, d10?.ExtrinsicChange));
            }
        }
    }
    Console.WriteLine($"Decomposition CSV written to: {Path.GetFullPath(dcOutPath)}");

    return 0;
}

// "vc-dte-availability-scan" -- 2026-09-24, cheap existence-only scan (Instrument rows only, no
// bar-building) to determine which (calendar date, option expiry) pairs -- i.e. which DTE
// buckets -- have any NIFTY option chain listed at all, ahead of the DTE-expansion experiment.
// Read-only diagnostic; does not build or reuse any relationship methodology.
//   dotnet run --project NiftySignal.VolumeBarData -- vc-dte-availability-scan <fromDate> <toDate>
if (args.Length > 0 && string.Equals(args[0], "vc-dte-availability-scan", StringComparison.OrdinalIgnoreCase))
{
    var (dasPositional, dasNamed) = SplitNamedArgs(args);
    if (dasPositional.Length < 3
        || !DateOnly.TryParseExact(dasPositional[1], "yyyy-MM-dd", out var dasFromDate)
        || !DateOnly.TryParseExact(dasPositional[2], "yyyy-MM-dd", out var dasToDate))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc-dte-availability-scan <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--underlying=NIFTY]");
        return 1;
    }
    var dasUnderlying = dasNamed.TryGetValue("underlying", out var dasU) ? dasU : "NIFTY";

    await using var dasSource = new NiftySignalDbContext(tradeSourceOptions);
    for (var date = dasFromDate; date <= dasToDate; date = date.AddDays(1))
    {
        var hasFutures = await dasSource.Instruments.AnyAsync(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Future && i.Underlying == dasUnderlying);
        if (!hasFutures)
        {
            Console.WriteLine($"{date:yyyy-MM-dd} ({date.DayOfWeek}): no futures instrument -- no data at all this day, skipped.");
            continue;
        }

        var expiriesWithCounts = await dasSource.Instruments
            .Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.Underlying == dasUnderlying)
            .GroupBy(i => i.ExpiryDate)
            .Select(g => new { Expiry = g.Key, Count = g.Count(), Tokens = g.Select(x => x.Token).ToList() })
            .OrderBy(g => g.Expiry)
            .ToListAsync();

        if (expiriesWithCounts.Count == 0)
        {
            Console.WriteLine($"{date:yyyy-MM-dd} ({date.DayOfWeek}): hasFutures=True, but NO option chain instrument rows at all.");
            continue;
        }

        var parts = new List<string>();
        foreach (var e in expiriesWithCounts)
        {
            // Real tick presence, not just instrument-row existence -- sampled on a handful of
            // strikes near the middle of the chain (cheapest possible signal that ticks exist at
            // all for this (date, expiry) pair, without building full bars).
            var sampleTokens = e.Tokens.Skip(e.Tokens.Count / 2).Take(3).ToList();
            var dayStartUtc = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUniversalTime();
            var dayEndUtc = dayStartUtc.AddDays(1);
            var tickCount = await dasSource.Ticks.CountAsync(t => sampleTokens.Contains(t.Token) && t.ExchangeTimestamp >= dayStartUtc && t.ExchangeTimestamp < dayEndUtc);
            parts.Add($"expiry={e.Expiry:yyyy-MM-dd}(DTE={(e.Expiry!.Value.DayNumber - date.DayNumber)},n={e.Count},sampleTicks={tickCount})");
        }
        Console.WriteLine($"{date:yyyy-MM-dd} ({date.DayOfWeek}): hasFutures=True, chains: {string.Join("; ", parts)}");
    }

    return 0;
}

// "vc-underlying-inventory" -- 2026-09-24, cross-index validation pre-check. Cheap, read-only
// inventory of every distinct Underlying/InstrumentType combination in the trade-source database,
// with each one's date range and row count -- used to determine exactly what Sensex data (if any)
// exists before attempting to map the frozen Nifty methodology onto it.
//   dotnet run --project NiftySignal.VolumeBarData -- vc-underlying-inventory
if (args.Length > 0 && string.Equals(args[0], "vc-underlying-inventory", StringComparison.OrdinalIgnoreCase))
{
    await using var uiSource = new NiftySignalDbContext(tradeSourceOptions);
    var groups = await uiSource.Instruments
        .GroupBy(i => new { i.Underlying, i.InstrumentType })
        .Select(g => new { g.Key.Underlying, g.Key.InstrumentType, Count = g.Count(), MinDate = g.Min(i => i.AsOfDate), MaxDate = g.Max(i => i.AsOfDate) })
        .OrderBy(g => g.Underlying).ThenBy(g => g.InstrumentType)
        .ToListAsync();
    foreach (var g in groups)
    {
        Console.WriteLine($"Underlying={g.Underlying,-12} Type={g.InstrumentType,-10} rows={g.Count,-6} dateRange={g.MinDate:yyyy-MM-dd}..{g.MaxDate:yyyy-MM-dd}");
    }

    var (_, uiNamed) = SplitNamedArgs(args);
    if (uiNamed.TryGetValue("underlying", out var uiUnderlying))
    {
        var strikes = await uiSource.Instruments
            .Where(i => i.Underlying == uiUnderlying && i.InstrumentType == InstrumentType.Option)
            .Select(i => i.StrikePrice)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync();
        Console.WriteLine($"Distinct strikes for {uiUnderlying}: {string.Join(",", strikes)}");
        var futuresCloses = await uiSource.Instruments.Where(i => i.Underlying == uiUnderlying && i.InstrumentType == InstrumentType.Future).Select(i => new { i.AsOfDate, i.Token }).ToListAsync();
        Console.WriteLine($"{uiUnderlying} future instrument rows: {string.Join("; ", futuresCloses.Select(f => $"{f.AsOfDate:yyyy-MM-dd}:{f.Token}"))}");
    }
    return 0;
}

// "vc0dte-relationship-dte-expansion" -- 2026-09-24 DTE-expansion experiment (HARD FREEZE: same
// event-bar/threshold/Pattern-A-B/dynamic-ATM/crossover/control-group/forward-horizon/stale-
// missing-data methodology as every other vc0dte-relationship-* command -- NOT touched). Only
// new piece: reuses <see cref="SynchronizedOptionBarBuilder.BuildDayForChainAsync"/> (added this
// task as a pure additive sibling of the frozen 0-DTE-only BuildDayAsync) to build the SAME kind
// of synchronized option bars against a DIFFERENT expiry's chain, then feeds them into the
// completely unmodified UnderlyingOptionRelationshipRecorder.RecordAsync. No trading simulation,
// no optimization of DTE cutoffs (bucket boundaries fixed by DteBucketClassifier, chosen from the
// observed calendar structure, not fit to any result), no IV/Greeks/theoretical pricing.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-dte-expansion <fromDate> <toDate> --out=path.csv
if (args.Length > 0 && string.Equals(args[0], "vc0dte-relationship-dte-expansion", StringComparison.OrdinalIgnoreCase))
{
    const long deThreshold = 1300L;
    int[] deForwardHorizons = [1, 3, 5, 10];
    int[] dePriorHorizons = [3, 5, 10];
    const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    const string PatternB = ForwardValidationAnalysis.State4_BearishDivergence_PatternB;
    string[] deExcludedCategories = ["OptionDataIncomplete", "ContractTransition"];
    string[] deBucketOrder = ["0", "1", "4-6", "7-8", "11-13"];

    var (dePositional, deNamed) = SplitNamedArgs(args);
    if (dePositional.Length < 3
        || !DateOnly.TryParseExact(dePositional[1], "yyyy-MM-dd", out var deFromDate)
        || !DateOnly.TryParseExact(dePositional[2], "yyyy-MM-dd", out var deToDate)
        || !deNamed.TryGetValue("out", out var deOutPath))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-dte-expansion <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> --out=path.csv");
        return 1;
    }

    Console.WriteLine("=== vc0dte-relationship-dte-expansion: HARD FREEZE, VALIDATION ONLY -- does Pattern A/B survive outside 0-DTE? No trading simulation, no threshold optimization, no IV/Greeks. ===");
    Console.WriteLine();
    static string Fmt4(decimal? v) => v?.ToString("F4") ?? "--";

    // ---- Phase 1: discover real (date, expiry) pairs -- cheap existence + tick-presence scan, same as vc-dte-availability-scan ----
    Console.WriteLine("### Phase 1 -- data-availability scan (exact dates/DTE distribution used, no fabrication) ###");
    var dePairs = new List<(DateOnly Date, DateOnly Expiry, int Dte, string Bucket)>();
    await using (var deScanSource = new NiftySignalDbContext(tradeSourceOptions))
    {
        for (var date = deFromDate; date <= deToDate; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
            var hasFutures = await deScanSource.Instruments.AnyAsync(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Future && i.Underlying == "NIFTY");
            if (!hasFutures) { continue; }

            var expiries = await deScanSource.Instruments
                .Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.Underlying == "NIFTY")
                .Select(i => i.ExpiryDate)
                .Distinct()
                .OrderBy(e => e)
                .ToListAsync();

            foreach (var expiry in expiries)
            {
                if (expiry is null) { continue; }
                var dte = expiry.Value.DayNumber - date.DayNumber;
                var bucket = DteBucketClassifier.Classify(dte);
                if (bucket == DteBucketClassifier.Other)
                {
                    Console.WriteLine($"  {date:yyyy-MM-dd}: expiry={expiry:yyyy-MM-dd} DTE={dte} -- not in a defined bucket, excluded (not forced).");
                    continue;
                }
                dePairs.Add((date, expiry.Value, dte, bucket));
                Console.WriteLine($"  {date:yyyy-MM-dd}: expiry={expiry:yyyy-MM-dd} DTE={dte} bucket={bucket}");
            }
        }
    }
    Console.WriteLine();

    foreach (var bucket in deBucketOrder)
    {
        var bp = dePairs.Where(p => p.Bucket == bucket).ToList();
        var sessions = bp.Select(p => p.Date).Distinct().OrderBy(d => d).ToList();
        Console.WriteLine($"  Bucket [{bucket}]: {bp.Count} (date,expiry) pairs across {sessions.Count} distinct calendar sessions: {string.Join(", ", sessions.Select(d => d.ToString("yyyy-MM-dd")))}{(sessions.Count < 2 ? "  <-- FLAGGED: fewer than 2 independent sessions, insufficient for generalization" : "")}");
    }
    Console.WriteLine();

    if (dePairs.Count == 0)
    {
        Console.WriteLine("No usable (date, expiry) pairs found in the requested range -- stopping. No fabricated methodology.");
        return 1;
    }

    // ---- Phase 2: build relationship rows per (date, expiry) pair, reusing the frozen methodology ----
    Console.WriteLine("### Phase 2 -- building relationship observations per (date, expiry) pair (frozen methodology, unchanged) ###");
    var pairRows = new List<(DateOnly Date, DateOnly Expiry, int Dte, string Bucket, List<RelationshipObservation> Rows)>();
    foreach (var dateGroup in dePairs.GroupBy(p => p.Date).OrderBy(g => g.Key))
    {
        await using var deSource = new NiftySignalDbContext(tradeSourceOptions);
        var futureBars = await FutureEventBarBuilder.BuildDayAsync(deSource, dateGroup.Key, deThreshold, CancellationToken.None);
        if (futureBars.Count == 0) { continue; }

        foreach (var p in dateGroup)
        {
            var chain = await deSource.Instruments
                .Where(i => i.AsOfDate == p.Date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate == p.Expiry && i.Underlying == "NIFTY")
                .OrderBy(i => i.StrikePrice).ThenBy(i => i.OptionType)
                .ToListAsync();
            if (chain.Count == 0) { continue; }

            var optionBars = await SynchronizedOptionBarBuilder.BuildDayForChainAsync(deSource, p.Date, chain, futureBars, CancellationToken.None);
            var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(deSource, p.Date, chain, futureBars, optionBars, CancellationToken.None);
            pairRows.Add((p.Date, p.Expiry, p.Dte, p.Bucket, rows));
            Console.WriteLine($"  {p.Date:yyyy-MM-dd} expiry={p.Expiry:yyyy-MM-dd} (DTE={p.Dte}, bucket={p.Bucket}): {rows.Count} relationship observations.");
        }
    }
    Console.WriteLine();

    decimal? PriorMove(List<(DateOnly Date, DateOnly Expiry, List<RelationshipObservation> Rows, RelationshipObservation Row)> members, int i, int n)
        => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(members[i].Rows, members[i].Row.EventId - n, n).PercentChange;
    decimal? ForwardMove((DateOnly Date, DateOnly Expiry, List<RelationshipObservation> Rows, RelationshipObservation Row) m, int h)
        => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(m.Rows, m.Row.EventId, h).PercentChange;

    (decimal? OptionChange, decimal? IntrinsicChange, decimal? ExtrinsicChange)? Decompose(
        (DateOnly Date, DateOnly Expiry, List<RelationshipObservation> Rows, RelationshipObservation Row) m, int h, OptionType side)
    {
        var price = side == OptionType.Call ? m.Row.CeAverageLtp : m.Row.PeAverageLtp;
        if (price is null) { return null; }
        var strike = m.Row.AtmStrike;
        var signalIntrinsic = OptionValueDecomposition.ComputeIntrinsic(m.Row.FuturesClose, strike, side);
        var change = side == OptionType.Call
            ? UnderlyingOptionRelationshipSummary.ComputeCeChange(m.Rows, m.Row.EventId, h)
            : UnderlyingOptionRelationshipSummary.ComputePeChange(m.Rows, m.Row.EventId, h);
        if (change.AbsoluteChange is null) { return (null, null, null); }
        var endIdx = m.Row.EventId + h;
        var forwardFutures = m.Rows[endIdx].FuturesClose;
        var forwardPrice = price.Value + change.AbsoluteChange.Value;
        var signalExtrinsic = OptionValueDecomposition.ComputeExtrinsic(price.Value, signalIntrinsic);
        var forwardIntrinsic = OptionValueDecomposition.ComputeIntrinsic(forwardFutures, strike, side);
        var forwardExtrinsic = OptionValueDecomposition.ComputeExtrinsic(forwardPrice, forwardIntrinsic);
        return (change.AbsoluteChange, forwardIntrinsic - signalIntrinsic, forwardExtrinsic - signalExtrinsic);
    }

    var deCsvRows = new List<string>();

    foreach (var bucket in deBucketOrder)
    {
        var bucketPairs = pairRows.Where(p => p.Bucket == bucket).ToList();
        var sessionDates = bucketPairs.Select(p => p.Date).Distinct().ToList();
        if (bucketPairs.Count == 0)
        {
            Console.WriteLine($"### Bucket [{bucket}] -- NO DATA, skipped ###");
            Console.WriteLine();
            continue;
        }

        Console.WriteLine($"### Bucket [{bucket}] -- {bucketPairs.Count} (date,expiry) pairs, {sessionDates.Count} distinct calendar sessions ###");
        if (sessionDates.Count < 2)
        {
            Console.WriteLine("  *** FLAGGED: fewer than 2 independent calendar sessions in this bucket -- results below are reported for completeness only, NOT sufficient for generalization. ***");
        }

        var allRows = bucketPairs.SelectMany(p => p.Rows.Select(r => (p.Date, p.Expiry, Rows: p.Rows, Row: r))).ToList();
        var upPop = allRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Up && !deExcludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
        var downPop = allRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Down && !deExcludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
        var patternAObs = upPop.Where(x => x.Row.RelationshipCategory == PatternA).ToList();
        var controlAObs = upPop.Where(x => x.Row.RelationshipCategory != PatternA).ToList();
        var patternBObs = downPop.Where(x => x.Row.RelationshipCategory == PatternB).ToList();
        var controlBObs = downPop.Where(x => x.Row.RelationshipCategory != PatternB).ToList();

        int EpisodeCount(string pattern) => bucketPairs.Sum(p => EpisodeAnalysis.DetectEpisodes(p.Date, p.Rows, pattern, []).Count);
        Console.WriteLine($"  Population: PatternA={patternAObs.Count} (episodes={EpisodeCount(PatternA)}, ControlA={controlAObs.Count}), PatternB={patternBObs.Count} (episodes={EpisodeCount(PatternB)}, ControlB={controlBObs.Count})");

        // ---- Section 3: underlying-relationship validation ----
        Console.WriteLine("  -- Section 3: underlying (futures) forward return validation --");
        foreach (var (label, members) in new[] { ("Pattern A", patternAObs), ("Pattern B", patternBObs) })
        {
            Console.WriteLine($"    [{label}] n={members.Count}");
            foreach (var h in deForwardHorizons)
            {
                var m = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(x => ForwardMove(x, h)));
                Console.WriteLine($"      +{h,2}: n={m.N} mean={Fmt4(m.Mean)}% med={Fmt4(m.Median)}% pos={m.PositivePct:F1}% neg={m.NegativePct:F1}%");
            }
        }

        // ---- Section 4: option response decomposition (Pattern A -> PE, Pattern B -> CE) ----
        Console.WriteLine("  -- Section 4: option response decomposition (directionally appropriate side only) --");
        foreach (var (label, members, side) in new[] { ("Pattern A -> PE", patternAObs, OptionType.Put), ("Pattern B -> CE", patternBObs, OptionType.Call) })
        {
            Console.WriteLine($"    [{label}] n={members.Count}");
            foreach (var h in deForwardHorizons)
            {
                var results = members.Select(m => Decompose(m, h, side)).Where(r => r is not null).Select(r => r!.Value).ToList();
                var optM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.OptionChange));
                var intM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.IntrinsicChange));
                var extM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.ExtrinsicChange));
                Console.WriteLine($"      +{h,2}: n={optM.N} OptionRs[mean={Fmt4(optM.Mean)} med={Fmt4(optM.Median)}] IntrinsicRs[mean={Fmt4(intM.Mean)}] ExtrinsicRs[mean={Fmt4(extM.Mean)}]");
            }
        }

        // ---- Section 6: descriptive exit/reversal-hypothesis check for Pattern B (and A for symmetry) ----
        Console.WriteLine("  -- Section 6: preceding vs subsequent underlying movement (descriptive only -- NOT an exit signal) --");
        foreach (var (label, members) in new[] { ("Pattern A", patternAObs), ("Pattern B", patternBObs) })
        {
            foreach (var n in dePriorHorizons)
            {
                var prior = Enumerable.Range(0, members.Count).Select(i => PriorMove(members, i, n)).ToList();
                var fwd = members.Select(m => ForwardMove(m, n)).ToList();
                var priorM = ForwardValidationAnalysis.ComputeForwardMetrics(prior);
                var fwdM = ForwardValidationAnalysis.ComputeForwardMetrics(fwd);
                var opposingCount = Enumerable.Range(0, members.Count).Count(i =>
                {
                    var p = PriorMove(members, i, n);
                    var f = ForwardMove(members[i], n);
                    return p is not null && f is not null && Math.Sign(p.Value) != 0 && Math.Sign(f.Value) != 0 && Math.Sign(p.Value) != Math.Sign(f.Value);
                });
                var bothAvailable = Enumerable.Range(0, members.Count).Count(i => PriorMove(members, i, n) is not null && ForwardMove(members[i], n) is not null);
                Console.WriteLine($"    [{label}] n={n}: Prior{-n}={Fmt4(priorM.Median)}% (mean {Fmt4(priorM.Mean)}%)  Fwd+{n}={Fmt4(fwdM.Median)}% (mean {Fmt4(fwdM.Mean)}%)  opposing-sign rate={(bothAvailable > 0 ? 100.0 * opposingCount / bothAvailable : 0):F1}% (n={bothAvailable})");
            }
        }

        // ---- Section 7: matched-control comparison, computed within this bucket ----
        Console.WriteLine("  -- Section 7: matched-control comparison (tercile-matched, direction-held-constant, computed within this bucket) --");
        foreach (var (label, patternMembers, controlMembers, direction) in new[] { ("A", patternAObs, controlAObs, upPop), ("B", patternBObs, controlBObs, downPop) })
        {
            var dirPriorAbs = Enumerable.Range(0, direction.Count).Select(i => PriorMove(direction, i, 3)).Where(v => v is not null).Select(v => Math.Abs(v!.Value)).ToList();
            if (dirPriorAbs.Count < 3)
            {
                Console.WriteLine($"    [Pattern {label}] insufficient direction-population size ({dirPriorAbs.Count}) for tercile matching in this bucket -- skipped.");
                continue;
            }
            var (low33, high67) = ForwardValidationAnalysis.ComputeTerciles(dirPriorAbs);
            foreach (var h in deForwardHorizons)
            {
                var pm = ForwardValidationAnalysis.ComputeForwardMetrics(patternMembers.Select(m => ForwardMove(m, h)));
                var cm = ForwardValidationAnalysis.ComputeForwardMetrics(controlMembers.Select(m => ForwardMove(m, h)));
                Console.WriteLine($"    [Pattern {label} vs Control {label}] +{h,2}: Futures% Pattern[n={pm.N} med={Fmt4(pm.Median)}%] Ctrl[n={cm.N} med={Fmt4(cm.Median)}%]");
            }
        }
        Console.WriteLine();

        foreach (var (label, members, side) in new[] { ("PatternA", patternAObs, OptionType.Put), ("PatternB", patternBObs, OptionType.Call) })
        {
            foreach (var m in members)
            {
                var d1 = Decompose(m, 1, side); var d3 = Decompose(m, 3, side); var d5 = Decompose(m, 5, side); var d10 = Decompose(m, 10, side);
                deCsvRows.Add(string.Join(',',
                    m.Date.ToString("yyyy-MM-dd"), m.Expiry.ToString("yyyy-MM-dd"), m.Row.Dte, bucket, m.Row.EventId, label, side,
                    ForwardMove(m, 1), ForwardMove(m, 3), ForwardMove(m, 5), ForwardMove(m, 10),
                    d1?.OptionChange, d1?.IntrinsicChange, d1?.ExtrinsicChange,
                    d3?.OptionChange, d3?.IntrinsicChange, d3?.ExtrinsicChange,
                    d5?.OptionChange, d5?.IntrinsicChange, d5?.ExtrinsicChange,
                    d10?.OptionChange, d10?.IntrinsicChange, d10?.ExtrinsicChange));
            }
        }
    }

    using (var writer = new StreamWriter(deOutPath))
    {
        writer.WriteLine("TradingDate,Expiry,Dte,Bucket,EventId,Pattern,Side,"
            + "FuturesFwd1Pct,FuturesFwd3Pct,FuturesFwd5Pct,FuturesFwd10Pct,"
            + "OptionChange1,IntrinsicChange1,ExtrinsicChange1,OptionChange3,IntrinsicChange3,ExtrinsicChange3,"
            + "OptionChange5,IntrinsicChange5,ExtrinsicChange5,OptionChange10,IntrinsicChange10,ExtrinsicChange10");
        foreach (var row in deCsvRows) { writer.WriteLine(row); }
    }
    Console.WriteLine($"DTE-expansion CSV written to: {Path.GetFullPath(deOutPath)}");

    return 0;
}

// "vc0dte-relationship-transition" -- 2026-09-24 Pre -> Pattern -> Post transition analysis
// (HARD FREEZE: same event-bar/threshold/Pattern-A-B/dynamic-ATM/episode/control/forward-horizon/
// stale-missing-data methodology as every other vc0dte-relationship-* command -- NOT touched).
// Pattern occurrences are deduplicated to one row per EPISODE (its FIRST event), reusing
// EpisodeAnalysis.DetectEpisodes unmodified; the control population is intentionally left at its
// existing, unmodified per-EVENT granularity (the task's own instruction: reuse the existing
// control methodology unless technically incapable -- it is not, this is a resolution difference,
// not a capability gap, and is stated explicitly rather than silently chosen). Only new logic:
// EpisodeTransitionAnalysis's two pure sign-orientation helpers and its divergence classifier,
// both built entirely from existing Compute*Change functions and RelationshipDirectionExtensions.Classify.
// No trading simulation, no optimization of any horizon/threshold/filter, no IV/Greeks.
//   dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-transition <fromDate> <toDate> --out=path.csv
if (args.Length > 0 && string.Equals(args[0], "vc0dte-relationship-transition", StringComparison.OrdinalIgnoreCase))
{
    const long trThreshold = 1300L;
    int[] trForwardHorizons = [1, 3, 5, 10];
    int[] trPriorHorizons = [1, 3, 5, 10];
    int[] trTercileHorizons = [3, 5, 10];
    const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    const string PatternB = ForwardValidationAnalysis.State4_BearishDivergence_PatternB;
    string[] trExcludedCategories = ["OptionDataIncomplete", "ContractTransition"];
    var trIstOffset = TimeSpan.FromHours(5.5);

    var (trPositional, trNamed) = SplitNamedArgs(args);
    if (trPositional.Length < 3
        || !DateOnly.TryParseExact(trPositional[1], "yyyy-MM-dd", out var trFromDate)
        || !DateOnly.TryParseExact(trPositional[2], "yyyy-MM-dd", out var trToDate)
        || !trNamed.TryGetValue("out", out var trOutPath))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc0dte-relationship-transition <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> --out=path.csv");
        return 1;
    }

    Console.WriteLine("=== vc0dte-relationship-transition: HARD FREEZE, VALIDATION ONLY -- what does Pattern A/B represent in time (early warning / late confirmation / temporary countertrend)? No trading simulation. ===");
    Console.WriteLine();
    static string Fmt4(decimal? v) => v?.ToString("F4") ?? "--";

    // ---- Phase 1: primary (frozen) 0-DTE dataset -- the SAME 3 days every prior experiment used ----
    var trDayRows = new List<(DateOnly Date, List<RelationshipObservation> Rows)>();
    for (var date = trFromDate; date <= trToDate; date = date.AddDays(1))
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
        var (trChain, trFutureBars, trOptionBars) = await LoadVc0DteDayAsync(date, trThreshold);
        if (trFutureBars.Count == 0 || trChain.Count == 0) { continue; }
        await using var trSource = new NiftySignalDbContext(tradeSourceOptions);
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(trSource, date, trChain, trFutureBars, trOptionBars, CancellationToken.None);
        trDayRows.Add((date, rows));
        Console.WriteLine($"  {date:yyyy-MM-dd}: {rows.Count} relationship observations loaded (reused, unmodified recorder).");
    }
    Console.WriteLine();

    var allRows = trDayRows.SelectMany(d => d.Rows.Select(r => (d.Date, d.Rows, Row: r))).ToList();
    var upPop = allRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Up && !trExcludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
    var downPop = allRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Down && !trExcludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
    var controlAObs = upPop.Where(x => x.Row.RelationshipCategory != PatternA).ToList();
    var controlBObs = downPop.Where(x => x.Row.RelationshipCategory != PatternB).ToList();

    decimal? PriorMove(List<RelationshipObservation> rows, int eventId, int n) => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, eventId - n, n).PercentChange;
    decimal? ForwardMove(List<RelationshipObservation> rows, int eventId, int h) => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, eventId, h).PercentChange;
    decimal? PriorCe(List<RelationshipObservation> rows, int eventId, int n) => UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, eventId - n, n).PercentChange;
    decimal? PriorPe(List<RelationshipObservation> rows, int eventId, int n) => UnderlyingOptionRelationshipSummary.ComputePeChange(rows, eventId - n, n).PercentChange;
    decimal? ForwardCe(List<RelationshipObservation> rows, int eventId, int h) => UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, eventId, h).PercentChange;
    decimal? ForwardPe(List<RelationshipObservation> rows, int eventId, int h) => UnderlyingOptionRelationshipSummary.ComputePeChange(rows, eventId, h).PercentChange;

    // ---- Phase 2: episode-level (one row per episode's FIRST event) transition rows for the primary dataset ----
    var trEpisodes = new List<(DateOnly Date, List<RelationshipObservation> Rows, EpisodeAnalysis.Episode Episode, bool PatternIsUp)>();
    foreach (var (date, rows) in trDayRows)
    {
        foreach (var ep in EpisodeAnalysis.DetectEpisodes(date, rows, PatternA, [])) { trEpisodes.Add((date, rows, ep, true)); }
        foreach (var ep in EpisodeAnalysis.DetectEpisodes(date, rows, PatternB, [])) { trEpisodes.Add((date, rows, ep, false)); }
    }
    Console.WriteLine($"Episodes detected (primary 0-DTE dataset): Pattern A={trEpisodes.Count(e => e.PatternIsUp)}, Pattern B={trEpisodes.Count(e => !e.PatternIsUp)}");
    var episodeLengths = trEpisodes.Select(e => e.Episode.EventCount).ToList();
    Console.WriteLine($"Episode length distribution: median={ForensicValidationAnalysis.ComputePercentiles(episodeLengths.Select(l => (decimal)l).ToList()).Median:F1}, min={episodeLengths.Min()}, max={episodeLengths.Max()}, 1-event episodes={episodeLengths.Count(l => l == 1)} ({100.0 * episodeLengths.Count(l => l == 1) / episodeLengths.Count:F1}%)");
    Console.WriteLine();

    void ReportPattern(string label, bool patternIsUp, List<(DateOnly Date, List<RelationshipObservation> Rows, EpisodeAnalysis.Episode Episode, bool PatternIsUp)> episodes)
    {
        var members = episodes.Where(e => e.PatternIsUp == patternIsUp).ToList();
        var sessions = members.Select(e => e.Date).Distinct().ToList();
        Console.WriteLine($"### {label} -- {members.Count} episodes across {sessions.Count} calendar sessions ({string.Join(", ", sessions.Select(d => d.ToString("yyyy-MM-dd")))}) ###");

        // ---- Section 2: PRE / PATTERN EVENT / POST, per-episode (first event) ----
        Console.WriteLine("  -- PRE-PATTERN (futures/CE/PE movement ending at the signal event) --");
        foreach (var n in trPriorHorizons)
        {
            var futM = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => PriorMove(m.Rows, m.Episode.StartEventId, n)));
            var ceM = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => PriorCe(m.Rows, m.Episode.StartEventId, n)));
            var peM = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => PriorPe(m.Rows, m.Episode.StartEventId, n)));
            Console.WriteLine($"    -{n,2}: Futures[n={futM.N} med={Fmt4(futM.Median)}%] CE[med={Fmt4(ceM.Median)}%] PE[med={Fmt4(peM.Median)}%]");
        }
        var preDivergence = members.Select(m => EpisodeTransitionAnalysis.ClassifyCePeDivergence(PriorCe(m.Rows, m.Episode.StartEventId, 3), PriorPe(m.Rows, m.Episode.StartEventId, 3))).ToList();
        Console.WriteLine($"    Pre-pattern (-3) CE-vs-PE divergence: Divergent={preDivergence.Count(d => d == "Divergent")}, Aligned={preDivergence.Count(d => d == "Aligned")}, Flat/Unavailable={preDivergence.Count(d => d is "Flat" or "Unavailable")}");
        var avgDuration = members.Select(m => m.Episode.EventCount > 0 ? (double?)m.Rows[m.Episode.StartEventId].FuturesEventDurationMs : null).Where(v => v is not null).Select(v => v!.Value).ToList();
        var avgVolume = members.Select(m => (double)m.Rows[m.Episode.StartEventId].FuturesVolume).ToList();
        Console.WriteLine($"    Signal-event duration (ms): median={(avgDuration.Count > 0 ? avgDuration.OrderBy(v => v).ElementAt(avgDuration.Count / 2) : 0):F0}. Signal-event futures volume: median={(avgVolume.Count > 0 ? avgVolume.OrderBy(v => v).ElementAt(avgVolume.Count / 2) : 0):F0}.");

        Console.WriteLine("  -- PATTERN EVENT (signal, first event of episode) --");
        var side = patternIsUp ? OptionType.Put : OptionType.Call;
        var moneyness = members.Select(m => OptionValueDecomposition.ComputeMoneyness(m.Rows[m.Episode.StartEventId].FuturesClose, m.Rows[m.Episode.StartEventId].AtmStrike, side)).ToList();
        var moneynessP = ForensicValidationAnalysis.ComputePercentiles(moneyness);
        var signalPrices = members.Select(m => side == OptionType.Put ? m.Rows[m.Episode.StartEventId].PeAverageLtp : m.Rows[m.Episode.StartEventId].CeAverageLtp).Where(p => p is not null).Select(p => p!.Value).ToList();
        var priceP = ForensicValidationAnalysis.ComputePercentiles(signalPrices);
        var dteValues = members.Select(m => m.Rows[m.Episode.StartEventId].Dte).Distinct().OrderBy(d => d).ToList();
        Console.WriteLine($"    Signalling {(side == OptionType.Put ? "PE" : "CE")} price: n={priceP.N} median={priceP.Median:F2}. Moneyness: median={moneynessP.Median:F2}. DTE values present: {string.Join(",", dteValues)}.");
        foreach (var bucketGroup in members.GroupBy(m => Vc0DteBehaviorSummary.SessionBucket(m.Rows[m.Episode.StartEventId].StartTimestamp, trIstOffset)).OrderBy(g => g.Key))
        {
            Console.WriteLine($"    [{bucketGroup.Key}]: n={bucketGroup.Count()}");
        }

        // ---- Section 3/4: critical comparison + oriented pre/post metrics ----
        Console.WriteLine("  -- POST-PATTERN + CRITICAL COMPARISON (oriented: positive PreSameDir = already moving in defining direction; positive PostOpposing = reversed) --");
        var preSameDirBy = new Dictionary<int, ForwardValidationAnalysis.ForwardMetrics>();
        var postOpposingBy = new Dictionary<int, ForwardValidationAnalysis.ForwardMetrics>();
        foreach (var n in trForwardHorizons)
        {
            var preOriented = members.Select(m => EpisodeTransitionAnalysis.OrientToDefiningDirection(PriorMove(m.Rows, m.Episode.StartEventId, n), patternIsUp));
            var postOriented = members.Select(m => EpisodeTransitionAnalysis.OrientToOpposingDirection(ForwardMove(m.Rows, m.Episode.StartEventId, n), patternIsUp));
            var preM = ForwardValidationAnalysis.ComputeForwardMetrics(preOriented);
            var postM = ForwardValidationAnalysis.ComputeForwardMetrics(postOriented);
            preSameDirBy[n] = preM;
            postOpposingBy[n] = postM;
            var postCeM = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => ForwardCe(m.Rows, m.Episode.StartEventId, n)));
            var postPeM = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => ForwardPe(m.Rows, m.Episode.StartEventId, n)));
            Console.WriteLine($"    n={n,2}: PreSameDir[n={preM.N} med={Fmt4(preM.Median)}%] PostOpposing[n={postM.N} med={Fmt4(postM.Median)}% pos%={postM.PositivePct:F1}] Fwd CE[med={Fmt4(postCeM.Median)}%] Fwd PE[med={Fmt4(postPeM.Median)}%]");
        }

        // ---- Section 4: timing classification (data-driven, no invented numeric threshold) ----
        var pre3 = preSameDirBy[3].Median ?? 0; var pre5 = preSameDirBy[5].Median ?? 0; var pre10 = preSameDirBy[10].Median ?? 0;
        var post3 = postOpposingBy[3].Median ?? 0; var post5 = postOpposingBy[5].Median ?? 0; var post10 = postOpposingBy[10].Median ?? 0;
        var post1 = postOpposingBy[1].Median ?? 0;
        var preAvg = (Math.Abs(pre3) + Math.Abs(pre5) + Math.Abs(pre10)) / 3m;
        var postAvg = (Math.Abs(post3) + Math.Abs(post5) + Math.Abs(post10)) / 3m;
        var ratio = preAvg > 0 ? postAvg / preAvg : (postAvg > 0 ? decimal.MaxValue : 1m);
        var fadesAfterPeak = post1 > 0 && post10 < post1 * 0.5m; // post-pattern move visibly shrinks from its early value by +10 -- descriptive check, not a tuned threshold.
        string classification;
        if (fadesAfterPeak && post10 <= post3) { classification = "C: Temporary countertrend (opposing move fades toward/through +10 rather than sustaining)"; }
        else if (ratio >= 1.5m) { classification = "A: Early-warning-leaning (post-pattern opposing move noticeably exceeds pre-pattern same-direction move)"; }
        else if (ratio <= 0.75m) { classification = "B: Late-stage-confirmation-leaning (pre-pattern same-direction move is as large as or larger than the post-pattern opposing move)"; }
        else { classification = "D: Mixed/unclear (pre- and post-pattern magnitudes are comparable)"; }
        Console.WriteLine($"    Pre-pattern avg |move| (n=3,5,10): {preAvg:F4}%. Post-pattern avg |opposing move| (n=3,5,10): {postAvg:F4}%. Ratio(post/pre)={(preAvg > 0 ? ratio.ToString("F2") : "n/a (pre~0)")}.");
        Console.WriteLine($"    Classification: {classification}");
        Console.WriteLine();

        // ---- Section 6: path check (continuation vs bounce-back, using only the frozen 1/3/5/10 horizons) ----
        Console.WriteLine($"    Path (PostOpposing median by horizon): +1={Fmt4(postOpposingBy[1].Median)}%  +3={Fmt4(postOpposingBy[3].Median)}%  +5={Fmt4(postOpposingBy[5].Median)}%  +10={Fmt4(postOpposingBy[10].Median)}%");
        Console.WriteLine();

        // ---- Section 5: matched-control comparison (existing per-event methodology, unmodified) ----
        var (controlObs, direction) = patternIsUp ? (controlAObs, upPop) : (controlBObs, downPop);
        Console.WriteLine("  -- MATCHED-CONTROL COMPARISON (existing tercile-matched, direction-held-constant, per-EVENT control; pattern side is per-EPISODE) --");
        foreach (var n in trTercileHorizons)
        {
            var dirPriorAbs = direction.Select(x => PriorMove(x.Rows, x.Row.EventId, n)).Where(v => v is not null).Select(v => Math.Abs(v!.Value)).ToList();
            if (dirPriorAbs.Count < 3) { continue; }
            var (low33, high67) = ForwardValidationAnalysis.ComputeTerciles(dirPriorAbs);
            foreach (var bucket in new[] { "Low", "Mid", "High" })
            {
                var patternBucket = members.Where(m => { var p = PriorMove(m.Rows, m.Episode.StartEventId, n); return p is not null && ConditionalMovementAnalysis.ClassifyTercileBucket(Math.Abs(p.Value), low33, high67) == bucket; }).ToList();
                var controlBucket = controlObs.Where(x => { var p = PriorMove(x.Rows, x.Row.EventId, n); return p is not null && ConditionalMovementAnalysis.ClassifyTercileBucket(Math.Abs(p.Value), low33, high67) == bucket; }).ToList();
                var pm = ForwardValidationAnalysis.ComputeForwardMetrics(patternBucket.Select(m => ForwardMove(m.Rows, m.Episode.StartEventId, n)));
                var cm = ForwardValidationAnalysis.ComputeForwardMetrics(controlBucket.Select(x => ForwardMove(x.Rows, x.Row.EventId, n)));
                Console.WriteLine($"    [Prior-{n} {bucket}] Pattern[n={pm.N} med={Fmt4(pm.Median)}%] Control[n={cm.N} med={Fmt4(cm.Median)}%]");
            }
        }
        Console.WriteLine();
    }

    ReportPattern("Pattern A (episode-level)", true, trEpisodes);
    ReportPattern("Pattern B (episode-level)", false, trEpisodes);

    // ---- Phase 3: DTE-bucket breakdown (item 8) -- reuses the exact same (date,expiry) discovery/bucket classification as vc0dte-relationship-dte-expansion ----
    Console.WriteLine("### DTE-bucket breakdown (session-count caveats apply -- see docs) ###");
    var trPairs = new List<(DateOnly Date, DateOnly Expiry, int Dte, string Bucket)>();
    await using (var trScanSource = new NiftySignalDbContext(tradeSourceOptions))
    {
        for (var date = trFromDate; date <= trToDate; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
            var hasFutures = await trScanSource.Instruments.AnyAsync(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Future && i.Underlying == "NIFTY");
            if (!hasFutures) { continue; }
            var expiries = await trScanSource.Instruments.Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.Underlying == "NIFTY").Select(i => i.ExpiryDate).Distinct().OrderBy(e => e).ToListAsync();
            foreach (var expiry in expiries)
            {
                if (expiry is null) { continue; }
                var dte = expiry.Value.DayNumber - date.DayNumber;
                var bucket = DteBucketClassifier.Classify(dte);
                if (bucket != DteBucketClassifier.Other) { trPairs.Add((date, expiry.Value, dte, bucket)); }
            }
        }
    }
    var trBucketPairRows = new List<(DateOnly Date, DateOnly Expiry, int Dte, string Bucket, List<RelationshipObservation> Rows)>();
    foreach (var dateGroup in trPairs.GroupBy(p => p.Date).OrderBy(g => g.Key))
    {
        await using var trBucketSource = new NiftySignalDbContext(tradeSourceOptions);
        var futureBars = await FutureEventBarBuilder.BuildDayAsync(trBucketSource, dateGroup.Key, trThreshold, CancellationToken.None);
        if (futureBars.Count == 0) { continue; }
        foreach (var p in dateGroup)
        {
            var chain = await trBucketSource.Instruments.Where(i => i.AsOfDate == p.Date && i.InstrumentType == InstrumentType.Option && i.ExpiryDate == p.Expiry && i.Underlying == "NIFTY").OrderBy(i => i.StrikePrice).ThenBy(i => i.OptionType).ToListAsync();
            if (chain.Count == 0) { continue; }
            var optionBars = await SynchronizedOptionBarBuilder.BuildDayForChainAsync(trBucketSource, p.Date, chain, futureBars, CancellationToken.None);
            var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(trBucketSource, p.Date, chain, futureBars, optionBars, CancellationToken.None);
            trBucketPairRows.Add((p.Date, p.Expiry, p.Dte, p.Bucket, rows));
        }
    }
    foreach (var bucket in new[] { "0", "1", "4-6", "7-8", "11-13" })
    {
        var bp = trBucketPairRows.Where(p => p.Bucket == bucket).ToList();
        var sessions = bp.Select(p => p.Date).Distinct().ToList();
        if (bp.Count == 0) { continue; }
        var bucketEpisodes = new List<(EpisodeAnalysis.Episode Episode, List<RelationshipObservation> Rows, bool PatternIsUp)>();
        foreach (var p in bp)
        {
            foreach (var ep in EpisodeAnalysis.DetectEpisodes(p.Date, p.Rows, PatternA, [])) { bucketEpisodes.Add((ep, p.Rows, true)); }
            foreach (var ep in EpisodeAnalysis.DetectEpisodes(p.Date, p.Rows, PatternB, [])) { bucketEpisodes.Add((ep, p.Rows, false)); }
        }
        Console.WriteLine($"  Bucket [{bucket}] ({sessions.Count} sessions{(sessions.Count < 2 ? ", FLAGGED insufficient" : "")}):");
        foreach (var patternIsUp in new[] { true, false })
        {
            var members = bucketEpisodes.Where(e => e.PatternIsUp == patternIsUp).ToList();
            if (members.Count == 0) { continue; }
            var pre3 = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => EpisodeTransitionAnalysis.OrientToDefiningDirection(PriorMove(m.Rows, m.Episode.StartEventId, 3), patternIsUp)));
            var post3 = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => EpisodeTransitionAnalysis.OrientToOpposingDirection(ForwardMove(m.Rows, m.Episode.StartEventId, 3), patternIsUp)));
            var post10 = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => EpisodeTransitionAnalysis.OrientToOpposingDirection(ForwardMove(m.Rows, m.Episode.StartEventId, 10), patternIsUp)));
            Console.WriteLine($"    Pattern {(patternIsUp ? "A" : "B")}: episodes={members.Count}, PreSameDir(-3) med={Fmt4(pre3.Median)}%, PostOpposing(+3) med={Fmt4(post3.Median)}%, PostOpposing(+10) med={Fmt4(post10.Median)}%");
        }
    }
    Console.WriteLine();

    // ---- CSV: one row per episode (primary dataset) ----
    using (var writer = new StreamWriter(trOutPath))
    {
        writer.WriteLine("TradingDate,Pattern,EpisodeId,StartEventId,EventCount,"
            + "Pre1Pct,Pre3Pct,Pre5Pct,Pre10Pct,SignalFuturesReturn1,SignalCeReturn1,SignalPeReturn1,SignalPrice,Moneyness,Dte,SessionBucket,"
            + "Post1Pct,Post3Pct,Post5Pct,Post10Pct,PreSameDir3,PreSameDir5,PreSameDir10,PostOpposing1,PostOpposing3,PostOpposing5,PostOpposing10");
        foreach (var (date, rows, ep, patternIsUp) in trEpisodes)
        {
            var side = patternIsUp ? OptionType.Put : OptionType.Call;
            var signal = rows[ep.StartEventId];
            var signalPrice = side == OptionType.Put ? signal.PeAverageLtp : signal.CeAverageLtp;
            var moneyness = OptionValueDecomposition.ComputeMoneyness(signal.FuturesClose, signal.AtmStrike, side);
            var sessionBucket = Vc0DteBehaviorSummary.SessionBucket(signal.StartTimestamp, trIstOffset);
            writer.WriteLine(string.Join(',',
                date.ToString("yyyy-MM-dd"), patternIsUp ? "PatternA" : "PatternB", ep.EpisodeId, ep.StartEventId, ep.EventCount,
                PriorMove(rows, ep.StartEventId, 1), PriorMove(rows, ep.StartEventId, 3), PriorMove(rows, ep.StartEventId, 5), PriorMove(rows, ep.StartEventId, 10),
                signal.FuturesChange1, signal.CeChange1, signal.PeChange1, signalPrice, moneyness, signal.Dte, sessionBucket,
                ForwardMove(rows, ep.StartEventId, 1), ForwardMove(rows, ep.StartEventId, 3), ForwardMove(rows, ep.StartEventId, 5), ForwardMove(rows, ep.StartEventId, 10),
                EpisodeTransitionAnalysis.OrientToDefiningDirection(PriorMove(rows, ep.StartEventId, 3), patternIsUp),
                EpisodeTransitionAnalysis.OrientToDefiningDirection(PriorMove(rows, ep.StartEventId, 5), patternIsUp),
                EpisodeTransitionAnalysis.OrientToDefiningDirection(PriorMove(rows, ep.StartEventId, 10), patternIsUp),
                EpisodeTransitionAnalysis.OrientToOpposingDirection(ForwardMove(rows, ep.StartEventId, 1), patternIsUp),
                EpisodeTransitionAnalysis.OrientToOpposingDirection(ForwardMove(rows, ep.StartEventId, 3), patternIsUp),
                EpisodeTransitionAnalysis.OrientToOpposingDirection(ForwardMove(rows, ep.StartEventId, 5), patternIsUp),
                EpisodeTransitionAnalysis.OrientToOpposingDirection(ForwardMove(rows, ep.StartEventId, 10), patternIsUp)));
        }
    }
    Console.WriteLine($"Transition-analysis CSV written to: {Path.GetFullPath(trOutPath)}");

    return 0;
}

// "vc-sensex-relationship-validation" -- 2026-09-24 cross-index validation (HARD FREEZE: the
// Nifty-derived Pattern A/B definitions, event-bar/threshold/dynamic-ATM/episode/control/forward-
// horizon/stale-missing-data methodology are applied UNCHANGED -- no Sensex-specific tuning, no
// new pattern discovery). Reuses UnderlyingOptionRelationshipRecorder.RecordForUnderlyingAsync
// and FutureEventBarBuilder.BuildDayForUnderlyingAsync (both added this task as pure additive
// underlying-parameterized siblings of the frozen NIFTY-only methods -- verified byte-identical
// NIFTY behaviour via the existing, unmodified tests, which still pass), plus
// SynchronizedOptionBarBuilder.BuildDayForChainAsync (already underlying-agnostic -- takes the
// chain directly, added during the DTE-Expansion experiment), EpisodeAnalysis.DetectEpisodes,
// EpisodeTransitionAnalysis, OptionValueDecomposition, and the Conditional Analysis control
// construction, all completely unmodified. Documented market-structure difference (see the
// dated findings-doc section): Sensex has no Index (spot) instrument in this dataset at all --
// Spot* fields will simply report Unavailable throughout, exactly as the existing "never
// fabricate" convention already handles for any day lacking spot data. No trading simulation, no
// IV/Greeks, no threshold tuning.
//   dotnet run --project NiftySignal.VolumeBarData -- vc-sensex-relationship-validation <fromDate> <toDate> --out=path.csv
if (args.Length > 0 && string.Equals(args[0], "vc-sensex-relationship-validation", StringComparison.OrdinalIgnoreCase))
{
    const long svThreshold = 1300L; // FROZEN -- same numeric value as Nifty, not re-tuned for Sensex.
    const string svUnderlying = "SENSEX";
    int[] svForwardHorizons = [1, 3, 5, 10];
    int[] svPriorHorizons = [1, 3, 5, 10];
    int[] svTercileHorizons = [3, 5, 10];
    const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    const string PatternB = ForwardValidationAnalysis.State4_BearishDivergence_PatternB;
    string[] svExcludedCategories = ["OptionDataIncomplete", "ContractTransition"];
    var svIstOffset = TimeSpan.FromHours(5.5);

    var (svPositional, svNamed) = SplitNamedArgs(args);
    if (svPositional.Length < 3
        || !DateOnly.TryParseExact(svPositional[1], "yyyy-MM-dd", out var svFromDate)
        || !DateOnly.TryParseExact(svPositional[2], "yyyy-MM-dd", out var svToDate)
        || !svNamed.TryGetValue("out", out var svOutPath))
    {
        Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- vc-sensex-relationship-validation <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> --out=path.csv");
        return 1;
    }

    Console.WriteLine("=== vc-sensex-relationship-validation: HARD FREEZE, CROSS-INDEX VALIDATION ONLY -- does the frozen Nifty Pattern A/B relationship also occur on Sensex? No trading simulation, no tuning. ===");
    Console.WriteLine();
    static string Fmt4(decimal? v) => v?.ToString("F4") ?? "--";

    var svDayRows = new List<(DateOnly Date, List<RelationshipObservation> Rows)>();
    for (var date = svFromDate; date <= svToDate; date = date.AddDays(1))
    {
        await using var svSource = new NiftySignalDbContext(tradeSourceOptions);
        var futureBars = await FutureEventBarBuilder.BuildDayForUnderlyingAsync(svSource, date, svThreshold, svUnderlying, CancellationToken.None);
        if (futureBars.Count == 0) { continue; }
        var chain = await svSource.Instruments
            .Where(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Option && i.Underlying == svUnderlying)
            .OrderBy(i => i.StrikePrice).ThenBy(i => i.OptionType)
            .ToListAsync();
        if (chain.Count == 0) { continue; }
        var optionBars = await SynchronizedOptionBarBuilder.BuildDayForChainAsync(svSource, date, chain, futureBars, CancellationToken.None);
        var rows = await UnderlyingOptionRelationshipRecorder.RecordForUnderlyingAsync(svSource, date, chain, futureBars, optionBars, svUnderlying, CancellationToken.None);
        svDayRows.Add((date, rows));
        var dteValues = chain.Where(i => i.ExpiryDate is not null).Select(i => i.ExpiryDate!.Value.DayNumber - date.DayNumber).Distinct().ToList();
        Console.WriteLine($"  {date:yyyy-MM-dd}: {rows.Count} relationship observations ({futureBars.Count} future event bars, DTE={string.Join(",", dteValues)}, chain size={chain.Count}).");
    }
    Console.WriteLine();

    if (svDayRows.Count == 0)
    {
        Console.WriteLine("No usable Sensex sessions found in the requested range -- stopping. No fabricated methodology.");
        return 1;
    }

    var allRows = svDayRows.SelectMany(d => d.Rows.Select(r => (d.Date, d.Rows, Row: r))).ToList();
    var upPop = allRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Up && !svExcludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
    var downPop = allRows.Where(x => x.Row.FuturesDirection1 == RelationshipDirection.Down && !svExcludedCategories.Contains(x.Row.RelationshipCategory)).ToList();
    var controlAObs = upPop.Where(x => x.Row.RelationshipCategory != PatternA).ToList();
    var controlBObs = downPop.Where(x => x.Row.RelationshipCategory != PatternB).ToList();

    decimal? PriorMove(List<RelationshipObservation> rows, int eventId, int n) => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, eventId - n, n).PercentChange;
    decimal? ForwardMove(List<RelationshipObservation> rows, int eventId, int h) => UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, eventId, h).PercentChange;

    var svEpisodes = new List<(DateOnly Date, List<RelationshipObservation> Rows, EpisodeAnalysis.Episode Episode, bool PatternIsUp)>();
    foreach (var (date, rows) in svDayRows)
    {
        foreach (var ep in EpisodeAnalysis.DetectEpisodes(date, rows, PatternA, [])) { svEpisodes.Add((date, rows, ep, true)); }
        foreach (var ep in EpisodeAnalysis.DetectEpisodes(date, rows, PatternB, [])) { svEpisodes.Add((date, rows, ep, false)); }
    }
    Console.WriteLine($"Episodes detected: Pattern A={svEpisodes.Count(e => e.PatternIsUp)}, Pattern B={svEpisodes.Count(e => !e.PatternIsUp)}");
    Console.WriteLine();

    (decimal? OptionChange, decimal? IntrinsicChange, decimal? ExtrinsicChange)? Decompose(List<RelationshipObservation> rows, RelationshipObservation row, int h, OptionType side)
    {
        var price = side == OptionType.Call ? row.CeAverageLtp : row.PeAverageLtp;
        if (price is null) { return null; }
        var strike = row.AtmStrike;
        var signalIntrinsic = OptionValueDecomposition.ComputeIntrinsic(row.FuturesClose, strike, side);
        var change = side == OptionType.Call
            ? UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, row.EventId, h)
            : UnderlyingOptionRelationshipSummary.ComputePeChange(rows, row.EventId, h);
        if (change.AbsoluteChange is null) { return (null, null, null); }
        var forwardFutures = rows[row.EventId + h].FuturesClose;
        var forwardPrice = price.Value + change.AbsoluteChange.Value;
        var signalExtrinsic = OptionValueDecomposition.ComputeExtrinsic(price.Value, signalIntrinsic);
        var forwardIntrinsic = OptionValueDecomposition.ComputeIntrinsic(forwardFutures, strike, side);
        var forwardExtrinsic = OptionValueDecomposition.ComputeExtrinsic(forwardPrice, forwardIntrinsic);
        return (change.AbsoluteChange, forwardIntrinsic - signalIntrinsic, forwardExtrinsic - signalExtrinsic);
    }

    void ReportPattern(string label, bool patternIsUp)
    {
        var members = svEpisodes.Where(e => e.PatternIsUp == patternIsUp).ToList();
        var sessions = members.Select(e => e.Date).Distinct().ToList();
        Console.WriteLine($"### {label} -- {members.Count} episodes across {sessions.Count} independent calendar session(s) ({string.Join(", ", sessions.Select(d => d.ToString("yyyy-MM-dd")))}) ###");
        if (members.Count == 0) { Console.WriteLine("  NO EPISODES -- pattern did not occur in this sample."); Console.WriteLine(); return; }
        if (sessions.Count < 2) { Console.WriteLine("  *** FLAGGED: fewer than 2 independent calendar sessions -- results below cannot support a generalizable conclusion, reported for completeness only. ***"); }

        Console.WriteLine("  -- Underlying relationship validation --");
        foreach (var h in svForwardHorizons)
        {
            var m = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(x => ForwardMove(x.Rows, x.Episode.StartEventId, h)));
            Console.WriteLine($"    +{h,2}: n={m.N} mean={Fmt4(m.Mean)}% med={Fmt4(m.Median)}% pos={m.PositivePct:F1}% neg={m.NegativePct:F1}%");
        }

        Console.WriteLine("  -- Pre -> Pattern -> Post timing (oriented, same convention as the Nifty Transition experiment) --");
        var preSameDirBy = new Dictionary<int, ForwardValidationAnalysis.ForwardMetrics>();
        var postOpposingBy = new Dictionary<int, ForwardValidationAnalysis.ForwardMetrics>();
        foreach (var n in svPriorHorizons)
        {
            var preM = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => EpisodeTransitionAnalysis.OrientToDefiningDirection(PriorMove(m.Rows, m.Episode.StartEventId, n), patternIsUp)));
            var postM = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => EpisodeTransitionAnalysis.OrientToOpposingDirection(ForwardMove(m.Rows, m.Episode.StartEventId, n), patternIsUp)));
            preSameDirBy[n] = preM; postOpposingBy[n] = postM;
            Console.WriteLine($"    n={n,2}: PreSameDir[med={Fmt4(preM.Median)}%] PostOpposing[med={Fmt4(postM.Median)}% pos%={postM.PositivePct:F1}]");
        }
        var preAvg = new[] { 3, 5, 10 }.Select(n => Math.Abs(preSameDirBy[n].Median ?? 0)).Average();
        var postAvg = new[] { 3, 5, 10 }.Select(n => Math.Abs(postOpposingBy[n].Median ?? 0)).Average();
        Console.WriteLine($"    Pre avg |move|={preAvg:F4}%, Post avg |opposing move|={postAvg:F4}%, ratio={(preAvg > 0 ? (postAvg / preAvg).ToString("F2") : "n/a")}");
        Console.WriteLine($"    Path: +1={Fmt4(postOpposingBy[1].Median)}%  +3={Fmt4(postOpposingBy[3].Median)}%  +5={Fmt4(postOpposingBy[5].Median)}%  +10={Fmt4(postOpposingBy[10].Median)}%");

        Console.WriteLine("  -- Option response (directionally appropriate side) --");
        var side = patternIsUp ? OptionType.Put : OptionType.Call;
        foreach (var h in svForwardHorizons)
        {
            var results = members.Select(m => Decompose(m.Rows, m.Rows[m.Episode.StartEventId], h, side)).Where(r => r is not null).Select(r => r!.Value).ToList();
            var optM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.OptionChange));
            var intM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.IntrinsicChange));
            var extM = ForwardValidationAnalysis.ComputeForwardMetrics(results.Select(r => r.ExtrinsicChange));
            Console.WriteLine($"    +{h,2}: n={optM.N} OptionRs[med={Fmt4(optM.Median)}] IntrinsicRs[mean={Fmt4(intM.Mean)}] ExtrinsicRs[mean={Fmt4(extM.Mean)}]");
        }

        Console.WriteLine("  -- Matched-control comparison (existing tercile-matched, direction-held-constant methodology, unmodified) --");
        var (controlObs, direction) = patternIsUp ? (controlAObs, upPop) : (controlBObs, downPop);
        foreach (var n in svTercileHorizons)
        {
            var dirPriorAbs = direction.Select(x => PriorMove(x.Rows, x.Row.EventId, n)).Where(v => v is not null).Select(v => Math.Abs(v!.Value)).ToList();
            if (dirPriorAbs.Count < 3) { Console.WriteLine($"    [n={n}] insufficient direction-population size ({dirPriorAbs.Count}) for tercile matching -- skipped."); continue; }
            var (low33, high67) = ForwardValidationAnalysis.ComputeTerciles(dirPriorAbs);
            var pm = ForwardValidationAnalysis.ComputeForwardMetrics(members.Select(m => ForwardMove(m.Rows, m.Episode.StartEventId, n)));
            var cm = ForwardValidationAnalysis.ComputeForwardMetrics(controlObs.Select(x => ForwardMove(x.Rows, x.Row.EventId, n)));
            Console.WriteLine($"    [n={n}] Pattern[n={pm.N} med={Fmt4(pm.Median)}%] Control[n={cm.N} med={Fmt4(cm.Median)}%]");
        }
        Console.WriteLine();
    }

    ReportPattern("Pattern A (Sensex)", true);
    ReportPattern("Pattern B (Sensex)", false);

    using (var writer = new StreamWriter(svOutPath))
    {
        writer.WriteLine("TradingDate,Pattern,EpisodeId,StartEventId,Dte,Pre3Pct,Pre5Pct,Pre10Pct,SignalPrice,Moneyness,Post1Pct,Post3Pct,Post5Pct,Post10Pct");
        foreach (var (date, rows, ep, patternIsUp) in svEpisodes)
        {
            var side = patternIsUp ? OptionType.Put : OptionType.Call;
            var signal = rows[ep.StartEventId];
            var signalPrice = side == OptionType.Put ? signal.PeAverageLtp : signal.CeAverageLtp;
            var moneyness = OptionValueDecomposition.ComputeMoneyness(signal.FuturesClose, signal.AtmStrike, side);
            writer.WriteLine(string.Join(',',
                date.ToString("yyyy-MM-dd"), patternIsUp ? "PatternA" : "PatternB", ep.EpisodeId, ep.StartEventId, signal.Dte,
                PriorMove(rows, ep.StartEventId, 3), PriorMove(rows, ep.StartEventId, 5), PriorMove(rows, ep.StartEventId, 10),
                signalPrice, moneyness,
                ForwardMove(rows, ep.StartEventId, 1), ForwardMove(rows, ep.StartEventId, 3), ForwardMove(rows, ep.StartEventId, 5), ForwardMove(rows, ep.StartEventId, 10)));
        }
    }
    Console.WriteLine($"Sensex cross-index validation CSV written to: {Path.GetFullPath(svOutPath)}");

    return 0;
}

if (args.Length < 2 || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd", out var fromDate) || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var toDate))
{
    Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.VolumeBarData -- <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [barVolumeThreshold] [sourceDatabaseNameOverride]");
    Console.Error.WriteLine("   or: dotnet run --project NiftySignal.VolumeBarData -- analyze <date:yyyy-MM-dd> [windowBars] [barVolumeThreshold]");
    Console.Error.WriteLine("   or: dotnet run --project NiftySignal.VolumeBarData -- trade <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> <metric> [entryPercentile] [trendWindowBars] [barVolumeThreshold]");
    Console.Error.WriteLine("   or: dotnet run --project NiftySignal.VolumeBarData -- calibrate <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> <metric> [barVolumeThresholds]");
    return 1;
}

var barVolumeThreshold = args.Length > 2 ? long.Parse(args[2]) : 1300L;
var sourceDatabaseNameOverride = args.Length > 3 ? args[3] : "niftysignal_vm_copy";

var sourceConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = sourceDatabaseNameOverride }.ConnectionString;
var sourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(sourceConnectionString).Options;

await using (var destination = new VolumeBarDbContext(volumeBarOptions))
{
    // Creates the niftysignal_volume_bars database and applies the schema on first run -- a
    // no-op on every subsequent run once it's already current.
    await destination.Database.MigrateAsync();
}

var populatedDays = 0;
var skippedDays = 0;

for (var date = fromDate; date <= toDate; date = date.AddDays(1))
{
    await using var source = new NiftySignalDbContext(sourceOptions);
    await using var destination = new VolumeBarDbContext(volumeBarOptions);

    var result = await VolumeBarPopulator.PopulateDayAsync(source, destination, date, barVolumeThreshold, CancellationToken.None);

    switch (result.Outcome)
    {
        case VolumeBarPopulationOutcome.Populated:
            Console.WriteLine($"{date:yyyy-MM-dd}: populated {result.RowCount} volume bars (threshold={barVolumeThreshold}).");
            populatedDays++;
            break;
        case VolumeBarPopulationOutcome.AlreadyPopulated:
            Console.WriteLine($"{date:yyyy-MM-dd}: already populated at threshold={barVolumeThreshold} -- skipped.");
            skippedDays++;
            break;
        case VolumeBarPopulationOutcome.NoTradableData:
            Console.WriteLine($"{date:yyyy-MM-dd}: no future tick data for this date -- skipped.");
            skippedDays++;
            break;
    }
}

Console.WriteLine($"Done. {populatedDays} day(s) populated, {skippedDays} day(s) skipped.");
return 0;
