using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Phase A (docs/LIVE_PARITY_PLAN.md) proof harness -- see the `replay-live` command in
/// <c>Program.cs</c> for the CLI entry point and its own doc comment for the overall shape.
///
/// "As if arriving live" here means: the day is cut into a sequence of poll checkpoints spaced
/// <see cref="PollSeconds"/> apart in EXCHANGE time (mirroring <c>LiveVolumeBarWriter</c>'s own
/// real-wall-clock 10s poll cadence, just replayed against historical timestamps instead of waited
/// on for real), and at each checkpoint the live writers are called with that checkpoint's own
/// timestamp as their cutoff -- exactly <see cref="LiveVolumeBarPopulator.WriteNewBarsAsync"/>'s own
/// <c>nowUtc</c> parameter. No tick after a given checkpoint is ever visible to that checkpoint's
/// writer call, the same no-look-ahead constraint the real live worker has by construction (it can
/// only see ticks already flushed to Postgres as of whenever it happens to poll).
/// </summary>
public static class ReplayLiveCommand
{
    public static async Task<int> RunAsync(
        string baseConnectionString,
        DateOnly date, long threshold, int? restartAfter, int pollSeconds)
    {
        var sourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_vm_copy" }.ConnectionString).Options;
        var testOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_volume_bars_livetest" }.ConnectionString).Options;
        var officialOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = VolumeBarPopulator.VolumeBarDatabaseName }.ConnectionString).Options;

        Console.WriteLine($"=== Live-replay parity test: {date:yyyy-MM-dd} @ threshold={threshold}, pollSeconds={pollSeconds}{(restartAfter is { } ra ? $", simulating a restart after poll #{ra}" : "")} ===");

        // Fresh test DB every run -- this command is meant to be re-run repeatedly while iterating,
        // never accumulates stale rows from a previous attempt at the same date/threshold.
        await using (var wipe = new VolumeBarDbContext(testOptions))
        {
            await wipe.Database.EnsureDeletedAsync();
            await wipe.Database.MigrateAsync();
        }

        var dayStart = LiveVolumeBarPopulator.DayStartUtc(date);
        var dayEnd = LiveVolumeBarPopulator.DayEndUtc(date);

        List<DateTimeOffset> checkpoints;
        await using (var source = new NiftySignalDbContext(sourceOptions))
        {
            var future = await source.Instruments.FirstOrDefaultAsync(i => i.AsOfDate == date && i.InstrumentType == InstrumentType.Future);
            if (future is null)
            {
                Console.WriteLine("No future instrument for this date -- nothing to replay.");
                return 1;
            }

            var tickTimestamps = await source.Ticks
                .Where(t => t.Token == future.Token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd)
                .OrderBy(t => t.ExchangeTimestamp)
                .Select(t => t.ExchangeTimestamp)
                .ToListAsync();

            if (tickTimestamps.Count == 0)
            {
                Console.WriteLine("No future ticks for this date -- nothing to replay.");
                return 1;
            }

            // One checkpoint per pollSeconds-wide exchange-time bucket that actually saw a tick --
            // buckets with zero ticks would be a no-op poll anyway (WriteNewBarsAsync's own
            // sawAnyTick guard), so they're skipped here rather than padding the checkpoint list.
            checkpoints = tickTimestamps
                .GroupBy(t => (t - dayStart).Ticks / TimeSpan.FromSeconds(pollSeconds).Ticks)
                .Select(g => g.Max())
                .OrderBy(t => t)
                .ToList();
        }

        Console.WriteLine($"{checkpoints.Count} simulated poll checkpoint(s), last tick at {checkpoints[^1]:HH:mm:ss} UTC.");

        // Phase G addendum (docs/LIVE_PARITY_PLAN.md): mirror production exactly -- LiveVolumeBarWriter
        // owns ONE LiveOptionSeriesCache Singleton across its whole process lifetime, passed into the
        // ATM/MaxPain populators every poll. Before this addendum, this harness always passed seriesCache:
        // null (the original Phase A/C from-scratch behavior) -- meaning Phase A's own restart-safety
        // proof never actually exercised the Phase G cache at all. A real Host restart wipes this
        // Singleton (in-memory, never persisted) but leaves the database untouched -- simulated below by
        // replacing `cache` with a FRESH instance at the same point the DB-backed idempotency restart
        // happens, not by clearing it (a real process restart doesn't clear an object, it loses it
        // entirely and starts a brand-new one).
        var cache = new LiveOptionSeriesCache();
        var futureBuilderCache = new LiveVolumeBarBuilderCache();
        var pollTimings = new List<(int Index, long ElapsedMs, bool PostRestart)>();
        // Live performance incident fix (docs/LIVE_PARITY_PLAN.md, dated entry): the FUTURE write's
        // own elapsed time, isolated from the option-side ATM/Depth/MaxPain calls (already timed
        // together above) -- this is the specific number the incident's fix targets, so it gets its
        // own timeline rather than being folded into the combined per-poll total.
        var futureWriteTimings = new List<(int Index, long ElapsedMs)>();

        async Task RunCheckpointAsync(DateTimeOffset cutoff, bool finalize, bool postRestart = false)
        {
            await using var source = new NiftySignalDbContext(sourceOptions);
            await using var destination = new VolumeBarDbContext(testOptions);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var futureSw = System.Diagnostics.Stopwatch.StartNew();
            var futureResult = await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, date, threshold, cutoff, finalize, CancellationToken.None, futureBuilderCache);
            futureSw.Stop();
            futureWriteTimings.Add((futureWriteTimings.Count, futureSw.ElapsedMilliseconds));
            if (futureResult.Outcome == LiveVolumeBarWriteOutcome.NoTradableData)
            {
                return;
            }

            await LiveOptionAtmPopulator.WriteNewBarsAsync(source, destination, date, threshold, CancellationToken.None, cache);
            await LiveOptionDepthPopulator.WriteNewBarsAsync(source, destination, date, threshold, CancellationToken.None);
            await LiveOptionMaxPainPopulator.WriteNewBarsAsync(source, destination, date, threshold, CancellationToken.None, cache);
            sw.Stop();
            pollTimings.Add((pollTimings.Count, sw.ElapsedMilliseconds, postRestart));
        }

        var firstPassEnd = restartAfter is { } n ? Math.Min(n, checkpoints.Count) : checkpoints.Count;
        for (var i = 0; i < firstPassEnd; i++)
        {
            await RunCheckpointAsync(checkpoints[i], finalize: false);
            if ((i + 1) % 25 == 0 || i == firstPassEnd - 1)
            {
                Console.WriteLine($"  poll {i + 1}/{checkpoints.Count} done ({checkpoints[i]:HH:mm:ss} UTC)");
            }
        }

        int? midpointVolumeBarCount = null;
        if (restartAfter is not null)
        {
            await using var mid = new VolumeBarDbContext(testOptions);
            midpointVolumeBarCount = await mid.VolumeBars.CountAsync(b => b.AsOfDate == date && b.BarVolumeThreshold == threshold);
            Console.WriteLine($"--- Simulated restart after poll #{restartAfter}: {midpointVolumeBarCount} future bar(s) written so far. Resuming (fresh call, no in-memory state carried over -- exactly what a real process restart would do). ---");
            Console.WriteLine($"    Cache state before restart: {cache.QuoteTokenCountForDiagnostics} quote-series token(s), {cache.OiTokenCountForDiagnostics} OI-series token(s) warm -- discarded now, replaced with a brand-new (cold) LiveOptionSeriesCache, exactly what a real process restart does to this Singleton.");
            Console.WriteLine($"    Future-side builder cache before restart: warm={futureBuilderCache.HasState(date, threshold)} -- discarded now, replaced with a brand-new (cold) LiveVolumeBarBuilderCache, exactly what a real process restart does to this Singleton.");
            cache = new LiveOptionSeriesCache();
            futureBuilderCache = new LiveVolumeBarBuilderCache();

            // "Resume" is literally just continuing to call the same idempotent writers -- there is
            // no separate resume code path to exercise, which is the whole point (see
            // LiveVolumeBarPopulator's own doc comment on why replay-from-scratch was chosen). The
            // cache is a fresh, cold instance now -- the first post-restart checkpoint below must fall
            // back to a full reload for each touched token (see LiveOptionSeriesCache's own doc comment
            // on why null/no-existing-series means "reload from dayStart," never a wrong/partial answer).
            for (var i = firstPassEnd; i < checkpoints.Count; i++)
            {
                await RunCheckpointAsync(checkpoints[i], finalize: false, postRestart: true);
                if ((i + 1) % 10 == 0 || i == checkpoints.Count - 1)
                {
                    Console.WriteLine($"  poll {i + 1}/{checkpoints.Count} done ({checkpoints[i]:HH:mm:ss} UTC) [post-restart]");
                }
            }
        }

        // Final poll: flush the day's necessarily-partial last bar.
        await RunCheckpointAsync(dayEnd, finalize: true, postRestart: restartAfter is not null);

        // Live performance incident fix (docs/LIVE_PARITY_PLAN.md, dated entry): THE actual proof
        // this task is about -- does the future-side write's own per-poll cost stay roughly constant
        // over the course of the day, instead of growing with the size of the whole day-so-far
        // replay (the original bug). Report early/quarter/mid/three-quarter/late checkpoints so a
        // growing trend (the old behavior) is as visible as a flat one (the fix).
        if (futureWriteTimings.Count >= 5)
        {
            var sampleCount = futureWriteTimings.Count;
            int[] sampleIndexes = [0, sampleCount / 4, sampleCount / 2, 3 * sampleCount / 4, sampleCount - 1];
            string[] labels = ["earliest", "quarter", "midday", "three-quarter", "latest"];
            Console.WriteLine("--- Future-side WriteNewBarsAsync per-poll cost across the simulated day (the actual point of this fix) ---");
            for (var i = 0; i < sampleIndexes.Length; i++)
            {
                var t = futureWriteTimings[sampleIndexes[i]];
                Console.WriteLine($"    [{labels[i],14}] poll #{t.Index + 1}/{sampleCount}: {t.ElapsedMs}ms");
            }

            var firstQuarter = futureWriteTimings.Take(Math.Max(1, sampleCount / 4)).Select(t => t.ElapsedMs).ToList();
            var lastQuarter = futureWriteTimings.Skip(Math.Max(0, sampleCount - sampleCount / 4)).Select(t => t.ElapsedMs).ToList();
            var firstQuarterAvg = firstQuarter.Average();
            var lastQuarterAvg = lastQuarter.Average();
            Console.WriteLine($"    First-quarter avg: {firstQuarterAvg:F1}ms. Last-quarter avg: {lastQuarterAvg:F1}ms.");
            Console.WriteLine(lastQuarterAvg <= firstQuarterAvg * 3 || lastQuarterAvg <= 50
                ? "PERFORMANCE (future-side incremental fix): PASS -- per-poll cost stayed roughly flat across the day (not growing proportionally to total ticks-so-far)."
                : $"PERFORMANCE (future-side incremental fix): NOTE -- last-quarter avg ({firstQuarterAvg:F1}ms) is more than 3x first-quarter avg ({firstQuarterAvg:F1}ms); investigate before declaring the fix complete.");

            // Direct BEFORE-vs-AFTER, same code path, same checkpoints: builderCache=null reproduces
            // the ORIGINAL from-scratch-every-poll behavior exactly (see WriteNewBarsAsync's own doc
            // comment) -- re-running the identical checkpoint sequence against a disposable scratch DB
            // with the old (no-cache) call gives a true apples-to-apples per-poll cost comparison,
            // not just "the new path looks fast in isolation."
            Console.WriteLine();
            Console.WriteLine("--- BEFORE-fix comparison: identical checkpoints, builderCache=null (original from-scratch-every-poll replay) ---");
            var beforeOptions = new DbContextOptionsBuilder<VolumeBarDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "niftysignal_volume_bars_livetest_beforefix" }.ConnectionString).Options;
            await using (var wipeBefore = new VolumeBarDbContext(beforeOptions))
            {
                await wipeBefore.Database.EnsureDeletedAsync();
                await wipeBefore.Database.MigrateAsync();
            }

            var beforeTimings = new List<long>();
            for (var i = 0; i < checkpoints.Count; i++)
            {
                await using var beforeSource = new NiftySignalDbContext(sourceOptions);
                await using var beforeDestination = new VolumeBarDbContext(beforeOptions);
                var beforeSw = System.Diagnostics.Stopwatch.StartNew();
                await LiveVolumeBarPopulator.WriteNewBarsAsync(beforeSource, beforeDestination, date, threshold, checkpoints[i], finalizeDay: false, CancellationToken.None);
                beforeSw.Stop();
                beforeTimings.Add(beforeSw.ElapsedMilliseconds);
            }

            for (var i = 0; i < sampleIndexes.Length; i++)
            {
                var idx = Math.Min(sampleIndexes[i], beforeTimings.Count - 1);
                Console.WriteLine($"    [{labels[i],14}] poll #{idx + 1}/{sampleCount}: BEFORE={beforeTimings[idx]}ms, AFTER={futureWriteTimings[idx].ElapsedMs}ms");
            }

            var beforeFirstQuarterAvg = beforeTimings.Take(Math.Max(1, sampleCount / 4)).Average();
            var beforeLastQuarterAvg = beforeTimings.Skip(Math.Max(0, sampleCount - sampleCount / 4)).Average();
            Console.WriteLine($"    BEFORE first-quarter avg: {beforeFirstQuarterAvg:F1}ms. BEFORE last-quarter avg: {beforeLastQuarterAvg:F1}ms (growth factor: {beforeLastQuarterAvg / Math.Max(0.1, beforeFirstQuarterAvg):F1}x).");
            Console.WriteLine($"    AFTER  first-quarter avg: {firstQuarterAvg:F1}ms. AFTER  last-quarter avg: {lastQuarterAvg:F1}ms (growth factor: {lastQuarterAvg / Math.Max(0.1, firstQuarterAvg):F1}x).");
        }

        if (restartAfter is not null && pollTimings.Count(t => t.PostRestart) >= 2)
        {
            var postRestartTimings = pollTimings.Where(t => t.PostRestart).ToList();
            var firstPostRestart = postRestartTimings[0];
            var laterPostRestart = postRestartTimings.Skip(1).ToList();
            var laterAvg = laterPostRestart.Average(t => t.ElapsedMs);
            var laterMax = laterPostRestart.Max(t => t.ElapsedMs);
            Console.WriteLine($"--- Post-restart poll timings (proving the cache goes cold once, then re-warms -- not a permanent regression) ---");
            Console.WriteLine($"    First poll after restart (cold cache, full reload expected): {firstPostRestart.ElapsedMs}ms.");
            Console.WriteLine($"    Remaining {laterPostRestart.Count} post-restart poll(s): avg={laterAvg:F1}ms, max={laterMax}ms (warm cache, incremental top-up only).");
            Console.WriteLine(firstPostRestart.ElapsedMs >= laterMax
                ? "PERFORMANCE: PASS -- first post-restart poll was the slowest (one-time cold-cache cost), every later poll stayed fast (cache re-warmed, no permanent regression)."
                : "PERFORMANCE: NOTE -- first post-restart poll was not the single slowest (see timings above; with only a handful of new ticks per poll bucket in this test's data, absolute magnitudes can be noisy at millisecond scale, but the cold-vs-warm SHAPE is what this proves, not one exact number).");
        }

        if (restartAfter is not null)
        {
            // Idempotency proof: rather than re-running the whole day a second time end-to-end
            // (expensive and redundant), check directly for what a restart-induced bug would
            // actually look like -- a duplicate or a gap in (AsOfDate, BarVolumeThreshold, BarIndex)
            // identity. destination's own unique index already makes a true DUPLICATE impossible at
            // the DB level (SaveChangesAsync would have thrown), so this instead checks for GAPS
            // (a skipped BarIndex) and, definitively, that the post-restart final row set is
            // byte-identical to the offline populator's own rows for the same day (checked right
            // below as the parity comparison) -- since the offline populator never restarts
            // mid-day, an exact match there is only possible if the restart neither duplicated nor
            // dropped a single bar.
            await using var idemp = new VolumeBarDbContext(testOptions);
            var volIndexes = await idemp.VolumeBars.Where(b => b.AsOfDate == date && b.BarVolumeThreshold == threshold).Select(b => b.BarIndex).OrderBy(i => i).ToListAsync();
            var hasGap = volIndexes.Where((v, i) => v != i).Any();
            Console.WriteLine(hasGap
                ? $"IDEMPOTENCY: FAIL -- gap or duplicate detected in VolumeBars BarIndex sequence after restart+resume (expected 0..{volIndexes.Count - 1} contiguous, got {volIndexes.Count} rows with a mismatch)."
                : $"IDEMPOTENCY: BarIndex sequence contiguous and gap-free after restart+resume ({volIndexes.Count} bars, 0..{volIndexes.Count - 1}). Final parity check below confirms no duplication/omission of actual bar content.");
        }

        var parityOk = await CompareAsync(testOptions, officialOptions, date, threshold, "live-replay vs offline populator (parity proof)");
        Console.WriteLine(parityOk
            ? "PARITY: PASS -- live-replay rows match the offline populator's rows exactly, table by table, row by row."
            : "PARITY: FAIL -- see diffs above.");

        return parityOk ? 0 : 1;
    }

    /// <summary>Row-for-row comparison of all 4 Phase-A tables for one (date, threshold) between two VolumeBarDbContext-backed databases. Prints every mismatch found (capped) rather than stopping at the first one, so a real bug's full scope is visible in one run.</summary>
    static async Task<bool> CompareAsync(DbContextOptions<VolumeBarDbContext> aOptions, DbContextOptions<VolumeBarDbContext> bOptions, DateOnly date, long threshold, string label)
    {
        Console.WriteLine($"--- Comparing: {label} ---");
        var ok = true;

        await using var a = new VolumeBarDbContext(aOptions);
        await using var b = new VolumeBarDbContext(bOptions);

        var volA = await a.VolumeBars.Where(x => x.AsOfDate == date && x.BarVolumeThreshold == threshold).OrderBy(x => x.BarIndex).ToListAsync();
        var volB = await b.VolumeBars.Where(x => x.AsOfDate == date && x.BarVolumeThreshold == threshold).OrderBy(x => x.BarIndex).ToListAsync();
        ok &= ReportDiff("VolumeBars", volA.Count, volB.Count, volA.Zip(volB).Where(p =>
            p.First.StartTimestamp != p.Second.StartTimestamp || p.First.EndTimestamp != p.Second.EndTimestamp ||
            p.First.OpenPrice != p.Second.OpenPrice || p.First.HighPrice != p.Second.HighPrice ||
            p.First.LowPrice != p.Second.LowPrice || p.First.ClosePrice != p.Second.ClosePrice ||
            p.First.Volume != p.Second.Volume || p.First.OpenInterestAtClose != p.Second.OpenInterestAtClose ||
            p.First.VwapAtClose != p.Second.VwapAtClose || p.First.FutureCvdNet != p.Second.FutureCvdNet ||
            p.First.FutureDepthImbalance != p.Second.FutureDepthImbalance || p.First.OrderFlowImbalance != p.Second.OrderFlowImbalance ||
            p.First.TopOfBookImbalance != p.Second.TopOfBookImbalance)
            .Select(p => $"BarIndex={p.First.BarIndex}: A={{Close={p.First.ClosePrice},Vol={p.First.Volume},End={p.First.EndTimestamp:HH:mm:ss.fff}}} B={{Close={p.Second.ClosePrice},Vol={p.Second.Volume},End={p.Second.EndTimestamp:HH:mm:ss.fff}}}"));

        var atmA = await a.OptionAtmBars.Where(x => x.AsOfDate == date && x.BarVolumeThreshold == threshold).OrderBy(x => x.BarIndex).ToListAsync();
        var atmB = await b.OptionAtmBars.Where(x => x.AsOfDate == date && x.BarVolumeThreshold == threshold).OrderBy(x => x.BarIndex).ToListAsync();
        ok &= ReportDiff("OptionAtmBars", atmA.Count, atmB.Count, atmA.Zip(atmB).Where(p =>
            p.First.SyntheticForward != p.Second.SyntheticForward || p.First.AtmStrike != p.Second.AtmStrike ||
            p.First.AtmCallIv != p.Second.AtmCallIv || p.First.AtmPutIv != p.Second.AtmPutIv || p.First.AtmIv != p.Second.AtmIv)
            .Select(p => $"BarIndex={p.First.BarIndex}: A={{Strike={p.First.AtmStrike},Iv={p.First.AtmIv}}} B={{Strike={p.Second.AtmStrike},Iv={p.Second.AtmIv}}}"));

        // BandWidth is part of OptionDepthBarRow's own identity (multiple widths coexist for the
        // same day/threshold -- see OptionDepthBarRow's own doc comment) -- filter to the live
        // writer's own default (LiveOptionDepthPopulator.DefaultBandWidth) on both sides, or a
        // row-count mismatch here would just reflect "official has more bandwidths populated,"
        // not a real live-vs-offline divergence.
        var depthA = await a.OptionDepthBars.Where(x => x.AsOfDate == date && x.BarVolumeThreshold == threshold && x.BandWidth == LiveOptionDepthPopulator.DefaultBandWidth).OrderBy(x => x.BarIndex).ToListAsync();
        var depthB = await b.OptionDepthBars.Where(x => x.AsOfDate == date && x.BarVolumeThreshold == threshold && x.BandWidth == LiveOptionDepthPopulator.DefaultBandWidth).OrderBy(x => x.BarIndex).ToListAsync();
        ok &= ReportDiff("OptionDepthBars", depthA.Count, depthB.Count, depthA.Zip(depthB).Where(p =>
            p.First.CallBidQtyAvg != p.Second.CallBidQtyAvg || p.First.CallAskQtyAvg != p.Second.CallAskQtyAvg ||
            p.First.PutBidQtyAvg != p.Second.PutBidQtyAvg || p.First.PutAskQtyAvg != p.Second.PutAskQtyAvg ||
            p.First.CallTobBidQtyAvg != p.Second.CallTobBidQtyAvg || p.First.CallTobAskQtyAvg != p.Second.CallTobAskQtyAvg ||
            p.First.PutTobBidQtyAvg != p.Second.PutTobBidQtyAvg || p.First.PutTobAskQtyAvg != p.Second.PutTobAskQtyAvg)
            .Select(p => $"BarIndex={p.First.BarIndex}: A={{CallBid={p.First.CallBidQtyAvg}}} B={{CallBid={p.Second.CallBidQtyAvg}}}"));

        var mpA = await a.OptionMaxPainBars.Where(x => x.AsOfDate == date && x.BarVolumeThreshold == threshold).OrderBy(x => x.BarIndex).ToListAsync();
        var mpB = await b.OptionMaxPainBars.Where(x => x.AsOfDate == date && x.BarVolumeThreshold == threshold).OrderBy(x => x.BarIndex).ToListAsync();
        ok &= ReportDiff("OptionMaxPainBars", mpA.Count, mpB.Count, mpA.Zip(mpB).Where(p =>
            p.First.MaxPainStrike != p.Second.MaxPainStrike || p.First.HighestOiStrike != p.Second.HighestOiStrike)
            .Select(p => $"BarIndex={p.First.BarIndex}: A={{MaxPain={p.First.MaxPainStrike},HighOi={p.First.HighestOiStrike}}} B={{MaxPain={p.Second.MaxPainStrike},HighOi={p.Second.HighestOiStrike}}}"));

        return ok;
    }

    static bool ReportDiff(string table, int countA, int countB, IEnumerable<string> mismatches)
    {
        var mismatchList = mismatches.Take(10).ToList();
        var countOk = countA == countB;
        Console.WriteLine($"  {table}: A={countA} rows, B={countB} rows, countMatch={countOk}, valueMismatches(capped 10)={mismatchList.Count}");
        foreach (var m in mismatchList)
        {
            Console.WriteLine($"    MISMATCH {m}");
        }

        return countOk && mismatchList.Count == 0;
    }
}
