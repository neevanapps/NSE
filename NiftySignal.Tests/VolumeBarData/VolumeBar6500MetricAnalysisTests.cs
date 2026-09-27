using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class VolumeBar6500MetricAnalysisTests
{
    [Fact]
    public void Spearman_IsTieAwareAndPerfectForIdenticalRankStructure()
    {
        var rho = VolumeBar6500MetricAnalysis.Spearman(
            new[] { 1.0, 2.0, 2.0, 4.0, 5.0 },
            new[] { 10.0, 20.0, 20.0, 40.0, 50.0 });

        Assert.NotNull(rho);
        Assert.Equal(1.0, rho!.Value, 12);
    }

    [Fact]
    public void Spearman_ReturnsNullForConstantSeries()
    {
        var rho = VolumeBar6500MetricAnalysis.Spearman(
            new[] { 1.0, 1.0, 1.0, 1.0 },
            new[] { 1.0, 2.0, 3.0, 4.0 });

        Assert.Null(rho);
    }

    [Fact]
    public void Analyze_ProducesFourMetricsThreeHorizonsAndTwoPredeclaredScopes()
    {
        var observations = SyntheticObservations();

        var result = VolumeBar6500MetricAnalysis.Analyze(observations);

        Assert.Equal(4 * 3 * 2, result.Summary.Count);

        foreach (var metric in VolumeBar6500MetricAnalysis.Metrics)
        {
            foreach (var horizon in VolumeBar6500MetricAnalysis.Horizons)
            {
                Assert.Contains(result.Summary, r =>
                    r.Metric == metric.Name &&
                    r.HorizonBars == horizon.Bars &&
                    r.Scope == "AllValid");

                Assert.Contains(result.Summary, r =>
                    r.Metric == metric.Name &&
                    r.HorizonBars == horizon.Bars &&
                    r.Scope == "SignalBarVolumeLt13000");
            }
        }
    }

    [Fact]
    public void Analyze_ExtremeVolumeSensitivityRemovesOnlySignalBarsAtOrAbove13000()
    {
        var observations = SyntheticObservations();

        var result = VolumeBar6500MetricAnalysis.Analyze(observations);

        var primary = result.Summary.Single(r =>
            r.Metric == "DepthImbalance" &&
            r.HorizonBars == 1 &&
            r.Scope == "AllValid");

        var sensitivity = result.Summary.Single(r =>
            r.Metric == "DepthImbalance" &&
            r.HorizonBars == 1 &&
            r.Scope == "SignalBarVolumeLt13000");

        Assert.Equal(observations.Count, primary.N);
        Assert.Equal(observations.Count(o => o.ObservedVolume < 13_000), sensitivity.N);
        Assert.True(sensitivity.N < primary.N);
    }

    [Fact]
    public void Analyze_LeaveOneSessionOutUsesSessionAsTheRemovalUnit()
    {
        var observations = SyntheticObservations();

        var result = VolumeBar6500MetricAnalysis.Analyze(observations);

        var rows = result.LeaveOneSessionOut
            .Where(r => r.Metric == "DepthImbalance" && r.HorizonBars == 1)
            .OrderBy(r => r.ExcludedDate)
            .ToArray();

        Assert.Equal(3, rows.Length);
        Assert.Equal(
            observations.Select(o => o.TradingDate).Distinct().OrderBy(x => x),
            rows.Select(r => r.ExcludedDate));

        foreach (var row in rows)
        {
            Assert.Equal(
                observations.Count(o => o.TradingDate != row.ExcludedDate),
                row.N);
        }
    }

    [Fact]
    public void Analyze_ReportsMetricRelationshipToSignalBarMoveSeparatelyFromForwardResponse()
    {
        var observations = SyntheticObservations();

        var result = VolumeBar6500MetricAnalysis.Analyze(observations);

        var row = result.Summary.Single(r =>
            r.Metric == "BarDurationUrgency" &&
            r.HorizonBars == 1 &&
            r.Scope == "AllValid");

        Assert.NotNull(row.PooledSpearman);
        Assert.NotNull(row.MetricVsSignalBarChangeSpearman);
        Assert.NotNull(row.SignalBarChangeVsForwardSpearman);
    }

    static List<VolumeBar6500Revalidation.Observation> SyntheticObservations()
    {
        var rows = new List<VolumeBar6500Revalidation.Observation>();
        var dates = new[]
        {
            new DateOnly(2026, 9, 8),
            new DateOnly(2026, 9, 9),
            new DateOnly(2026, 9, 10),
        };

        var index = 0;
        foreach (var date in dates)
        {
            for (var i = 0; i < 10; i++)
            {
                index++;
                var x = index - 15.5;
                var signalChange = i % 2 == 0 ? x / 4.0 : -x / 5.0;
                var future1 = (x * 0.7) + ((i % 3) - 1);
                var future2 = (x * 0.5) - ((i % 4) - 1.5);
                var future4 = (x * 0.3) + ((i % 5) - 2);
                var observedVolume = i == 0 ? 13_000L : 6_500L + (i * 100L);
                var open = 25_000m + index;
                var close = open + (decimal)signalChange;
                var high = Math.Max(open, close) + 2m;
                var low = Math.Min(open, close) - 2m;

                rows.Add(new VolumeBar6500Revalidation.Observation(
                    date,
                    i % 3,
                    i,
                    new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)).AddSeconds(i), TimeSpan.FromHours(5.5)),
                    new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)).AddSeconds(i).AddMilliseconds(200), TimeSpan.FromHours(5.5)),
                    close,
                    open,
                    high,
                    low,
                    signalChange,
                    signalChange / (double)open,
                    (double)(high - low),
                    observedVolume,
                    observedVolume - 6_500,
                    20 + i,
                    20 + i,
                    10 + i,
                    10.2 + i,
                    x / 100.0,
                    x / 120.0,
                    x / 600.0,
                    x * 10.0,
                    (long)(x * 100.0),
                    signalChange == 0 ? null : Math.Sign(signalChange) / (10.0 + i),
                    future1,
                    future2,
                    future4,
                    observedVolume + 100,
                    (observedVolume + 100) * 2,
                    (observedVolume + 100) * 4,
                    Math.Max(0, future1 + 2),
                    Math.Max(0, future2 + 3),
                    Math.Max(0, future4 + 4),
                    Math.Max(0, -future1 + 2),
                    Math.Max(0, -future2 + 3),
                    Math.Max(0, -future4 + 4)));
            }
        }

        return rows;
    }
}
