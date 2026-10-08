namespace NiftySignal.AdaptiveObserver;

/// <summary>One persisted residual reading (per bar, per variant). Unavailable bars are represented, not omitted.</summary>
public sealed record ResidualReadingPoint(
    int BarSeq,
    bool IsAvailable,
    double CeResidual,
    double PeResidual,
    double CeResidualPct,
    double PeResidualPct,
    double DirectionalResidualPct);

/// <summary>The fixed 09:30 premium baselines the residual model divided by: the center component for ATM, the sum of the side's components for ATM±2.</summary>
public sealed record ResidualBases(double CeBase, double PeBase);

/// <summary>Values derived for one bar. Every member is null when its inputs (or the adjacent previous bar) are unavailable.</summary>
public sealed record ResidualDerivedPoint(
    int BarSeq,
    double? CeResidualDeltaPct,
    double? PeResidualDeltaPct,
    double? AdjacentDirectionalResidualDelta,
    double? StraddleResidualPct,
    double? StraddleNormalizedDirectionalPct);

/// <summary>
/// Shared pure projection of the new residual metrics (08-Oct plan section 32.4). No database and no wall clock: it is used by the Dashboard,
/// by the commentary frame builder and by tests/replay, so all of them see the same numbers.
///
/// Delta semantics are ADJACENT-BAR: a delta exists only when the immediately preceding completed bar (BarSeq - 1) had an available residual for the
/// same variant. A reading is never compared with an earlier valid reading across an unavailable bar. This deliberately differs from the legacy
/// persisted <c>ResidualDelta</c> (which bridges gaps and is left untouched for restart compatibility).
/// </summary>
public static class AdaptiveResidualProjection
{
    public static IReadOnlyList<ResidualDerivedPoint> Derive(IEnumerable<ResidualReadingPoint> readings, ResidualBases bases)
    {
        var bySeq = readings.GroupBy(x => x.BarSeq).ToDictionary(g => g.Key, g => g.First());
        var result = new List<ResidualDerivedPoint>(bySeq.Count);
        foreach (var current in bySeq.Values.OrderBy(x => x.BarSeq))
        {
            if (!current.IsAvailable)
            {
                result.Add(new ResidualDerivedPoint(current.BarSeq, null, null, null, null, null));
                continue;
            }

            var hasPrevious = bySeq.TryGetValue(current.BarSeq - 1, out var previous) && previous.IsAvailable;
            double? ce = hasPrevious ? current.CeResidualPct - previous!.CeResidualPct : null;
            double? pe = hasPrevious ? current.PeResidualPct - previous!.PeResidualPct : null;
            // Defined as DirectionalResidualPct(t) - DirectionalResidualPct(t-1); computed as the CE delta minus the PE delta, which is the
            // identical quantity (Directional = CE% - PE%) and guarantees the stated identity exactly, without floating-point drift.
            double? directional = ce is { } c && pe is { } p ? c - p : null;

            // Straddle values need a positive common 09:30 CE+PE baseline (the model already requires each side's baseline to be positive).
            var baseline = bases.CeBase + bases.PeBase;
            var usable = double.IsFinite(baseline) && baseline > 0d;
            result.Add(new ResidualDerivedPoint(
                current.BarSeq,
                ce,
                pe,
                directional,
                usable ? 100d * (current.CeResidual + current.PeResidual) / baseline : null,
                usable ? 100d * (current.CeResidual - current.PeResidual) / baseline : null));
        }

        return result;
    }
}
