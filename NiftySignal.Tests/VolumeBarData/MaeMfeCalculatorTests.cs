using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-22 MAE/MFE analytics task (docs/VOLUME_BAR_FINDINGS.md): coverage for
/// <see cref="MaeMfeCalculator.Compute"/> against deterministic synthetic tick paths with a known
/// worst/best excursion, per the project's own "prefer a separable, unit-testable function over an
/// untested inline calculation" instruction.
/// </summary>
public class MaeMfeCalculatorTests
{
    [Fact]
    public void Compute_DipThenRally_ReportsBothExcursionsFromEntry()
    {
        // Entry at 100, path dips to 80 (worst -20) then rallies to 130 (best +30) before exiting at 110.
        var entryPrice = 100m;
        var path = new[] { 100m, 95m, 80m, 90m, 110m, 130m, 110m };

        var result = MaeMfeCalculator.Compute(entryPrice, path);

        Assert.Equal(20m, result.MaePoints);
        Assert.Equal(30m, result.MfePoints);
        Assert.Equal(20m, result.MaePercent);
        Assert.Equal(30m, result.MfePercent);
    }

    [Fact]
    public void Compute_PriceOnlyRises_MaeIsZeroNotNegative()
    {
        // Never dips below entry -- MAE must clamp at 0, not go negative.
        var path = new[] { 100m, 105m, 110m, 120m };

        var result = MaeMfeCalculator.Compute(100m, path);

        Assert.Equal(0m, result.MaePoints);
        Assert.Equal(20m, result.MfePoints);
    }

    [Fact]
    public void Compute_PriceOnlyFalls_MfeIsZeroNotNegative()
    {
        var path = new[] { 100m, 90m, 80m, 70m };

        var result = MaeMfeCalculator.Compute(100m, path);

        Assert.Equal(30m, result.MaePoints);
        Assert.Equal(0m, result.MfePoints);
    }

    [Fact]
    public void Compute_EmptyPath_ReturnsZeroes()
    {
        var result = MaeMfeCalculator.Compute(100m, Array.Empty<decimal>());

        Assert.Equal(0m, result.MaePoints);
        Assert.Equal(0m, result.MfePoints);
        Assert.Equal(0m, result.MaePercent);
        Assert.Equal(0m, result.MfePercent);
    }

    [Fact]
    public void Compute_PercentIsRelativeToEntryPrice()
    {
        // Rs 20 entry, Rs 4 adverse move -> 20% MAE, distinguishing this from a points-only check.
        var path = new[] { 20m, 16m, 22m };

        var result = MaeMfeCalculator.Compute(20m, path);

        Assert.Equal(4m, result.MaePoints);
        Assert.Equal(20m, result.MaePercent);
        Assert.Equal(2m, result.MfePoints);
        Assert.Equal(10m, result.MfePercent);
    }
}
