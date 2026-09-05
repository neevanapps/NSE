using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public class WelfordRollingWindowTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 3, 9, 15, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void MeanAndStdDev_MatchNaiveComputation_OnTheSameData()
    {
        // Plan section 13's explicit test: Welford vs naive computation on the same data.
        var random = new Random(42);
        var values = Enumerable.Range(0, 200).Select(_ => random.NextDouble() * 100).ToList();

        var window = new WelfordRollingWindow(TimeSpan.FromHours(1));
        for (var i = 0; i < values.Count; i++)
        {
            // All within the window (well under an hour apart) -- nothing evicts.
            window.Add(Start.AddSeconds(i), values[i]);
        }

        var naiveMean = values.Average();
        var naiveVariance = values.Sum(v => (v - naiveMean) * (v - naiveMean)) / (values.Count - 1);
        var naiveStdDev = Math.Sqrt(naiveVariance);

        Assert.Equal(naiveMean, window.Mean, 1e-9);
        Assert.Equal(naiveStdDev, window.StdDev, 1e-9);
    }

    [Fact]
    public void MeanAndStdDev_MatchNaiveComputation_AfterPointsHaveAgedOutOfTheWindow()
    {
        // Same check, but this time eviction has actually happened -- the incremental
        // "remove" formula needs to agree with recomputing from scratch, not just the
        // "add" formula in isolation.
        var random = new Random(7);
        var windowDuration = TimeSpan.FromMinutes(10);
        var window = new WelfordRollingWindow(windowDuration);

        var allPoints = new List<(DateTimeOffset Timestamp, double Value)>();
        for (var i = 0; i < 100; i++)
        {
            var timestamp = Start.AddSeconds(i * 15); // spans 25 minutes total, window is 10
            var value = random.NextDouble() * 50;
            allPoints.Add((timestamp, value));
            window.Add(timestamp, value);
        }

        var latest = allPoints[^1].Timestamp;
        var stillInWindow = allPoints.Where(p => p.Timestamp >= latest - windowDuration).Select(p => p.Value).ToList();
        var naiveMean = stillInWindow.Average();
        var naiveStdDev = Math.Sqrt(stillInWindow.Sum(v => (v - naiveMean) * (v - naiveMean)) / (stillInWindow.Count - 1));

        Assert.Equal(stillInWindow.Count, window.Count);
        Assert.Equal(naiveMean, window.Mean, 1e-9);
        Assert.Equal(naiveStdDev, window.StdDev, 1e-9);
    }

    [Fact]
    public void Add_EvictsOnlyPointsOlderThanTheWindow_NotEverythingBeforeTheLatestAdd()
    {
        var window = new WelfordRollingWindow(TimeSpan.FromMinutes(5));

        window.Add(Start, 10);                              // t=0
        window.Add(Start.AddMinutes(3), 20);                 // t=3min
        window.Add(Start.AddMinutes(6), 30);                 // t=6min -- cutoff is t=1min, evicts only t=0

        Assert.Equal(2, window.Count);
        Assert.Equal(25.0, window.Mean, 1e-9); // average of 20 and 30, not 10
    }

    [Fact]
    public void IsWarmedUp_IsFalse_BeforeTheFullWindowDurationHasElapsed()
    {
        var window = new WelfordRollingWindow(TimeSpan.FromMinutes(30));

        window.Add(Start, 100);
        window.Add(Start.AddMinutes(10), 105);

        Assert.False(window.IsWarmedUp);
    }

    [Fact]
    public void IsWarmedUp_IsTrue_OnceTheFullWindowDurationHasElapsed()
    {
        var window = new WelfordRollingWindow(TimeSpan.FromMinutes(30));

        window.Add(Start, 100);
        window.Add(Start.AddMinutes(15), 105);
        window.Add(Start.AddMinutes(30), 110);

        Assert.True(window.IsWarmedUp);
    }

    [Fact]
    public void IsWarmedUp_IsFalse_WithOnlyASinglePoint_EvenAcrossALongTimeSpan()
    {
        // A single stale point re-timestamped an hour later shouldn't count as "warmed up"
        // -- there's no meaningful spread with one observation regardless of elapsed time.
        var window = new WelfordRollingWindow(TimeSpan.FromMinutes(5));

        window.Add(Start, 100);

        Assert.False(window.IsWarmedUp);
    }

    [Fact]
    public void ComputeZScore_ReturnsNull_BeforeWarmUp()
    {
        var window = new WelfordRollingWindow(TimeSpan.FromMinutes(30));
        window.Add(Start, 100);

        Assert.Null(window.ComputeZScore(105));
    }

    [Fact]
    public void ComputeZScore_ClipsToPositiveThree_ForAnExtremeOutlier()
    {
        var window = new WelfordRollingWindow(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 10; i++)
        {
            window.Add(Start.AddSeconds(i * 10), 100 + (i % 2)); // tight cluster around 100-101
        }

        var z = window.ComputeZScore(100_000); // wildly outside the observed range

        Assert.Equal(3.0, z);
    }

    [Fact]
    public void ComputeZScore_ClipsToNegativeThree_ForAnExtremeOutlier()
    {
        var window = new WelfordRollingWindow(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 10; i++)
        {
            window.Add(Start.AddSeconds(i * 10), 100 + (i % 2));
        }

        var z = window.ComputeZScore(-100_000);

        Assert.Equal(-3.0, z);
    }

    [Fact]
    public void ComputeZScore_ReturnsZero_ForAValueExactlyAtTheMean()
    {
        // 8 points spaced 10s apart span exactly 70s -- with a 70s window, the very last
        // Add's cutoff (70s - 70s = 0s) evicts nothing (t=0 isn't < 0), so all 8 survive
        // and the 90/110 alternation stays perfectly balanced (mean exactly 100). Elapsed
        // time (70s) also exactly meets the window, so this is warmed up too.
        var window = new WelfordRollingWindow(TimeSpan.FromSeconds(70));
        for (var i = 0; i < 8; i++)
        {
            window.Add(Start.AddSeconds(i * 10), i % 2 == 0 ? 90 : 110);
        }

        var z = window.ComputeZScore(100);

        Assert.NotNull(z);
        Assert.Equal(0.0, z.Value, 1e-9);
    }

    [Fact]
    public void ComputeZScore_ReturnsNull_WhenTheWindowHasNoSpread()
    {
        // Every value identical -- StdDev is 0, and dividing by it would be nonsense.
        var window = new WelfordRollingWindow(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 10; i++)
        {
            window.Add(Start.AddSeconds(i * 5), 50);
        }

        Assert.Null(window.ComputeZScore(60));
    }

    [Fact]
    public void StdDev_NeverGoesNaN_AcrossManyAddAndEvictCycles()
    {
        // Live-caught 2026-09-04: floating-point cancellation in Remove's incremental M2
        // update can drift it slightly negative after enough add/evict churn, and
        // Math.Sqrt(negative) is NaN -- which then silently passed the old `StdDev < 1e-12`
        // guard (any comparison against NaN is false in .NET), reached CompositeScore, and
        // crashed the dashboard's JSON serialization. A tight cluster of near-identical
        // values keeps the true variance close to the precision floor, which is exactly
        // where cancellation error is most likely to tip it negative.
        var window = new WelfordRollingWindow(TimeSpan.FromSeconds(30));
        var random = new Random(2026_09_04);

        for (var i = 0; i < 5000; i++)
        {
            // Every point outlives the 30s window within a few Adds, so this churns
            // through continuous evict-then-add cycles for the whole run.
            var value = 100.0 + (random.NextDouble() - 0.5) * 1e-6;
            window.Add(Start.AddSeconds(i), value);

            Assert.False(double.IsNaN(window.StdDev), $"StdDev went NaN at iteration {i}");
        }

        // The guard itself: a NaN StdDev must not silently slip past ComputeZScore either.
        Assert.False(double.IsNaN(window.ComputeZScore(100.0) ?? 0.0));
    }
}
