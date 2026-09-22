using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Phase C (docs/LIVE_PARITY_PLAN.md) proof harness -- see the `replay-live-score` command in
/// <c>Program.cs</c>. Complements <see cref="ReplayLiveCommand"/> (Phase A's bar-WRITING parity
/// proof) with the scoring/entry-signal side: drives <see cref="TradingDaySession"/> -- the exact
/// class <c>NiftySignal.Host.LiveOptionsScoreEngine</c> uses live -- bar by bar, in order, over one
/// or more already-populated historical days (read from the OFFICIAL <c>niftysignal_volume_bars</c>
/// database, i.e. the same bars Phase A already proved a live replay reproduces row-for-row -- this
/// harness does not re-prove bar-writing parity, only scoring/entry parity on top of bars already
/// known-good), and compares its output against what <c>TradeSimulator.SimulateDayAsync</c> itself
/// computed and traded for the SAME day, via that method's own <c>onBarEvaluated</c> diagnostic hook
/// (Phase C addition -- optional, no-op by default, does not alter the backtest's own logic).
///
/// Running 2+ dates in one process call, each getting its own freshly-constructed
/// <see cref="TradingDaySession"/>, is this harness's own day-boundary/no-cross-day-leakage proof:
/// if a bug ever shared tracker state across days, that day's live-side scores would diverge from
/// <c>TradeSimulator</c>'s own (which always starts a fresh set of trackers per
/// <c>SimulateDayAsync</c> call by construction) -- the per-bar comparison below would catch it.
/// </summary>
public static class ReplayLiveScoreCommand
{
    public static async Task<int> RunAsync(string baseConnectionString, IReadOnlyList<DateOnly> dates, long threshold, CancellationToken ct)
    {
        var officialOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = VolumeBarPopulator.VolumeBarDatabaseName }.ConnectionString).Options;
        var scratchOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_volume_bars_livescoretest" }.ConnectionString).Options;
        var sourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_vm_copy" }.ConnectionString).Options;

        Console.WriteLine($"=== Live-score-replay parity test: {string.Join(", ", dates.Select(d => d.ToString("yyyy-MM-dd")))} @ threshold={threshold} ===");

        // Fresh scratch DB every run, same convention ReplayLiveCommand's own testOptions uses --
        // this command is meant to be re-run freely while iterating.
        await using (var wipe = new VolumeBarDbContext(scratchOptions))
        {
            await wipe.Database.EnsureDeletedAsync(ct);
            await wipe.Database.MigrateAsync(ct);
        }

        var overallOk = true;
        var sharedPriceCache = new Dictionary<(DateOnly, string), OptionPriceSeries>();

        foreach (var date in dates)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {date:yyyy-MM-dd} ---");

            // Step 1: read the day's already-populated, Phase-A-proven bars from the OFFICIAL
            // database (read-only) and drive a BRAND NEW TradingDaySession -- same "fresh trackers
            // per day" construction TradeSimulator.SimulateDayAsync itself uses, proving day-boundary
            // isolation by construction, not just by assertion (see this class's own doc comment).
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
                Console.WriteLine($"No official bars for {date:yyyy-MM-dd} @ {threshold} -- skipping (populate it first via the offline populators).");
                overallOk = false;
                continue;
            }

            if (depthByIndex.Count == 0)
            {
                Console.WriteLine($"WARNING: no OptionDepthBars at BandWidth={TradingDaySession.DepthBandWidth} for {date:yyyy-MM-dd} -- the Open leg will score null every bar. Populate via `populate-options-depth ... {TradingDaySession.DepthBandWidth}` first.");
            }

            var session = new TradingDaySession(date, threshold);
            var liveScores = new List<LiveOptionsScoreRow>();
            var liveEntries = new List<LiveEntrySignalRow>();
            var openByEntryBarIndex = new Dictionary<int, LiveEntrySignalRow>();

            foreach (var bar in bars)
            {
                atmByIndex.TryGetValue(bar.BarIndex, out var atmBar);
                depthByIndex.TryGetValue(bar.BarIndex, out var depthBar);
                maxPainByIndex.TryGetValue(bar.BarIndex, out var maxPainBar);

                var result = session.ProcessBar(bar, atmBar, depthBar, maxPainBar);
                liveScores.Add(result.ScoreRow);

                if (result.Opened is { } opened)
                {
                    var row = new LiveEntrySignalRow
                    {
                        AsOfDate = date,
                        BarVolumeThreshold = threshold,
                        Strategy = LiveVolumeBarStrategyId.Options,
                        Side = opened.Side,
                        EntryBarIndex = opened.EntryBarIndex,
                        EntryTimestamp = opened.EntryTimestamp,
                        EntryScore = opened.EntryScore,
                        EntryPercentile = opened.EntryPercentile,
                    };
                    liveEntries.Add(row);
                    openByEntryBarIndex[opened.EntryBarIndex] = row;
                }

                if (result.Closed is { } closed && openByEntryBarIndex.TryGetValue(closed.EntryBarIndex, out var openRow))
                {
                    openRow.ExitBarIndex = closed.ExitBarIndex;
                    openRow.ExitTimestamp = closed.ExitTimestamp;
                    openRow.ExitReason = closed.ExitReason;
                }
            }

            if (session.FlushEndOfDay(bars[^1].EndTimestamp) is { } eodClosed && openByEntryBarIndex.TryGetValue(eodClosed.EntryBarIndex, out var eodRow))
            {
                eodRow.ExitBarIndex = eodClosed.ExitBarIndex;
                eodRow.ExitTimestamp = eodClosed.ExitTimestamp;
                eodRow.ExitReason = eodClosed.ExitReason;
            }

            // Persist to the scratch DB too -- proves the actual persistence path/schema round-trips
            // (unique indexes included), not just that the in-memory objects look right.
            await using (var scratch = new VolumeBarDbContext(scratchOptions))
            {
                scratch.LiveOptionsScoreBars.AddRange(liveScores);
                scratch.LiveEntrySignals.AddRange(liveEntries);
                await scratch.SaveChangesAsync(ct);
            }

            Console.WriteLine($"Live-side: {liveScores.Count} bars scored, {liveEntries.Count} entry signal(s) fired.");

            // Step 2: run the OFFICIAL backtest for the same day/metric/config, capturing its own
            // per-bar scaledScore/percentile/maxPainConfirmScore via the Phase C onBarEvaluated hook
            // (additive, no-op by default -- see TradeSimulator.SimulateDayAsync's own doc comment).
            var officialPerBar = new Dictionary<int, (double? ScaledScore, double? Percentile, double? MaxPainConfirmScore)>();
            void OnBarEvaluated(VolumeBarRow bar, double? scaledScore, double? percentile, double? maxPainConfirmScore) =>
                officialPerBar[bar.BarIndex] = (scaledScore, percentile, maxPainConfirmScore);

            List<VolumeBarTrade> officialTrades;
            await using (var source = new NiftySignalDbContext(sourceOptions))
            await using (var official = new VolumeBarDbContext(officialOptions))
            {
                officialTrades = await TradeSimulator.SimulateDayAsync(
                    source, official, date, threshold, VolumeBarMetric.OptionsScoreThreeWaySwitchMaxPainConfirmed, 90.0, 15, ct,
                    sharedPriceCache, stopLossPercent: null, rollingSubBarThreshold: null, bandWidth: TradingDaySession.DepthBandWidth,
                    onBarEvaluated: OnBarEvaluated);
            }

            Console.WriteLine($"Offline backtest: {officialPerBar.Count} bars evaluated, {officialTrades.Count} trade(s) fired.");

            var ok = CompareScores(liveScores, officialPerBar) & CompareEntries(liveEntries, officialTrades, bars);
            Console.WriteLine(ok ? $"PARITY ({date:yyyy-MM-dd}): PASS" : $"PARITY ({date:yyyy-MM-dd}): FAIL -- see diffs above.");
            overallOk &= ok;
        }

        Console.WriteLine();
        Console.WriteLine(overallOk
            ? "OVERALL: PASS -- live scoring path matches the offline backtest exactly on every date tested, and each date's session started with no state carried over from the previous one (independently-constructed TradingDaySession per date, proven by matching TradeSimulator's own always-fresh-per-day trackers bar for bar)."
            : "OVERALL: FAIL -- see diffs above.");

        return overallOk ? 0 : 1;
    }

    static bool CompareScores(List<LiveOptionsScoreRow> liveScores, Dictionary<int, (double? ScaledScore, double? Percentile, double? MaxPainConfirmScore)> officialPerBar)
    {
        var ok = true;
        var mismatches = 0;
        const double tolerance = 1e-9;

        foreach (var live in liveScores)
        {
            if (!officialPerBar.TryGetValue(live.BarIndex, out var official))
            {
                Console.WriteLine($"  MISMATCH BarIndex={live.BarIndex}: live scored it, offline backtest has no entry for it at all.");
                ok = false;
                mismatches++;
                continue;
            }

            var scaledDiff = !NullableClose(live.ScaledScore, official.ScaledScore, tolerance);
            var percentileDiff = !NullableClose(live.Percentile, official.Percentile, tolerance);
            var maxPainDiff = !NullableClose(live.MaxPainConfirmScore, official.MaxPainConfirmScore, tolerance);

            if (scaledDiff || percentileDiff || maxPainDiff)
            {
                if (mismatches < 10)
                {
                    Console.WriteLine($"  MISMATCH BarIndex={live.BarIndex}: live={{Scaled={live.ScaledScore},Pctl={live.Percentile},MP={live.MaxPainConfirmScore}}} official={{Scaled={official.ScaledScore},Pctl={official.Percentile},MP={official.MaxPainConfirmScore}}}");
                }

                ok = false;
                mismatches++;
            }
        }

        Console.WriteLine($"  Score comparison: {liveScores.Count} live bars vs {officialPerBar.Count} official bars, {mismatches} mismatch(es).");
        return ok;
    }

    static bool CompareEntries(List<LiveEntrySignalRow> liveEntries, List<VolumeBarTrade> officialTrades, List<VolumeBarRow> bars)
    {
        var barIndexByEndTimestamp = bars.ToDictionary(b => b.EndTimestamp, b => b.BarIndex);
        var ok = true;

        if (liveEntries.Count != officialTrades.Count)
        {
            Console.WriteLine($"  MISMATCH: live fired {liveEntries.Count} entry signal(s), offline backtest fired {officialTrades.Count} trade(s).");
            ok = false;
        }

        var pairCount = Math.Min(liveEntries.Count, officialTrades.Count);
        for (var i = 0; i < pairCount; i++)
        {
            var live = liveEntries[i];
            var trade = officialTrades[i];
            var tradeEntryBarIndex = barIndexByEndTimestamp.GetValueOrDefault(trade.EntryTime, -1);
            var tradeExitBarIndex = barIndexByEndTimestamp.GetValueOrDefault(trade.ExitTime, -1);

            var mismatch = live.EntryBarIndex != tradeEntryBarIndex
                || live.Side != trade.Side
                || Math.Abs(live.EntryScore - (double)trade.EntryScore) > 1e-9
                || live.ExitBarIndex != tradeExitBarIndex
                || !string.Equals(live.ExitReason, trade.ExitReason, StringComparison.Ordinal);

            if (mismatch)
            {
                Console.WriteLine($"  MISMATCH entry #{i}: live={{EntryBar={live.EntryBarIndex},Side={live.Side},Score={live.EntryScore:F4},ExitBar={live.ExitBarIndex},Exit={live.ExitReason}}} "
                    + $"offline={{EntryBar={tradeEntryBarIndex},Side={trade.Side},Score={trade.EntryScore:F4},ExitBar={tradeExitBarIndex},Exit={trade.ExitReason}}}");
                ok = false;
            }
        }

        Console.WriteLine($"  Entry-signal comparison: {liveEntries.Count} live vs {officialTrades.Count} offline, {(ok ? "all matched" : "mismatches above")}.");
        return ok;
    }

    /// <summary>
    /// Restart-safety proof for <see cref="TradingDaySession.RebuildAsync"/> -- the one piece of
    /// Phase C novel risk Phase A's own idempotency test didn't cover (Phase A proved bar WRITING
    /// survives a restart; this proves tracker STATE does too). Scores the first
    /// <paramref name="restartAfterBarIndex"/> bars directly (writing rows to the scratch DB, same as
    /// a real process would have before stopping), then discards the in-memory session entirely and
    /// calls <see cref="TradingDaySession.RebuildAsync"/> -- literally what a fresh process restart
    /// would do -- before finishing the day. Compares the FULL day's result (session's own live
    /// scores/entries, spanning across the simulated restart) against the offline backtest, same
    /// comparison <see cref="RunAsync"/> uses -- a restart-induced tracker-state bug would show up as
    /// a percentile/score divergence starting exactly at BarIndex <paramref name="restartAfterBarIndex"/> + 1.
    /// </summary>
    public static async Task<int> RunRestartTestAsync(string baseConnectionString, DateOnly date, long threshold, int restartAfterBarIndex, CancellationToken ct)
    {
        var officialOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = VolumeBarPopulator.VolumeBarDatabaseName }.ConnectionString).Options;
        var scratchOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_volume_bars_livescoretest_restart" }.ConnectionString).Options;
        var sourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_vm_copy" }.ConnectionString).Options;

        Console.WriteLine($"=== Live-score restart-safety test: {date:yyyy-MM-dd} @ threshold={threshold}, simulated restart after BarIndex {restartAfterBarIndex} ===");

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

        // Seed the scratch DB with COPIES of the same bar tables (mirroring production reality --
        // LiveOptionsScoreEngine's VolumeBarDbContext is the ONE shared niftysignal_volume_bars
        // database that both LiveVolumeBarWriter's bar tables and this engine's own score/signal
        // tables live in together; RebuildAsync reads the bar tables from the SAME context it's
        // given). Fresh Id (0, DB-generated) on every copy -- these are new rows in a new database,
        // not the same identity as the official ones.
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
            scratchSeed.OptionAtmBars.AddRange(atmByIndex.Values.Select(a => new OptionAtmBarRow
            {
                AsOfDate = a.AsOfDate, BarIndex = a.BarIndex, BarVolumeThreshold = a.BarVolumeThreshold, EndTimestamp = a.EndTimestamp,
                SyntheticForward = a.SyntheticForward, AtmStrike = a.AtmStrike, AtmCallIv = a.AtmCallIv, AtmPutIv = a.AtmPutIv, AtmIv = a.AtmIv,
            }));
            scratchSeed.OptionDepthBars.AddRange(depthByIndex.Values.Select(d => new OptionDepthBarRow
            {
                AsOfDate = d.AsOfDate, BarIndex = d.BarIndex, BarVolumeThreshold = d.BarVolumeThreshold, BandWidth = d.BandWidth, EndTimestamp = d.EndTimestamp,
                CallBidQtyAvg = d.CallBidQtyAvg, CallAskQtyAvg = d.CallAskQtyAvg, PutBidQtyAvg = d.PutBidQtyAvg, PutAskQtyAvg = d.PutAskQtyAvg,
                CallTobBidQtyAvg = d.CallTobBidQtyAvg, CallTobAskQtyAvg = d.CallTobAskQtyAvg, PutTobBidQtyAvg = d.PutTobBidQtyAvg, PutTobAskQtyAvg = d.PutTobAskQtyAvg,
            }));
            scratchSeed.OptionMaxPainBars.AddRange(maxPainByIndex.Values.Select(m => new OptionMaxPainBarRow
            {
                AsOfDate = m.AsOfDate, BarIndex = m.BarIndex, BarVolumeThreshold = m.BarVolumeThreshold, EndTimestamp = m.EndTimestamp,
                MaxPainStrike = m.MaxPainStrike, HighestOiStrike = m.HighestOiStrike,
            }));
            await scratchSeed.SaveChangesAsync(ct);
        }

        var liveEntries = new List<LiveEntrySignalRow>();
        var openByEntryBarIndex = new Dictionary<int, LiveEntrySignalRow>();
        var allScores = new List<LiveOptionsScoreRow>();

        async Task<TradingDaySession> ProcessRangeAsync(TradingDaySession session, IEnumerable<VolumeBarRow> range)
        {
            var rows = new List<LiveOptionsScoreRow>();
            foreach (var bar in range)
            {
                atmByIndex.TryGetValue(bar.BarIndex, out var atmBar);
                depthByIndex.TryGetValue(bar.BarIndex, out var depthBar);
                maxPainByIndex.TryGetValue(bar.BarIndex, out var maxPainBar);
                var result = session.ProcessBar(bar, atmBar, depthBar, maxPainBar);
                rows.Add(result.ScoreRow);
                allScores.Add(result.ScoreRow);

                if (result.Opened is { } opened)
                {
                    var row = new LiveEntrySignalRow
                    {
                        AsOfDate = date, BarVolumeThreshold = threshold, Strategy = LiveVolumeBarStrategyId.Options, Side = opened.Side,
                        EntryBarIndex = opened.EntryBarIndex, EntryTimestamp = opened.EntryTimestamp,
                        EntryScore = opened.EntryScore, EntryPercentile = opened.EntryPercentile,
                    };
                    liveEntries.Add(row);
                    openByEntryBarIndex[opened.EntryBarIndex] = row;
                }

                if (result.Closed is { } closed && openByEntryBarIndex.TryGetValue(closed.EntryBarIndex, out var openRow))
                {
                    openRow.ExitBarIndex = closed.ExitBarIndex;
                    openRow.ExitTimestamp = closed.ExitTimestamp;
                    openRow.ExitReason = closed.ExitReason;
                }
            }

            await using var scratch = new VolumeBarDbContext(scratchOptions);
            scratch.LiveOptionsScoreBars.AddRange(rows);
            await scratch.SaveChangesAsync(ct);
            return session;
        }

        var firstHalf = bars.Where(b => b.BarIndex <= restartAfterBarIndex);
        var sessionBeforeRestart = new TradingDaySession(date, threshold);
        await ProcessRangeAsync(sessionBeforeRestart, firstHalf);
        Console.WriteLine($"Pre-restart: scored bars 0..{restartAfterBarIndex} ({sessionBeforeRestart.LastProcessedBarIndex + 1} bars), {liveEntries.Count} signal(s) so far, HasOpenSignal={sessionBeforeRestart.HasOpenSignal}.");

        // "Restart": the in-memory session above is simply abandoned (never referenced again) --
        // RebuildAsync re-derives an equivalent session purely from the scratch DB's own persisted
        // LiveOptionsScoreRow rows + the official bar tables, exactly what a real process restart
        // would do.
        await using var scratchForRebuild = new VolumeBarDbContext(scratchOptions);
        var rebuiltSession = await TradingDaySession.RebuildAsync(scratchForRebuild, date, threshold, ct);
        Console.WriteLine($"Post-restart (rebuilt): resumes at BarIndex {rebuiltSession.LastProcessedBarIndex + 1}, HasOpenSignal={rebuiltSession.HasOpenSignal}.");

        if (rebuiltSession.LastProcessedBarIndex != sessionBeforeRestart.LastProcessedBarIndex || rebuiltSession.HasOpenSignal != sessionBeforeRestart.HasOpenSignal)
        {
            Console.WriteLine("RESTART-SAFETY: FAIL -- rebuilt session's bookmark/open-signal state doesn't match the pre-restart session's.");
            return 1;
        }

        var secondHalf = bars.Where(b => b.BarIndex > restartAfterBarIndex);
        await ProcessRangeAsync(rebuiltSession, secondHalf);

        if (rebuiltSession.FlushEndOfDay(bars[^1].EndTimestamp) is { } eodClosed && openByEntryBarIndex.TryGetValue(eodClosed.EntryBarIndex, out var eodRow))
        {
            eodRow.ExitBarIndex = eodClosed.ExitBarIndex;
            eodRow.ExitTimestamp = eodClosed.ExitTimestamp;
            eodRow.ExitReason = eodClosed.ExitReason;
        }

        var officialPerBar = new Dictionary<int, (double? ScaledScore, double? Percentile, double? MaxPainConfirmScore)>();
        void OnBarEvaluated(VolumeBarRow bar, double? scaledScore, double? percentile, double? maxPainConfirmScore) =>
            officialPerBar[bar.BarIndex] = (scaledScore, percentile, maxPainConfirmScore);

        List<VolumeBarTrade> officialTrades;
        await using (var source = new NiftySignalDbContext(sourceOptions))
        await using (var official = new VolumeBarDbContext(officialOptions))
        {
            officialTrades = await TradeSimulator.SimulateDayAsync(
                source, official, date, threshold, VolumeBarMetric.OptionsScoreThreeWaySwitchMaxPainConfirmed, 90.0, 15, ct,
                null, stopLossPercent: null, rollingSubBarThreshold: null, bandWidth: TradingDaySession.DepthBandWidth,
                onBarEvaluated: OnBarEvaluated);
        }

        var ok = CompareScores(allScores, officialPerBar) & CompareEntries(liveEntries, officialTrades, bars);
        Console.WriteLine(ok
            ? "RESTART-SAFETY + PARITY: PASS -- post-restart rebuild reproduced the FULL day identically to a never-restarted offline backtest."
            : "RESTART-SAFETY + PARITY: FAIL -- see diffs above.");
        return ok ? 0 : 1;
    }

    static bool NullableClose(double? a, double? b, double tolerance)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return Math.Abs(a.Value - b.Value) <= tolerance;
    }
}
