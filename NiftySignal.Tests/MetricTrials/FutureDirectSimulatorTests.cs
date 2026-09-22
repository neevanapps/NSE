using NiftySignal.BacktestData;
using NiftySignal.MetricTrials;

namespace NiftySignal.Tests.MetricTrials;

public sealed class RollingTrendEfficiencyTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 10, 9, 15, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void SinglePoint_ReturnsNull()
    {
        var tracker = new RollingTrendEfficiency(TimeSpan.FromMinutes(15));
        Assert.Null(tracker.Observe(T0, 100));
    }

    [Fact]
    public void MonotonicMove_HasTrendEfficiencyOfOne_NoWastedMotion()
    {
        var tracker = new RollingTrendEfficiency(TimeSpan.FromMinutes(15));
        tracker.Observe(T0, 100);
        tracker.Observe(T0.AddSeconds(15), 101);
        var efficiency = tracker.Observe(T0.AddSeconds(30), 102);

        Assert.Equal(1.0, efficiency);
    }

    [Fact]
    public void BackAndForthMove_HasLowTrendEfficiency()
    {
        var tracker = new RollingTrendEfficiency(TimeSpan.FromMinutes(15));
        tracker.Observe(T0, 100);
        tracker.Observe(T0.AddSeconds(15), 110); // up 10
        var efficiency = tracker.Observe(T0.AddSeconds(30), 101); // back down to near start

        // Net change = |101-100| = 1; total path = 10 + 9 = 19 -> efficiency ~0.053, nowhere near 1.
        Assert.True(efficiency < 0.1);
    }

    [Fact]
    public void OldPoints_AreEvictedOnceTheyAgeOutOfTheWindow()
    {
        var tracker = new RollingTrendEfficiency(TimeSpan.FromMinutes(15));
        tracker.Observe(T0, 100);
        tracker.Observe(T0.AddMinutes(1), 200); // huge jump, but will age out
        var efficiency = tracker.Observe(T0.AddMinutes(16), 201); // only the second point should remain

        // If the first point had aged out correctly, the window holds just (200 at t+1min) and
        // (201 at t+16min) -- net change 1, path length 1, efficiency 1.0. If it hadn't been
        // evicted, the huge first jump would dominate and produce a very different number.
        Assert.Equal(1.0, efficiency);
    }
}

public sealed class FutureDirectSimulatorTests
{
    static readonly DateOnly Day = new(2026, 9, 10);
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    static CadenceContext Row(TimeOnly time, decimal? price, long? cvd5min, long? cvd15min = null) => new()
    {
        Timestamp = new DateTimeOffset(Day.ToDateTime(time), Ist),
        AsOfDate = Day,
        NearestExpiryDate = new DateOnly(2026, 9, 15),
        FutureCloseFromLastCadence = price,
        FutureCvdProxyNet5Min = cvd5min,
        FutureCvdProxyNet15Min = cvd15min,
    };

    static SimulationOptions Options() => new(EntryRankCutoff: 80, ExitRankCutoff: 50);

