using Microsoft.EntityFrameworkCore;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// Futures-crossover live-wiring task (2026-09-21): unit coverage for
/// <see cref="LiveFuturesCrossoverSession"/> -- the strict bar-ordering guard, the rolling-window
/// "not evaluable yet" gate, and (the most safety-critical property, same emphasis this project's
/// existing <c>TradingDaySession</c>/Phase-C tests already give restart-safety) that
/// <see cref="LiveFuturesCrossoverSession.RebuildAsync"/> reproduces an uninterrupted run's state
/// exactly, bar for bar, after a simulated mid-day restart.
/// </summary>
public class LiveFuturesCrossoverSessionTests
{
    static readonly DateOnly Day = new(2026, 9, 16);
    const long Threshold = 2600L;
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    /// <summary>
    /// Builds a deterministic, alternating-price synthetic day of <paramref name="count"/> 1-minute
    /// bars starting at 09:31 IST -- enough (with count &gt;= 41) to fill the 40-bar slow window and
    /// exercise the crossover logic beyond its "not yet evaluable" gate, without depending on any
    /// real historical data (this project's own established convention for scoring-formula unit
    /// tests, e.g. <c>OptionsThreeWayScoreCalculatorTests</c>).
    /// </summary>
    static List<VolumeBarRow> BuildBars(int count)
    {
        var bars = new List<VolumeBarRow>();
        var start = new DateTimeOffset(Day.Year, Day.Month, Day.Day, 9, 31, 0, IstOffset);
        decimal price = 24000m;

        for (var i = 0; i < count; i++)
        {
            // Alternating up/down price moves with a strong, consistent depth-imbalance signal
            // during the Open window (drives BOTH legs' own trackers with real variance, so the
            // rolling window has genuine, non-degenerate readings to average).
            var delta = i % 2 == 0 ? 5m : -3m;
            price += delta;
            var end = start.AddMinutes(i + 1);

            bars.Add(new VolumeBarRow
            {
                AsOfDate = Day,
                BarIndex = i,
                BarVolumeThreshold = Threshold,
                StartTimestamp = end.AddMinutes(-1),
                EndTimestamp = end,
                DurationSeconds = 30 + (i % 5) * 10,
                OpenPrice = price - delta,
                HighPrice = price + 1,
                LowPrice = price - 1,
                ClosePrice = price,
                Volume = Threshold,
                FutureDepthImbalance = 0.1 + 0.01 * (i % 7),
                TopOfBookImbalance = 0.05 + 0.02 * (i % 3),
            });
        }

        return bars;
    }

    [Fact]
    public void ProcessBar_OutOfOrderBarIndex_Throws()
    {
        var session = new LiveFuturesCrossoverSession(Day, Threshold);
        var bars = BuildBars(2);

        Assert.Throws<InvalidOperationException>(() => session.ProcessBar(bars[1]));
    }

    [Fact]
    public void ProcessBar_ReprocessingSameBarIndex_Throws()
    {
        var session = new LiveFuturesCrossoverSession(Day, Threshold);
        var bars = BuildBars(2);

        session.ProcessBar(bars[0]);
        Assert.Throws<InvalidOperationException>(() => session.ProcessBar(bars[0]));
    }

    [Fact]
    public void ProcessBar_BeforeWindowFull_FastSlowDiffAreNull()
    {
        var session = new LiveFuturesCrossoverSession(Day, Threshold);
        var bars = BuildBars(LiveFuturesCrossoverSession.SlowBars - 1);

        LiveCrossoverBarResult? last = null;
        foreach (var bar in bars)
        {
            last = session.ProcessBar(bar);
        }

        Assert.NotNull(last);
        Assert.Null(last!.ScoreRow.FastMa);
        Assert.Null(last.ScoreRow.SlowMa);
        Assert.Null(last.ScoreRow.Diff);
        Assert.False(last.ScoreRow.CrossedUp);
        Assert.False(last.ScoreRow.CrossedDown);
    }

