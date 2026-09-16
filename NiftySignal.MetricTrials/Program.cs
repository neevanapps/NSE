using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NiftySignal.BacktestData;
using NiftySignal.Domain.Enums;
using NiftySignal.MetricTrials;

// 2026-09-13: Core score option trade simulation -- the current combined-score candidate (8
// terms: DepthImbalance/ItmSkew/FutureCvdNet5Min/NotionalVolumeRatio/GammaExposure/
// TrendReversion15m/BasisChange/OiChangeDiff15m, see CoreScoreOptionSimulator's own doc comment
// for weights, signs, and the 11 Sep investigation that led to ItmSkew/GammaExposure being cut).
// Runs the SAME composite score through two different trading strategies for comparison:
// 1. Hysteresis threshold (SimulateDay) -- enters at +/-threshold, exits only once the score
//    crosses to the OPPOSITE threshold.
// 2. Fast/slow crossover (SimulateDayCrossover) -- EXPERIMENTAL (2026-09-13): smooths the same
//    score over two real-time trailing windows and trades the crossover between them, always
//    positioned once the first cross fires. See CoreScoreCrossoverOptions' own doc comment.
// The six earlier FutureCvdProxyNet5Min future-direct diagnostic variants that used to run here
// (2026-09-12, entry/exit-rank threshold trading and always-positioned EMA variants) are removed
// from this console tool's output -- that investigation concluded and fed into the Core score
// design; FutureDirectSimulator.cs itself is untouched and still covered by its own tests
// (NiftySignal.Tests/MetricTrials/FutureDirectSimulatorTests.cs), just no longer run/logged here.
//
// Usage: dotnet run --project NiftySignal.MetricTrials --
//   [--core-entry-score=N] [--core-entry-price-low=N] [--core-entry-price-high=N]
//   [--core-score-smoothing-cadences=N] [--crossover-fast-min=N] [--crossover-slow-min=N] [--next-week]
//   [--stop-loss-pct=N] [--max-consecutive-losses=N] (both Hysteresis only) [--correlation]
//   [--followups] (requires --correlation) [--date=yyyy-MM-dd] [--drop-itm-skew-and-gamma]
//   [--use-rolling-changes] [--invert-cvd] (composable with each other)
//
// Reads every populated day from niftysignal_backtest_analysis (read-only). Entry picks whichever
// strike is priced near [entryPriceRangeLow, entryPriceRangeHigh] (not ATM) for the signaled side
// (long call on a bullish score, long put on bearish), one position at a time, no SL/TP in either
// strategy. MAE/MFE are diagnostics only, never drive an exit.
double ParseOverride(string flag, double fallback) =>
    args.FirstOrDefault(a => a.StartsWith(flag, StringComparison.OrdinalIgnoreCase)) is { } arg
        ? double.Parse(arg[flag.Length..])
        : fallback;

bool HasFlag(string name) =>
    args.Contains($"--{name}", StringComparer.OrdinalIgnoreCase);

var hostProjectPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NiftySignal.Host");
var configuration = new ConfigurationBuilder()
    .SetBasePath(hostProjectPath)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var baseConnectionString = configuration.GetConnectionString("NiftySignalDb")
    ?? throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set in NiftySignal.Host/appsettings.Local.json.");
var connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = CadencePopulator.BacktestAnalysisDatabaseName }.ConnectionString;

var dbOptions = new DbContextOptionsBuilder<BacktestAnalysisDbContext>().UseNpgsql(connectionString).Options;
await using var db = new BacktestAnalysisDbContext(dbOptions);

var asOfDates = await db.CadenceContexts.Select(c => c.AsOfDate).Distinct().OrderBy(d => d).ToListAsync();
if (asOfDates.Count == 0)
{
    Console.Error.WriteLine("No populated days found in niftysignal_backtest_analysis.CadenceContexts.");
    return 1;
}

// --date=yyyy-MM-dd (2026-09-16): restrict the whole run to one day, e.g. to isolate --next-week's
// effect on just today without the other 4 days' totals diluting/obscuring it.
var dateFilterArg = args.FirstOrDefault(a => a.StartsWith("--date=", StringComparison.OrdinalIgnoreCase));
if (dateFilterArg is not null)
{
    if (!DateOnly.TryParseExact(dateFilterArg["--date=".Length..], "yyyy-MM-dd", out var dateFilter))
    {
        Console.Error.WriteLine("Usage: --date=yyyy-MM-dd");
        return 1;
    }

    asOfDates = asOfDates.Where(d => d == dateFilter).ToList();
    if (asOfDates.Count == 0)
    {
        Console.Error.WriteLine($"{dateFilter:yyyy-MM-dd}: not populated in niftysignal_backtest_analysis.");
        return 1;
    }
}

// .ToOffset(IST) -- CadenceContext.Timestamp is stored UTC (Npgsql's own requirement, see
// CadencePopulator's own comment on this), so printing EntryTime/ExitTime directly would show
// UTC wall-clock time, not the IST everyone reading this output actually thinks in.
string FormatIst(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(5.5)).ToString("HH:mm:ss");

void PrintCoreTrade(CoreScoreTrade trade)
{
    var side = trade.Side == NiftySignal.Domain.Enums.OptionType.Call ? "Call" : "Put";
    var duration = trade.ExitTime - trade.EntryTime;
    Console.WriteLine($"    {FormatIst(trade.EntryTime)} Long {side}@{trade.StrikePrice} entry={trade.EntryPrice:F2} score={trade.EntryScore:F1} -> {FormatIst(trade.ExitTime)} exit={trade.ExitPrice:F2} ({trade.ExitReason}, {duration.TotalMinutes:F1}min) MFE={trade.Mfe:F2} MAE={trade.Mae:F2} netPnl={trade.NetPnlPoints:F2} ({trade.NetPnlPercent:F1}%)");
}

