using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Phase D (docs/LIVE_PARITY_PLAN.md) proof harness -- see the `replay-live-papertrade` command in
/// <c>Program.cs</c>. Complements <see cref="ReplayLiveScoreCommand"/> (Phase C's scoring/entry-signal
/// parity proof) with the full paper-trade lifecycle: drives <see cref="TradingDaySession"/> bar by
/// bar over one or more already-Phase-A/C-proven historical days (read from the OFFICIAL
/// <c>niftysignal_volume_bars</c> database), and on every <see cref="LiveSignalOpened"/>/
/// <see cref="LiveSignalClosed"/> event calls the SAME <see cref="LivePaperTradeExecutor"/>
/// <c>NiftySignal.Host.LiveOptionsScoreEngine</c> calls live -- strike selection via
/// <see cref="NiftySignal.Scoring.AtmStrikeSelector"/>, live fill price via
/// <see cref="LivePaperTradeExecutor.GetLatestTickPriceAsync"/> -- then compares the resulting
/// <see cref="LivePaperTradeRow"/> rows against what <c>TradeSimulator.SimulateDayAsync</c> itself
/// traded for the same day/config (<see cref="VolumeBarMetric.OptionsScoreThreeWaySwitchMaxPainConfirmed"/>
/// @ 2600/90).
///
/// <b>Simulated decision latency</b>: per docs/LIVE_PARITY_PLAN.md's own "Fill-price policy" section,
/// live fill price is deliberately NOT computed at the bar's own EndTimestamp (that would just
/// reproduce <see cref="OptionPriceSeries.PriceAtOrBefore"/>'s own backtest-only lookback query,
/// yielding an identical price and hiding the very difference the plan documents). Instead each
/// decision timestamp is the bar's own EndTimestamp plus <see cref="SimulatedDecisionLatency"/> (10s
/// -- the exact <c>LiveOptionsScoreEngine.PollInterval</c> a real live poll would take to notice and
/// act on a just-closed bar), a faithful, reproducible stand-in for the real live service's own
/// "score computed, poll fires, decision made" latency -- not an arbitrary offset.
/// </summary>
public static class ReplayLivePaperTradeCommand
{
    static readonly TimeSpan SimulatedDecisionLatency = TimeSpan.FromSeconds(10);