    [Fact]
    public void NoEntry_BeforeNineThirty_EvenWithAQualifyingSignal()
    {
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 15), 100m, 1000),
            Row(new TimeOnly(9, 20), 100m, 1000),
            Row(new TimeOnly(9, 29), 100m, 5000), // strong signal, but still before 09:30
            Row(new TimeOnly(15, 15), 100m, 5000), // force-close boundary
        };

        var result = FutureDirectSimulator.SimulateDay(rows, Options());

        Assert.Empty(result.Trades);
    }

    [Fact]
    public void EntryAllowed_AtOrAfterNineThirty_WhenSignalQualifies()
    {
        // The two pre-09:30 rows build up real history (rank cutoffs are meaningless against a
        // single self-qualifying point -- SessionRankTracker's own doc comment already accepts
        // this as noisy-early-session behavior), so by 09:30 the 5000 signal is genuinely being
        // judged against a real distribution, not entering merely because it's the first value.
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 15), 100m, 100),
            Row(new TimeOnly(9, 20), 100m, 100),
            Row(new TimeOnly(9, 30), 100m, 5000), // strong, at-or-after 09:30 -> should enter long
            Row(new TimeOnly(9, 31), 101m, 4000), // decays but not below the exit threshold yet
            Row(new TimeOnly(15, 15), 102m, 4000), // force-close
        };

        var result = FutureDirectSimulator.SimulateDay(rows, Options());

        Assert.Single(result.Trades);
        Assert.Equal(1, result.Trades[0].Direction); // positive signal -> long
        Assert.Equal("ForceClose", result.Trades[0].ExitReason);
    }

    [Fact]
    public void NoNewEntry_AtOrAfterThreePm_EvenWithAQualifyingSignal()
    {
        // Background signal is exactly 0 (Math.Sign(0) == 0, blocked by the simulator's own
        // "sign must be nonzero" guard) so these rows can never themselves trigger an entry --
        // isolating the assertion to the time gate alone, not an accidental rank-threshold effect
        // from a single early data point trivially satisfying its own percentile.
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, 0),
            Row(new TimeOnly(10, 0), 100m, 0),
            Row(new TimeOnly(14, 59), 100m, 0),
            Row(new TimeOnly(15, 0), 100m, 9000), // exactly 3:00 PM -- no new entry allowed
            Row(new TimeOnly(15, 15), 100m, 9000),
        };

        var result = FutureDirectSimulator.SimulateDay(rows, Options());

        Assert.Empty(result.Trades);
    }

    [Fact]
    public void OpenPosition_IsForceClosedAtQuarterPastThree_RegardlessOfSignalState()
    {
        var rows = new List<CadenceContext>
        {
            // Enters right here at 09:30: the first signal ever seen trivially sits at its own
            // percentile against an otherwise-empty history (SessionRankTracker's own accepted
            // "noisy early session" behavior) -- irrelevant to what this test checks, which is
            // what happens to the resulting open position at the force-close boundary.
            Row(new TimeOnly(9, 30), 100m, 100),
            Row(new TimeOnly(9, 31), 100m, 5000),
            Row(new TimeOnly(15, 14), 110m, 5000), // signal still strong, position stays open
            Row(new TimeOnly(15, 15), 115m, 5000), // forced closed here regardless
        };

        var result = FutureDirectSimulator.SimulateDay(rows, Options());

        Assert.Single(result.Trades);
        Assert.Equal("ForceClose", result.Trades[0].ExitReason);
        Assert.Equal(115m, result.Trades[0].ExitPrice);
    }

    [Fact]
    public void SignFlip_ClosesAnOpenPosition_BeforeForceClose()
    {
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, 100), // enters long right here -- see the other test's comment on why
            Row(new TimeOnly(9, 31), 100m, 5000),
            Row(new TimeOnly(9, 32), 105m, -6000), // sign flips hard negative -> exit here
            Row(new TimeOnly(15, 15), 90m, -6000),
        };

        var result = FutureDirectSimulator.SimulateDay(rows, Options());

        Assert.Single(result.Trades);
        Assert.Equal("SignFlip", result.Trades[0].ExitReason);
        Assert.Equal(105m, result.Trades[0].ExitPrice);
    }

    [Fact]
    public void NullSignalCadence_IsSkippedForEntryExit_ButForceCloseStillApplies()
    {
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, 100), // enters long right here -- see the earlier test's comment on why
            Row(new TimeOnly(9, 31), 100m, 5000),
            Row(new TimeOnly(9, 32), 101m, null), // signal missing this cadence -- no decision made
            Row(new TimeOnly(15, 15), 108m, null), // force-close still fires even without a signal
        };

        var result = FutureDirectSimulator.SimulateDay(rows, Options());

        Assert.Single(result.Trades);
        Assert.Equal("ForceClose", result.Trades[0].ExitReason);
        Assert.Equal(108m, result.Trades[0].ExitPrice);
    }

    [Fact]
    public void DefaultFastExit_DecaysQuickly_WhenTheFiveMinuteSignalDropsRightAfterEntry()
    {
        // Same data the slow-exit test below uses -- this establishes the "before" behavior
        // that motivated adding the slow-exit option in the first place: the first live run's
        // trades all lasted 1-8 minutes because entry and exit both read the fast, twitchy
        // 5-minute signal.
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, cvd5min: 100, cvd15min: 100), // enters long here
            Row(new TimeOnly(9, 31), 101m, cvd5min: 1, cvd15min: 5000), // 5-min signal collapses
            Row(new TimeOnly(15, 15), 102m, cvd5min: 1, cvd15min: 5000),
        };

        var result = FutureDirectSimulator.SimulateDay(rows, Options() with { UseSlowExitSignal = false });

        Assert.Single(result.Trades);
        Assert.Equal("SignalDecay", result.Trades[0].ExitReason);
        Assert.Equal(101m, result.Trades[0].ExitPrice); // exited at 09:31, not held to force-close
    }

    [Fact]
    public void SlowExitSignal_IgnoresTheFastSignalCollapsing_AndHoldsOnTheFifteenMinuteNetInstead()
    {
        // Identical data to the test above, only UseSlowExitSignal flipped -- the 5-min signal
        // collapsing at 09:31 must NOT trigger an exit here, because exit decisions now read
        // FutureCvdProxyNet15Min (still strong at 5000), not FutureCvdProxyNet5Min.
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, cvd5min: 100, cvd15min: 100), // enters long here
            Row(new TimeOnly(9, 31), 101m, cvd5min: 1, cvd15min: 5000), // 5-min collapses, 15-min stays strong
            Row(new TimeOnly(15, 15), 102m, cvd5min: 1, cvd15min: 5000), // force-close
        };

        var result = FutureDirectSimulator.SimulateDay(rows, Options() with { UseSlowExitSignal = true });

        Assert.Single(result.Trades);
        Assert.Equal("ForceClose", result.Trades[0].ExitReason);
        Assert.Equal(102m, result.Trades[0].ExitPrice); // held all the way through, unlike fast-exit above
    }

    [Fact]
    public void SlowExitSignal_StillEntersOnTheFastFiveMinuteSignal_EntryTimingIsUnaffected()
    {
        // Entry always reads Net5Min regardless of UseSlowExitSignal -- only exit wiring changes.
        // A weak/absent 15-min signal at entry time must not block an otherwise-qualifying entry.
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, cvd5min: 5000, cvd15min: null), // 15-min not even warmed up yet
            Row(new TimeOnly(9, 31), 101m, cvd5min: 5000, cvd15min: 5000),
            Row(new TimeOnly(15, 15), 102m, cvd5min: 5000, cvd15min: 5000),
        };

        var result = FutureDirectSimulator.SimulateDay(rows, Options() with { UseSlowExitSignal = true });

        Assert.Single(result.Trades);
        Assert.Equal(new DateTimeOffset(Day.ToDateTime(new TimeOnly(9, 30)), Ist), result.Trades[0].EntryTime);
    }

    [Fact]
    public void NetPnlPoints_IsSignedByDirection()
    {
        var trade = new SimulatedTrade(
            EntryTime: default, EntryPrice: 100m, Direction: -1,
            ExitTime: default, ExitPrice: 90m, ExitReason: "Test");

        // Short, price fell 10 -> a 10-point gain, not a loss.
        Assert.Equal(10m, trade.NetPnlPoints);
    }
}

