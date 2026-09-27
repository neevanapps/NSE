using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class VolumeBar6500CvdControlAnalysisTests
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    [Fact]
    public void PartialSpearman_WithNoControls_MatchesTieAwareSpearman()
    {
        double[] x = [1, 1, 2, 3, 5, 8, 13, 13, 21];
        double[] y = [2, 1, 3, 4, 6, 7, 9, 8, 10];

        var expected = VolumeBar6500MetricAnalysis.Spearman(x, y);
        var actual = VolumeBar6500CvdControlAnalysis.PartialSpearman(x, y);

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Value, actual!.Value, 12);
    }

    [Fact]
    public void PartialSpearman_PerfectConfound_RemovesAllResidualVariation()
    {
        double[] z = [1, 2, 3, 4, 5, 6, 7, 8];
        double[] x = [1, 2, 3, 4, 5, 6, 7, 8];
        double[] y = [1, 2, 3, 4, 5, 6, 7, 8];

        var raw = VolumeBar6500CvdControlAnalysis.PartialSpearman(x, y);
        var controlled = VolumeBar6500CvdControlAnalysis.PartialSpearman(x, y, z);

        Assert.Equal(1.0, raw!.Value, 12);
        Assert.Null(controlled);
    }

    [Fact]
    public void PartialSpearman_IdenticalSignalsRemainPerfectAfterUnrelatedControl()
    {
        double[] x = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        double[] y = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        double[] z = [1, 3, 2, 5, 4, 7, 6, 9, 8, 10];

        var controlled = VolumeBar6500CvdControlAnalysis.PartialSpearman(x, y, z);

        Assert.NotNull(controlled);
        Assert.Equal(1.0, controlled!.Value, 12);
    }

    [Fact]
    public void Analyze_ProducesAllFrozenStagesHorizonsAndScopes()
    {
        var result = VolumeBar6500CvdControlAnalysis.Analyze(SyntheticObservations());

        Assert.Equal(
            VolumeBar6500CvdControlAnalysis.Stages.Length *
            VolumeBar6500CvdControlAnalysis.Horizons.Length *
            2,
            result.Summary.Count);

        foreach (var horizon in VolumeBar6500CvdControlAnalysis.Horizons)
        {
            foreach (var stage in VolumeBar6500CvdControlAnalysis.Stages)
            {
                Assert.Contains(result.Summary, r =>
                    r.HorizonBars == horizon.Bars &&
                    r.Scope == "AllValid" &&
                    r.Stage == stage.Name);

                Assert.Contains(result.Summary, r =>
                    r.HorizonBars == horizon.Bars &&
                    r.Scope == "SignalBarVolumeLt13000" &&
                    r.Stage == stage.Name);
            }
        }
    }

    [Fact]
    public void Analyze_CompleteCaseStagesUseIdenticalSamples()
    {
        var result = VolumeBar6500CvdControlAnalysis.Analyze(SyntheticObservations());

        foreach (var horizon in VolumeBar6500CvdControlAnalysis.Horizons)
        {
            var rows = result.Summary
                .Where(r =>
                    r.HorizonBars == horizon.Bars &&
                    r.Scope == "AllValid")
                .ToArray();

            var benchmark = rows.Single(r => r.Stage == "BenchmarkAllCvd");
            var completeRows = rows
                .Where(r => r.Stage != "BenchmarkAllCvd")
                .ToArray();

            Assert.All(completeRows, r =>
                Assert.Equal(completeRows[0].N, r.N));

            Assert.True(benchmark.N >= completeRows[0].N);
        }
    }

    [Fact]
    public void Analyze_ReusesTenDirectionMagnitudeCells()
    {
        var result = VolumeBar6500CvdControlAnalysis.Analyze(SyntheticObservations());

        var cells = result.Cells
            .Where(r =>
                r.HorizonBars == 1 &&
                r.Scope == "AllValid" &&
                r.Stage == "ControlAllThree")
            .ToArray();

        Assert.Equal(10, cells.Length);
        Assert.Equal(5, cells.Count(c => c.Direction == "UP"));
        Assert.Equal(5, cells.Count(c => c.Direction == "DOWN"));
    }

    [Fact]
    public void Analyze_OvershootSensitivityRemovesOnlyRowsNotDefinitions()
    {
        var result = VolumeBar6500CvdControlAnalysis.Analyze(SyntheticObservations());

        var primary = result.Summary.Single(r =>
            r.HorizonBars == 1 &&
            r.Scope == "AllValid" &&
            r.Stage == "CompleteCaseNoControls");

        var sensitivity = result.Summary.Single(r =>
            r.HorizonBars == 1 &&
            r.Scope == "SignalBarVolumeLt13000" &&
            r.Stage == "CompleteCaseNoControls");

        Assert.True(sensitivity.N < primary.N);

        var primaryCells = result.Cells
            .Where(r =>
                r.HorizonBars == 1 &&
                r.Scope == "AllValid" &&
                r.Stage == "ControlAllThree")
            .OrderBy(r => r.Direction)
            .ThenBy(r => r.MagnitudeQuintile)
            .ToArray();

        var sensitivityCells = result.Cells
            .Where(r =>
                r.HorizonBars == 1 &&
                r.Scope == "SignalBarVolumeLt13000" &&
                r.Stage == "ControlAllThree")
            .OrderBy(r => r.Direction)
            .ThenBy(r => r.MagnitudeQuintile)
            .ToArray();

        Assert.Equal(primaryCells.Length, sensitivityCells.Length);

        for (var i = 0; i < primaryCells.Length; i++)
        {
            Assert.Equal(primaryCells[i].Direction, sensitivityCells[i].Direction);
            Assert.Equal(primaryCells[i].MagnitudeQuintile, sensitivityCells[i].MagnitudeQuintile);
        }
    }

    [Fact]
    public void Analyze_LeaveOneSessionOutRemovesWholeSessions()
    {
        var observations = SyntheticObservations();
        var result = VolumeBar6500CvdControlAnalysis.Analyze(observations);

        var rows = result.LeaveOneSessionOut
            .Where(r =>
                r.HorizonBars == 1 &&
                r.Stage == "ControlAllThree")
            .OrderBy(r => r.ExcludedDate)
            .ToArray();

        Assert.Equal(3, rows.Length);

        foreach (var row in rows)
        {
            Assert.DoesNotContain(
                result.Sessions.Where(s =>
                    s.HorizonBars == 1 &&
                    s.Scope == "AllValid" &&
                    s.Stage == "ControlAllThree" &&
                    s.TradingDate == row.ExcludedDate),
                _ => false);
        }
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

        foreach (var (date, sessionIndex) in dates.Select((d, i) => (d, i)))
        {
            var bars = new List<VolumeBar6500Revalidation.Bar>();
            decimal previousClose = 25_000m;

            for (var i = 0; i < 100; i++)
            {
                var magnitudeBucket = (i % 20) + 1;
                var direction = ((i / 20) % 2 == 0) ? 1 : -1;
                var signalMove = direction * (0.5m + magnitudeBucket * 0.25m);

                var open = previousClose;
                var close = open + signalMove;

                var wickLow = (i % 5) * 0.15m + 0.2m;
                var wickHigh = ((i + 2) % 5) * 0.12m + 0.2m;

                var low = Math.Min(open, close) - wickLow;
                var high = Math.Max(open, close) + wickHigh;

                var observedVolume = i % 29 == 0
                    ? 13_000L
                    : 6_500L + ((i % 9) * 100L);

                var missingBook = i % 23 == 0;
                double? depth = missingBook
                    ? null
                    : direction * (0.02 + (i % 7) * 0.01);

                double? tob = missingBook
                    ? null
                    : direction * (0.03 + (i % 11) * 0.008);

                var cvd =
                    (long)(direction * (500 + magnitudeBucket * 40) +
                    ((i % 3) - 1) * 250);

                bars.Add(Bar(
                    date,
                    i,
                    open,
                    high,
                    low,
                    close,
                    observedVolume,
                    feedUpdates: 10 + (i % 7),
                    durationSeconds: 4 + (i % 13),
                    cvd,
                    depth,
                    tob));

                previousClose = close;
            }

            rows.AddRange(
                VolumeBar6500Revalidation.BuildObservations(
                    date,
                    sessionIndex,
                    bars));
        }

        return rows;
    }

    static VolumeBar6500Revalidation.Bar Bar(
        DateOnly date,
        int index,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long observedVolume,
        int feedUpdates,
        double durationSeconds,
        long cvd,
        double? depth,
        double? tob)
    {
        var start = new DateTimeOffset(
            date.ToDateTime(new TimeOnly(9, 15)).AddSeconds(index * 20),
            Ist);

        var end = start.AddSeconds(durationSeconds);

        return new VolumeBar6500Revalidation.Bar(
            index,
            start,
            end,
            start.AddMilliseconds(20),
            end.AddMilliseconds(20),
            (index * 100L) + 1,
            (index * 100L) + feedUpdates,
            open,
            high,
            low,
            close,
            observedVolume,
            observedVolume - VolumeBar6500Revalidation.ThresholdContracts,
            1_000_000 + (index * 100),
            feedUpdates,
            missingToZero(depth, feedUpdates),
            cvd,
            depth,
            0.0,
            tob,
            false,
            (double)close - 1.0);

        static int missingToZero(double? value, int count) =>
            value is null ? 0 : count;
    }
}
