using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>2026-09-25, coverage for <see cref="RollingStateAnalysis.Annotate"/> -- the rolling
/// 2600x5 state-machine's entry/continuation/exit/age/episode annotation.</summary>
public sealed class RollingStateAnalysisTests
{
    [Fact]
    public void Annotate_FirstObservation_OtherGetsNoState()
    {
        var result = RollingStateAnalysis.Annotate(["Other"]);
        Assert.Equal(0, result[0].StateAge);
        Assert.Equal(-1, result[0].StateEpisodeId);
        Assert.False(result[0].IsStateEntry);
        Assert.False(result[0].IsStateExit);
    }

    [Fact]
    public void Annotate_OtherThenB_IsNewEntryAtAge1()
    {
        var result = RollingStateAnalysis.Annotate(["Other", "B"]);
        Assert.True(result[1].IsStateEntry);
        Assert.False(result[1].IsStateContinuation);
        Assert.Equal(1, result[1].StateAge);
        Assert.Equal("Other", result[1].PreviousState);
    }

    [Fact]
    public void Annotate_ConsecutiveB_IsOneEpisodeWithIncreasingAge()
    {
        var result = RollingStateAnalysis.Annotate(["Other", "B", "B", "B", "B"]);
        Assert.Equal([1, 2, 3, 4], result.Skip(1).Select(r => r.StateAge));
        Assert.True(result.Skip(1).Select(r => r.StateEpisodeId).Distinct().Count() == 1); // one episode id throughout.
        Assert.True(result[1].IsStateEntry);
        Assert.True(result[2].IsStateContinuation && result[3].IsStateContinuation && result[4].IsStateContinuation);
    }

    [Fact]
    public void Annotate_BThenOther_IsExitNotEntry()
    {
        var result = RollingStateAnalysis.Annotate(["B", "B", "Other"]);
        Assert.True(result[2].IsStateExit);
        Assert.False(result[2].IsStateEntry);
        Assert.Equal(0, result[2].StateAge);
        Assert.Equal(-1, result[2].StateEpisodeId);
    }

    [Fact]
    public void Annotate_DirectBToA_MarksBothExitAndEntryOnSameRow()
    {
        var result = RollingStateAnalysis.Annotate(["B", "B", "A"]);
        Assert.True(result[2].IsStateExit);
        Assert.True(result[2].IsStateEntry);
        Assert.Equal(1, result[2].StateAge);
    }

    [Fact]
    public void Annotate_NewBAfterEarlierBEpisode_GetsADifferentEpisodeId()
    {
        var result = RollingStateAnalysis.Annotate(["B", "Other", "B"]);
        Assert.NotEqual(result[0].StateEpisodeId, result[2].StateEpisodeId);
        Assert.Equal(1, result[2].StateAge); // fresh episode, not a continuation of the first.
    }
}
