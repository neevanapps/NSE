namespace NiftySignal.VolumeBarData;

/// <summary>
/// Frozen V1 projection chosen from the NiftyResearcher 09:30 leave-one-session-out study.
/// It predicts full-day NIFTY futures traded volume from traded volume observed from 09:15 to 09:30,
/// then converts that prediction into a lot-aligned base event-bar threshold.
///
/// Research sample: 13 discovery sessions ending 2026-09-25.
/// Validation result: linear LOOCV reduced bars/day standard deviation from 72.44 (fixed 13k)
/// to 52.92, materially better than the median-opening-fraction estimator (80.23).
///
/// This class is intentionally tiny and deterministic so the live observer uses exactly the
/// same frozen rule. It is NOT self-updating and does not recalibrate intraday.
/// </summary>
public static class OpeningVolumeProjectionV1
{
    public const double Intercept = 1_228_063.4608804993d;
    public const double Slope = 4.699614102486805d;
    public const int TargetBarsPerDay = 160;
    public const int RoundingLots = 50;

    public static double PredictFullDayVolume(long openingVolume)
    {
        if (openingVolume < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openingVolume));
        }

        return Math.Max(1d, Intercept + Slope * openingVolume);
    }

    public static long SelectBaseBarVolume(long openingVolume, int lotSize)
    {
        if (lotSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lotSize));
        }

        var predicted = PredictFullDayVolume(openingVolume);
        var raw = predicted / TargetBarsPerDay;
        return RoundToLotBlock(raw, lotSize, RoundingLots);
    }

    public static long RoundToLotBlock(double rawBarVolume, int lotSize, int roundingLots)
    {
        if (rawBarVolume <= 0) throw new ArgumentOutOfRangeException(nameof(rawBarVolume));
        if (lotSize <= 0) throw new ArgumentOutOfRangeException(nameof(lotSize));
        if (roundingLots <= 0) throw new ArgumentOutOfRangeException(nameof(roundingLots));

        var block = checked((long)lotSize * roundingLots);
        var blocks = Math.Max(1L, (long)Math.Round(rawBarVolume / block, MidpointRounding.AwayFromZero));
        return checked(blocks * block);
    }
}
