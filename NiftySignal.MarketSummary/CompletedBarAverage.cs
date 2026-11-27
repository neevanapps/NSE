namespace NiftySignal.MarketSummary;

/// <summary>
/// The average completed-bar price of ONE exact contract over the latest <c>window</c> consecutive completed bars ending at the target bar. Valid only when every one of those bars has a finite positive price
/// for that contract: no bridging, no zero-fill, no other strike. The Option Price Averages grid and the Avg5 entry condition share this definition.
/// </summary>
public static class CompletedBarAverage
{
    /// <param name="priceOf">The contract's completed-bar price for a bar sequence, null when that bar has no row for it.</param>
    public static (double? Value, int Observations, int? MissingBar, bool Invalid) Compute(Func<int, (bool Present, double? Price)> priceOf, int targetBarSeq, int window)
    {
        double sum = 0d;
        var n = 0;
        for (var seq = targetBarSeq - window + 1; seq <= targetBarSeq; seq++)
        {
            if (seq < 1) return (null, n, null, false);
            var (present, price) = priceOf(seq);
            if (!present) return (null, n, seq, false);
            if (price is not { } v || !double.IsFinite(v) || v <= 0d) return (null, n, seq, true);
            sum += v;
            n++;
        }

        return (sum / n, n, null, false);
    }
}