void PrintDayResult(DateOnly asOfDate, DateOnly scoredExpiry, string expiryLabel, CoreScoreDayResult result)
{
    var dayWinRate = result.Trades.Count > 0 ? 100.0 * result.WinCount / result.Trades.Count : 0;
    Console.WriteLine($"{asOfDate:yyyy-MM-dd} ({expiryLabel}={scoredExpiry:yyyy-MM-dd}): {result.Trades.Count} trades, {dayWinRate:F1}% win rate, net {result.NetPnlPoints:F2} pts ({result.TotalPnlPercent:F1}%, avgPerTrade={result.AvgPnlPercent:F1}%), avgMFE={result.AvgMfe:F2} avgMAE={result.AvgMae:F2}");
    foreach (var trade in result.Trades)
    {
        PrintCoreTrade(trade);
    }
}

void PrintOverall(string label, IReadOnlyList<CoreScoreDayResult> dayResults)
{
    var allTrades = dayResults.SelectMany(r => r.Trades).ToList();
    var totalPnl = allTrades.Sum(t => t.NetPnlPoints);
    var totalPnlPercent = allTrades.Sum(t => t.NetPnlPercent);
    var winRate = allTrades.Count > 0 ? 100.0 * allTrades.Count(t => t.NetPnlPoints > 0) / allTrades.Count : 0;
    var avgMfe = allTrades.Count > 0 ? allTrades.Average(t => t.Mfe) : 0;
    var avgMae = allTrades.Count > 0 ? allTrades.Average(t => t.Mae) : 0;
    Console.WriteLine($"--- {label} total: {allTrades.Count} trades, {winRate:F1}% win rate, net {totalPnl:F2} pts ({totalPnlPercent:F1}%), avgMFE={avgMfe:F2} avgMAE={avgMae:F2} ---");
    Console.WriteLine();
}

// Load each day's raw rows ONCE, reused by both strategies below -- avoids two round-trips per day.
// --next-week (2026-09-15, experimental -- prompted by the 09-15 Hysteresis blowup: a 0-DTE
// ThisWeek chain's theta cliff turned an already-known "stuck score" gap (first seen 09-11) into a
// -58%/-72% loss on two trades held 135/172 minutes. NextWeek's chain is never 0-DTE, so this asks
// whether scoring+trading OFF THAT CHAIN INSTEAD removes the decay tail risk. Every raw metric
// computation, weight, and rule inside CoreScoreOptionSimulator is completely unchanged -- it never
// hardcodes "ThisWeek", it just scores+trades whichever expiry's rows it's handed. Only which
// expiry we hand it changes here. Days with no second (NextWeek) expiry present that trading day
// are skipped for this mode -- StrikeCadenceSnapshot persists both chains side by side (see its own
// doc comment), but a day right at rollover could still be missing one.
var useNextWeek = HasFlag("next-week");
var dayData = new List<(DateOnly AsOfDate, DateOnly ScoredExpiry, IReadOnlyList<StrikeCadenceSnapshot> StrikeRows, IReadOnlyList<CadenceContext> CadenceContexts)>();
foreach (var asOfDate in asOfDates)
{
    var strikeRows = await db.StrikeCadenceSnapshots.Where(s => s.AsOfDate == asOfDate).ToListAsync();
    if (strikeRows.Count == 0)
    {
        continue;
    }

    var cadenceContexts = await db.CadenceContexts.Where(c => c.AsOfDate == asOfDate).ToListAsync();
    // ThisWeek = nearest ExpiryDate, NextWeek = the one after -- per the 2026-09-13 standing rule
    // NextWeek is dropped for the DEFAULT run below; --next-week opts into scoring+trading it
    // instead, not in addition.
    var distinctExpiries = strikeRows.Select(s => s.ExpiryDate).Distinct().OrderBy(e => e).ToList();
    if (useNextWeek)
    {
        if (distinctExpiries.Count < 2)
        {
            Console.WriteLine($"{asOfDate:yyyy-MM-dd}: no NextWeek expiry chain present -- skipping for --next-week.");
            continue;
        }

        dayData.Add((asOfDate, distinctExpiries[1], strikeRows, cadenceContexts));
    }
    else
    {
        dayData.Add((asOfDate, distinctExpiries[0], strikeRows, cadenceContexts));
    }
}

var expiryLabel = useNextWeek ? "NextWeek" : "ThisWeek";
Console.WriteLine(useNextWeek
    ? "*** Scoring and trading the NEXT-WEEK expiry chain instead of ThisWeek (experimental, --next-week) ***"
    : "*** Scoring and trading the ThisWeek (near) expiry chain (default) ***");
Console.WriteLine();

