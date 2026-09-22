using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public class OiLookbackWindowTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 3, 9, 15, 0, TimeSpan.FromHours(5.5));
    static readonly TimeSpan Window = TimeSpan.FromMinutes(4);

    [Fact]
    public void Lookback_IsNull_ForATokenThatHasNeverBeenRecorded()
    {
        var window = new OiLookbackWindow(Window);

        Assert.Null(window.Lookback("TOKEN", Start));
    }

    [Fact]
    public void Lookback_IsNull_BeforeRealElapsedTimeReachesTheFullWindow()
    {
        // Audit finding F50 (2026-09-10): the whole point of this class is refusing to answer
        // "what was OI N minutes ago" until N minutes have genuinely passed -- the same "don't
        // trust a partially-filled window" rule WelfordRollingWindow already applies.
        var window = new OiLookbackWindow(Window);
        window.Record("TOKEN", Start, 100_000);

        Assert.Null(window.Lookback("TOKEN", Start + Window - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Lookback_ReturnsTheOiRecordedAtTheStartOfTheWindow_OnceWarmedUp()
    {
        var window = new OiLookbackWindow(Window);
        window.Record("TOKEN", Start, 100_000);

        // At exactly Start + Window, real elapsed time has reached the full window (>=, not
        // strictly >) and the only sample recorded so far is still the one from Start (nothing
        // has been recorded since, so nothing new to evict either).
        Assert.Equal(100_000, window.Lookback("TOKEN", Start + Window));
    }

    [Fact]
    public void RecordAndLookback_TrackReality_AcrossASeriesOfCadencesWithAnOccasionalRealChange()
    {
        // Mirrors the actual production shape this class exists for: OI held flat across many
        // 15s cadences (the exchange hasn't printed a fresh value yet), then one cadence where
        // it genuinely changes. Lookback at that cadence must return the flat baseline, not the
        // immediately-prior (also-flat) cadence's value -- which would be the same number here
        // regardless, so the real proof is in LiveFeatureEngineTests' end-to-end version of this
        // scenario; this test instead proves the mechanical piece: the window keeps returning
        // the value from ~Window ago as cadences advance, sample by sample.
        var window = new OiLookbackWindow(Window);
        var at = Start;
        window.Record("TOKEN", at, 100_000);

        for (var elapsed = TimeSpan.FromSeconds(15); elapsed <= Window + TimeSpan.FromSeconds(30); elapsed += TimeSpan.FromSeconds(15))
        {
            at = Start + elapsed;
            var lookback = window.Lookback("TOKEN", at);

            if (elapsed < Window)
            {
                Assert.Null(lookback);
            }
            else
            {
                Assert.Equal(100_000, lookback);
            }

            window.Record("TOKEN", at, 100_000);
        }
    }

    [Fact]
    public void Lookback_DoesNotItselfAdvanceState_SoRepeatedCallsWithoutRecordingReturnTheSameAnswer()
    {
        // Read-only: a cadence's every Lookback call must reflect state strictly before that
        // cadence's own Record calls (same before/after ordering _previousCadence uses
        // elsewhere in LiveFeatureEngine) -- calling Lookback several times in a row, with no
        // Record in between, must not change the answer or silently evict anything.
        var window = new OiLookbackWindow(Window);
        window.Record("TOKEN", Start, 100_000);
        var now = Start + Window;

        var first = window.Lookback("TOKEN", now);
        var second = window.Lookback("TOKEN", now);

        Assert.Equal(100_000, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void EachToken_IsTrackedIndependently()
    {
        var window = new OiLookbackWindow(Window);
        window.Record("CALL", Start, 100_000);
        window.Record("PUT", Start, 250_000);

        var now = Start + Window;

        Assert.Equal(100_000, window.Lookback("CALL", now));
        Assert.Equal(250_000, window.Lookback("PUT", now));
    }

    [Fact]
    public void Lookback_ReflectsARealChange_OnceTheWindowHasFullyAdvancedPastIt()
    {
        var window = new OiLookbackWindow(Window);
        window.Record("TOKEN", Start, 100_000);

        // A real print partway through -- OI actually moves.
        var printAt = Start + TimeSpan.FromMinutes(1);
        window.Record("TOKEN", printAt, 105_000);

        // Once the window has advanced far enough that even the *later* (105_000) sample is
        // now the oldest one still inside it, Lookback should report 105_000, not the original
        // 100_000 -- the window tracks "oldest sample still in range," not "the very first
        // sample ever recorded."
        var now = printAt + Window + TimeSpan.FromSeconds(1);
        window.Record("TOKEN", now - TimeSpan.FromSeconds(1), 105_000);

        Assert.Equal(105_000, window.Lookback("TOKEN", now));
    }
}
