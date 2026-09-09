namespace NiftySignal.Scoring;

/// <summary>
/// The appsettings-bindable shape of <see cref="ScoreWeights"/> (2026-09-09, external review --
/// see <see cref="Rules.RulesetConfigOptions"/>'s doc comment for why this is a separate mapped
/// DTO rather than binding <see cref="ScoreWeights"/> itself: positional records with default
/// parameter values don't bind reliably through the standard ConfigurationBinder). Every default
/// below matches <see cref="ScoreWeights.Default"/>'s current live values exactly -- kept in
/// sync deliberately, not automatically, so a change to one without the other is a visible diff,
/// not a silent divergence. <see cref="ScoreWeights.Default"/> itself stays as-is for the ~280
/// existing tests (and the read-only NiftySignal.ScoreReplay tool) that construct/compare
/// against it directly via `with`-expressions; only the live Host's actual composite-score
/// call site switches to reading the hot-reloaded, validated value instead.
/// </summary>
public sealed class ScoreWeightsOptions
{
    public const string SectionName = "ScoreWeights";

    public string Version { get; set; } = "live-2026-09-09";

    public double OiBuildupNet { get; set; } = 0.3125;
    public double Pcr { get; set; } = 0.19;
    public double FuturesBasis { get; set; } = 0.1425;
    public double IvSkew { get; set; } = 0.1425;
    public double PriceMomentum { get; set; }
    public double DepthImbalance { get; set; } = 0.1625;
    public double VixChange { get; set; } = 0.05;
    public double GammaExposure { get; set; }
    public double VolumePcr { get; set; }
    public double SpreadRatio { get; set; }
    public double VannaExposure { get; set; }
    public double CharmExposure { get; set; }
    public double CvdProxy { get; set; }
    public double StraddleRichness { get; set; }

    public ScoreWeights ToScoreWeights() => new(
        Version: Version,
        OiBuildupNet: OiBuildupNet,
        Pcr: Pcr,
        FuturesBasis: FuturesBasis,
        IvSkew: IvSkew,
        PriceMomentum: PriceMomentum,
        DepthImbalance: DepthImbalance,
        VixChange: VixChange,
        GammaExposure: GammaExposure,
        VolumePcr: VolumePcr,
        SpreadRatio: SpreadRatio,
        VannaExposure: VannaExposure,
        CharmExposure: CharmExposure,
        CvdProxy: CvdProxy,
        StraddleRichness: StraddleRichness);
}

/// <summary>
/// The validate-before-swap gate for weight reloads. Two rules, deliberately not more: no
/// individual weight may be negative (a negative weight silently inverts a component's
/// direction -- exactly the kind of typo this gate exists to catch), and the fourteen weights
/// must still sum to 1.0 within a small tolerance (matches <see cref="ScoreWeights.Total"/>'s
/// own existing test coverage) -- a reload that drifts off 1.0 quietly rescales every score
/// against the composite's own tanh(raw/k) squash without anyone deciding that on purpose. Does
/// NOT enforce the standing "diagnostic weights stay at 0 until several clean correlation-script
/// sessions" rule from <see cref="ScoreWeights.Default"/>'s own doc comment -- that's a research
/// discipline for a human to observe, not a structural invariant a validator can safely encode
/// (a future, evidence-backed nonzero diagnostic weight is a valid config, not a bug).
/// </summary>
public static class ScoreWeightsValidator
{
    const double TotalTolerance = 1e-6;

    public static IReadOnlyList<string> Validate(ScoreWeights weights)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(weights.Version))
        {
            errors.Add("Version must not be blank.");
        }

        foreach (var (name, value) in Components(weights))
        {
            if (value < 0)
            {
                errors.Add($"{name} must not be negative (was {value}).");
            }
        }

        if (Math.Abs(weights.Total - 1.0) > TotalTolerance)
        {
            errors.Add($"Weights must sum to 1.0 (summed to {weights.Total}).");
        }

        return errors;
    }

    static IEnumerable<(string Name, double Value)> Components(ScoreWeights w)
    {
        yield return (nameof(w.OiBuildupNet), w.OiBuildupNet);
        yield return (nameof(w.Pcr), w.Pcr);
        yield return (nameof(w.FuturesBasis), w.FuturesBasis);
        yield return (nameof(w.IvSkew), w.IvSkew);
        yield return (nameof(w.PriceMomentum), w.PriceMomentum);
        yield return (nameof(w.DepthImbalance), w.DepthImbalance);
        yield return (nameof(w.VixChange), w.VixChange);
        yield return (nameof(w.GammaExposure), w.GammaExposure);
        yield return (nameof(w.VolumePcr), w.VolumePcr);
        yield return (nameof(w.SpreadRatio), w.SpreadRatio);
        yield return (nameof(w.VannaExposure), w.VannaExposure);
        yield return (nameof(w.CharmExposure), w.CharmExposure);
        yield return (nameof(w.CvdProxy), w.CvdProxy);
        yield return (nameof(w.StraddleRichness), w.StraddleRichness);
    }
}
