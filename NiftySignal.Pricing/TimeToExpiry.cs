namespace NiftySignal.Pricing;

/// <summary>
/// Calendar-days/365 to expiry. Computed to 15:30 IST on the expiry date and floored at two
/// minutes -- without the floor, the IV solver becomes numerically unstable as T approaches
/// zero (vega collapses toward zero, Newton-Raphson divides by near-nothing) right on expiry
/// afternoon, which is exactly when accurate IV matters most.
///
/// Audit finding F6 (2026-09-08) briefly replaced this with a trading-day (weekday-count)
/// version, on the theory that billing Saturday/Sunday as risk time overstates T. Reverted
/// (2026-09-09 external review): index options are European and price on calendar time --
/// weekend theta decay is real, every vendor IV comparison assumes calendar/365, and a
/// weekday-count loop with no NSE holiday calendar would silently undercharge any week that
/// contains a market holiday. The actual bug worth fixing was the <see cref="Minimum"/> floor
/// (previously 1 hour, audit finding F20 -- kept every Greek/IV/theoretical price pretending
/// T=1h for the entire final hour before expiry, exactly the highest-gamma stretch); tightened
/// to 2 minutes here instead, resolving F20 directly rather than via a day-counting change. If
/// a trading-day T is ever wanted, it needs its own persisted diagnostic column compared
/// against this one for a real week before ever becoming the live value -- not a silent swap
/// of the only T in the process, which is what the reverted version did.
/// </summary>
public static class TimeToExpiry
{
    static readonly TimeOnly MarketClose = new(15, 30);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public static readonly TimeSpan Minimum = TimeSpan.FromMinutes(2);

    public static double YearsUntilExpiry(DateOnly expiryDate, DateTimeOffset asOf)
    {
        var expiryMoment = new DateTimeOffset(expiryDate.ToDateTime(MarketClose), IstOffset);
        var remaining = expiryMoment - asOf;

        if (remaining < Minimum)
        {
            remaining = Minimum;
        }

        return remaining.TotalDays / 365.0;
    }
}
