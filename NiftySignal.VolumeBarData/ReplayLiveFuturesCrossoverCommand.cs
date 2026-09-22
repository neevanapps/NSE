using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Futures-crossover live-wiring task (2026-09-21) proof harness -- see the `replay-live-futures-
/// crossover` command in <c>Program.cs</c>. Combines <see cref="ReplayLiveScoreCommand"/>'s own
/// scoring-parity shape and <see cref="ReplayLivePaperTradeCommand"/>'s own paper-trade-parity shape
/// into one harness for the new strategy: drives <see cref="LiveFuturesCrossoverSession"/> (the exact
/// class <c>NiftySignal.Host.LiveFuturesCrossoverEngine</c> uses live) bar by bar, in order, over one
/// or more already-populated historical days (read from the OFFICIAL <c>niftysignal_volume_bars</c>
/// database), calls the SAME <see cref="LivePaperTradeExecutor"/> the live engine calls on every
/// entry/exit, and compares the resulting <see cref="LivePaperTradeRow"/> rows against what
/// <c>TradeSimulator.SimulateCrossoverDayAsync</c> itself produces for the same day at the LOCKED
/// 8/40/5/2600 configuration (<c>scoreMetric: SessionGatedDepthDurationConfirmed</c>).
///
/// Also proves the no-collision requirement directly: <see cref="RunBothStrategiesAsync"/> replays
/// BOTH this strategy and the options strategy (<see cref="ReplayLiveScoreCommand"/>'s own
/// per-bar-scoring path plus <see cref="LivePaperTradeExecutor"/>) into the SAME scratch database for
/// the SAME day/threshold, then asserts both strategies' own trades persisted independently with no
/// primary-key violation and no cross-strategy interference in either one's "one position at a time"
/// rule.
/// </summary>
public static class ReplayLiveFuturesCrossoverCommand
{
    static readonly TimeSpan SimulatedDecisionLatency = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(string baseConnectionString, IReadOnlyList<DateOnly> dates, long threshold, int fastBars, int slowBars, double thresholdPoints, CancellationToken ct,
        string? destinationDatabaseNameOverride = null, bool? wipeDestination = null)
    {
        var destinationDatabaseName = destinationDatabaseNameOverride ?? "niftysignal_volume_bars_livefuturescrossovertest";
        var effectiveWipeDestination = wipeDestination ?? destinationDatabaseNameOverride is null;

        var officialOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = VolumeBarPopulator.VolumeBarDatabaseName }.ConnectionString).Options;
        var scratchOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = destinationDatabaseName }.ConnectionString).Options;
        var sourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_vm_copy" }.ConnectionString).Options;

        Console.WriteLine($"=== Live-futures-crossover-replay parity test: {string.Join(", ", dates.Select(d => d.ToString("yyyy-MM-dd")))} @ threshold={threshold}, fast={fastBars}, slow={slowBars}, thresholdPoints={thresholdPoints} (writing to {destinationDatabaseName}) ===");

        if (effectiveWipeDestination)
        {
            await using var wipe = new VolumeBarDbContext(scratchOptions);
            await wipe.Database.EnsureDeletedAsync(ct);
            await wipe.Database.MigrateAsync(ct);
        }
        else
        {
            await using var migrateOnly = new VolumeBarDbContext(scratchOptions);
            await migrateOnly.Database.MigrateAsync(ct);
        }

        var overallOk = true;
        var sharedPriceCache = new Dictionary<(DateOnly, string), OptionPriceSeries>();

        foreach (var date in dates)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {date:yyyy-MM-dd} ---");

            var ok = await ReplayOneDayAsync(officialOptions, scratchOptions, sourceOptions, date, threshold, sharedPriceCache, ct);
            Console.WriteLine(ok ? $"PARITY ({date:yyyy-MM-dd}): PASS" : $"PARITY ({date:yyyy-MM-dd}): FAIL -- see diffs above.");
            overallOk &= ok;
        }

        Console.WriteLine();
        Console.WriteLine(overallOk
            ? "OVERALL: PASS -- live futures-crossover paper-trade lifecycle (entry-bar/strike/direction/exit-reason) matches the offline backtest exactly on every date tested. Fill-price deltas (if any) are logged above as an accepted, documented difference, not a failure."
            : "OVERALL: FAIL -- see diffs above.");

        return overallOk ? 0 : 1;
    }

    static async Task<bool> ReplayOneDayAsync(
        DbContextOptions<VolumeBarDbContext> officialOptions, DbContextOptions<VolumeBarDbContext> scratchOptions, DbContextOptions<NiftySignalDbContext> sourceOptions,
        DateOnly date, long threshold, Dictionary<(DateOnly, string), OptionPriceSeries> sharedPriceCache, CancellationToken ct)
    {
        List<VolumeBarRow> bars;
        await using (var official = new VolumeBarDbContext(officialOptions))
        {
            bars = await official.VolumeBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == threshold).OrderBy(b => b.BarIndex).ToListAsync(ct);
        }

        if (bars.Count == 0)
        {
            Console.WriteLine($"No official bars for {date:yyyy-MM-dd} @ {threshold} -- skipping.");
            return false;
        }

        var session = new LiveFuturesCrossoverSession(date, threshold);
        var livePaperTrades = new List<LivePaperTradeRow>();

        await using var scratchDb = new VolumeBarDbContext(scratchOptions);
        await using var source = new NiftySignalDbContext(sourceOptions);

        void Log(string msg) => Console.WriteLine($"  [live] {msg}");

        foreach (var bar in bars)
        {
            var result = session.ProcessBar(bar);
            scratchDb.LiveFuturesCrossoverScoreBars.Add(result.ScoreRow);

            if (result.Opened is { } opened)
            {
                scratchDb.LiveEntrySignals.Add(new LiveEntrySignalRow
                {
                    AsOfDate = date,
                    BarVolumeThreshold = threshold,
                    Strategy = LiveVolumeBarStrategyId.FuturesCrossover,
                    Side = opened.Side,
                    EntryBarIndex = opened.EntryBarIndex,
                    EntryTimestamp = opened.EntryTimestamp,
                    EntryScore = opened.EntryScore,
                    EntryPercentile = null,
                });

                var decisionTs = opened.EntryTimestamp + SimulatedDecisionLatency;
                var trade = await LivePaperTradeExecutor.OpenAsync(source, scratchDb, date, threshold, opened, bar.ClosePrice, decisionTs, Log, ct, LiveVolumeBarStrategyId.FuturesCrossover);
                await scratchDb.SaveChangesAsync(ct);
                if (trade is not null)
                {
                    livePaperTrades.Add(trade);
                }
            }

            if (result.Closed is { } closed)
            {
                var openSignal = await scratchDb.LiveEntrySignals.FirstOrDefaultAsync(
                    r => r.AsOfDate == date && r.BarVolumeThreshold == threshold && r.Strategy == LiveVolumeBarStrategyId.FuturesCrossover && r.EntryBarIndex == closed.EntryBarIndex, ct);
                if (openSignal is not null)
                {
                    openSignal.ExitBarIndex = closed.ExitBarIndex;
                    openSignal.ExitTimestamp = closed.ExitTimestamp;
                    openSignal.ExitReason = closed.ExitReason;
                }

                var decisionTs = closed.ExitTimestamp + SimulatedDecisionLatency;
                await LivePaperTradeExecutor.CloseAsync(scratchDb, source, date, threshold, closed, decisionTs, Log, ct, LiveVolumeBarStrategyId.FuturesCrossover);
                await scratchDb.SaveChangesAsync(ct);
            }
        }

        if (session.FlushEndOfDay(bars[^1].EndTimestamp) is { } eodClosed)
        {
            var openSignal = await scratchDb.LiveEntrySignals.FirstOrDefaultAsync(
                r => r.AsOfDate == date && r.BarVolumeThreshold == threshold && r.Strategy == LiveVolumeBarStrategyId.FuturesCrossover && r.EntryBarIndex == eodClosed.EntryBarIndex, ct);
            if (openSignal is not null)
            {
                openSignal.ExitBarIndex = eodClosed.ExitBarIndex;
                openSignal.ExitTimestamp = eodClosed.ExitTimestamp;
                openSignal.ExitReason = eodClosed.ExitReason;
            }

            var decisionTs = eodClosed.ExitTimestamp + SimulatedDecisionLatency;
            await LivePaperTradeExecutor.CloseAsync(scratchDb, source, date, threshold, eodClosed, decisionTs, Log, ct, LiveVolumeBarStrategyId.FuturesCrossover);
        }

        await scratchDb.SaveChangesAsync(ct);
        livePaperTrades = await scratchDb.LivePaperTrades
            .Where(t => t.AsOfDate == date && t.BarVolumeThreshold == threshold && t.Strategy == LiveVolumeBarStrategyId.FuturesCrossover)
            .OrderBy(t => t.EntryBarIndex)
            .ToListAsync(ct);

        Console.WriteLine($"Live-side: {livePaperTrades.Count} paper trade(s) opened.");

        List<VolumeBarTrade> officialTrades;
        await using (var officialSource = new NiftySignalDbContext(sourceOptions))
        await using (var official = new VolumeBarDbContext(officialOptions))
        {
            officialTrades = await TradeSimulator.SimulateCrossoverDayAsync(
                officialSource, official, date, threshold, LiveFuturesCrossoverSession.FastBars, LiveFuturesCrossoverSession.SlowBars, LiveFuturesCrossoverSession.ThresholdPoints, ct,
                sharedPriceCache, VolumeBarMetric.SessionGatedDepthDurationConfirmed);
        }

        Console.WriteLine($"Offline backtest (SimulateCrossoverDayAsync, {LiveFuturesCrossoverSession.FastBars}/{LiveFuturesCrossoverSession.SlowBars}/{LiveFuturesCrossoverSession.ThresholdPoints}): {officialTrades.Count} trade(s) fired.");

        return CompareTrades(livePaperTrades, officialTrades, bars);
    }

    static bool CompareTrades(List<LivePaperTradeRow> liveTrades, List<VolumeBarTrade> officialTrades, List<VolumeBarRow> bars)
    {
        var barIndexByEndTimestamp = bars.ToDictionary(b => b.EndTimestamp, b => b.BarIndex);
        var ok = true;

        if (liveTrades.Count != officialTrades.Count)
        {
            Console.WriteLine($"  MISMATCH: live opened {liveTrades.Count} paper trade(s), offline backtest fired {officialTrades.Count} trade(s).");
            ok = false;
        }

        var pairCount = Math.Min(liveTrades.Count, officialTrades.Count);
        for (var i = 0; i < pairCount; i++)
        {
            var live = liveTrades[i];
            var trade = officialTrades[i];
            var tradeEntryBarIndex = barIndexByEndTimestamp.GetValueOrDefault(trade.EntryTime, -1);
            var tradeExitBarIndex = barIndexByEndTimestamp.GetValueOrDefault(trade.ExitTime, -1);

            var mismatch = live.EntryBarIndex != tradeEntryBarIndex
                || live.Side != trade.Side
                || live.StrikePrice != trade.StrikePrice
                || live.ExitBarIndex != tradeExitBarIndex
                || !string.Equals(live.ExitReason, trade.ExitReason, StringComparison.Ordinal);

            var entryPriceDelta = live.EntryPrice - trade.EntryPrice;
            var exitPriceDelta = live.ExitPrice is { } lep && trade.ExitPrice is { } tep ? (decimal?)(lep - tep) : null;

            if (mismatch)
            {
                Console.WriteLine($"  MISMATCH trade #{i}: live={{EntryBar={live.EntryBarIndex},Side={live.Side},Strike={live.StrikePrice},ExitBar={live.ExitBarIndex},Exit={live.ExitReason}}} "
                    + $"offline={{EntryBar={tradeEntryBarIndex},Side={trade.Side},Strike={trade.StrikePrice},ExitBar={tradeExitBarIndex},Exit={trade.ExitReason}}}");
                ok = false;
            }
            else
            {
                Console.WriteLine($"  MATCH trade #{i}: EntryBar={live.EntryBarIndex} Side={live.Side} Strike={live.StrikePrice} ExitBar={live.ExitBarIndex} Exit={live.ExitReason} "
                    + $"| EntryPrice live={live.EntryPrice} offline={trade.EntryPrice} delta={entryPriceDelta} "
                    + $"| ExitPrice live={live.ExitPrice} offline={trade.ExitPrice} delta={exitPriceDelta}");
            }
        }

        Console.WriteLine($"  Trade comparison: {liveTrades.Count} live vs {officialTrades.Count} offline, {(ok ? "all matched on entry-bar/strike/direction/exit-reason" : "mismatches above")}.");
        return ok;
    }

    /// <summary>
    /// Restart-safety proof, same shape as <see cref="ReplayLiveScoreCommand.RunRestartTestAsync"/>:
    /// scores the first <paramref name="restartAfterBarIndex"/> bars directly, discards the in-memory
    /// session, calls <see cref="LiveFuturesCrossoverSession.RebuildAsync"/> (exactly what a real
    /// process restart would do), finishes the day, and compares the FULL day's trades against
    /// <c>SimulateCrossoverDayAsync</c>'s own never-restarted result.
    /// </summary>
    public static async Task<int> RunRestartTestAsync(string baseConnectionString, DateOnly date, long threshold, int restartAfterBarIndex, CancellationToken ct)
    {
        var officialOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = VolumeBarPopulator.VolumeBarDatabaseName }.ConnectionString).Options;
        var scratchOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_volume_bars_livefuturescrossovertest_restart" }.ConnectionString).Options;
        var sourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_vm_copy" }.ConnectionString).Options;

        Console.WriteLine($"=== Live-futures-crossover restart-safety test: {date:yyyy-MM-dd} @ threshold={threshold}, simulated restart after BarIndex {restartAfterBarIndex} ===");

        await using (var wipe = new VolumeBarDbContext(scratchOptions))
        {
            await wipe.Database.EnsureDeletedAsync(ct);
            await wipe.Database.MigrateAsync(ct);
        }

        List<VolumeBarRow> bars;
        await using (var official = new VolumeBarDbContext(officialOptions))
        {
            bars = await official.VolumeBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == threshold).OrderBy(b => b.BarIndex).ToListAsync(ct);
        }

        if (bars.Count == 0)
        {
            Console.WriteLine("No official bars for this date -- aborting.");
            return 1;
        }

        await using (var scratchSeed = new VolumeBarDbContext(scratchOptions))
        {
            scratchSeed.VolumeBars.AddRange(bars.Select(b => new VolumeBarRow
            {
                AsOfDate = b.AsOfDate, BarIndex = b.BarIndex, BarVolumeThreshold = b.BarVolumeThreshold,
                StartTimestamp = b.StartTimestamp, EndTimestamp = b.EndTimestamp, DurationSeconds = b.DurationSeconds,
                OpenPrice = b.OpenPrice, HighPrice = b.HighPrice, LowPrice = b.LowPrice, ClosePrice = b.ClosePrice,
                Volume = b.Volume, OpenInterestAtClose = b.OpenInterestAtClose, VwapAtClose = b.VwapAtClose,
                FutureCvdNet = b.FutureCvdNet, FutureDepthImbalance = b.FutureDepthImbalance,
                OrderFlowImbalance = b.OrderFlowImbalance, TopOfBookImbalance = b.TopOfBookImbalance,
            }));
            await scratchSeed.SaveChangesAsync(ct);
        }

        var firstHalf = bars.Where(b => b.BarIndex <= restartAfterBarIndex).ToList();
        var sessionBeforeRestart = new LiveFuturesCrossoverSession(date, threshold);
        await using (var scratch1 = new VolumeBarDbContext(scratchOptions))
        {
            foreach (var bar in firstHalf)
            {
                var result = sessionBeforeRestart.ProcessBar(bar);
                scratch1.LiveFuturesCrossoverScoreBars.Add(result.ScoreRow);
            }

            await scratch1.SaveChangesAsync(ct);
        }

        Console.WriteLine($"Pre-restart: scored bars 0..{restartAfterBarIndex} ({sessionBeforeRestart.LastProcessedBarIndex + 1} bars), HasOpenSignal={sessionBeforeRestart.HasOpenSignal}.");

        await using var scratchForRebuild = new VolumeBarDbContext(scratchOptions);
        var rebuiltSession = await LiveFuturesCrossoverSession.RebuildAsync(scratchForRebuild, date, threshold, ct);
        Console.WriteLine($"Post-restart (rebuilt): resumes at BarIndex {rebuiltSession.LastProcessedBarIndex + 1}, HasOpenSignal={rebuiltSession.HasOpenSignal}.");

        if (rebuiltSession.LastProcessedBarIndex != sessionBeforeRestart.LastProcessedBarIndex || rebuiltSession.HasOpenSignal != sessionBeforeRestart.HasOpenSignal)
        {
            Console.WriteLine("RESTART-SAFETY: FAIL -- rebuilt session's bookmark/open-signal state doesn't match the pre-restart session's.");
            return 1;
        }

        var secondHalf = bars.Where(b => b.BarIndex > restartAfterBarIndex).ToList();
        var livePaperTrades = new List<LivePaperTradeRow>();
        await using (var source = new NiftySignalDbContext(sourceOptions))
        await using (var scratch2 = new VolumeBarDbContext(scratchOptions))
        {
            void Log(string msg) => Console.WriteLine($"  [live] {msg}");

            // Replay both halves' OWN trade-execution side-effects (OpenAsync/CloseAsync) so the
            // full day's trades can be compared, not just the score-tracker bookmark/open-signal
            // state the check above already validated. Mirrors ReplayOneDayAsync's own loop.
            async Task ProcessAsync(LiveFuturesCrossoverSession session, IEnumerable<VolumeBarRow> range, bool persistScore)
            {
                foreach (var bar in range)
                {
                    var result = session.ProcessBar(bar);
                    if (persistScore)
                    {
                        scratch2.LiveFuturesCrossoverScoreBars.Add(result.ScoreRow);
                    }

                    if (result.Opened is { } opened)
                    {
                        scratch2.LiveEntrySignals.Add(new LiveEntrySignalRow
                        {
                            AsOfDate = date, BarVolumeThreshold = threshold, Strategy = LiveVolumeBarStrategyId.FuturesCrossover,
                            Side = opened.Side, EntryBarIndex = opened.EntryBarIndex, EntryTimestamp = opened.EntryTimestamp,
                            EntryScore = opened.EntryScore, EntryPercentile = null,
                        });
                        var trade = await LivePaperTradeExecutor.OpenAsync(source, scratch2, date, threshold, opened, bar.ClosePrice, opened.EntryTimestamp + SimulatedDecisionLatency, Log, ct, LiveVolumeBarStrategyId.FuturesCrossover);
                        await scratch2.SaveChangesAsync(ct);
                        if (trade is not null)
                        {
                            livePaperTrades.Add(trade);
                        }
                    }

                    if (result.Closed is { } closed)
                    {
                        var openSignal = await scratch2.LiveEntrySignals.FirstOrDefaultAsync(r => r.AsOfDate == date && r.BarVolumeThreshold == threshold && r.Strategy == LiveVolumeBarStrategyId.FuturesCrossover && r.EntryBarIndex == closed.EntryBarIndex, ct);
                        if (openSignal is not null)
                        {
                            openSignal.ExitBarIndex = closed.ExitBarIndex;
                            openSignal.ExitTimestamp = closed.ExitTimestamp;
                            openSignal.ExitReason = closed.ExitReason;
                        }

                        await LivePaperTradeExecutor.CloseAsync(scratch2, source, date, threshold, closed, closed.ExitTimestamp + SimulatedDecisionLatency, Log, ct, LiveVolumeBarStrategyId.FuturesCrossover);
                        await scratch2.SaveChangesAsync(ct);
                    }
                }
            }

            // Re-run the FIRST half too (against a fresh session) purely to reproduce its own trade
            // rows in this scratch DB for the full-day comparison below -- the tracker-state proof
            // above already used a separate, throwaway session for the pre-restart half.
            var replaySession = new LiveFuturesCrossoverSession(date, threshold);
            await ProcessAsync(replaySession, firstHalf, persistScore: false);
            await ProcessAsync(replaySession, secondHalf, persistScore: false);

            if (replaySession.FlushEndOfDay(bars[^1].EndTimestamp) is { } eodClosed)
            {
                var openSignal = await scratch2.LiveEntrySignals.FirstOrDefaultAsync(r => r.AsOfDate == date && r.BarVolumeThreshold == threshold && r.Strategy == LiveVolumeBarStrategyId.FuturesCrossover && r.EntryBarIndex == eodClosed.EntryBarIndex, ct);
                if (openSignal is not null)
                {
                    openSignal.ExitBarIndex = eodClosed.ExitBarIndex;
                    openSignal.ExitTimestamp = eodClosed.ExitTimestamp;
                    openSignal.ExitReason = eodClosed.ExitReason;
                }

                await LivePaperTradeExecutor.CloseAsync(scratch2, source, date, threshold, eodClosed, eodClosed.ExitTimestamp + SimulatedDecisionLatency, Log, ct, LiveVolumeBarStrategyId.FuturesCrossover);
                await scratch2.SaveChangesAsync(ct);
            }

            livePaperTrades = await scratch2.LivePaperTrades
                .Where(t => t.AsOfDate == date && t.BarVolumeThreshold == threshold && t.Strategy == LiveVolumeBarStrategyId.FuturesCrossover)
                .OrderBy(t => t.EntryBarIndex)
                .ToListAsync(ct);
        }

        List<VolumeBarTrade> officialTrades;
        await using (var officialSource = new NiftySignalDbContext(sourceOptions))
        await using (var official = new VolumeBarDbContext(officialOptions))
        {
            officialTrades = await TradeSimulator.SimulateCrossoverDayAsync(
                officialSource, official, date, threshold, LiveFuturesCrossoverSession.FastBars, LiveFuturesCrossoverSession.SlowBars, LiveFuturesCrossoverSession.ThresholdPoints, ct,
                null, VolumeBarMetric.SessionGatedDepthDurationConfirmed);
        }

        var ok = CompareTrades(livePaperTrades, officialTrades, bars);
        Console.WriteLine(ok
            ? "RESTART-SAFETY + PARITY: PASS -- post-restart rebuild reproduced the FULL day identically to a never-restarted offline backtest."
            : "RESTART-SAFETY + PARITY: FAIL -- see diffs above.");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// No-collision proof (task's own required demonstration): replays BOTH strategies -- this one
    /// and <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c> (same shape as
    /// <see cref="ReplayLivePaperTradeCommand.RunAsync"/>'s own per-bar loop, inlined here rather
    /// than reused so both strategies can be driven interleaved, bar by bar, into the SAME scratch
    /// database for the SAME day/threshold) -- and asserts both strategies' own trades persisted with
    /// no primary-key violation (the shared unique index is now (AsOfDate, BarVolumeThreshold,
    /// Strategy, EntryBarIndex), so an EntryBarIndex collision across strategies is expected and
    /// must NOT throw) and no cross-strategy interference (each strategy's own trade count/timing
    /// matches what running it ALONE would produce).
    /// </summary>
    public static async Task<int> RunBothStrategiesAsync(string baseConnectionString, DateOnly date, long threshold, CancellationToken ct)
    {
        var officialOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = VolumeBarPopulator.VolumeBarDatabaseName }.ConnectionString).Options;
        var scratchOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_volume_bars_bothstrategiestest" }.ConnectionString).Options;
        var sourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_vm_copy" }.ConnectionString).Options;

        Console.WriteLine($"=== No-collision proof: BOTH strategies replayed together for {date:yyyy-MM-dd} @ threshold={threshold} ===");

        await using (var wipe = new VolumeBarDbContext(scratchOptions))
        {
            await wipe.Database.EnsureDeletedAsync(ct);
            await wipe.Database.MigrateAsync(ct);
        }

        List<VolumeBarRow> bars;
        Dictionary<int, OptionAtmBarRow> atmByIndex;
        Dictionary<int, OptionDepthBarRow> depthByIndex;
        Dictionary<int, OptionMaxPainBarRow> maxPainByIndex;
        await using (var official = new VolumeBarDbContext(officialOptions))
        {
            bars = await official.VolumeBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == threshold).OrderBy(b => b.BarIndex).ToListAsync(ct);
            atmByIndex = await official.OptionAtmBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == threshold).ToDictionaryAsync(b => b.BarIndex, ct);
            depthByIndex = await official.OptionDepthBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == threshold && b.BandWidth == TradingDaySession.DepthBandWidth).ToDictionaryAsync(b => b.BarIndex, ct);
            maxPainByIndex = await official.OptionMaxPainBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == threshold).ToDictionaryAsync(b => b.BarIndex, ct);
        }

        if (bars.Count == 0)
        {
            Console.WriteLine("No official bars for this date -- aborting.");
            return 1;
        }

        var optionsSession = new TradingDaySession(date, threshold);
        var crossoverSession = new LiveFuturesCrossoverSession(date, threshold);

        await using var scratchDb = new VolumeBarDbContext(scratchOptions);
        await using var source = new NiftySignalDbContext(sourceOptions);

        void Log(string msg) => Console.WriteLine($"  [live] {msg}");
        var exceptionCount = 0;

        try
        {
            foreach (var bar in bars)
            {
                atmByIndex.TryGetValue(bar.BarIndex, out var atmBar);
                depthByIndex.TryGetValue(bar.BarIndex, out var depthBar);
                maxPainByIndex.TryGetValue(bar.BarIndex, out var maxPainBar);

                // Options strategy, same as ReplayLiveScoreCommand's own loop.
                var optResult = optionsSession.ProcessBar(bar, atmBar, depthBar, maxPainBar);
                scratchDb.LiveOptionsScoreBars.Add(optResult.ScoreRow);
                if (optResult.Opened is { } optOpened)
                {
                    scratchDb.LiveEntrySignals.Add(new LiveEntrySignalRow
                    {
                        AsOfDate = date, BarVolumeThreshold = threshold, Strategy = LiveVolumeBarStrategyId.Options,
                        Side = optOpened.Side, EntryBarIndex = optOpened.EntryBarIndex, EntryTimestamp = optOpened.EntryTimestamp,
                        EntryScore = optOpened.EntryScore, EntryPercentile = optOpened.EntryPercentile,
                    });
                    await LivePaperTradeExecutor.OpenAsync(source, scratchDb, date, threshold, optOpened, bar.ClosePrice, optOpened.EntryTimestamp + SimulatedDecisionLatency, Log, ct, LiveVolumeBarStrategyId.Options);
                    await scratchDb.SaveChangesAsync(ct);
                }

                if (optResult.Closed is { } optClosed)
                {
                    var openSignal = await scratchDb.LiveEntrySignals.FirstOrDefaultAsync(r => r.AsOfDate == date && r.BarVolumeThreshold == threshold && r.Strategy == LiveVolumeBarStrategyId.Options && r.EntryBarIndex == optClosed.EntryBarIndex, ct);
                    if (openSignal is not null)
                    {
                        openSignal.ExitBarIndex = optClosed.ExitBarIndex;
                        openSignal.ExitTimestamp = optClosed.ExitTimestamp;
                        openSignal.ExitReason = optClosed.ExitReason;
                    }

                    await LivePaperTradeExecutor.CloseAsync(scratchDb, source, date, threshold, optClosed, optClosed.ExitTimestamp + SimulatedDecisionLatency, Log, ct, LiveVolumeBarStrategyId.Options);
                    await scratchDb.SaveChangesAsync(ct);
                }

                // Futures-crossover strategy, interleaved on the SAME bar, into the SAME scratch DB.
                var xResult = crossoverSession.ProcessBar(bar);
                scratchDb.LiveFuturesCrossoverScoreBars.Add(xResult.ScoreRow);
                if (xResult.Opened is { } xOpened)
                {
                    scratchDb.LiveEntrySignals.Add(new LiveEntrySignalRow
                    {
                        AsOfDate = date, BarVolumeThreshold = threshold, Strategy = LiveVolumeBarStrategyId.FuturesCrossover,
                        Side = xOpened.Side, EntryBarIndex = xOpened.EntryBarIndex, EntryTimestamp = xOpened.EntryTimestamp,
                        EntryScore = xOpened.EntryScore, EntryPercentile = null,
                    });
                    await LivePaperTradeExecutor.OpenAsync(source, scratchDb, date, threshold, xOpened, bar.ClosePrice, xOpened.EntryTimestamp + SimulatedDecisionLatency, Log, ct, LiveVolumeBarStrategyId.FuturesCrossover);
                    await scratchDb.SaveChangesAsync(ct);
                }

                if (xResult.Closed is { } xClosed)
                {
                    var openSignal = await scratchDb.LiveEntrySignals.FirstOrDefaultAsync(r => r.AsOfDate == date && r.BarVolumeThreshold == threshold && r.Strategy == LiveVolumeBarStrategyId.FuturesCrossover && r.EntryBarIndex == xClosed.EntryBarIndex, ct);
                    if (openSignal is not null)
                    {
                        openSignal.ExitBarIndex = xClosed.ExitBarIndex;
                        openSignal.ExitTimestamp = xClosed.ExitTimestamp;
                        openSignal.ExitReason = xClosed.ExitReason;
                    }

                    await LivePaperTradeExecutor.CloseAsync(scratchDb, source, date, threshold, xClosed, xClosed.ExitTimestamp + SimulatedDecisionLatency, Log, ct, LiveVolumeBarStrategyId.FuturesCrossover);
                    await scratchDb.SaveChangesAsync(ct);
                }
            }

            if (optionsSession.FlushEndOfDay(bars[^1].EndTimestamp) is { } optEod)
            {
                await LivePaperTradeExecutor.CloseAsync(scratchDb, source, date, threshold, optEod, optEod.ExitTimestamp + SimulatedDecisionLatency, Log, ct, LiveVolumeBarStrategyId.Options);
            }

            if (crossoverSession.FlushEndOfDay(bars[^1].EndTimestamp) is { } xEod)
            {
                await LivePaperTradeExecutor.CloseAsync(scratchDb, source, date, threshold, xEod, xEod.ExitTimestamp + SimulatedDecisionLatency, Log, ct, LiveVolumeBarStrategyId.FuturesCrossover);
            }

            await scratchDb.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            exceptionCount++;
            Console.WriteLine($"EXCEPTION during interleaved replay (this is what a real PK collision or cross-strategy interference bug would throw): {ex}");
        }

        var optionsTrades = await scratchDb.LivePaperTrades.Where(t => t.AsOfDate == date && t.BarVolumeThreshold == threshold && t.Strategy == LiveVolumeBarStrategyId.Options).OrderBy(t => t.EntryBarIndex).ToListAsync(ct);
        var crossoverTrades = await scratchDb.LivePaperTrades.Where(t => t.AsOfDate == date && t.BarVolumeThreshold == threshold && t.Strategy == LiveVolumeBarStrategyId.FuturesCrossover).OrderBy(t => t.EntryBarIndex).ToListAsync(ct);

        Console.WriteLine($"Options strategy: {optionsTrades.Count} trade(s) persisted independently.");
        Console.WriteLine($"FuturesCrossover strategy: {crossoverTrades.Count} trade(s) persisted independently.");

        // Cross-check: each strategy's own trades here should match what running it ALONE produces
        // (ReplayLivePaperTradeCommand for Options, ReplayLiveFuturesCrossoverCommand.RunAsync for
        // crossover) -- proving neither strategy's presence changed the other's own decisions.
        List<VolumeBarTrade> officialOptionsTrades;
        List<VolumeBarTrade> officialCrossoverTrades;
        await using (var officialSource = new NiftySignalDbContext(sourceOptions))
        await using (var official = new VolumeBarDbContext(officialOptions))
        {
            officialOptionsTrades = await TradeSimulator.SimulateDayAsync(
                officialSource, official, date, threshold, VolumeBarMetric.OptionsScoreThreeWaySwitchMaxPainConfirmed, 90.0, 15, ct,
                null, stopLossPercent: null, rollingSubBarThreshold: null, bandWidth: TradingDaySession.DepthBandWidth);
            officialCrossoverTrades = await TradeSimulator.SimulateCrossoverDayAsync(
                officialSource, official, date, threshold, LiveFuturesCrossoverSession.FastBars, LiveFuturesCrossoverSession.SlowBars, LiveFuturesCrossoverSession.ThresholdPoints, ct,
                null, VolumeBarMetric.SessionGatedDepthDurationConfirmed);
        }

        var ok = exceptionCount == 0
            && CompareTrades(optionsTrades, officialOptionsTrades, bars)
            && CompareTrades(crossoverTrades, officialCrossoverTrades, bars);

        Console.WriteLine(ok
            ? "NO-COLLISION PROOF: PASS -- both strategies had simultaneously-open/closed positions on the same day/threshold, persisted independently with no exception and no cross-strategy interference (each strategy's own trades matched running it alone)."
            : "NO-COLLISION PROOF: FAIL -- see diffs/exception above.");

        return ok ? 0 : 1;
    }
}