// --correlation (2026-09-16): user watched live today and reported the score reading bullish
// (and the fast/slow crossover reading bullish) WHILE Nifty was actually falling -- i.e. possibly
// the wrong sign, not just noise. Checks that directly and quantitatively rather than by eyeballing
// trade logs: for every cadence with a CoreScore, does its sign (and separately the fast-vs-slow
// crossover's sign) actually predict the FORWARD price move over the next 5/15 real minutes? Uses
// CadenceContext.FutureChangeForDay (cumulative since day open, leakage-safe as a per-cadence
// value -- see that class's own doc comment) so the "forward move" is just a difference of two
// already-computed cumulative values, no new lookahead-unsafe field needed. This is diagnostic only
// -- it doesn't feed into or change SimulateDay/SimulateDayCrossover/SimulateDayCombined at all.
if (HasFlag("correlation"))
{
    foreach (var day in dayData)
    {
        var diagnostics = CoreScoreOptionSimulator.BuildDiagnostics(day.StrikeRows, day.CadenceContexts, day.ScoredExpiry,
            fastWindowMinutes: (int)ParseOverride("--crossover-fast-min=", 5.0), slowWindowMinutes: (int)ParseOverride("--crossover-slow-min=", 15.0));

        var contextsByTime = day.CadenceContexts.Where(c => c.FutureChangeForDay is not null).OrderBy(c => c.Timestamp).ToList();

        decimal? ForwardMove(DateTimeOffset from, TimeSpan horizon)
        {
            var atOrBefore = contextsByTime.LastOrDefault(c => c.Timestamp <= from);
            var atOrAfter = contextsByTime.FirstOrDefault(c => c.Timestamp >= from + horizon);
            return atOrBefore is not null && atOrAfter is not null
                ? atOrAfter.FutureChangeForDay - atOrBefore.FutureChangeForDay
                : null;
        }

        // Strike-identity-safe option-price forward move (moved up from Follow-up 1b so the main
        // per-component check below can use it too, not just the ItmSkew follow-up): the ATM CALL
        // strike is resolved ONCE at the starting cadence and that SAME strike's own forward-filled
        // mark price is read at t+horizon -- never "whichever strike is ATM by the time the window
        // ends," which would silently splice across an ATM re-centering event as spot moves (the
        // exact discipline this project's own CVD/PCR/OI-diff investigations learned the hard way).
        var expiryRowsForOption = day.StrikeRows.Where(r => r.ExpiryDate == day.ScoredExpiry).ToList();
        var filledMark = expiryRowsForOption
            .GroupBy(r => (r.OptionType, r.StrikePrice))
            .ToDictionary(g => g.Key, g =>
            {
                decimal? last = null;
                var series = new Dictionary<DateTimeOffset, decimal?>();
                foreach (var row in g.OrderBy(r => r.Timestamp))
                {
                    last = row.MarkPrice ?? last;
                    series[row.Timestamp] = last;
                }
                return series;
            });
        var atmCallStrikeByTime = expiryRowsForOption
            .Where(r => r.OptionType == OptionType.Call && r.StrikeOffsetFromAtm == 0)
            .GroupBy(r => r.Timestamp)
            .ToDictionary(g => g.Key, g => g.First().StrikePrice);
        var timestampsSorted = atmCallStrikeByTime.Keys.OrderBy(t => t).ToList();

        decimal? ForwardOptionMove(DateTimeOffset from, TimeSpan horizon)
        {
            var atOrBeforeTs = timestampsSorted.LastOrDefault(t => t <= from);
            if (atOrBeforeTs == default || !atmCallStrikeByTime.TryGetValue(atOrBeforeTs, out var strike))
            {
                return null;
            }

            if (!filledMark.TryGetValue((OptionType.Call, strike), out var series))
            {
                return null;
            }

            var startTs = series.Keys.Where(t => t <= from).DefaultIfEmpty().Max();
            var endTs = series.Keys.Where(t => t >= from + horizon).DefaultIfEmpty().Min();
            if (startTs == default || endTs == default || series[startTs] is not { } startMark || series[endTs] is not { } endMark)
            {
                return null;
            }

            return endMark - startMark;
        }

        void ReportBucket(string label, IEnumerable<(double Signal, decimal Move)> pairs)
        {
            var list = pairs.ToList();
            if (list.Count == 0)
            {
                Console.WriteLine($"    {label}: no observations.");
                return;
            }

            var bullishRight = list.Count(p => p.Signal > 0 && p.Move > 0);
            var bullishWrong = list.Count(p => p.Signal > 0 && p.Move < 0);
            var bearishRight = list.Count(p => p.Signal < 0 && p.Move < 0);
            var bearishWrong = list.Count(p => p.Signal < 0 && p.Move > 0);
            var signalCount = bullishRight + bullishWrong + bearishRight + bearishWrong;
            var hitRate = signalCount > 0 ? 100.0 * (bullishRight + bearishRight) / signalCount : 0;

            var meanS = list.Average(p => p.Signal);
            var meanM = list.Average(p => (double)p.Move);
            var cov = list.Sum(p => (p.Signal - meanS) * ((double)p.Move - meanM));
            var sdS = Math.Sqrt(list.Sum(p => Math.Pow(p.Signal - meanS, 2)));
            var sdM = Math.Sqrt(list.Sum(p => Math.Pow((double)p.Move - meanM, 2)));
            var correlation = sdS > 0 && sdM > 0 ? cov / (sdS * sdM) : double.NaN;

            Console.WriteLine($"    {label}: n={list.Count}, correlation={correlation:F3}, directional hit rate={hitRate:F1}% over {signalCount} signed cadences (bullish-right={bullishRight}, bullish-wrong={bullishWrong}, bearish-right={bearishRight}, bearish-wrong={bearishWrong})");
        }

        Console.WriteLine($"=== Correlation check: {day.AsOfDate:yyyy-MM-dd} ({expiryLabel}={day.ScoredExpiry:yyyy-MM-dd}) ===");
        ReportBucket("CoreScore sign vs forward 5-min move", diagnostics
            .Where(d => d.CoreScore is not null)
            .Select(d => (d.CoreScore!.Value, ForwardMove(d.Timestamp, TimeSpan.FromMinutes(5))))
            .Where(p => p.Item2 is not null)
            .Select(p => (p.Item1, p.Item2!.Value)));
        ReportBucket("CoreScore sign vs forward 15-min move", diagnostics
            .Where(d => d.CoreScore is not null)
            .Select(d => (d.CoreScore!.Value, ForwardMove(d.Timestamp, TimeSpan.FromMinutes(15))))
            .Where(p => p.Item2 is not null)
            .Select(p => (p.Item1, p.Item2!.Value)));
        ReportBucket("Fast-minus-slow sign vs forward 5-min move", diagnostics
            .Where(d => d.CoreScoreFast is not null && d.CoreScoreSlow is not null)
            .Select(d => (d.CoreScoreFast!.Value - d.CoreScoreSlow!.Value, ForwardMove(d.Timestamp, TimeSpan.FromMinutes(5))))
            .Where(p => p.Item2 is not null)
            .Select(p => (p.Item1, p.Item2!.Value)));
        ReportBucket("Fast-minus-slow sign vs forward 15-min move", diagnostics
            .Where(d => d.CoreScoreFast is not null && d.CoreScoreSlow is not null)
            .Select(d => (d.CoreScoreFast!.Value - d.CoreScoreSlow!.Value, ForwardMove(d.Timestamp, TimeSpan.FromMinutes(15))))
            .Where(p => p.Item2 is not null)
            .Select(p => (p.Item1, p.Item2!.Value)));

        // Per-component breakdown (15-min horizon only, to keep this readable) -- pinpoints WHICH
        // of the 8 terms is driving an aggregate CoreScore breakdown like today's, rather than
        // leaving it as one unexplained composite number.
        (string Name, Func<CoreScoreCadenceDiagnostics, double?> Signed)[] components =
        [
            ("DepthImbalance", d => d.DepthImbalanceSigned),
            ("ItmSkew", d => d.ItmSkewSigned),
            ("FutureCvdNet5Min", d => d.FutureCvdNet5MinSigned),
            ("NotionalVolumeRatio", d => d.NotionalVolumeRatioSigned),
            ("GammaExposure", d => d.GammaExposureSigned),
            ("TrendReversion15m", d => d.TrendReversion15mSigned),
            ("BasisChange", d => d.BasisChangeSigned),
            ("OiChangeDiff15m", d => d.OiChangeDiff15mSigned),
        ];
        foreach (var (name, signed) in components)
        {
            ReportBucket($"  component {name} vs forward 5-min move", diagnostics
                .Select(d => (Signed: signed(d), Move: ForwardMove(d.Timestamp, TimeSpan.FromMinutes(5))))
                .Where(p => p.Signed is not null && p.Move is not null)
                .Select(p => (p.Signed!.Value, p.Move!.Value)));
            ReportBucket($"  component {name} vs forward 15-min move", diagnostics
                .Select(d => (Signed: signed(d), Move: ForwardMove(d.Timestamp, TimeSpan.FromMinutes(15))))
                .Where(p => p.Signed is not null && p.Move is not null)
                .Select(p => (p.Signed!.Value, p.Move!.Value)));
        }

        // 2026-09-17: FutureCvdNet5Min's own original SCORE_CANDIDATES.md confirmation was checked
        // at its own 5-min net window's matching forward horizon (5-min), not 15-min, and only
        // against the future's price -- the 15-min component check above is a different test.
        // Filling that gap here (vs OPTION price, both horizons) before drawing any conclusion about
        // whether this term is actually behaving as documented in this composite specifically.
        ReportBucket("  component FutureCvdNet5Min vs OPTION (ATM call) forward 5-min move", diagnostics
            .Select(d => (Signed: d.FutureCvdNet5MinSigned, Move: ForwardOptionMove(d.Timestamp, TimeSpan.FromMinutes(5))))
            .Where(p => p.Signed is not null && p.Move is not null)
            .Select(p => (p.Signed!.Value, p.Move!.Value)));
        ReportBucket("  component FutureCvdNet5Min vs OPTION (ATM call) forward 15-min move", diagnostics
            .Select(d => (Signed: d.FutureCvdNet5MinSigned, Move: ForwardOptionMove(d.Timestamp, TimeSpan.FromMinutes(15))))
            .Where(p => p.Signed is not null && p.Move is not null)
            .Select(p => (p.Signed!.Value, p.Move!.Value)));

        // 2026-09-17: isolate WHY the day-pattern above doesn't match this file's own original
        // finding ("positive on 3 of 4 days; 10 Sep is the recurring exception") -- is it the
        // percentile-rank transform (RankSigned/SessionRankTracker) changing which days look good,
        // or does the raw CadenceContext.FutureCvdProxyNet5Min value itself already show a different
        // day pattern than originally reported? Correlates the RAW value directly, bypassing
        // BuildScoreCadences/RankSigned entirely -- same raw column, no transform at all.
        var rawCvdPairs5 = day.CadenceContexts
            .Where(c => c.FutureCvdProxyNet5Min is not null)
            .Select(c => (Signal: (double)c.FutureCvdProxyNet5Min!.Value, Move: ForwardMove(c.Timestamp, TimeSpan.FromMinutes(5))))
            .Where(p => p.Move is not null)
            .Select(p => (p.Signal, p.Move!.Value));
        var rawCvdPairs15 = day.CadenceContexts
            .Where(c => c.FutureCvdProxyNet5Min is not null)
            .Select(c => (Signal: (double)c.FutureCvdProxyNet5Min!.Value, Move: ForwardMove(c.Timestamp, TimeSpan.FromMinutes(15))))
            .Where(p => p.Move is not null)
            .Select(p => (p.Signal, p.Move!.Value));
        ReportBucket("  RAW (untransformed) FutureCvdProxyNet5Min vs forward 5-min move", rawCvdPairs5);
        ReportBucket("  RAW (untransformed) FutureCvdProxyNet5Min vs forward 15-min move", rawCvdPairs15);

        // 2026-09-17 (user's own question): the composite only ever reads FutureCvdProxyNet5Min --
        // never Net15Min, even though CadencePopulator computes both and the ORIGINAL SQL script
        // (validate-and-correlate-cvd-rolling.sql) tested both side by side. ItmSkew's own fix
        // turned out to be "wrong horizon", not "wrong sign" -- worth checking whether the same is
        // true here before concluding the 5-min window itself is the problem.
        var rawCvd15Pairs5 = day.CadenceContexts
            .Where(c => c.FutureCvdProxyNet15Min is not null)
            .Select(c => (Signal: (double)c.FutureCvdProxyNet15Min!.Value, Move: ForwardMove(c.Timestamp, TimeSpan.FromMinutes(5))))
            .Where(p => p.Move is not null)
            .Select(p => (p.Signal, p.Move!.Value));
        var rawCvd15Pairs15 = day.CadenceContexts
            .Where(c => c.FutureCvdProxyNet15Min is not null)
            .Select(c => (Signal: (double)c.FutureCvdProxyNet15Min!.Value, Move: ForwardMove(c.Timestamp, TimeSpan.FromMinutes(15))))
            .Where(p => p.Move is not null)
            .Select(p => (p.Signal, p.Move!.Value));
        ReportBucket("  RAW (untransformed) FutureCvdProxyNet15Min vs forward 5-min move", rawCvd15Pairs5);
        ReportBucket("  RAW (untransformed) FutureCvdProxyNet15Min vs forward 15-min move", rawCvd15Pairs15);

        // 2026-09-17: reproduce scripts/validate-and-correlate-cvd-rolling.sql's EXACT forward-move
        // method -- a fixed ROW-COUNT LEAD(20) on FutureCloseFromLastCadence (20 rows * 15s = 5min,
        // ASSUMING no gaps in the cadence sequence), not a time-based lookup on the cumulative
        // FutureChangeForDay like ForwardMove above. If cadences have any gaps (a quiet period with
        // no ticks), a 20-row lead stops meaning "5 real minutes" and the two methods diverge --
        // this isolates whether THAT is the source of the day-pattern mismatch.
        var contextsOrdered = day.CadenceContexts.OrderBy(c => c.Timestamp).ToList();
        IEnumerable<(double, decimal)> RowCountLeadPairs(int leadRows)
        {
            for (var i = 0; i + leadRows < contextsOrdered.Count; i++)
            {
                var c = contextsOrdered[i];
                var future = contextsOrdered[i + leadRows];
                if (c.FutureCvdProxyNet5Min is { } cvd && c.FutureCloseFromLastCadence is { } nowClose && future.FutureCloseFromLastCadence is { } laterClose)
                {
                    yield return ((double)cvd, laterClose - nowClose);
                }
            }
        }
        ReportBucket("  SQL-METHOD (row-count LEAD(20), FutureCloseFromLastCadence) FutureCvdProxyNet5Min vs fwd 5-min", RowCountLeadPairs(20));
        ReportBucket("  SQL-METHOD (row-count LEAD(60), FutureCloseFromLastCadence) FutureCvdProxyNet5Min vs fwd 15-min", RowCountLeadPairs(60));

        // Gap check: how many row-count-based 5-min leads actually cover LESS or MORE than 5 real
        // minutes of elapsed time -- direct evidence for/against the row-count-vs-time-based theory.
        var gapMismatches = 0;
        var totalChecked = 0;
        for (var i = 0; i + 20 < contextsOrdered.Count; i++)
        {
            totalChecked++;
            var elapsed = contextsOrdered[i + 20].Timestamp - contextsOrdered[i].Timestamp;
            if (Math.Abs((elapsed - TimeSpan.FromMinutes(5)).TotalSeconds) > 15)
            {
                gapMismatches++;
            }
        }
        Console.WriteLine($"    Gap check: {gapMismatches} of {totalChecked} row-count-20-lead windows deviate from 5 real minutes by more than 15s");

        // --followups (2026-09-16): four targeted, economically-motivated re-checks of ItmSkew and
        // GammaExposure specifically, agreed after the 2026-09-16 revision downgraded both -- NOT a
        // parameter/weight grid search over the trading strategy (that risks the exact
        // single-day-artifact trap that closed OiBuildupNet/live-Pcr/FuturesBasis; see
        // docs/SCORE_CANDIDATES.md). Each is a metric-level correlation check, same discipline as
        // every other candidate in that file.
        if (HasFlag("followups"))
        {
            // Follow-up 1 (REVISED 2026-09-16, same session): a real shift in market demand/supply
            // plays out over MINUTES, not one 15-second tick to the next -- exactly why every other
            // change-based metric in this project (FutureCvdProxyNet5Min, BasisChange, OiChangeDiff15m)
            // is a multi-minute ROLLING WINDOW, never a single-cadence delta. The original version of
            // this follow-up used a cadence-to-cadence (~15s) delta, which tests noise, not the same
            // "change over time" idea those metrics validated -- corrected here to a proper 5-min and
            // 15-min rolling-window change (current level minus the level N real minutes ago, nearest
            // cadence at or before that point), same convention as `BasisChange`'s own rolling-sum
            // version. Applied to BOTH `ItmSkew` (the level was found structurally one-sided) and
            // `GammaExposure` (never tested as a change at all until now -- "is dealer positioning
            // building up or unwinding" is arguably a more natural demand/supply-shift read than its
            // raw level).
            IEnumerable<(double Signal, decimal Move)> RollingChangeVsForward(
                Func<CoreScoreCadenceDiagnostics, double?> selector, TimeSpan window, TimeSpan forwardHorizon,
                Func<DateTimeOffset, TimeSpan, decimal?>? forwardFn = null)
            {
                forwardFn ??= ForwardMove;
                var withValue = diagnostics.Where(d => selector(d) is not null).OrderBy(d => d.Timestamp).ToList();
                foreach (var d in withValue)
                {
                    var pastPoint = withValue.LastOrDefault(p => p.Timestamp <= d.Timestamp - window);
                    if (pastPoint is null)
                    {
                        continue;
                    }

                    var delta = selector(d)!.Value - selector(pastPoint)!.Value;
                    if (forwardFn(d.Timestamp, forwardHorizon) is { } move)
                    {
                        yield return (delta, move);
                    }
                }
            }

            foreach (var (name, selector) in new (string, Func<CoreScoreCadenceDiagnostics, double?>)[]
                     {
                         ("ItmSkew", d => d.ItmSkewRaw),
                         ("GammaExposure", d => d.GammaExposureRaw),
                     })
            {
                foreach (var windowMinutes in new[] { 5, 15 })
                {
                    ReportBucket($"Follow-up 1: {name} {windowMinutes}-min ROLLING CHANGE vs forward 5-min move",
                        RollingChangeVsForward(selector, TimeSpan.FromMinutes(windowMinutes), TimeSpan.FromMinutes(5)));
                    ReportBucket($"Follow-up 1: {name} {windowMinutes}-min ROLLING CHANGE vs forward 15-min move",
                        RollingChangeVsForward(selector, TimeSpan.FromMinutes(windowMinutes), TimeSpan.FromMinutes(15)));
                }
            }

            // Follow-up 1b (2026-09-17): the rolling-change checks above only checked the FUTURE's
            // price (CadenceContext.FutureChangeForDay). ItmSkew's original Table 3 confirmation
            // checked BOTH targets (option and future) before being trusted -- do the same here,
            // using the shared ForwardOptionMove defined above (also used by the main per-component
            // check further up).
            {
                foreach (var windowMinutes in new[] { 5, 15 })
                {
                    ReportBucket($"Follow-up 1b: ItmSkew {windowMinutes}-min ROLLING CHANGE vs OPTION (ATM call) forward 5-min move",
                        RollingChangeVsForward(d => d.ItmSkewRaw, TimeSpan.FromMinutes(windowMinutes), TimeSpan.FromMinutes(5), ForwardOptionMove));
                    ReportBucket($"Follow-up 1b: ItmSkew {windowMinutes}-min ROLLING CHANGE vs OPTION (ATM call) forward 15-min move",
                        RollingChangeVsForward(d => d.ItmSkewRaw, TimeSpan.FromMinutes(windowMinutes), TimeSpan.FromMinutes(15), ForwardOptionMove));
                }
            }

            // Follow-up 2: SAME-STRIKE call/put IV skew, not the cross-strike Itm2Atm1 band (ITM
            // calls at low strikes vs ITM puts at high strikes -- the band identified as the
            // structural cause of ItmSkew's permanent one-sidedness). Forward-fills IV per
            // (OptionType, StrikePrice), same convention BuildScoreCadences itself uses, then
            // averages PutIv-CallIv only across strikes quoted on BOTH sides at that cadence.
            {
                var expiryRows = day.StrikeRows.Where(r => r.ExpiryDate == day.ScoredExpiry).ToList();
                var filledIv = expiryRows
                    .GroupBy(r => (r.OptionType, r.StrikePrice))
                    .SelectMany(g =>
                    {
                        double? last = null;
                        var rows = new List<(DateTimeOffset Timestamp, OptionType OptionType, decimal StrikePrice, double? Iv)>();
                        foreach (var row in g.OrderBy(r => r.Timestamp))
                        {
                            last = row.ImpliedVolatility ?? last;
                            rows.Add((row.Timestamp, g.Key.OptionType, g.Key.StrikePrice, last));
                        }
                        return rows;
                    })
                    .ToList();

                var pairs5 = new List<(double, decimal)>();
                var pairs15 = new List<(double, decimal)>();
                foreach (var group in filledIv.GroupBy(x => x.Timestamp))
                {
                    var calls = group.Where(x => x.OptionType == OptionType.Call && x.Iv is not null).ToDictionary(x => x.StrikePrice, x => x.Iv!.Value);
                    var puts = group.Where(x => x.OptionType == OptionType.Put && x.Iv is not null).ToDictionary(x => x.StrikePrice, x => x.Iv!.Value);
                    var diffs = calls.Keys.Intersect(puts.Keys).Select(k => puts[k] - calls[k]).ToList();
                    if (diffs.Count == 0)
                    {
                        continue;
                    }

                    var avgDiff = diffs.Average();
                    if (ForwardMove(group.Key, TimeSpan.FromMinutes(5)) is { } m5) pairs5.Add((avgDiff, m5));
                    if (ForwardMove(group.Key, TimeSpan.FromMinutes(15)) is { } m15) pairs15.Add((avgDiff, m15));
                }
                ReportBucket("Follow-up 2: SAME-STRIKE Put-Call IV skew vs forward 5-min move", pairs5);
                ReportBucket("Follow-up 2: SAME-STRIKE Put-Call IV skew vs forward 15-min move", pairs15);
            }

            // Follow-up 3 (and 4 -- run once per chain via --next-week, same as everything else in
            // this file): does GammaExposure work when call/put OI is decisively imbalanced (large
            // |raw|) versus being noise near balance? Median-split by |GammaExposureRaw| for the
            // day, correlate each half separately against the same forward-15-min target.
            {
                var withRaw = diagnostics.Where(d => d.GammaExposureRaw is not null && d.GammaExposureSigned is not null).ToList();
                if (withRaw.Count > 0)
                {
                    var median = withRaw.Select(d => Math.Abs(d.GammaExposureRaw!.Value)).OrderBy(x => x).ElementAt(withRaw.Count / 2);
                    var highMag = withRaw.Where(d => Math.Abs(d.GammaExposureRaw!.Value) >= median)
                        .Select(d => (Signed: d.GammaExposureSigned!.Value, Move: ForwardMove(d.Timestamp, TimeSpan.FromMinutes(15))))
                        .Where(p => p.Move is not null).Select(p => (p.Signed, p.Move!.Value));
                    var lowMag = withRaw.Where(d => Math.Abs(d.GammaExposureRaw!.Value) < median)
                        .Select(d => (Signed: d.GammaExposureSigned!.Value, Move: ForwardMove(d.Timestamp, TimeSpan.FromMinutes(15))))
                        .Where(p => p.Move is not null).Select(p => (p.Signed, p.Move!.Value));
                    ReportBucket("Follow-up 3: GammaExposure, HIGH |raw| half (decisive OI tilt) vs forward 15-min", highMag);
                    ReportBucket("Follow-up 3: GammaExposure, LOW |raw| half (near-balanced OI) vs forward 15-min", lowMag);
                }
            }
        }

        Console.WriteLine();
    }

    return 0;
}

