namespace NiftySignal.Scoring;

/// <summary>
/// One component's contribution to the composite score. Persisted alongside the final
/// score (plan section 6: "without it you cannot diagnose why a signal fired") --
/// ZScore/WeightedContribution are null when that component wasn't warmed up yet, which is
/// itself diagnostically useful (shows which metric was missing, not just that the score
/// was withheld).
/// </summary>
public sealed record ScoreComponentBreakdown(
    string Name,
    double Weight,
    double? ZScore,
    double? WeightedContribution);