public sealed class EmaSmootherTests
{
    [Fact]
    public void FirstValue_IsReturnedUnchanged_NoPriorHistoryToBlendWith()
    {
        var ema = new EmaSmoother(spanCadences: 3.0);
        Assert.Equal(10.0, ema.Observe(10.0));
    }

    [Fact]
    public void SubsequentValues_BlendWithThePreviousEmaByAlpha()
    {
        // span=3 -> alpha = 2/(3+1) = 0.5
        var ema = new EmaSmoother(spanCadences: 3.0);
        ema.Observe(10.0);
        Assert.Equal(15.0, ema.Observe(20.0)); // 0.5*20 + 0.5*10
        Assert.Equal(22.5, ema.Observe(30.0)); // 0.5*30 + 0.5*15
    }

    [Fact]
    public void SpanOfOne_ProducesNoSmoothingAtAll_AlphaIsExactlyOne()
    {
        // alpha = 2/(1+1) = 1.0 -> every observation fully replaces the previous one.
        var ema = new EmaSmoother(spanCadences: 1.0);
        ema.Observe(10.0);
        Assert.Equal(-500.0, ema.Observe(-500.0));
    }
}

public sealed class AlwaysPositionedSimulatorTests
{
    static readonly DateOnly Day = new(2026, 9, 10);
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    static CadenceContext Row(TimeOnly time, decimal? price, long? cvd15min) => new()
    {
        Timestamp = new DateTimeOffset(Day.ToDateTime(time), Ist),
        AsOfDate = Day,
        NearestExpiryDate = new DateOnly(2026, 9, 15),
        FutureCloseFromLastCadence = price,
        FutureCvdProxyNet15Min = cvd15min,
    };

