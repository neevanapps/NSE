namespace NiftySignal.Features;

/// <summary>
/// Plan section 5.2's four quadrants, plus Neutral for the case the plan's table doesn't
/// cover: either price or OI didn't actually move. Forcing one of the four directional
/// labels onto a zero-change input would fabricate a signal that isn't there.
/// </summary>
public enum OiBuildupClassification
{
    Neutral,
    LongBuildup,
    ShortBuildup,
    LongUnwinding,
    ShortCovering,
}

/// <summary>Table-driven classification per plan section 5.2. Applied per strike per side.</summary>
public static class OiBuildupClassifier
{
    public static OiBuildupClassification Classify(decimal priceChange, decimal openInterestChange)
    {
        if (priceChange == 0 || openInterestChange == 0)
        {
            return OiBuildupClassification.Neutral;
        }

        return (priceChange > 0, openInterestChange > 0) switch
        {
            (true, true) => OiBuildupClassification.LongBuildup,     // price up, OI up
            (false, true) => OiBuildupClassification.ShortBuildup,   // price down, OI up
            (false, false) => OiBuildupClassification.LongUnwinding, // price down, OI down
            (true, false) => OiBuildupClassification.ShortCovering,  // price up, OI down
        };
    }
}
