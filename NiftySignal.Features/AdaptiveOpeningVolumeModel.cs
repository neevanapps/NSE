namespace NiftySignal.Features;

/// <summary>
/// Observation-only V1 forecast fitted on the 13 unsealed NIFTY-futures sessions
/// 2026-09-08..2026-09-25. The model was selected only after leave-one-session-out
/// validation; it is not a trading rule.
///
/// FullDayVolume = Intercept + Slope * VolumeTradedBefore09:30IST.
///
/// The forecast is frozen once at 09:30 IST. The resulting exact-volume bar size is
/// rounded to the nearest 50 exchange lots and then kept fixed for the rest of the session.
/// </summary>
public static class AdaptiveOpeningVolumeModel
{
    public const double Intercept = 1_228_063.4608804994;
    public const double Slope = 4.699614102486805;
    public const int TargetBarsPerDay = 160;
    public const int RoundingLots = 50;

    public static AdaptiveVolumeForecast Forecast(long openingVolume, int lotSize)
    {
        if (openingVolume < 0) throw new ArgumentOutOfRangeException(nameof(openingVolume));
        if (lotSize <= 0) throw new ArgumentOutOfRangeException(nameof(lotSize));

        var predicted = Math.Max(1d, Intercept + Slope * openingVolume);
        var rawBarVolume = predicted / TargetBarsPerDay;
        var block = checked((long)lotSize * RoundingLots);
        var blocks = Math.Max(1L, (long)Math.Round(rawBarVolume / block, MidpointRounding.AwayFromZero));
        var baseBarVolume = checked(blocks * block);

        return new AdaptiveVolumeForecast(
            openingVolume,
            predicted,
            rawBarVolume,
            baseBarVolume,
            checked(baseBarVolume * 10L),
            lotSize);
    }
}

public sealed record AdaptiveVolumeForecast(
    long OpeningVolume,
    double ExpectedDayVolume,
    double RawBaseBarVolume,
    long BaseBarVolume,
    long Rolling10BarVolume,
    int LotSize);
