using NiftySignal.Domain.Enums;
using NiftySignal.Scoring;

namespace NiftySignal.Tests.Scoring;

public class CoreScoreRawFormulasTests
{
    [Fact]
    public void DepthImbalance_IsCallAverageMinusPutAverage_WhenBothSidesPresent()
    {
        var result = CoreScoreRawFormulas.DepthImbalance(callDepth: [0.8, 0.6], putDepth: [-0.2, -0.4]);

        Assert.Equal(0.7 - -0.3, result!.Value, 1e-9);
    }

    [Fact]
    public void DepthImbalance_IsNull_WhenCallSideIsEmpty()
    {
        Assert.Null(CoreScoreRawFormulas.DepthImbalance(callDepth: [], putDepth: [-0.2]));
    }

    [Fact]
    public void DepthImbalance_IsNull_WhenPutSideIsEmpty()
    {
        Assert.Null(CoreScoreRawFormulas.DepthImbalance(callDepth: [0.8], putDepth: []));
    }

    [Fact]
    public void ItmSkew_IsPutAverageMinusCallAverage_ReversedSignFromDepthImbalance()
    {
        var result = CoreScoreRawFormulas.ItmSkew(callIv: [0.14, 0.16], putIv: [0.20, 0.22]);

        Assert.Equal(0.21 - 0.15, result!.Value, 1e-9);
    }

    [Fact]
    public void ItmSkew_IsNull_WhenEitherSideIsEmpty()
    {
        Assert.Null(CoreScoreRawFormulas.ItmSkew(callIv: [], putIv: [0.2]));
        Assert.Null(CoreScoreRawFormulas.ItmSkew(callIv: [0.2], putIv: []));
    }

    [Fact]
    public void GammaExposure_IsSignedSum_CallPositivePutNegative()
    {
        var result = CoreScoreRawFormulas.GammaExposure([
            (OptionType.Call, 0.002, 100_000.0),
            (OptionType.Call, 0.001, 50_000.0),
            (OptionType.Put, 0.0015, 80_000.0),
        ]);

        Assert.Equal((0.002 * 100_000) + (0.001 * 50_000) - (0.0015 * 80_000), result!.Value, 1e-9);
    }

    [Fact]
    public void GammaExposure_IsNull_WhenNoStrikesQualify()
    {
        Assert.Null(CoreScoreRawFormulas.GammaExposure([]));
    }

    [Fact]
    public void GammaExposure_IsZero_NotNull_WhenCallAndPutExactlyOffset()
    {
        // A real net-zero exposure across a non-empty set is a legitimate value, not "no data".
        var result = CoreScoreRawFormulas.GammaExposure([
            (OptionType.Call, 0.002, 100_000.0),
            (OptionType.Put, 0.002, 100_000.0),
        ]);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.Value, 1e-9);
    }

    [Fact]
    public void NotionalVolumeRatio_IsLogOfPutOverCall_WhenBothPositive()
    {
        var result = CoreScoreRawFormulas.NotionalVolumeRatio(callNotional: [1000.0, 2000.0], putNotional: [1500.0, 1500.0]);

        Assert.Equal(Math.Log(3000.0 / 3000.0), result!.Value, 1e-9);
    }

    [Fact]
    public void NotionalVolumeRatio_IsNull_WhenEitherSideIsNonPositive()
    {
        Assert.Null(CoreScoreRawFormulas.NotionalVolumeRatio(callNotional: [], putNotional: [1500.0]));
        Assert.Null(CoreScoreRawFormulas.NotionalVolumeRatio(callNotional: [1000.0], putNotional: []));
    }

    [Fact]
    public void BasisChange_IsFutureChangeMinusSpotChange_NotNegated()
    {
        // NOT sign-flipped here -- callers negate when computing the signed/ranked value, matching
        // CoreScoreSnapshot.BasisChangeRaw/ScoreCadence.BasisChangeRaw's own diagnostic convention.
        var result = CoreScoreRawFormulas.BasisChange(futureChange: 12.5m, spotChange: 4.25m);

        Assert.Equal(8.25, result!.Value, 1e-9);
    }

    [Fact]
    public void BasisChange_IsNull_WhenEitherChangeIsNull()
    {
        Assert.Null(CoreScoreRawFormulas.BasisChange(futureChange: null, spotChange: 4.25m));
        Assert.Null(CoreScoreRawFormulas.BasisChange(futureChange: 12.5m, spotChange: null));
    }

    [Fact]
    public void BasisChange_DoesDecimalSubtractionBeforeCasting_AvoidingIndependentDoubleRoundingNoise()
    {
        // Regression coverage for the 2026-09-17 bug: casting each side to double BEFORE
        // subtracting introduces noise a single decimal subtraction (then one cast) does not --
        // 0.3 and 0.2 are exactly representable in decimal but not in binary floating point, the
        // textbook case where "cast independently, then subtract" and "subtract, then cast once"
        // land on two different doubles.
        const decimal futureChange = 0.3m;
        const decimal spotChange = 0.2m;

        var viaSharedFormula = CoreScoreRawFormulas.BasisChange(futureChange, spotChange);
        var viaIndependentDoubleCasts = (double)futureChange - (double)spotChange;

        Assert.Equal((double)(futureChange - spotChange), viaSharedFormula!.Value); // exact: matches the shared formula's own decimal-first order
        Assert.NotEqual(viaSharedFormula.Value, viaIndependentDoubleCasts); // demonstrates the two orders genuinely diverge for this input
    }

    [Theory]
    [InlineData(OptionType.Call, -2, true)]
    [InlineData(OptionType.Call, -1, true)]
    [InlineData(OptionType.Call, 0, true)]
    [InlineData(OptionType.Call, 1, false)]
    [InlineData(OptionType.Call, -3, false)]
    [InlineData(OptionType.Put, 0, true)]
    [InlineData(OptionType.Put, 1, true)]
    [InlineData(OptionType.Put, 2, true)]
    [InlineData(OptionType.Put, -1, false)]
    [InlineData(OptionType.Put, 3, false)]
    public void InItm2Atm1_MatchesTheDocumentedBandBoundaries(OptionType type, int offset, bool expected)
    {
        Assert.Equal(expected, CoreScoreBands.InItm2Atm1(type, offset));
    }
}