// --drop-itm-skew-and-gamma (2026-09-16, user's own hypothesis): ItmSkew and GammaExposure are
// both derived from option IV/Greeks (ItmSkew directly from PutAvgIv/CallAvgIv; GammaExposure's
// per-strike Gamma is itself computed FROM IV via Black-Scholes) -- checked separately and found
// BOTH permanently one-sided across every day/chain examined so far (ItmSkew: 0 bullish reads out
// of 7 day/chain combinations checked; GammaExposure: near-0 on 3 of 5 days). If that's a shared
// upstream IV problem, dropping both at once (weight -> 0, not just excluding a day) tests whether
// the composite does better without two terms that may be correlated-wrong rather than independent.
// Zero weight, not omission -- CompositeScoreCalculator-style "renormalize by present weight" logic
// in BuildScoreCadences already treats a zero-weight-but-present term as contributing nothing to
// either the numerator or the weight denominator, identical to the term being absent that cadence.
// --use-rolling-changes (2026-09-17): brings ItmSkew and GammaExposure back into the composite,
// NOT on their closed levels -- on the rolling-CHANGE reformulation that showed a real, dual-target
// (future AND option) confirmed signal for ItmSkew specifically (+0.12 to +0.19 vs future, +0.13 to
// +0.16 vs option, all 3 non-expiry ThisWeek days). GammaExposure's own change is far less
// consistent (window disagreement, incoherent on NextWeek) -- included per explicit instruction, at
// half the weight, since the evidence is weaker. Provisional weights, not tuned: 0.065 for
// ItmSkewChange15m matches its old (closed) level weight; 0.03 for GammaExposureChange5m is half of
// its old (closed) level weight, reflecting the thinner evidence.
var weights = HasFlag("drop-itm-skew-and-gamma") || HasFlag("use-rolling-changes")
    ? new CoreScoreWeights(ItmSkewWeight: 0, GammaExposureWeight: 0)
    : new CoreScoreWeights();
