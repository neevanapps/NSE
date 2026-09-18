namespace NiftySignal.VolumeBarData;

/// <summary>
/// Two candidate "pinning strike" reference points, as of one future volume-bar's close -- 2026-09-18,
/// Phase 1 metric 5, the last of the plan's 5 core options metrics. A point-in-time snapshot (a
/// pinning strike is a level, same shape as <see cref="OptionAtmBarRow"/>/<see cref="OptionSkew25DeltaBarRow"/>,
/// not accumulated across the bar's window).
///
/// Deliberately spans the FULL listed chain, not the ATM±1/ATM±10 windows metrics 2-4 use: both
/// max pain and "highest OI" are magnitude-driven concepts by construction -- a deep OTM strike
/// with outsized OI can still set either one, so windowing to a band near the current price would
/// silently exclude the strikes that matter most for this specific question.
/// </summary>
public sealed class OptionMaxPainBarRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    public required int BarIndex { get; set; }

    public required long BarVolumeThreshold { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    /// <summary>The strike that minimizes total option-buyer payout if the underlying settled there at expiry -- <c>argmin_K [ Σ CallOi(s)·max(K−s,0) + Σ PutOi(s)·max(s−K,0) ]</c> over every strike K in the full chain, using each strike's own OI-at-close. Null only if the chain had no usable OI anywhere yet (very start of day).</summary>
    public decimal? MaxPainStrike { get; set; }

    /// <summary>The single strike with the largest COMBINED (call + put) OI-at-close across the full chain -- the cheaper, more literal "highest OI strike" proxy some traders use instead of computing true max pain.</summary>
    public decimal? HighestOiStrike { get; set; }
}
