namespace NiftySignal.AdaptiveObserver;

/// <summary>Frozen V1 model selected by the 13-session out-of-fold 09:30 study.</summary>
public sealed record AdaptiveDiscoverySeedSpec(
    long OpeningVolume0930,
    double TrainingIntercept,
    double TrainingSlope,
    double PredictedFullDayVolume,
    long AdaptiveBarVolume,
    int ExpectedCompleteBars);

public static class OpeningVolumeProjectionV1
{
    public const string ModelVersion = "adaptive-v1";
    public const double Intercept = 1_228_063.4608804993d;
    public const double Slope = 4.699614102486805d;
    public const int TargetBarsPerDay = 160;
    public const int RoundingLots = 50;

    /// <summary>
    /// Exact leave-one-session-out thresholds used by the adaptive discovery simulation.
    /// Historical bootstrap MUST use these for these dates; applying the final full-sample live
    /// model retrospectively would alter the discovery rolling-state distribution.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, AdaptiveDiscoverySeedSpec> DiscoveryOutOfFold { get; } =
        new Dictionary<DateOnly, AdaptiveDiscoverySeedSpec>
        {
            [new(2026, 9, 8)]  = new(303940, 1254806d, 4.780363d, 2707749.25d, 16250, 127),
            [new(2026, 9, 9)]  = new(481715, 1168568d, 4.994043d, 3574273.45d, 22750, 144),
            [new(2026, 9, 10)] = new(234845, 1329691d, 4.539421d, 2395751.32d, 16250, 101),
            [new(2026, 9, 11)] = new(438425, 1205558d, 4.823682d, 3320380.89d, 19500, 162),
            [new(2026, 9, 15)] = new(421265, 1277482d, 4.408453d, 3134608.82d, 19500, 181),
            [new(2026, 9, 16)] = new(344630, 1214517d, 5.009097d, 2940802.45d, 19500, 102),
            [new(2026, 9, 17)] = new(160030, 1265061d, 4.609283d, 2002684.19d, 13000, 142),
            [new(2026, 9, 18)] = new(132405, 1362200d, 4.351004d, 1938295.16d, 13000, 112),
            [new(2026, 9, 21)] = new(140140, 1333410d, 4.429993d, 1954229.43d, 13000, 120),
            [new(2026, 9, 22)] = new(159965, 1008549d, 5.235670d, 1846072.55d, 13000, 212),
            [new(2026, 9, 23)] = new(195650, 1312846d, 4.517661d, 2196726.25d, 13000, 134),
            [new(2026, 9, 24)] = new(428025, 1390600d, 3.768939d, 3003800.22d, 19500, 218),
            [new(2026, 9, 25)] = new(179075, 846285.3d, 5.577886d, 1845145.26d, 13000, 279),
        };

    public static double PredictFullDayVolume(long openingVolume)
    {
        if (openingVolume < 0) throw new ArgumentOutOfRangeException(nameof(openingVolume));
        return Math.Max(1d, Intercept + Slope * openingVolume);
    }

    public static long SelectBaseBarVolume(long openingVolume, int lotSize)
    {
        if (lotSize <= 0) throw new ArgumentOutOfRangeException(nameof(lotSize));
        return RoundToLotBlock(PredictFullDayVolume(openingVolume) / TargetBarsPerDay, lotSize, RoundingLots);
    }

    public static long RoundToLotBlock(double value, int lotSize, int roundingLots)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (lotSize <= 0) throw new ArgumentOutOfRangeException(nameof(lotSize));
        if (roundingLots <= 0) throw new ArgumentOutOfRangeException(nameof(roundingLots));

        var block = checked((long)lotSize * roundingLots);
        var blocks = Math.Max(1L, (long)Math.Round(value / block, MidpointRounding.AwayFromZero));
        return checked(block * blocks);
    }
}
