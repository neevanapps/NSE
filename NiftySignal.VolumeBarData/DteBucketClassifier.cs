namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, DTE-expansion experiment. Groups a raw DTE (days-to-expiry) value into a bucket
/// label. The bucket boundaries below reflect the ACTUAL calendar structure discovered by
/// <c>vc-dte-availability-scan</c> for this dataset (NIFTY weekly expiry falls on Tuesdays only,
/// with real tick data present roughly Wed-Fri/Mon-Tue each week) -- they are NOT fit to any
/// result. On any given trading day the front-week chain's DTE is one of {0,1,4,5,6} (no
/// Saturday/Sunday trading, so DTE never lands on 2 or 3) and the back-week (next Tuesday's)
/// chain's DTE is exactly the front value + 7, i.e. one of {7,8,11,12,13}. DTE values outside
/// these five groups are classified "Other" and excluded from the experiment rather than forced
/// into a bucket, per the task's own "do not force these buckets" instruction.
/// </summary>
public static class DteBucketClassifier
{
    public const string Other = "Other";

    public static string Classify(int dte) => dte switch
    {
        0 => "0",
        1 => "1",
        >= 4 and <= 6 => "4-6",
        >= 7 and <= 8 => "7-8",
        >= 11 and <= 13 => "11-13",
        _ => Other,
    };
}
