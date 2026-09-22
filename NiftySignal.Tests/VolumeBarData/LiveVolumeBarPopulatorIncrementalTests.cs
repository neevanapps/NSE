using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// Live performance incident fix (docs/LIVE_PARITY_PLAN.md, dated entry): proves
/// <see cref="LiveVolumeBarBuilderCache"/>-driven incremental future-side bar building
/// (<see cref="LiveVolumeBarPopulator.WriteNewBarsAsync"/>'s new <c>builderCache</c> parameter)
/// produces BYTE-IDENTICAL results to the original from-scratch-every-poll replay, and that a
/// simulated mid-day restart (cache discarded, brand-new instance) still converges to the exact
/// same day. Same deterministic, no-database-round-trip convention
/// <see cref="LiveOptionSeriesCacheRestartTests"/> already establishes for the option-side cache.
/// </summary>
public sealed class LiveVolumeBarPopulatorIncrementalTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 16); // a Wednesday -- matches a real historical trading day used elsewhere in this codebase's replay proofs.
    const string FutureToken = "FUTTOK";
    const long Threshold = 1000;
    static readonly DateTimeOffset DayStart = LiveVolumeBarPopulator.DayStartUtc(AsOfDate);
    static readonly DateTimeOffset DayEnd = LiveVolumeBarPopulator.DayEndUtc(AsOfDate);

    static MarketDepth Depth(int i) => new(
        Bid1Price: 23000m + i, Bid1Qty: 100 + i, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: 23001m + i, Ask1Qty: 90 + i, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    /// <summary>630 ticks spread evenly across the trading day, cumulative volume rising 40/tick (so a 1000-volume threshold completes a bar roughly every 25 ticks -- multiple bars, multiple poll-sized chunks), every third tick carrying a depth snapshot (so CVD/depth/OFI/TOB accumulators -- all internal to VolumeBarBuilder -- are genuinely exercised across a resume boundary, not just OHLC/volume).</summary>
    static List<Tick> GenerateTicks()
    {
        const int count = 630;
        var interval = (DayEnd - DayStart) / count;
        var ticks = new List<Tick>();
        for (var i = 0; i < count; i++)
        {
            ticks.Add(new Tick
            {
                Token = FutureToken,
                Exchange = Exchange.Nfo,
                ExchangeTimestamp = DayStart + interval * i,
                ReceivedAt = DayStart + interval * i,
                LastPrice = 23000m + (i % 17) - 8,
                Volume = i * 40,
                OpenInterest = 500000 + i * 3,
                Depth = i % 3 == 0 ? Depth(i) : null,
            });
        }

        return ticks;
    }

    static NiftySignalDbContext NewSourceDb(List<Tick> ticks)
    {
        var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{Guid.NewGuid()}").Options);
        source.Instruments.Add(new Instrument
        {
            Token = FutureToken,
            Exchange = Exchange.Nfo,
            TradingSymbol = "NIFTY-FUT",
            InstrumentType = InstrumentType.Future,
            Underlying = "NIFTY",
            AsOfDate = AsOfDate,
        });
        source.Ticks.AddRange(ticks);
        source.SaveChanges();
        return source;
    }

    static VolumeBarDbContext NewDestinationDb() =>
        new(new DbContextOptionsBuilder<VolumeBarDbContext>().UseInMemoryDatabase($"dest-{Guid.NewGuid()}").Options);

    static async Task<List<VolumeBarRow>> ReadBarsAsync(VolumeBarDbContext db) =>
        await db.VolumeBars.Where(b => b.AsOfDate == AsOfDate && b.BarVolumeThreshold == Threshold).OrderBy(b => b.BarIndex).ToListAsync();

    static bool RowsEqual(VolumeBarRow a, VolumeBarRow b) =>
        a.BarIndex == b.BarIndex && a.StartTimestamp == b.StartTimestamp && a.EndTimestamp == b.EndTimestamp &&
        a.OpenPrice == b.OpenPrice && a.HighPrice == b.HighPrice && a.LowPrice == b.LowPrice && a.ClosePrice == b.ClosePrice &&
        a.Volume == b.Volume && a.OpenInterestAtClose == b.OpenInterestAtClose && a.VwapAtClose == b.VwapAtClose &&
        a.FutureCvdNet == b.FutureCvdNet && a.FutureDepthImbalance == b.FutureDepthImbalance &&
        a.OrderFlowImbalance == b.OrderFlowImbalance && a.TopOfBookImbalance == b.TopOfBookImbalance;

    /// <summary>The baseline every incremental run below is checked against: a single from-scratch call straight to dayEnd with finalizeDay=true and no builderCache -- exactly today's (pre-fix) production behavior.</summary>
    static async Task<List<VolumeBarRow>> RunColdFullReplayAsync(List<Tick> ticks)
    {
        await using var source = NewSourceDb(ticks);
        await using var destination = NewDestinationDb();
        var result = await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, AsOfDate, Threshold, DayEnd, finalizeDay: true, CancellationToken.None);
        Assert.Equal(LiveVolumeBarWriteOutcome.Written, result.Outcome);
        return await ReadBarsAsync(destination);
    }

    [Fact]
    public async Task IncrementalBuilderCache_ProducesByteIdenticalBars_ToColdFullReplay()
    {
        var ticks = GenerateTicks();
        var reference = await RunColdFullReplayAsync(ticks);
        Assert.True(reference.Count > 10, "test fixture should produce a meaningful number of bars");

        await using var source = NewSourceDb(ticks);
        await using var destination = NewDestinationDb();
        var cache = new LiveVolumeBarBuilderCache();

        // 21 poll checkpoints of 30 ticks each -- mirrors LiveVolumeBarWriter's real cadence: many
        // small polls, each only exposing ticks up to its own cutoff (no look-ahead).
        const int chunk = 30;
        for (var i = chunk; i < ticks.Count; i += chunk)
        {
            var cutoff = ticks[i - 1].ExchangeTimestamp;
            await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, AsOfDate, Threshold, cutoff, finalizeDay: false, CancellationToken.None, cache);
        }

        var finalResult = await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, AsOfDate, Threshold, DayEnd, finalizeDay: true, CancellationToken.None, cache);
        Assert.Equal(LiveVolumeBarWriteOutcome.Written, finalResult.Outcome);

        var incremental = await ReadBarsAsync(destination);

        Assert.Equal(reference.Count, incremental.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.True(RowsEqual(reference[i], incremental[i]), $"BarIndex={i} differs: reference={{Close={reference[i].ClosePrice},Vol={reference[i].Volume},End={reference[i].EndTimestamp:HH:mm:ss.fff},Cvd={reference[i].FutureCvdNet}}} incremental={{Close={incremental[i].ClosePrice},Vol={incremental[i].Volume},End={incremental[i].EndTimestamp:HH:mm:ss.fff},Cvd={incremental[i].FutureCvdNet}}}");
        }

        // Bar-index sequence must stay contiguous and gap-free -- the idempotency guarantee this
        // task must not change.
        var indexes = incremental.Select(b => b.BarIndex).ToList();
        Assert.Equal(Enumerable.Range(0, indexes.Count), indexes);
    }

    [Fact]
    public async Task SimulatedMidDayRestart_CacheDiscardedAndRebuilt_StillConvergesToTheSameDay()
    {
        var ticks = GenerateTicks();
        var reference = await RunColdFullReplayAsync(ticks);

        await using var source = NewSourceDb(ticks);
        await using var destination = NewDestinationDb();
        var cache = new LiveVolumeBarBuilderCache();

        const int chunk = 30;
        var checkpoints = new List<DateTimeOffset>();
        for (var i = chunk; i < ticks.Count; i += chunk)
        {
            checkpoints.Add(ticks[i - 1].ExchangeTimestamp);
        }

        var restartAfter = checkpoints.Count / 2;

        // Phase 1: warm the cache across the first half of the day's polls, same as real steady-state operation.
        for (var i = 0; i < restartAfter; i++)
        {
            await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, AsOfDate, Threshold, checkpoints[i], finalizeDay: false, CancellationToken.None, cache);
        }

        Assert.True(cache.HasState(AsOfDate, Threshold), "cache should be warm before the simulated restart");
        var barsBeforeRestart = await destination.VolumeBars.CountAsync(b => b.AsOfDate == AsOfDate && b.BarVolumeThreshold == Threshold);
        Assert.True(barsBeforeRestart > 0, "at least one bar should already be persisted before the simulated restart");

        // Phase 2: simulate a real Host restart -- the Singleton is gone, a brand-new (cold) cache
        // takes its place. The database (destination) is untouched, exactly like a real restart.
        cache = new LiveVolumeBarBuilderCache();
        Assert.False(cache.HasState(AsOfDate, Threshold));

        // The first poll after "restart" must fall back to a full-day replay and still only WRITE
        // bars not already persisted (existingMaxIndex) -- proving the restart neither duplicates
        // nor drops a bar.
        for (var i = restartAfter; i < checkpoints.Count; i++)
        {
            await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, AsOfDate, Threshold, checkpoints[i], finalizeDay: false, CancellationToken.None, cache);
        }

        var finalResult = await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, AsOfDate, Threshold, DayEnd, finalizeDay: true, CancellationToken.None, cache);
        Assert.Equal(LiveVolumeBarWriteOutcome.Written, finalResult.Outcome);

        var afterRestart = await ReadBarsAsync(destination);

        Assert.Equal(reference.Count, afterRestart.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.True(RowsEqual(reference[i], afterRestart[i]), $"BarIndex={i} differs after simulated restart.");
        }

        var indexes = afterRestart.Select(b => b.BarIndex).ToList();
        Assert.Equal(Enumerable.Range(0, indexes.Count), indexes); // no gap, no duplicate.
    }

    [Fact]
    public async Task FinalizeDay_ClearsCachedBuilder_SoASecondPollTheSameDayFallsBackToFullReplay_NotADoubleFinalize()
    {
        var ticks = GenerateTicks();
        await using var source = NewSourceDb(ticks);
        await using var destination = NewDestinationDb();
        var cache = new LiveVolumeBarBuilderCache();

        await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, AsOfDate, Threshold, ticks[300].ExchangeTimestamp, finalizeDay: false, CancellationToken.None, cache);
        Assert.True(cache.HasState(AsOfDate, Threshold));

        await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, AsOfDate, Threshold, DayEnd, finalizeDay: true, CancellationToken.None, cache);
        Assert.False(cache.HasState(AsOfDate, Threshold), "the cache must not offer a finalized builder for reuse -- a stray extra poll the same day must fall back to a full, correct replay.");

        // A stray extra poll after finalize: falls back to full replay (cache empty), still produces
        // the exact same completed set of bars (existingMaxIndex means nothing NEW gets written).
        var strayResult = await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, AsOfDate, Threshold, DayEnd, finalizeDay: true, CancellationToken.None, cache);
        Assert.Equal(LiveVolumeBarWriteOutcome.NoNewBars, strayResult.Outcome);
    }
}