if (HasFlag("use-rolling-changes"))
{
    weights = weights with { ItmSkewChange15mWeight = 0.065, GammaExposureChange5mWeight = 0.03 };
}
// --invert-cvd (2026-09-17): composable with the flags above -- tests FutureCvdNet5Min's sign
// flipped, on top of whatever else is already configured. See CoreScoreWeights.InvertFutureCvdNet5Min's
// own doc comment for why this is being tested at all.
if (HasFlag("invert-cvd"))
{
    weights = weights with { InvertFutureCvdNet5Min = true };
}
// --drop-cvd (2026-09-17): the third option alongside keep-as-is and invert -- zero its weight
// entirely, same treatment ItmSkew/GammaExposure already got, since neither keep nor invert looked
// like a clean win on their own.
if (HasFlag("drop-cvd"))
{
    weights = weights with { FutureCvdNet5MinWeight = 0 };
}
if (HasFlag("drop-itm-skew-and-gamma"))
{
    Console.WriteLine("*** ItmSkew and GammaExposure weights zeroed out (--drop-itm-skew-and-gamma) ***");
    Console.WriteLine();
}
if (HasFlag("use-rolling-changes"))
{
    Console.WriteLine("*** ItmSkew/GammaExposure LEVELS zeroed; ItmSkewChange15m=0.065, GammaExposureChange5m=0.03 added (--use-rolling-changes) ***");
    Console.WriteLine();
}
if (HasFlag("invert-cvd"))
{
    Console.WriteLine("*** FutureCvdNet5Min sign INVERTED (--invert-cvd) ***");
    Console.WriteLine();
}