    [Fact]
    public void ProcessBar_WindowFull_FastSlowDiffBecomeAvailable()
    {
        var session = new LiveFuturesCrossoverSession(Day, Threshold);
        var bars = BuildBars(LiveFuturesCrossoverSession.SlowBars);

        LiveCrossoverBarResult? last = null;
        foreach (var bar in bars)
        {
            last = session.ProcessBar(bar);
        }

        Assert.NotNull(last!.ScoreRow.FastMa);
        Assert.NotNull(last.ScoreRow.SlowMa);
        Assert.NotNull(last.ScoreRow.Diff);
        Assert.Equal(last.ScoreRow.FastMa!.Value - last.ScoreRow.SlowMa!.Value, last.ScoreRow.Diff!.Value, 1e-9);
    }

    [Fact]
    public void FlushEndOfDay_NoOpenSignal_ReturnsNull()
    {
        var session = new LiveFuturesCrossoverSession(Day, Threshold);
        Assert.Null(session.FlushEndOfDay(DateTimeOffset.UtcNow));
    }

    static VolumeBarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<VolumeBarDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task RebuildAsync_NoScoredBarsYet_ReturnsFreshSession()
    {
        await using var db = NewDb();
        var rebuilt = await LiveFuturesCrossoverSession.RebuildAsync(db, Day, Threshold, CancellationToken.None);

        Assert.Equal(-1, rebuilt.LastProcessedBarIndex);
        Assert.False(rebuilt.HasOpenSignal);
    }

    [Fact]
    public async Task RebuildAsync_AfterSimulatedRestart_ReproducesUninterruptedRun_BarForBar()
    {
        // Same restart-safety proof shape as this project's own Phase-C
        // ReplayLiveScoreCommand.RunRestartTestAsync: score the first half directly, "restart" (the
        // in-memory session is simply discarded), RebuildAsync from persisted rows + the bar table,
        // then finish the day and compare EVERY per-bar diagnostic field against an uninterrupted
        // run over the same bars.
        var bars = BuildBars(LiveFuturesCrossoverSession.SlowBars + 15);
        const int restartAfterBarIndex = 24;

        // Uninterrupted reference run.
        var referenceSession = new LiveFuturesCrossoverSession(Day, Threshold);
        var referenceRows = bars.Select(b => referenceSession.ProcessBar(b).ScoreRow).ToList();

        // Restart-simulated run: persist bars + first-half scores to an InMemory DB, discard the
        // session, rebuild, then finish the day.
        await using var db = NewDb();
        db.VolumeBars.AddRange(bars);
        await db.SaveChangesAsync();

        var preRestartSession = new LiveFuturesCrossoverSession(Day, Threshold);
        foreach (var bar in bars.Where(b => b.BarIndex <= restartAfterBarIndex))
        {
            var result = preRestartSession.ProcessBar(bar);
            db.LiveFuturesCrossoverScoreBars.Add(result.ScoreRow);
        }

        await db.SaveChangesAsync();

        var rebuilt = await LiveFuturesCrossoverSession.RebuildAsync(db, Day, Threshold, CancellationToken.None);
        Assert.Equal(preRestartSession.LastProcessedBarIndex, rebuilt.LastProcessedBarIndex);
        Assert.Equal(preRestartSession.HasOpenSignal, rebuilt.HasOpenSignal);

        var postRestartRows = bars.Where(b => b.BarIndex > restartAfterBarIndex).Select(b => rebuilt.ProcessBar(b).ScoreRow).ToList();
        var rebuiltFullRun = referenceRows.Where(r => r.BarIndex <= restartAfterBarIndex).ToList().Concat(postRestartRows).ToList();

        Assert.Equal(referenceRows.Count, rebuiltFullRun.Count);
        for (var i = 0; i < referenceRows.Count; i++)
        {
            var expected = referenceRows[i];
            var actual = rebuiltFullRun[i];
            Assert.Equal(expected.BarIndex, actual.BarIndex);
            Assert.Equal(expected.RawScore, actual.RawScore);
            Assert.Equal(expected.ScaledScore, actual.ScaledScore);
            Assert.Equal(expected.FastMa, actual.FastMa);
            Assert.Equal(expected.SlowMa, actual.SlowMa);
            Assert.Equal(expected.Diff, actual.Diff);
            Assert.Equal(expected.CrossedUp, actual.CrossedUp);
            Assert.Equal(expected.CrossedDown, actual.CrossedDown);
        }
    }
}
