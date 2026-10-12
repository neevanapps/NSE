// PORT of research/bar-size-v2 e402baf NiftySignal.AdaptiveObserver/OpeningVolumeProjectionV2.cs, byte-for-byte except the namespace and this header (the research-only
// ApplyScale experiment of 97b0814 is deliberately NOT ported). The M1 rule is the one the NPT1 strategies were researched on; adaptive-v1 (OpeningVolumeProjectionV1) is untouched.
using NiftySignal.AdaptiveObserver;

namespace NiftySignal.Npt1M1;

/// <summary>One prior session's volumes as known at the end of that session (futures trade volume with the observer's own volume definition).</summary>
public sealed record PriorSession(DateOnly Date, long OpeningVolume, long WindowVolume);

/// <summary>The base bar volume chosen for one date together with every input that produced it (provenance for the export).</summary>
public sealed record BarSizeDecision(
    string Method,
    double Beta,
    string MethodStatus,
    bool OpeningFallback,
    long OpeningVolume,
    IReadOnlyList<DateOnly> BaselineSessions,
    double? Baseline,
    double? OpenRef,
    double? RelOpen,
    double? Adj,
    bool ClampHit,
    double? RawBar,
    long BaseBar,
    string FallbackSource);

/// <summary>
/// Leak-free base-bar-volume rule, fixed at 09:30 from PRIOR sessions only (never the trade date itself):
///   baseline = median(window volume 09:30-15:00 over the last min(20, available) prior sessions),
///   open_ref = median(opening volume 09:15-09:30 over the same sessions),
///   adj      = clamp((opening volume of D / open_ref) ^ beta, 0.75, 1.33),
///   bar      = round(baseline / 110 * adj / lot) * lot (at least one lot).
/// All constants are pre-registered and not tuned. <see cref="OpeningVolumeProjectionV1"/> is untouched.
/// </summary>
public static class OpeningVolumeProjectionV2
{
    public const int TargetWindowBars = 110;
    public const int Lookback = 20;
    public const int MinPrior = 5;
    public const double ClampLow = 0.75d;
    public const double ClampHigh = 1.33d;
    public const int RoundingLots = 1;
    public const double BetaM0 = 0.0d;
    public const double BetaM1 = 0.5d;

    public const string StatusOk = "ok";
    public const string StatusInsufficientHistory = "insufficient_history";

    public static string MethodName(double beta) => beta == BetaM0 ? "M0" : beta == BetaM1 ? "M1" : throw new ArgumentOutOfRangeException(nameof(beta), "Only the pre-registered betas 0.0 (M0) and 0.5 (M1) exist.");

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("A median needs at least one value.", nameof(values));
        var s = values.OrderBy(x => x).ToArray();
        var m = s.Length / 2;
        return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2d;
    }

    /// <summary>
    /// Pure rule. Only sessions strictly before <paramref name="day"/> are eligible, whatever the caller passes in. With fewer than <see cref="MinPrior"/> of them the
    /// status is <see cref="StatusInsufficientHistory"/> and BaseBar is 0 (the caller applies the documented V1 fallback, see <see cref="Resolve"/>).
    /// When the opening is not fully covered the opening volume of D is replaced by open_ref (the median of the prior openings) and OpeningFallback is set.
    /// </summary>
    public static BarSizeDecision Compute(DateOnly day, long openingVolume, bool openingFullyCovered, IEnumerable<PriorSession> sessions, double beta, int lotSize)
    {
        if (lotSize <= 0) throw new ArgumentOutOfRangeException(nameof(lotSize));
        var method = MethodName(beta);
        var prior = sessions.Where(x => x.Date < day && x.OpeningVolume > 0 && x.WindowVolume > 0).OrderByDescending(x => x.Date).Take(Lookback).OrderBy(x => x.Date).ToList();
        if (prior.Count < MinPrior)
        {
            return new BarSizeDecision(method, beta, StatusInsufficientHistory, false, openingVolume, prior.Select(x => x.Date).ToList(), null, null, null, null, false, null, 0, "");
        }

        var baseline = Median(prior.Select(x => (double)x.WindowVolume).ToList());
        var openRef = Median(prior.Select(x => (double)x.OpeningVolume).ToList());
        var fallback = !openingFullyCovered;
        var effectiveOpening = fallback ? openRef : openingVolume;
        if (effectiveOpening <= 0) throw new ArgumentOutOfRangeException(nameof(openingVolume), "A fully covered opening needs a positive opening volume.");
        var rel = effectiveOpening / openRef;
        var unclamped = Math.Pow(rel, beta);
        var adj = Math.Min(ClampHigh, Math.Max(ClampLow, unclamped));
        var raw = baseline / TargetWindowBars * adj;
        var bar = Math.Max(1L, (long)Math.Round(raw / lotSize, MidpointRounding.AwayFromZero)) * lotSize;
        return new BarSizeDecision(method, beta, StatusOk, fallback, (long)effectiveOpening, prior.Select(x => x.Date).ToList(), baseline, openRef, rel, adj, adj != unclamped, raw, bar, "");
    }

    /// <summary>
    /// <see cref="Compute"/> plus the documented fallback: with insufficient history the V1 out-of-fold value is used for dates in the discovery table,
    /// otherwise the live V1 value. The fallback is recorded in FallbackSource and the status stays insufficient_history.
    /// </summary>
    public static BarSizeDecision Resolve(DateOnly day, long openingVolume, bool openingFullyCovered, IEnumerable<PriorSession> sessions, double beta, int lotSize)
    {
        var d = Compute(day, openingVolume, openingFullyCovered, sessions, beta, lotSize);
        if (d.MethodStatus == StatusOk)
        {
            return d;
        }

        if (OpeningVolumeProjectionV1.DiscoveryOutOfFold.TryGetValue(day, out var oof))
        {
            return d with { BaseBar = oof.AdaptiveBarVolume, FallbackSource = "V1_out_of_fold" };
        }

        return d with { BaseBar = OpeningVolumeProjectionV1.SelectBaseBarVolume(openingVolume, lotSize), FallbackSource = "V1_live" };
    }
}
