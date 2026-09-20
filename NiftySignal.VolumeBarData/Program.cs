using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NiftySignal.BacktestData;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
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

var tradeSourceConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_vm_copy" }.ConnectionString;
var tradeSourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(tradeSourceConnectionString).Options;

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
async Task<List<VolumeBarTrade>> RunRangeAsync(DateOnly from, DateOnly to, VolumeBarMetric metric, double entryPercentile, int trendWindowBars, long threshold, decimal? stopLossPercent = null, long? rollingSubBarThreshold = null, int depthBandWidth = OptionDepthPopulator.DefaultBandWidth, TimeSpan? optionsSwitchTime = null, TimeSpan? entryWindowStartOverride = null, (double Open, double Mid, double Close)? sessionWeightsOnFutures = null)
{
    var result = new List<VolumeBarTrade>();
    for (var date = from; date <= to; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        result.AddRange(await TradeSimulator.SimulateDayAsync(source, volumeBars, date, threshold, metric, entryPercentile, trendWindowBars, CancellationToken.None, sharedOptionPriceCache, stopLossPercent, rollingSubBarThreshold, depthBandWidth, optionsSwitchTime, entryWindowStartOverride, sessionWeightsOnFutures));
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

    Console.WriteLine($"=== Crossover simulation: metric={coScoreMetric} fast={coFastBars} slow={coSlowBars} thresholdPoints={coThreshold} barThreshold={coBarVolumeThreshold} ===");
    var coAllTrades = new List<VolumeBarTrade>();
    for (var date = coFromDate; date <= coToDate; date = date.AddDays(1))
    {
        await using var source = new NiftySignalDbContext(tradeSourceOptions);
        await using var volumeBars = new VolumeBarDbContext(volumeBarOptions);
        var dayTrades = await TradeSimulator.SimulateCrossoverDayAsync(source, volumeBars, date, coBarVolumeThreshold, coFastBars, coSlowBars, coThreshold, CancellationToken.None, sharedOptionPriceCache, coScoreMetric, coBandWidth);
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
    var instrument = await source.Instruments
        .Where(i => i.AsOfDate == rawDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.StrikePrice == strike && i.OptionType == side)
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

    Console.WriteLine($"=== Volume-bar trade simulation: metric={metric}, entry-percentile>={entryPercentile}, bar-threshold={tradeThreshold}, stopLoss={(stopLossPercent is { } slp ? $"{slp:P0}" : "none")}, rolling={(rollingSubBarThreshold is { } rsbt ? $"{rsbt}-wide" : "no")}, depthBandWidth={tradeDepthBandWidth} ===");
    var allTrades = new List<VolumeBarTrade>();
    for (var date = tradeFromDate; date <= tradeToDate; date = date.AddDays(1))
    {
        var dayTrades = await RunRangeAsync(date, date, metric, entryPercentile, trendWindowBars, tradeThreshold, stopLossPercent, rollingSubBarThreshold, tradeDepthBandWidth, tradeSwitchTime, tradeEntryStart);
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
    // 2026-09-20, item 13 follow-up: only meaningful for FinalScoreSessionWeighted -- weight-on-FuturesScore
    // per session phase (OptionsScore always gets 1 minus it), overriding the 0.7/0.5/0.3 default so the
    // 3 phase weights can be swept via calibrate without recompiling.
    var calWOpen = calNamed.TryGetValue("wopen", out var calWOpenStr) ? double.Parse(calWOpenStr) : (double?)null;
    var calWMid = calNamed.TryGetValue("wmid", out var calWMidStr) ? double.Parse(calWMidStr) : (double?)null;
    var calWClose = calNamed.TryGetValue("wclose", out var calWCloseStr) ? double.Parse(calWCloseStr) : (double?)null;
    (double Open, double Mid, double Close)? calSessionWeights = calWOpen is not null || calWMid is not null || calWClose is not null
        ? (calWOpen ?? 0.7, calWMid ?? 0.5, calWClose ?? 0.3)
        : null;
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

                trades.AddRange(await RunRangeAsync(date, date, calMetric, percentile, 15, threshold, stopLossPercent: null, rollingSubBarThreshold: calRollingSubBarThreshold, depthBandWidth: calDepthBandWidth, optionsSwitchTime: calSwitchTime, entryWindowStartOverride: calEntryStart, sessionWeightsOnFutures: calSessionWeights));
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
