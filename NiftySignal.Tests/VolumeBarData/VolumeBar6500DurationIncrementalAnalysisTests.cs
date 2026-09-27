using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class VolumeBar6500DurationIncrementalAnalysisTests
{
    [Fact]
    public void Analyze_CreatesTwoDirectionsTimesFiveMagnitudeCells()
    {
        var result = VolumeBar6500DurationIncrementalAnalysis.Analyze(SyntheticObservations());

        Assert.Equal(10, result.Definitions.Count);
        Assert.Equal(5, result.Definitions.Count(d => d.Direction == "UP"));
        Assert.Equal(5, result.Definitions.Count(d => d.Direction == "DOWN"));

        Assert.All(result.Definitions, d => Assert.Equal(20.0, d.DurationMedianSeconds, 10));
    }

    [Fact]
    public void Analyze_FasterMatchedBarsShowPositiveIncrementalReversalInSyntheticData()
    {
        var result = VolumeBar6500DurationIncrementalAnalysis.Analyze(SyntheticObservations());

        var summary = result.Summary.Single(r =>
            r.HorizonBars == 1 &&
            r.Scope == "AllValid");

        Assert.Equal(10, summary.ValidCellCount);
        Assert.True(summary.MeanCellFastMinusSlowReversalPoints > 0);
        Assert.True(summary.MedianCellFastMinusSlowReversalPoints > 0);
        Assert.True(summary.MedianCellDurationVsReversalSpearman < 0);

        Assert.Equal(3, summary.SessionCount);
        Assert.Equal(3, summary.PositiveSessionFastMinusSlowCount);
        Assert.Equal(0, summary.NegativeSessionFastMinusSlowCount);
    }

    [Fact]
    public void Analyze_ReversalAlignmentWorksForUpAndDownSignalBars()
    {
        var result = VolumeBar6500DurationIncrementalAnalysis.Analyze(SyntheticObservations());

        var rows = result.Cells
            .Where(r => r.HorizonBars == 1 && r.Scope == "AllValid")
            .ToArray();

        Assert.Contains(rows, r => r.Direction == "UP");
        Assert.Contains(rows, r => r.Direction == "DOWN");

        Assert.All(rows, r =>
        {
            Assert.NotNull(r.FastMinusSlowMeanReversalPoints);
            Assert.True(r.FastMinusSlowMeanReversalPoints > 0);
        });
    }

    [Fact]
    public void Analyze_SampledVolumeSensitivityUsesSameFrozenCellDefinitions()
    {
        var result = VolumeBar6500DurationIncrementalAnalysis.Analyze(SyntheticObservations());

        var primary = result.Summary.Single(r =>
            r.HorizonBars == 1 &&
            r.Scope == "AllValid");

        var sensitivity = result.Summary.Single(r =>
            r.HorizonBars == 1 &&
            r.Scope == "SignalBarVolumeLt13000");

        Assert.True(sensitivity.N < primary.N);

        foreach (var primaryCell in result.Cells.Where(r =>
            r.HorizonBars == 1 && r.Scope == "AllValid"))
        {
            var sensitivityCell = result.Cells.Single(r =>
                r.HorizonBars == 1 &&
                r.Scope == "SignalBarVolumeLt13000" &&
                r.Direction == primaryCell.Direction &&
                r.MagnitudeQuintile == primaryCell.MagnitudeQuintile);

            Assert.Equal(primaryCell.MoveMagnitudeMinObserved, sensitivityCell.MoveMagnitudeMinObserved, 10);
            Assert.Equal(primaryCell.MoveMagnitudeMaxObserved, sensitivityCell.MoveMagnitudeMaxObserved, 10);
            Assert.Equal(primaryCell.DurationMedianSeconds, sensitivityCell.DurationMedianSeconds, 10);
        }
    }

    [Fact]
    public void Analyze_LeaveOneSessionOutRemovesWholeSessionsAndKeepsFrozenMatching()
    {
        var observations = SyntheticObservations();
        var result = VolumeBar6500DurationIncrementalAnalysis.Analyze(observations);

        var rows = result.LeaveOneSessionOut
            .Where(r => r.HorizonBars == 1)
            .OrderBy(r => r.ExcludedDate)
            .ToArray();

        Assert.Equal(3, rows.Length);

        foreach (var row in rows)
        {
            Assert.Equal(
                observations.Count(o => o.TradingDate != row.ExcludedDate),
                row.N);

            Assert.Equal(10, row.ValidCellCount);
            Assert.True(row.MeanCellFastMinusSlowReversalPoints > 0);
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

        var durations = new[] { 5.0, 10.0, 30.0, 60.0 };
        var barIndex = 0;

        foreach (var (date, sessionIndex) in dates.Select((date, index) => (date, index)))
        {
            foreach (var directionSign in new[] { -1, 1 })
            {
                for (var magnitude = 1; magnitude <= 20; magnitude++)
                {
                    foreach (var duration in durations)
                    {
                        var signalChange = directionSign * (magnitude + (sessionIndex * 0.001));
                        var reversal1 = 4.0 - (duration * 0.04) + (magnitude * 0.01);
                        var reversal2 = 3.5 - (duration * 0.035) + (magnitude * 0.008);
                        var reversal4 = 3.0 - (duration * 0.03) + (magnitude * 0.006);

                        var forward1 = -directionSign * reversal1;
                        var forward2 = -directionSign * reversal2;
                        var forward4 = -directionSign * reversal4;

                        var open = 25_000m + barIndex;
                        var close = open + (decimal)signalChange;
                        var high = Math.Max(open, close) + 1m;
                        var low = Math.Min(open, close) - 1m;

                        var extremeSampledVolume =
                            magnitude == 20 && Math.Abs(duration - 60.0) < 0.0001;

                        var observedVolume = extremeSampledVolume
                            ? 13_000L
                            : 6_500L + magnitude;

                        var timestamp = new DateTimeOffset(
                            date.ToDateTime(new TimeOnly(9, 15)).AddSeconds(barIndex),
                            TimeSpan.FromHours(5.5));

                        rows.Add(new VolumeBar6500Revalidation.Observation(
                            date,
                            sessionIndex,
                            barIndex,
                            timestamp,
                            timestamp.AddMilliseconds(200),
                            close,
                            open,
                            high,
                            low,
                            signalChange,
                            signalChange / (double)open,
                            (double)(high - low),
                            observedVolume,
                            observedVolume - 6_500,
                            10,
                            10,
                            duration,
                            duration + 0.2,
                            0.0,
                            0.0,
                            0.0,
                            0.0,
                            0,
                            directionSign / duration,
                            forward1,
                            forward2,
                            forward4,
                            6_500,
                            13_000,
                            26_000,
                            Math.Max(0, forward1 + 5),
                            Math.Max(0, forward2 + 5),
                            Math.Max(0, forward4 + 5),
                            Math.Max(0, -forward1 + 5),
                            Math.Max(0, -forward2 + 5),
                            Math.Max(0, -forward4 + 5)));

                        barIndex++;
                    }
                }
            }
        }

        return rows;
    }
}