// --- Strategy 1: hysteresis threshold ---
var entryScoreThreshold = ParseOverride("--core-entry-score=", 30.0);
var entryPriceRangeLow = (decimal)ParseOverride("--core-entry-price-low=", 100.0);
var entryPriceRangeHigh = (decimal)ParseOverride("--core-entry-price-high=", 150.0);
var scoreSmoothingCadences = (int)ParseOverride("--core-score-smoothing-cadences=", 1.0);
var stopLossArg = args.FirstOrDefault(a => a.StartsWith("--stop-loss-pct=", StringComparison.OrdinalIgnoreCase));
var stopLossPct = stopLossArg is not null ? (decimal?)double.Parse(stopLossArg["--stop-loss-pct=".Length..]) : null;
var maxConsecutiveLossesArg = args.FirstOrDefault(a => a.StartsWith("--max-consecutive-losses=", StringComparison.OrdinalIgnoreCase));
var maxConsecutiveLosses = maxConsecutiveLossesArg is not null ? (int?)int.Parse(maxConsecutiveLossesArg["--max-consecutive-losses=".Length..]) : null;

Console.WriteLine($"=== Core score option simulation - HYSTERESIS THRESHOLD (entry-score>={entryScoreThreshold}, smoothing={scoreSmoothingCadences} cadence(s), strike price in [{entryPriceRangeLow},{entryPriceRangeHigh}], stop-loss={(stopLossPct is { } sl ? $"{sl}%" : "none")}, max-consecutive-losses={(maxConsecutiveLosses is { } mcl ? mcl.ToString() : "none")}) ===");
var hysteresisResults = new List<CoreScoreDayResult>();
foreach (var day in dayData)
{
    var result = CoreScoreOptionSimulator.SimulateDay(day.StrikeRows, day.CadenceContexts, day.ScoredExpiry,
        new CoreScoreSimulationOptions(EntryScoreThreshold: entryScoreThreshold,
            EntryPriceRangeLow: entryPriceRangeLow, EntryPriceRangeHigh: entryPriceRangeHigh,
            ScoreSmoothingCadences: scoreSmoothingCadences, StopLossPct: stopLossPct, MaxConsecutiveLosses: maxConsecutiveLosses, Weights: weights));
    hysteresisResults.Add(result);
    PrintDayResult(day.AsOfDate, day.ScoredExpiry, expiryLabel, result);
}
PrintOverall("Hysteresis threshold", hysteresisResults);

