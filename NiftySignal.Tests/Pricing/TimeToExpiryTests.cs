using NiftySignal.Pricing;

namespace NiftySignal.Tests.Pricing;

public class TimeToExpiryTests
{
    [Fact]
    public void YearsUntilExpiry_ForOneYearOut_IsCalendarTime_NotTradingDays()
    {
        // 02 Jan 2026 (Friday) to 02 Jan 2027 (Saturday) inclusive is 365 calendar days --
        // 2026-09-09 external review reverted audit finding F6's trading-day (weekday-count)
        // version: index options price on calendar time, and a weekday loop with no NSE
        // holiday calendar would silently undercharge any week containing a market holiday.
        var asOf = new DateTimeOffset(2026, 1, 2, 9, 15, 0, TimeSpan.FromHours(5.5));
        var expiry = new DateOnly(2027, 1, 2);

        var years = TimeToExpiry.YearsUntilExpiry(expiry, asOf);

        var calendarDays = (new DateTimeOffset(2027, 1, 2, 15, 30, 0, TimeSpan.FromHours(5.5)) - asOf).TotalDays;
        Assert.Equal(calendarDays / 365.0, years, 1e-9);
        Assert.True(years > 0.99, "A calendar year out should be close to 1.0 trading years, not discounted for weekends.");
    }

    [Fact]
    public void YearsUntilExpiry_IncludesWeekendDays_ForASpanCrossingOneWeekend()
    {
        // Friday close to Monday close: 3 calendar days, all counted -- weekend theta decay
        // is real, not excluded (contrast with the reverted trading-day version, which would
        // have asserted this at 1 day, not 3).
        var fridayClose = new DateTimeOffset(2026, 1, 2, 15, 30, 0, TimeSpan.FromHours(5.5));
        var mondayExpiry = new DateOnly(2026, 1, 5);

        var years = TimeToExpiry.YearsUntilExpiry(mondayExpiry, fridayClose);

        Assert.Equal(3.0 / 365.0, years, 1e-9);
    }

    [Fact]
    public void YearsUntilExpiry_ComputesToMarketCloseOnExpiryDate_NotMidnight()
    {
        // 5.5 hours before close -- comfortably above the 2-minute floor, so this isolates
        // "measured against 15:30, not midnight" from the flooring behavior tested below.
        var expiry = new DateOnly(2026, 9, 3);
        var tenAm = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.FromHours(5.5));

        var years = TimeToExpiry.YearsUntilExpiry(expiry, tenAm);

        var expected = TimeSpan.FromHours(5.5).TotalDays / 365.0;
        Assert.Equal(expected, years, 1e-9);
    }

    [Fact]
    public void YearsUntilExpiry_FloorsAtTwoMinutes_WhenLessThanThatRemainsBeforeClose()
    {
        // 30 seconds to go -- below the floor, so this must clamp to the minimum rather
        // than return the true (numerically dangerous) near-zero remaining time. Floor
        // tightened from 1 hour to 2 minutes (2026-09-09 external review, resolving audit
        // finding F20): the 1-hour floor kept every Greek/IV/theoretical price pretending
        // T=1h for the entire final hour before expiry, exactly the highest-gamma stretch.
        var expiry = new DateOnly(2026, 9, 3);
        var thirtySecondsBeforeClose = new DateTimeOffset(2026, 9, 3, 15, 29, 30, TimeSpan.FromHours(5.5));

        var years = TimeToExpiry.YearsUntilExpiry(expiry, thirtySecondsBeforeClose);

        Assert.Equal(TimeToExpiry.Minimum.TotalDays / 365.0, years, 1e-9);
    }

    [Fact]
    public void YearsUntilExpiry_DoesNotFloor_WhenJustAboveTheTwoMinuteFloor()
    {
        // 3 minutes to go -- above the floor, so the true remaining time should be used,
        // not clamped. Boundary companion to the floor test above.
        var expiry = new DateOnly(2026, 9, 3);
        var threeMinutesBeforeClose = new DateTimeOffset(2026, 9, 3, 15, 27, 0, TimeSpan.FromHours(5.5));

        var years = TimeToExpiry.YearsUntilExpiry(expiry, threeMinutesBeforeClose);

        Assert.Equal(TimeSpan.FromMinutes(3).TotalDays / 365.0, years, 1e-9);
    }

    [Fact]
    public void YearsUntilExpiry_FloorsAtTwoMinutes_WhenExpiryHasEffectivelyPassed()
    {
        var expiry = new DateOnly(2026, 9, 3);
        var wellAfterClose = new DateTimeOffset(2026, 9, 3, 16, 0, 0, TimeSpan.FromHours(5.5));

        var years = TimeToExpiry.YearsUntilExpiry(expiry, wellAfterClose);

        Assert.Equal(TimeToExpiry.Minimum.TotalDays / 365.0, years, 1e-9);
    }

    [Fact]
    public void YearsUntilExpiry_FloorsAtTwoMinutes_WhenExpiryIsInThePast()
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
