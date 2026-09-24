namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, Pattern A selectivity/frequency CALIBRATION (descriptive, not a trading strategy --
/// see docs/VolumeCandle_0DTE_Findings.md's "Pattern A Selectivity Calibration" section). The
/// research question this answers is deliberately different from every prior
/// "vc0dte-relationship-a-*-crossover*" command: not "does one more filter improve the frozen
/// Pattern A trade simulation," but "does Pattern A have ANY natural strength dimension at all --
/// one whose top-percentile subset fires less often AND shows materially stronger forward
/// information, without inventing a new indicator family or optimizing thresholds by eye."
///
/// Every candidate variable below is built ENTIRELY from quantities the frozen
/// <see cref="RelationshipObservation"/>/<see cref="UnderlyingOptionRelationshipSummary"/>/
/// <see cref="PriceCrossoverEngine"/> machinery already produces -- no new bar construction, no
/// new event clock, no new smoothing formula. Each is computed strictly from information at or
/// before the signal event itself (eventId-1 -> eventId for the one-event-bar changes, the
/// already-frozen (5,20) crossover trace for <see cref="StrengthVariable.CrossoverDistance"/>) --
/// the same no-look-ahead discipline every other calculator in this project already follows.
///
/// Two candidates the user's own spec explicitly named are deliberately NOT included here, with
/// the reasoning stated rather than silently dropped (spec's own instruction: "if no such region
/// exists, say so" applies equally to "if a listed candidate isn't usable, say so"):
///  - "Divergence maturity" (<see cref="CrossoverResolutionDiagnostics.CountConsecutiveSameSign"/>)
///    has NO natural single "high=stronger" direction to rank by -- the already-completed
///    divergence-maturity experiment found a mature/persisting divergence state ("Confirms")
///    produces WORSE, not better, trades than a fresh one. A monotonic top-X% ranking would
///    silently pick a direction by convention (descending = "most mature"), which is exactly the
///    kind of unexamined sign assumption the working agreement's quant principles rule out. It
///    stays a separately-investigated diagnostic (already covered by its own command), and is
///    still reported descriptively (mean state age within each selected group) so a reader can see
///    whether a candidate's calibration is quietly reproducing the maturity effect.
///  - The futures' OWN trend/crossover context (<see cref="RelationshipObservation.FuturesCrossoverState"/>,
///    fast=3/slow=10) describes the underlying's regime, not a property of the Pattern A divergence
///    itself -- folding it in here would be a regime filter smuggled into a single-pattern
///    calibration, which the working agreement's "no gating during single-metric evaluation"
///    principle rules out even outside the strict single-metric-trial context it was written for.
///    A regime signal gets its own docs/SCORE_CANDIDATES.md entry and evaluation cycle, not this one.
/// </summary>
public static class PatternASelectivityCalibration
{
    public enum StrengthVariable { UnderlyingMoveMagnitude, CeReactionMagnitude, PeReactionMagnitude, DivergenceMagnitude, CrossoverDistance }

    public static readonly StrengthVariable[] AllVariables =
    [
        StrengthVariable.UnderlyingMoveMagnitude, StrengthVariable.CeReactionMagnitude, StrengthVariable.PeReactionMagnitude,
        StrengthVariable.DivergenceMagnitude, StrengthVariable.CrossoverDistance,
    ];

    /// <summary>The predefined percentile levels the spec fixes in advance -- no arbitrary numeric threshold search. 1.00 ("0%/all observations") is the unfiltered baseline.</summary>
    public static readonly decimal[] PercentileLevels = [1.00m, 0.50m, 0.30m, 0.20m, 0.15m, 0.10m, 0.05m];

    public static string LevelLabel(decimal level) => level == 1.00m ? "All (0% filtered)" : $"Top {level * 100:F0}%";

    /// <summary>
    /// One candidate's no-look-ahead value AT <paramref name="eventId"/>, or null if it cannot be
    /// computed there (missing/stale/transitioned option data, or the (5,20) crossover trace still
    /// warming up) -- never fabricated. The SAME definition is used both to rank the Pattern A
    /// EPISODE population (calibration) and, applied to any qualifying event's own value against a
    /// fixed numeric cutoff, as a live-evaluable trade-simulator entry gate (diagnostic-only trade
    /// mapping) -- one definition, two uses, so the trade-simulator mapping isn't a different rule
    /// in disguise.
    /// </summary>
    public static decimal? ComputeValue(StrengthVariable variable, IReadOnlyList<RelationshipObservation> rows, int eventId, IReadOnlyList<PriceCrossoverEngine.Step?> relSpreadTrace)
    {
        if (eventId <= 0 || eventId >= rows.Count)
        {
            return null;
        }

        switch (variable)
        {
            case StrengthVariable.UnderlyingMoveMagnitude:
                var futuresPct = UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, eventId - 1, 1).PercentChange;
                return futuresPct is null ? null : Math.Abs(futuresPct.Value);

            case StrengthVariable.CeReactionMagnitude:
                var cePct = UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, eventId - 1, 1).PercentChange;
                return cePct is null ? null : Math.Abs(cePct.Value);

            case StrengthVariable.PeReactionMagnitude:
                var pePct = UnderlyingOptionRelationshipSummary.ComputePeChange(rows, eventId - 1, 1).PercentChange;
                return pePct is null ? null : Math.Abs(pePct.Value);

            case StrengthVariable.DivergenceMagnitude:
                // Collapses the spec's own "CE-vs-PE relative return" and "divergence magnitude"
                // bullets into ONE variable: at a Pattern A event, CE% is negative and PE% is
                // positive by construction, so their signed difference is already (deterministically)
                // one-signed -- the only new information a magnitude ranking can add is |CE%-PE%|
                // itself, computed here directly (not read from a separately-stored RelSpread series)
                // so this file has no hidden dependency on another command's local variable.
                var ceP = UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, eventId - 1, 1).PercentChange;
                var peP = UnderlyingOptionRelationshipSummary.ComputePeChange(rows, eventId - 1, 1).PercentChange;
                return ceP is null || peP is null ? null : Math.Abs(ceP.Value - peP.Value);

            case StrengthVariable.CrossoverDistance:
                // The already-frozen (5,20) CE/PE relative-return crossover's own MA gap magnitude
                // -- reuses PriceCrossoverEngine completely unmodified, no new fast/slow combo.
                if (eventId >= relSpreadTrace.Count)
                {
                    return null;
                }
                var step = relSpreadTrace[eventId];
                return step is { FastMa: { } fastMa, SlowMa: { } slowMa } ? (decimal)Math.Abs(fastMa - slowMa) : null;

            default:
                throw new ArgumentOutOfRangeException(nameof(variable), variable, null);
        }
    }

    /// <summary>
    /// The VALUE cutoff such that at least <paramref name="topFraction"/> of <paramref name="values"/>
    /// are &gt;= it -- nearest-rank on the DESCENDING order, same convention every percentile
    /// calculator in this project already uses (<see cref="ForensicValidationAnalysis.ComputePercentiles"/>,
    /// <see cref="ForwardValidationAnalysis.ComputeTerciles"/>), just read from the top instead of
    /// the bottom. topFraction=1.00 returns the population minimum (every observation qualifies).
    /// A tie at the cutoff can admit slightly more than topFraction*N observations -- never fewer,
    /// never resolved by an arbitrary tie-break.
    /// </summary>
    public static decimal PercentileThreshold(IReadOnlyList<decimal> values, decimal topFraction)
    {
        if (values.Count == 0)
        {
            return 0m;
        }
        var sortedDescending = values.OrderDescending().ToList();
        var keepCount = Math.Clamp((int)Math.Ceiling(topFraction * sortedDescending.Count), 1, sortedDescending.Count);
        return sortedDescending[keepCount - 1];
    }
}
