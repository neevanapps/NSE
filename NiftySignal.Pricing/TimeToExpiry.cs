namespace NiftySignal.Pricing;

/// <summary>
/// Trading-days/365 to expiry (2026-09-08, audit finding F6 -- was calendar-days/365, which
/// bills Saturday/Sunday as risk time the market never actually trades, overstating T on a
/// Friday-observed Tuesday expiry by roughly 40% and understating solved IV by a similar
/// fraction since IV scales roughly with 1/sqrt(T)). Computed to 15:30 IST on the expiry date
/// and floored at one hour (plan section 1.3/4.1) -- without the floor, the IV solver becomes
/// numerically unstable as T approaches zero (vega collapses toward zero, Newton-Raphson
/// divides by near-nothing) right on expiry afternoon, which is exactly when accurate IV
/// matters most.
/// </summary>
public static class TimeToExpiry
{
    static readonly TimeOnly MarketClose = new(15, 30);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public static readonly TimeSpan Minimum = TimeSpan.FromHours(1);

    public static double YearsUntilExpiry(DateOnly expiryDate, DateTimeOffset asOf)
    {
        var expiryMoment = new DateTimeOffset(expiryDate.ToDateTime(MarketClose), IstOffset);
        var remaining = expiryMoment - asOf;

        if (remaining < Minimum)
        {
            remaining = Minimum;
        }

        // asOf is always a trading-day cadence (the engine only runs during market hours on
        // weekdays) and expiryDate is always a weekday (NSE weekly expiry), so neither endpoint
        // of this range can itself be a Saturday/Sunday -- safe to count weekend days inclusive
        // of both bounds without a boundary-day partial-count correction.
        var weekendDays = CountWeekendDays(asOf.ToOffset(IstOffset).Date, expiryMoment.Date);
        var tradingDaysRemaining = Math.Max(Minimum.TotalDays, remaining.TotalDays - weekendDays);

        return tradingDaysRemaining / 365.0;
    }

    static int CountWeekendDays(DateTime fromDateInclusive, DateTime toDateInclusive)
    {
        var count = 0;
        for (var day = fromDateInclusive.Date; day <= toDateInclusive.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                count++;
            }
        }

        return count;
    }
}
