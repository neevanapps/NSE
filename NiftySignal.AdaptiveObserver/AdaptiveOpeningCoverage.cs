namespace NiftySignal.AdaptiveObserver;

public static class AdaptiveOpeningCoverage
{
    // A conservative collection-coverage check, not a claim that every exchange update was captured.
    // Require an observed futures tick in each minute of the 15-minute opening window.
    public static int CoveredMinutes(IEnumerable<CleanObserverTick> ticks, DateTimeOffset open, DateTimeOffset cutoff) =>
        ticks.Where(x => x.AvailableAt >= open && x.AvailableAt < cutoff)
            .Select(x => (int)(x.AvailableAt - open).TotalMinutes).Distinct().Count();

    public static long Median(IEnumerable<long> priorValidatedOpeningVolumes)
    {
        var values = priorValidatedOpeningVolumes.Where(x => x > 0).OrderBy(x => x).ToArray();
        if (values.Length == 0) throw new InvalidOperationException("No validated prior opening volume is available for median fallback.");
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle]
            : checked(values[middle - 1] + (values[middle] - values[middle - 1]) / 2);
    }
}
