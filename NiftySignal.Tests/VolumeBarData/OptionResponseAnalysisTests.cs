using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>2026-09-24, option-response validation. Coverage for <see cref="OptionResponseAnalysis.ClassifyExclusionReason"/> -- the one new pure calculation this task adds.</summary>
public sealed class OptionResponseAnalysisTests
{
    [Fact]
    public void ClassifyExclusionReason_ValueAvailable_ReturnsAvailable()
    {
        var change = new UnderlyingOptionRelationshipSummary.SeriesChange(5m, 5m, StartAvailable: true, EndAvailable: true, SameContract: true, StaleInvolved: false);

        Assert.Equal("Available", OptionResponseAnalysis.ClassifyExclusionReason(change));
    }

    [Fact]
    public void ClassifyExclusionReason_ContractChanged_ReturnsContractTransition_EvenIfDataWasTechnicallyAvailable()
    {
        var change = new UnderlyingOptionRelationshipSummary.SeriesChange(null, null, StartAvailable: true, EndAvailable: true, SameContract: false, StaleInvolved: false);

        Assert.Equal("ContractTransition", OptionResponseAnalysis.ClassifyExclusionReason(change));
    }

    [Fact]
    public void ClassifyExclusionReason_MissingAtEitherEndpoint_ReturnsMissingOrStaleData()
    {
        Assert.Equal("MissingOrStaleData", OptionResponseAnalysis.ClassifyExclusionReason(
            new UnderlyingOptionRelationshipSummary.SeriesChange(null, null, StartAvailable: false, EndAvailable: true, SameContract: true, StaleInvolved: false)));
        Assert.Equal("MissingOrStaleData", OptionResponseAnalysis.ClassifyExclusionReason(
            new UnderlyingOptionRelationshipSummary.SeriesChange(null, null, StartAvailable: true, EndAvailable: false, SameContract: true, StaleInvolved: false)));
    }

    [Fact]
    public void ClassifyExclusionReason_ContractTransitionChecked_BeforeMissingData()
    {
        // Both conditions true at once -- the transition reason must win (a transitioned contract
        // is never meaningfully described as "missing data" instead).
        var change = new UnderlyingOptionRelationshipSummary.SeriesChange(null, null, StartAvailable: false, EndAvailable: false, SameContract: false, StaleInvolved: false);

        Assert.Equal("ContractTransition", OptionResponseAnalysis.ClassifyExclusionReason(change));
    }
}
