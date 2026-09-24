using NiftySignal.Domain.Enums;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>2026-09-23, multi-day behaviour validation addendum (point 12). Coverage for <see cref="OptionBarQualityAudit"/>'s missing/stale roll-up and per-day breakdown.</summary>
public sealed class OptionBarQualityAuditTests
{
    static SynchronizedOptionEventBar Bar(DateOnly date, int eventId, bool missing) => new(
        eventId, date, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 25000m, OptionType.Call, 0, "TOK",
        missing ? null : 100m, missing ? null : 100m, missing ? null : 100m, missing ? null : 100m, missing ? null : 100m, missing ? null : 100.0,
        missing ? 0 : 1, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MissingData: missing, IsStale: missing, LastKnownPrice: null, AgeMs: null);

    [Fact]
    public void Compute_TotalsAndPercentagesAreCorrect()
    {
        var date = new DateOnly(2026, 9, 8);
        var bars = new List<SynchronizedOptionEventBar> { Bar(date, 0, false), Bar(date, 1, true), Bar(date, 2, true), Bar(date, 3, false) };

        var result = OptionBarQualityAudit.Compute(bars);

        Assert.Equal(4, result.TotalBars);
        Assert.Equal(2, result.MissingCount);
        Assert.Equal(2, result.StaleCount);
        Assert.Equal(50.0, result.MissingPercent);
        Assert.Equal(50.0, result.StalePercent);
    }

    [Fact]
    public void Compute_BreaksDownByDay()
    {
        var day1 = new DateOnly(2026, 9, 8);
        var day2 = new DateOnly(2026, 9, 15);
        var bars = new List<SynchronizedOptionEventBar>
        {
            Bar(day1, 0, false), Bar(day1, 1, true),
            Bar(day2, 0, false), Bar(day2, 1, false), Bar(day2, 2, false),
        };

        var result = OptionBarQualityAudit.Compute(bars);

        Assert.Equal(2, result.ByDay.Count);
        var d1 = result.ByDay.Single(d => d.TradingDate == day1);
        Assert.Equal(2, d1.TotalBars);
        Assert.Equal(1, d1.MissingCount);
        var d2 = result.ByDay.Single(d => d.TradingDate == day2);
        Assert.Equal(3, d2.TotalBars);
        Assert.Equal(0, d2.MissingCount);
    }

    [Fact]
    public void Compute_EmptyInput_ReturnsZeroedResult_NeverDividesByZero()
    {
        var result = OptionBarQualityAudit.Compute([]);

        Assert.Equal(0, result.TotalBars);
        Assert.Equal(0.0, result.MissingPercent);
        Assert.Empty(result.ByDay);
    }
}
