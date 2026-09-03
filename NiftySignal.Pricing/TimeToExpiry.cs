namespace NiftySignal.Pricing;

/// <summary>
/// Calendar-days/365 to expiry, computed to 15:30 IST on the expiry date and floored at
/// one hour (plan section 1.3/4.1) -- without the floor, the IV solver becomes numerically
/// unstable as T approaches zero (vega collapses toward zero, Newton-Raphson divides by
/// near-nothing) right on expiry afternoon, which is exactly when accurate IV matters most.
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

        return remaining.TotalDays / 365.0;
    }
}