// --- Strategy 2: fast/slow crossover (experimental) ---
var crossoverFastMinutes = (int)ParseOverride("--crossover-fast-min=", 5.0);
var crossoverSlowMinutes = (int)ParseOverride("--crossover-slow-min=", 15.0);

Console.WriteLine($"=== Core score option simulation - FAST/SLOW CROSSOVER EXPERIMENT (fast={crossoverFastMinutes}min, slow={crossoverSlowMinutes}min, strike price in [{entryPriceRangeLow},{entryPriceRangeHigh}]) ===");
var crossoverResults = new List<CoreScoreDayResult>();
foreach (var day in dayData)
{
    var result = CoreScoreOptionSimulator.SimulateDayCrossover(day.StrikeRows, day.CadenceContexts, day.ScoredExpiry,
        new CoreScoreCrossoverOptions(FastWindowMinutes: crossoverFastMinutes, SlowWindowMinutes: crossoverSlowMinutes,
            EntryPriceRangeLow: entryPriceRangeLow, EntryPriceRangeHigh: entryPriceRangeHigh, Weights: weights));
    crossoverResults.Add(result);
    PrintDayResult(day.AsOfDate, day.ScoredExpiry, expiryLabel, result);
}
PrintOverall("Fast/slow crossover", crossoverResults);

