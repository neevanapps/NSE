namespace NiftySignal.AdaptiveObserver;

/// <summary>Frozen V1 model selected by the 13-session out-of-fold 09:30 study.</summary>
public static class OpeningVolumeProjectionV1
{
    public const string ModelVersion = "adaptive-v1";
    public const double Intercept = 1_228_063.4608804993d;
    public const double Slope = 4.699614102486805d;
    public const int TargetBarsPerDay = 160;
    public const int RoundingLots = 50;

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
