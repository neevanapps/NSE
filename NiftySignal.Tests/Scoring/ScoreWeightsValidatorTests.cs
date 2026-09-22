using NiftySignal.Scoring;

namespace NiftySignal.Tests.Scoring;

public class ScoreWeightsValidatorTests
{
    [Fact]
    public void Validate_AcceptsTheDefaultOptions_MappedToScoreWeights()
    {
        var weights = new ScoreWeightsOptions().ToScoreWeights();

        var errors = ScoreWeightsValidator.Validate(weights);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AcceptsScoreWeightsDefault_TheStaticFallback()
    {
        // The two "defaults" (ScoreWeightsOptions' and ScoreWeights.Default) are kept in sync
        // by hand, not automatically -- this pins that they haven't drifted apart.
        var errors = ScoreWeightsValidator.Validate(ScoreWeights.Default);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsANegativeWeight()
    {
        var options = new ScoreWeightsOptions { Pcr = -0.1 };

        var errors = ScoreWeightsValidator.Validate(options.ToScoreWeights());

        Assert.Contains(errors, e => e.Contains("Pcr"));
    }

    [Fact]
    public void Validate_RejectsWeightsThatDoNotSumToOne()
    {
        var options = new ScoreWeightsOptions { OiBuildupNet = 0.5 }; // pushes Total well past 1.0

        var errors = ScoreWeightsValidator.Validate(options.ToScoreWeights());

        Assert.Contains(errors, e => e.Contains("sum to 1.0"));
    }

    [Fact]
    public void Validate_RejectsABlankVersion()
    {
        var options = new ScoreWeightsOptions { Version = "" };

        var errors = ScoreWeightsValidator.Validate(options.ToScoreWeights());

        Assert.Contains(errors, e => e.Contains("Version"));
    }
}