    /// <param name="destinationDatabaseNameOverride">
    /// 2026-09-20, Phase E: defaults to the disposable scratch database
    /// ("niftysignal_volume_bars_livepapertradetest") this harness has always used. Phase E's
    /// verify-parity tool needs to read REAL, actually-persisted LivePaperTradeRow rows (not a
    /// fresh in-memory-only replay) for its own PASS demonstration -- since the live Host process
    /// has not itself traded these already-historical days, the only faithful way to produce that
    /// real persisted data is to run this SAME proven harness (identical LivePaperTradeExecutor
    /// calls the live Host itself makes) once, writing into the real niftysignal_volume_bars
    /// database instead of the scratch one. Passing a non-null override here also disables the
    /// destructive EnsureDeleted wipe below (see <paramref name="wipeDestination"/>'s own default) --
    /// the real database must never be dropped.
    /// </param>
    /// <param name="wipeDestination">
    /// Defaults to true (the original always-fresh-scratch behavior) when
    /// <paramref name="destinationDatabaseNameOverride"/> is null; defaults to false when an
    /// override IS given, since EnsureDeleted on the real niftysignal_volume_bars database would be
    /// catastrophic. Pass explicitly to override either default.
    /// </param>
    public static async Task<int> RunAsync(string baseConnectionString, IReadOnlyList<DateOnly> dates, long threshold, CancellationToken ct,
        string? destinationDatabaseNameOverride = null, bool? wipeDestination = null)
    {
        var destinationDatabaseName = destinationDatabaseNameOverride ?? "niftysignal_volume_bars_livepapertradetest";
        var effectiveWipeDestination = wipeDestination ?? destinationDatabaseNameOverride is null;

        var officialOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = VolumeBarPopulator.VolumeBarDatabaseName }.ConnectionString).Options;
        var scratchOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = destinationDatabaseName }.ConnectionString).Options;
        var sourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_vm_copy" }.ConnectionString).Options;

        Console.WriteLine($"=== Live-paper-trade-replay parity test: {string.Join(", ", dates.Select(d => d.ToString("yyyy-MM-dd")))} @ threshold={threshold} (writing to {destinationDatabaseName}) ===");

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
                Console.WriteLine($"No official bars for {date:yyyy-MM-dd} @ {threshold} -- skipping.");
                overallOk = false;
                continue;
            }

            var session = new TradingDaySession(date, threshold);
            var livePaperTrades = new List<LivePaperTradeRow>();

            await using var scratchDb = new VolumeBarDbContext(scratchOptions);
            await using var source = new NiftySignalDbContext(sourceOptions);

            void Log(string msg) => Console.WriteLine($"  [live] {msg}");

            foreach (var bar in bars)
            {
                atmByIndex.TryGetValue(bar.BarIndex, out var atmBar);
                depthByIndex.TryGetValue(bar.BarIndex, out var depthBar);
                maxPainByIndex.TryGetValue(bar.BarIndex, out var maxPainBar);

                var result = session.ProcessBar(bar, atmBar, depthBar, maxPainBar);
                scratchDb.LiveOptionsScoreBars.Add(result.ScoreRow);

                if (result.Opened is { } opened)
                {
                    var signalRow = new LiveEntrySignalRow
                    {
                        AsOfDate = date,
                        BarVolumeThreshold = threshold,
                        Side = opened.Side,
                        EntryBarIndex = opened.EntryBarIndex,
                        EntryTimestamp = opened.EntryTimestamp,
                        EntryScore = opened.EntryScore,
                        EntryPercentile = opened.EntryPercentile,
                    };
                    scratchDb.LiveEntrySignals.Add(signalRow);

                    var decisionTs = opened.EntryTimestamp + SimulatedDecisionLatency;
                    var trade = await LivePaperTradeExecutor.OpenAsync(source, scratchDb, date, threshold, opened, bar.ClosePrice, decisionTs, Log, ct);
                    await scratchDb.SaveChangesAsync(ct);
                    if (trade is not null)
                    {
                        livePaperTrades.Add(trade);
                    }
                }

                if (result.Closed is { } closed)
                {
                    var openSignal = await scratchDb.LiveEntrySignals.FirstOrDefaultAsync(
                        r => r.AsOfDate == date && r.BarVolumeThreshold == threshold && r.EntryBarIndex == closed.EntryBarIndex, ct);
                    if (openSignal is not null)
                    {
                        openSignal.ExitBarIndex = closed.ExitBarIndex;
                        openSignal.ExitTimestamp = closed.ExitTimestamp;
                        openSignal.ExitReason = closed.ExitReason;
                    }

                    var decisionTs = closed.ExitTimestamp + SimulatedDecisionLatency;
                    await LivePaperTradeExecutor.CloseAsync(scratchDb, source, date, threshold, closed, decisionTs, Log, ct);
                    await scratchDb.SaveChangesAsync(ct);
                }
            }

            if (session.FlushEndOfDay(bars[^1].EndTimestamp) is { } eodClosed)
            {
                var openSignal = await scratchDb.LiveEntrySignals.FirstOrDefaultAsync(
                    r => r.AsOfDate == date && r.BarVolumeThreshold == threshold && r.EntryBarIndex == eodClosed.EntryBarIndex, ct);
                if (openSignal is not null)
                {
                    openSignal.ExitBarIndex = eodClosed.ExitBarIndex;
                    openSignal.ExitTimestamp = eodClosed.ExitTimestamp;
                    openSignal.ExitReason = eodClosed.ExitReason;
                }

                var decisionTs = eodClosed.ExitTimestamp + SimulatedDecisionLatency;
                await LivePaperTradeExecutor.CloseAsync(scratchDb, source, date, threshold, eodClosed, decisionTs, Log, ct);
            }

            await scratchDb.SaveChangesAsync(ct);
            // Re-read to get final ExitPrice/ExitReason values reflected in our own comparison list.
            livePaperTrades = await scratchDb.LivePaperTrades
                .Where(t => t.AsOfDate == date && t.BarVolumeThreshold == threshold)
                .OrderBy(t => t.EntryBarIndex)
                .ToListAsync(ct);

            Console.WriteLine($"Live-side: {livePaperTrades.Count} paper trade(s) opened.");

            List<VolumeBarTrade> officialTrades;
            await using (var officialSource = new NiftySignalDbContext(sourceOptions))
            await using (var official = new VolumeBarDbContext(officialOptions))
            {
                officialTrades = await TradeSimulator.SimulateDayAsync(
                    officialSource, official, date, threshold, VolumeBarMetric.OptionsScoreThreeWaySwitchMaxPainConfirmed, 90.0, 15, ct,
                    sharedPriceCache, stopLossPercent: null, rollingSubBarThreshold: null, bandWidth: TradingDaySession.DepthBandWidth);
            }

            Console.WriteLine($"Offline backtest: {officialTrades.Count} trade(s) fired.");

            var ok = CompareTrades(livePaperTrades, officialTrades, bars);
            Console.WriteLine(ok ? $"PARITY ({date:yyyy-MM-dd}): PASS" : $"PARITY ({date:yyyy-MM-dd}): FAIL -- see diffs above.");
            overallOk &= ok;
        }

        Console.WriteLine();
        Console.WriteLine(overallOk
            ? "OVERALL: PASS -- live paper-trade lifecycle (strike/direction/entry-bar/exit-reason) matches the offline backtest exactly on every date tested. Fill-price deltas (if any) are logged above as an accepted, documented difference (docs/LIVE_PARITY_PLAN.md's fill-price policy), not a failure."
            : "OVERALL: FAIL -- see diffs above.");

        return overallOk ? 0 : 1;
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
}