    // span=1 -> alpha=1.0 -> the EMA equals the raw signal every time, isolating these tests to
    // the entry/flip/force-close logic rather than also reasoning about smoothing lag.
    static AlwaysPositionedOptions Options() => new(UseSlowSignal: true, EmaSpanCadences: 1.0);

    [Fact]
    public void NoPositionOpens_BeforeNineThirty_EvenWithAStrongSignal()
    {
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 15), 100m, 5000),
            Row(new TimeOnly(9, 29), 100m, 5000),
            Row(new TimeOnly(15, 15), 100m, 5000),
        };

        var result = FutureDirectSimulator.SimulateDayAlwaysPositioned(rows, Options());

        Assert.Empty(result.Trades);
    }

    [Fact]
    public void FirstPosition_OpensAtNineThirty_InTheDirectionOfTheSignal()
    {
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, 5000), // positive -> long
            Row(new TimeOnly(15, 15), 105m, 5000),
        };

        var result = FutureDirectSimulator.SimulateDayAlwaysPositioned(rows, Options());

        Assert.Single(result.Trades);
        Assert.Equal(1, result.Trades[0].Direction);
        Assert.Equal(new DateTimeOffset(Day.ToDateTime(new TimeOnly(9, 30)), Ist), result.Trades[0].EntryTime);
    }

    [Fact]
    public void DirectionFlip_ClosesTheOldLeg_AndOpensTheNewOne_AtTheSameTimeAndPrice()
    {
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, 5000), // long
            Row(new TimeOnly(9, 45), 110m, -5000), // flips short right here
            Row(new TimeOnly(15, 15), 105m, -5000), // force-close the short leg
        };

        var result = FutureDirectSimulator.SimulateDayAlwaysPositioned(rows, Options());

        Assert.Equal(2, result.Trades.Count);

        var firstLeg = result.Trades[0];
        Assert.Equal(1, firstLeg.Direction);
        Assert.Equal("Flip", firstLeg.ExitReason);
        Assert.Equal(110m, firstLeg.ExitPrice);

        var secondLeg = result.Trades[1];
        Assert.Equal(-1, secondLeg.Direction);
        Assert.Equal(110m, secondLeg.EntryPrice); // continuous -- opens exactly where the old leg closed
        Assert.Equal(firstLeg.ExitTime, secondLeg.EntryTime);
        Assert.Equal("ForceClose", secondLeg.ExitReason);
    }

    [Fact]
    public void Flip_IsAllowed_EvenAfterThreePm_ItIsNotANewEntryFromFlat()
    {
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, 5000), // long
            Row(new TimeOnly(15, 5), 110m, -5000), // after 15:00 -- still allowed, it's a flip not a fresh entry
            Row(new TimeOnly(15, 15), 108m, -5000),
        };

        var result = FutureDirectSimulator.SimulateDayAlwaysPositioned(rows, Options());

        Assert.Equal(2, result.Trades.Count);
        Assert.Equal("Flip", result.Trades[0].ExitReason);
    }

    [Fact]
    public void ForceClose_FiresAtQuarterPastThree_RegardlessOfSignalState()
    {
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, 5000),
            Row(new TimeOnly(15, 14), 120m, 5000), // still strongly positive, no flip due
            Row(new TimeOnly(15, 15), 122m, 5000),
        };

        var result = FutureDirectSimulator.SimulateDayAlwaysPositioned(rows, Options());

        Assert.Single(result.Trades);
        Assert.Equal("ForceClose", result.Trades[0].ExitReason);
        Assert.Equal(122m, result.Trades[0].ExitPrice);
    }

    [Fact]
    public void NullSignalCadence_HoldsTheCurrentDirection_NoTradeMade()
    {
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, 5000), // long
            Row(new TimeOnly(9, 45), 110m, null), // no reading this cadence -- keep holding long
            Row(new TimeOnly(15, 15), 115m, null), // force-close still fires without a signal
        };

        var result = FutureDirectSimulator.SimulateDayAlwaysPositioned(rows, Options());

        Assert.Single(result.Trades);
        Assert.Equal("ForceClose", result.Trades[0].ExitReason);
        Assert.Equal(115m, result.Trades[0].ExitPrice);
    }

    [Fact]
    public void TimeInMarket_SpansTheWholeSession_WhenNeverFlat()
    {
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30), 100m, 5000),
            Row(new TimeOnly(12, 0), 105m, 5000),
            Row(new TimeOnly(15, 15), 108m, 5000),
        };

        var result = FutureDirectSimulator.SimulateDayAlwaysPositioned(rows, Options());

        // 09:30 to 15:15 is the entire session window -- a position held continuously across it
        // should show essentially full coverage, the whole point of this mode.
        Assert.Equal(TimeSpan.FromHours(5.75), result.TimeInMarket);
    }

    [Fact]
    public void TrendEfficiencyGate_BlocksAFlip_DuringAChoppyMoment_ThenAllowsItOnceATrendResumes()
    {
        // 30s trend-efficiency window so only a couple of points are ever "in view" -- keeps the
        // hand-computed efficiency values small enough to verify by hand.
        //   09:30:00 100 -> 09:30:15 110: a clean 10-point move, efficiency 1.0 (first ever
        //     reading, ranks 100th percentile of itself) -- opens long here.
        //   09:30:30 100: straight back to where it started. Net change over the window's two
        //     surviving points (100@:00, 110@:15, 100@:30) is 0, path length is 20 -> efficiency
        //     EXACTLY 0. Ranked against [1.0, 0], this reading sits at the 50th percentile --
        //     below the 60 cutoff used here -- so the flip signal (now negative) is blocked and
        //     the long position keeps holding.
        //   09:30:45 130: the 09:30:00 point has aged out of the 30s window by now, leaving
        //     (110@:15, 100@:30, 130@:45) -- net change 20, path length 40, efficiency 0.5, which
        //     ranks 66.7th percentile against [1.0, 0, 0.5] -- clears the 60 cutoff, so the same
        //     negative signal now flips the position.
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30, 0), 100m, 5000),
            Row(new TimeOnly(9, 30, 15), 110m, 5000), // opens long
            Row(new TimeOnly(9, 30, 30), 100m, -5000), // flip signal present, but chop blocks it
            Row(new TimeOnly(9, 30, 45), 130m, -5000), // trend resumes -- flip goes through here
            Row(new TimeOnly(15, 15, 0), 125m, -5000), // force-close
        };
        var options = Options() with
        {
            UseTrendEfficiencyGate = true,
            TrendEfficiencyGateRankCutoff = 60,
            TrendEfficiencyWindow = TimeSpan.FromSeconds(30),
        };

        var result = FutureDirectSimulator.SimulateDayAlwaysPositioned(rows, options);

        Assert.Equal(2, result.Trades.Count);
        var firstLeg = result.Trades[0];
        Assert.Equal(1, firstLeg.Direction);
        Assert.Equal("Flip", firstLeg.ExitReason);
        // The blocked attempt at 09:30:30 must NOT show up as the exit -- it only actually flips
        // once the gate reopens at 09:30:45.
        Assert.Equal(new DateTimeOffset(Day.ToDateTime(new TimeOnly(9, 30, 45)), Ist), firstLeg.ExitTime);
        Assert.Equal(130m, firstLeg.ExitPrice);

        var secondLeg = result.Trades[1];
        Assert.Equal(-1, secondLeg.Direction);
        Assert.Equal("ForceClose", secondLeg.ExitReason);
    }

    [Fact]
    public void WithoutTheGate_TheSameChopDoesNotDelayTheFlip()
    {
        // Identical data and cutoff to the test above, gate simply turned off -- the flip fires
        // immediately at 09:30:30 instead of waiting for trend efficiency to clear 60 at 09:30:45.
        var rows = new List<CadenceContext>
        {
            Row(new TimeOnly(9, 30, 0), 100m, 5000),
            Row(new TimeOnly(9, 30, 15), 110m, 5000),
            Row(new TimeOnly(9, 30, 30), 100m, -5000),
            Row(new TimeOnly(9, 30, 45), 130m, -5000),
            Row(new TimeOnly(15, 15, 0), 125m, -5000),
        };
        var options = Options() with { UseTrendEfficiencyGate = false };

        var result = FutureDirectSimulator.SimulateDayAlwaysPositioned(rows, options);

        Assert.Equal(2, result.Trades.Count);
        Assert.Equal(new DateTimeOffset(Day.ToDateTime(new TimeOnly(9, 30, 30)), Ist), result.Trades[0].ExitTime);
        Assert.Equal(100m, result.Trades[0].ExitPrice);
    }
}