// --- Strategy 3: combined (A=Hysteresis AND B=Crossover must agree to enter, experimental) ---
Console.WriteLine($"=== Core score option simulation - COMBINED A+B EXPERIMENT (entry-score>={entryScoreThreshold}, fast={crossoverFastMinutes}min, slow={crossoverSlowMinutes}min, strike price in [{entryPriceRangeLow},{entryPriceRangeHigh}]) ===");
foreach (var exitOnEither in new[] { true, false })
{
    var label = exitOnEither ? "exit on EITHER opposite" : "exit only when BOTH opposite";
    Console.WriteLine($"--- Combined, {label} ---");
    var combinedResults = new List<CoreScoreDayResult>();
    foreach (var day in dayData)
    {
        var result = CoreScoreOptionSimulator.SimulateDayCombined(day.StrikeRows, day.CadenceContexts, day.ScoredExpiry,
            new CoreScoreCombinedOptions(EntryScoreThreshold: entryScoreThreshold, FastWindowMinutes: crossoverFastMinutes, SlowWindowMinutes: crossoverSlowMinutes,
                EntryPriceRangeLow: entryPriceRangeLow, EntryPriceRangeHigh: entryPriceRangeHigh, ExitOnEitherOpposite: exitOnEither, Weights: weights));
        combinedResults.Add(result);
        PrintDayResult(day.AsOfDate, day.ScoredExpiry, expiryLabel, result);
    }
    PrintOverall($"Combined ({label})", combinedResults);
}

return 0;
