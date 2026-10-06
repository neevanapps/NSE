namespace NiftySignal.AdaptiveObserver;

/// <summary>Shared minimum gate for observations and any future paper/order consumer.</summary>
public static class AdaptiveReadinessPolicy
{
    public const int RequiredBars = 10;

    public static bool IsValid(long volume, long target, double open, double high, double low,
        double close, int updates, DateTimeOffset start, DateTimeOffset end) =>
        target > 0 && volume == target && updates > 0 && end >= start
        && double.IsFinite(open) && double.IsFinite(high) && double.IsFinite(low) && double.IsFinite(close)
        && low > 0 && high >= low && open >= low && open <= high && close >= low && close <= high;

    public static int ConsecutiveValidBars(IEnumerable<ExactAdaptiveBar> bars, long target, int throughSeq) =>
        CountValidSuffix(bars.Where(x => x.BarSeq <= throughSeq).Select(x => (x.BarSeq,
            x.IsComplete && IsValid(x.Volume, target, x.Open, x.High, x.Low, x.Close,
                x.TradeUpdates, x.StartAvailableAtUtc, x.EndAvailableAtUtc))), throughSeq);

    public static int CountValidSuffix(IEnumerable<(int Seq, bool Valid)> bars, int throughSeq)
    {
        var count = 0;
        foreach (var bar in bars.OrderByDescending(x => x.Seq).Take(RequiredBars))
        {
            if (!bar.Valid || bar.Seq != throughSeq - count) break;
            count++;
        }
        return count;
    }
}
