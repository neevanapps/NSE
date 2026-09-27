using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class VolumeBar6500SecondMetricAnalysisTests
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    [Fact]
    public void BuildObservations_DerivesSecondBatchMetricsWithoutFutureInputs()
    {
        var date = new DateOnly(2026, 9, 8);
        var bars = new List<VolumeBar6500Revalidation.Bar>();

        for (var i = 0; i < 16; i++)
        {
            var close = i == 0 ? 100m : 100m + (i * 2m);
            var open = close - 2m;
            var high = close + 1m;
            var low = open - 1m;
            var oi = 1_000L + (i * 100L);

            bars.Add(Bar(
                date,
                i,
                open,
                high,
                low,
                close,
                6_500,
                oi,
                feedUpdates: 10,
                durationSeconds: 10,
                cvd: 100 + i,
                tob: 0.10 + (i * 0.001),
                vwap: (double)close - 1.5));
        }

        var rows = VolumeBar6500Revalidation.BuildObservations(date, 0, bars);
        var second = rows[1];

        Assert.Equal(2.0 / 6_500.0, second.PriceImpact!.Value, 12);
        Assert.Equal("LongBuildup", second.FutureOiBuildupState);
        Assert.Equal(100.0, second.FutureOiBuildupSignedMagnitude!.Value, 12);
        Assert.Equal(-1.0, second.TrendReversion15!.Value, 12);
        Assert.Equal(1.0, second.TrendPersistence15Raw!.Value, 12);

        Assert.Equal(10.0 / 6_500.0, second.TickDensity!.Value, 12);
        Assert.Equal(1.0, second.TickVelocity!.Value, 12);
        Assert.Equal(0.2, second.PriceEfficiency!.Value, 12);
        Assert.Equal(2.0, second.Churn!.Value, 12);
        Assert.Equal(1.5, second.VwapDeviation!.Value, 12);

        Assert.Equal(1_100L, second.OpenInterestAtClose);
        Assert.Equal((double)second.FuturesClose - 1.5, second.VwapAtClose!.Value, 12);
    }

    [Fact]
    public void Analyze_ProducesLockedMetricSetHorizonsAndScopes()
    {
        var observations = SyntheticObservations();

        var result = VolumeBar6500SecondMetricAnalysis.Analyze(observations);

        Assert.Equal(11 * 3 * 2, result.Summary.Count);
        Assert.Equal(3 * 2, result.Baseline.Count);

        foreach (var metric in VolumeBar6500SecondMetricAnalysis.Metrics)
        {
            foreach (var horizon in VolumeBar6500SecondMetricAnalysis.Horizons)
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
    public void Analyze_UsesSameTenFrozenMoveMagnitudeCells()
    {
        var observations = SyntheticObservations();

        var result = VolumeBar6500SecondMetricAnalysis.Analyze(observations);

        var cells = result.MoveControlledCells
            .Where(r =>
                r.Metric == "TickVelocity" &&
                r.HorizonBars == 1 &&
                r.Scope == "AllValid")
            .ToArray();

        Assert.Equal(10, cells.Length);
        Assert.Equal(5, cells.Count(c => c.Direction == "UP"));
        Assert.Equal(5, cells.Count(c => c.Direction == "DOWN"));
    }

    [Fact]
    public void Analyze_KeepsTrendPersistenceAsDiagnosticInsteadOfSecondCandidate()
    {
        var result = VolumeBar6500SecondMetricAnalysis.Analyze(SyntheticObservations());

        var rows = result.Summary
            .Where(r => r.Metric == "TrendPersistence15Raw")
            .ToArray();

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.DiagnosticOnly));

        Assert.All(
            result.Summary.Where(r => r.Metric == "TrendReversion15"),
            r => Assert.False(r.DiagnosticOnly));
    }

    [Fact]
    public void Analyze_ReportsOiStatesSeparatelyFromSignedMagnitude()
    {
        var result = VolumeBar6500SecondMetricAnalysis.Analyze(SyntheticObservations());

        Assert.NotEmpty(result.OiStates);
        Assert.Contains(result.OiStates, r => r.State == "LongBuildup");
        Assert.Contains(result.OiStates, r => r.State == "ShortBuildup");
        Assert.Contains(result.OiStates, r => r.State == "LongUnwinding");
        Assert.Contains(result.OiStates, r => r.State == "ShortCovering");
    }

    static List<VolumeBar6500Revalidation.Observation> SyntheticObservations()
    {
        var all = new List<VolumeBar6500Revalidation.Observation>();
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
            long previousOi = 1_000_000;

            for (var i = 0; i < 80; i++)
            {
                var magnitude = (i % 20) + 1;
                var direction = (i / 20) % 2 == 0 ? 1 : -1;
                var closeChange = direction * magnitude * 0.5m;

                var close = previousClose + closeChange;
                var open = close - (direction * magnitude * 0.25m);
                var high = Math.Max(open, close) + 1m;
                var low = Math.Min(open, close) - 1m;

                var oiDirection = i % 4 switch
                {
                    0 => 1,
                    1 => 1,
                    2 => -1,
                    _ => -1,
                };
                var oi = previousOi + (oiDirection * (100 + i));

                var observedVolume = i % 37 == 0 ? 13_000L : 6_500L + (i % 7) * 100L;
                var feedUpdates = 12 + (i % 9);
                var duration = 5.0 + (i % 11) * 2.0;
                var vwap = (double)close - (direction * 2.0);

                bars.Add(Bar(
                    date,
                    i,
                    open,
                    high,
                    low,
                    close,
                    observedVolume,
                    oi,
                    feedUpdates,
                    duration,
                    cvd: direction * (500 + i * 10L),
                    tob: direction * (0.05 + (i % 10) * 0.01),
                    vwap: vwap));

                previousClose = close;
                previousOi = oi;
            }

            // Force all four OI buildup states to occur by changing the price/OI signs in sequence.
            // The sequence above naturally supplies both price directions; this rewrite changes
            // selected OI closes without changing any future outcome.
            for (var i = 1; i < bars.Count; i++)
            {
                var priceUp = bars[i].Close > bars[i - 1].Close;
                var desiredOiUp = i % 4 is 0 or 1;
                var priorOi = bars[i - 1].OpenInterestAtClose!.Value;
                var newOi = priorOi + (desiredOiUp ? 100 + i : -(100 + i));
                bars[i] = bars[i] with { OpenInterestAtClose = newOi };
            }

            all.AddRange(VolumeBar6500Revalidation.BuildObservations(date, sessionIndex, bars));
        }

        return all;
    }

    static VolumeBar6500Revalidation.Bar Bar(
        DateOnly date,
        int index,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long observedVolume,
        long oi,
        int feedUpdates,
        double durationSeconds,
        long cvd,
        double tob,
        double vwap)
    {
        var start = new DateTimeOffset(
            date.ToDateTime(new TimeOnly(9, 15)).AddSeconds(index * 30),
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
            oi,
            feedUpdates,
            feedUpdates,
            cvd,
            0.02,
            5.0,
            tob,
            false,
            vwap);
    }
}
