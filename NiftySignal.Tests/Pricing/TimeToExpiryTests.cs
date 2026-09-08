using NiftySignal.Pricing;

namespace NiftySignal.Tests.Pricing;

public class TimeToExpiryTests
{
    [Fact]
    public void YearsUntilExpiry_ForOneYearOut_ExcludesWeekends()
    {
        // 02 Jan 2026 (Friday) to 02 Jan 2027 (Saturday) inclusive contains 105 weekend days --
        // trading time is calendar time minus those, not a plain calendar year (audit F6: the
        // old calendar-days/365 formula would have asserted this at ~1.0).
        var asOf = new DateTimeOffset(2026, 1, 2, 9, 15, 0, TimeSpan.FromHours(5.5));
        var expiry = new DateOnly(2027, 1, 2);

        var years = TimeToExpiry.YearsUntilExpiry(expiry, asOf);

        var calendarDays = (new DateTimeOffset(2027, 1, 2, 15, 30, 0, TimeSpan.FromHours(5.5)) - asOf).TotalDays;
        var expected = (calendarDays - 105) / 365.0;
        Assert.Equal(expected, years, 1e-9);
        Assert.True(years < 0.75, "365 calendar days should be well under 0.75 trading years once ~105 weekend days are excluded.");
    }

    [Fact]
    public void YearsUntilExpiry_ExcludesExactlyTheWeekendDays_ForASpanCrossingOneWeekend()
    {
        // Friday close to Monday close, in the exact market-close instant both endpoints use
        // -- 3 calendar days, 2 of them weekend, so trading time is deterministically exactly
        // 1 trading day, not the noisier fractional cases the other tests use.
        var fridayClose = new DateTimeOffset(2026, 1, 2, 15, 30, 0, TimeSpan.FromHours(5.5));
        var mondayExpiry = new DateOnly(2026, 1, 5);

        var years = TimeToExpiry.YearsUntilExpiry(mondayExpiry, fridayClose);

        Assert.Equal(1.0 / 365.0, years, 1e-9);
    }

    [Fact]
    public void YearsUntilExpiry_ComputesToMarketCloseOnExpiryDate_NotMidnight()
    {
        // 5.5 hours before close -- comfortably above the 1-hour floor, so this isolates
        // "measured against 15:30, not midnight" from the flooring behavior tested below.
        var expiry = new DateOnly(2026, 9, 3);
        var tenAm = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.FromHours(5.5));

        var years = TimeToExpiry.YearsUntilExpiry(expiry, tenAm);

        var expected = TimeSpan.FromHours(5.5).TotalDays / 365.0;
        Assert.Equal(expected, years, 1e-9);
    }

    [Fact]
    public void YearsUntilExpiry_FloorsAtOneHour_WhenLessThanAnHourRemainsBeforeClose()
    {
        // One minute to go -- below the floor, so this must clamp to the minimum rather
        // than return the true (numerically dangerous) near-zero remaining time.
        var expiry = new DateOnly(2026, 9, 3);
        var oneMinuteBeforeClose = new DateTimeOffset(2026, 9, 3, 15, 29, 0, TimeSpan.FromHours(5.5));

        var years = TimeToExpiry.YearsUntilExpiry(expiry, oneMinuteBeforeClose);

        Assert.Equal(TimeToExpiry.Minimum.TotalDays / 365.0, years, 1e-9);
    }

    [Fact]
    public void YearsUntilExpiry_FloorsAtOneHour_WhenExpiryHasEffectivelyPassed()
    {
        var expiry = new DateOnly(2026, 9, 3);
        var wellAfterClose = new DateTimeOffset(2026, 9, 3, 16, 0, 0, TimeSpan.FromHours(5.5));

        var years = TimeToExpiry.YearsUntilExpiry(expiry, wellAfterClose);

        Assert.Equal(TimeToExpiry.Minimum.TotalDays / 365.0, years, 1e-9);
    }

    [Fact]
    public void YearsUntilExpiry_FloorsAtOneHour_WhenExpiryIsInThePast()
    {
        var expiry = new DateOnly(2026, 1, 1);
        var muchLater = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.FromHours(5.5));

        var years = TimeToExpiry.YearsUntilExpiry(expiry, muchLater);

        Assert.Equal(TimeToExpiry.Minimum.TotalDays / 365.0, years, 1e-9);
    }

    [Fact]
    public void YearsUntilExpiry_IsUnaffectedByTheCallersUtcOffset()
    {
        // 15:30 IST is the rule regardless of what offset "asOf" happens to carry.
        var expiry = new DateOnly(2026, 9, 3);
        var asOfIst = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.FromHours(5.5));
        var asOfUtc = asOfIst.ToUniversalTime();

        var yearsFromIst = TimeToExpiry.YearsUntilExpiry(expiry, asOfIst);
        var yearsFromUtc = TimeToExpiry.YearsUntilExpiry(expiry, asOfUtc);

        Assert.Equal(yearsFromIst, yearsFromUtc, 1e-12);
    }
}
