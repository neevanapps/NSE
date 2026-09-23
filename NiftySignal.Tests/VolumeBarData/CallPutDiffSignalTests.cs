using NiftySignal.Domain.Enums;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// Experiment 2, 2026-09-22 "Call/Put premium momentum" task (docs/VOLUME_BAR_FINDINGS.md's dated
/// section): coverage for <see cref="CallPutDiffSignal"/>, the pure, DB-free Call-minus-Put
/// differencing function this task's combined-signal correlations are built on.
/// </summary>
public class CallPutDiffSignalTests
{
    static MaSpreadRelationshipAnalyzer.Sample MakeSample(
        DateOnly date, int barIndex, OptionType side, double spread, double? niftyFwd10 = null) =>
        new(date, new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMinutes(barIndex),
            barIndex, side, spread,
            NiftyFwd5: null, NiftyFwd10: niftyFwd10, NiftyFwd20: null,
            OptionFwd5: null, OptionFwd10: null, OptionFwd20: null);

    [Fact]
    public void Combine_SubtractsPutFromCall_PerMatchingBar()
    {
        var date = new DateOnly(2026, 9, 22);
        var calls = new List<MaSpreadRelationshipAnalyzer.Sample>
        {
            MakeSample(date, 0, OptionType.Call, spread: 0.05, niftyFwd10: 0.01),
            MakeSample(date, 1, OptionType.Call, spread: -0.02, niftyFwd10: -0.005),
        };
        var puts = new List<MaSpreadRelationshipAnalyzer.Sample>
        {
            MakeSample(date, 0, OptionType.Put, spread: -0.03),
            MakeSample(date, 1, OptionType.Put, spread: 0.04),
        };

        var rows = CallPutDiffSignal.Combine(calls, puts);

        Assert.Equal(2, rows.Count);
        Assert.Equal(0.08, rows[0].Diff, precision: 10); // 0.05 - (-0.03)
        Assert.Equal(0.01, rows[0].NiftyFwd10);
        Assert.Equal(-0.06, rows[1].Diff, precision: 10); // -0.02 - 0.04
        Assert.Equal(-0.005, rows[1].NiftyFwd10);
    }

    [Fact]
    public void Combine_DropsBarsPresentOnOnlyOneSide()
    {
        var date = new DateOnly(2026, 9, 22);
        var calls = new List<MaSpreadRelationshipAnalyzer.Sample>
        {
            MakeSample(date, 0, OptionType.Call, spread: 0.05),
            MakeSample(date, 1, OptionType.Call, spread: 0.02), // no matching Put bar
        };
        var puts = new List<MaSpreadRelationshipAnalyzer.Sample>
        {
            MakeSample(date, 0, OptionType.Put, spread: -0.01),
            MakeSample(date, 2, OptionType.Put, spread: 0.03), // no matching Call bar
        };

        var rows = CallPutDiffSignal.Combine(calls, puts);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.BarIndex);
        Assert.Equal(0.06, row.Diff, precision: 10);
    }

    [Fact]
    public void Combine_KeepsDatesSeparate_NoCrossDateJoin()
    {
        var date1 = new DateOnly(2026, 9, 22);
        var date2 = new DateOnly(2026, 9, 23);
        var calls = new List<MaSpreadRelationshipAnalyzer.Sample>
        {
            MakeSample(date1, 0, OptionType.Call, spread: 0.05),
        };
        var puts = new List<MaSpreadRelationshipAnalyzer.Sample>
        {
            MakeSample(date2, 0, OptionType.Put, spread: -0.01), // same BarIndex, different date
        };

        var rows = CallPutDiffSignal.Combine(calls, puts);

        Assert.Empty(rows);
    }

    [Fact]
    public void Combine_EmptyInput_ReturnsEmpty()
    {
        var rows = CallPutDiffSignal.Combine([], []);
        Assert.Empty(rows);
    }
}
