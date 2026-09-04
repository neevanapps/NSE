namespace NiftySignal.Domain.Entities;

/// <summary>
/// One cadence tick's worth of feature/score output, combining plan section 3.2's separate
/// "features" (raw pre-z-score values) and "scores" (weighted z-score components +
/// composite) tables into one row -- a scoped-down v1 given today's time budget; splitting
/// them apart later is cheap if that separation proves valuable, and nothing downstream
/// depends on them being separate yet.
/// </summary>
public sealed class ScoreSnapshot
{
    public long Id { get; set; }

    public required DateTimeOffset ComputedAt { get; set; }

    public double? OiBuildupNetRaw { get; set; }
    public double? PcrRaw { get; set; }
    public double? FuturesBasisRaw { get; set; }
    public double? IvSkewRaw { get; set; }
    public double? PriceMomentumRaw { get; set; }
    public double? DepthImbalanceRaw { get; set; }

    public double? OiBuildupNetZ { get; set; }
    public double? PcrZ { get; set; }
    public double? FuturesBasisZ { get; set; }
    public double? IvSkewZ { get; set; }
    public double? PriceMomentumZ { get; set; }
    public double? DepthImbalanceZ { get; set; }

    public double? CompositeScore { get; set; }

    public bool IsWarmedUp { get; set; }

    public required string WeightSetVersion { get; set; }
}
