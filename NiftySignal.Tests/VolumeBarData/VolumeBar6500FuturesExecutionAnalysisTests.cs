using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class VolumeBar6500FuturesExecutionAnalysisTests
{
    const string Mechanism = VolumeBar6500EpisodeStateAnalysis.AgreementState;
    const int Horizon = 1;

    [Fact]
    public void Analyze_BreakevenCostEqualsMeanGrossPoints()
    {
        var entries = new[]
        {
            Entry(new DateOnly(2026, 9, 8), 3.0),
            Entry(new DateOnly(2026, 9, 8), 1.0),
            Entry(new DateOnly(2026, 9, 9), 2.0),
        };

        var result = VolumeBar6500FuturesExecutionAnalysis.Analyze(entries);
        var breakeven = Assert.Single(result.Breakeven, x => x.Mechanism == Mechanism && x.HorizonBars == Horizon);

        Assert.Equal(2.0, breakeven.MeanGrossPoints, precision: 10);
        Assert.Equal(breakeven.MeanGrossPoints, breakeven.BreakevenCostPoints, precision: 10);
    }

    [Fact]
    public void Analyze_NetPointsSubtractCostAtEachGridPoint()
    {
        var entries = new[]
        {
            Entry(new DateOnly(2026, 9, 8), 4.0),
            Entry(new DateOnly(2026, 9, 9), 2.0),
        };

        var result = VolumeBar6500FuturesExecutionAnalysis.Analyze(entries);

        var atCost1 = Assert.Single(result.Summary, x => x.Mechanism == Mechanism && x.HorizonBars == Horizon && x.CostPoints == 1.0);
        Assert.Equal(2.0, atCost1.MeanNetPoints!.Value, precision: 10); // mean gross 3.0 - cost 1.0
        Assert.Equal(1.0, atCost1.NetPositiveRate!.Value, precision: 10); // both trades net positive at cost 1

        var atCost3 = Assert.Single(result.Summary, x => x.Mechanism == Mechanism && x.HorizonBars == Horizon && x.CostPoints == 3.0);
        Assert.Equal(0.5, atCost3.NetPositiveRate!.Value, precision: 10); // only the 4.0 trade clears a 3.0 cost
    }

    [Fact]
    public void Analyze_LeaveOneSessionOutExcludesEachSessionInTurn()
    {
        var entries = new[]
        {
            Entry(new DateOnly(2026, 9, 8), 5.0),
            Entry(new DateOnly(2026, 9, 9), 5.0),
            Entry(new DateOnly(2026, 9, 10), -1.0),
        };

        var result = VolumeBar6500FuturesExecutionAnalysis.Analyze(entries);
        var loo = Assert.Single(result.LeaveOneOut, x => x.Mechanism == Mechanism && x.HorizonBars == Horizon && x.CostPoints == 0.0);

        // excluding 09-10 (-1.0) leaves the two 5.0 sessions -> mean 5.0; excluding either 5.0
        // session leaves (5.0 + -1.0)/2 = 2.0.
        Assert.Equal(2.0, loo.LeaveOneSessionOutMinMeanNetPoints, precision: 10);
        Assert.Equal(5.0, loo.LeaveOneSessionOutMaxMeanNetPoints, precision: 10);
        Assert.Equal(3, loo.LeaveOneSessionOutCount);
        Assert.Equal(3, loo.LeaveOneSessionOutSameSignCount); // overall mean net is positive; all three folds stay positive
    }

    static VolumeBar6500EpisodeStateAnalysis.EntryObservation Entry(DateOnly date, double alignedForwardPoints) =>
        new(
            EpisodeId: $"{date:yyyyMMdd}-{Mechanism}-0001",
            TradingDate: date,
            Dte: 0,
            Mechanism: Mechanism,
            StateSign: 1,
            ExpectedDirectionSign: -1,
            OptionType: "Put",
            HorizonBars: Horizon,
            StartBarIndex: 0,
            StartAvailableAt: date.ToDateTime(new TimeOnly(9, 15)),
            EpisodeBarCount: 1,
            EpisodeElapsedSeconds: 20,
            EpisodeObservedVolume: 6500,
            EntryPriceMove: 1.0,
            EntryCvd: 100,
            EntryPriceAbsExpandingPercentile: 0.5,
            EntryCvdAbsExpandingPercentile: 0.5,
            ForwardPoints: -alignedForwardPoints,
            AlignedForwardPoints: alignedForwardPoints,
            UnderlyingCorrect: alignedForwardPoints > 0,
            OptionSignalStatus: null,
            Token: null,
            Strike: null,
            OptionOutcomeStatus: null,
            OptionNetReturnPct: null,
            OptionProfitable: null,
            MfePercent: null,
            MaePercent: null,
            TimeToMfeSeconds: null,
            TranslationOutcomeCategory: "NoTranslationSignal");
}
