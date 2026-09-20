namespace NiftySignal.VolumeBarData;

/// <summary>
/// One bar's live-computed <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c> reading -- Phase C of
/// docs/LIVE_PARITY_PLAN.md. Written by <see cref="TradingDaySession"/> (via
/// <c>NiftySignal.Host.LiveOptionsScoreEngine</c>) for every future <see cref="VolumeBarRow"/> once
/// its matching <see cref="OptionAtmBarRow"/>/<see cref="OptionDepthBarRow"/> (BandWidth=5)/
/// <see cref="OptionMaxPainBarRow"/> rows all exist -- same (AsOfDate, BarVolumeThreshold, BarIndex)
/// identity every other Phase-A table uses, so this joins back to all of them without ambiguity.
///
/// Deliberately stores BOTH the raw signed score in [-1,1] (<see cref="RawScore"/>) and the scaled
/// -100..100 value (<see cref="ScaledScore"/>) actually compared against <see cref="Percentile"/> and
/// the entry gate -- same duplication <c>TradeSimulator.cs</c> itself carries internally
/// (score/scaledScore), kept here rather than collapsed so a reader never has to re-derive one from
/// the other or guess which scale a downstream consumer expects.
/// </summary>
public sealed class LiveOptionsScoreRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    /// <summary>Matches the corresponding <see cref="VolumeBarRow.BarIndex"/> at the same threshold.</summary>
    public required int BarIndex { get; set; }

    public required long BarVolumeThreshold { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    /// <summary>The active leg this bar fell in (Open/Mid/Close, per <see cref="NiftySignal.Scoring.OptionsThreeWayScoreCalculator"/>'s own session-time switch) -- diagnostic only, not used by any downstream decision.</summary>
    public required string SessionLeg { get; set; }

    /// <summary>Raw signed score in [-1,1] from <see cref="NiftySignal.Scoring.OptionsThreeWayScoreCalculator.ComputeScore"/>. Null if this bar's active leg had no usable reading (e.g. no depth-bearing tick, or no prior IV to diff against).</summary>
    public double? RawScore { get; set; }

    /// <summary>100 x <see cref="RawScore"/> -- the same -100..100 scale <c>TradeSimulator.cs</c>'s own <c>scaledScore</c> uses for the entry/exit comparison.</summary>
    public double? ScaledScore { get; set; }

    /// <summary>
    /// <see cref="ScaledScore"/>'s own entry-percentile reading, 0-100. For this metric this is
    /// simply |ScaledScore| (already percentile-shaped by <see cref="NiftySignal.Features.SignedRank"/>
    /// construction -- verified against <c>TradeSimulator.EntryPercentile</c>'s own dispatch, which
    /// returns the magnitude directly for every metric except TrendReversion/Composite/OptionsScoreBlend/
    /// the two FinalScore blends, none of which this metric is). No second
    /// <see cref="NiftySignal.Features.SessionRankTracker"/> layer is needed or used here.
    /// </summary>
    public double? Percentile { get; set; }

    /// <summary>Max Pain confirmation gate's own signed, session-rank-normalized score this bar (<see cref="NiftySignal.Scoring.MaxPainConfirmationGate.ComputeScore"/>). Null if no Max Pain reading exists yet today.</summary>
    public double? MaxPainConfirmScore { get; set; }

    /// <summary>True only when <see cref="MaxPainConfirmScore"/> exists and its sign agrees with <see cref="ScaledScore"/>'s -- the same gate an entry additionally requires (<see cref="NiftySignal.Scoring.MaxPainConfirmationGate.Passes"/>). Stored per-bar (not just at entry time) so the gate's own hit rate is inspectable independent of whether an entry happened to be eligible that bar.</summary>
    public bool MaxPainConfirmPasses { get; set; }
}
