namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, Pattern-A-crossover incremental-information experiment (see
/// docs/VolumeCandle_0DTE_Findings.md's "Pattern A + Crossover" section). Reuses
/// <see cref="PriceCrossoverEngine"/> (already tested, already used inside the frozen
/// <see cref="UnderlyingOptionRelationshipRecorder"/> for CE/PE at fast=3/slow=10) completely
/// unmodified -- this file adds NO new crossover formula. Its only new logic is (a) aligning an
/// ALTERNATE event clock's own crossover-state sequence to the frozen relationship's signal
/// timestamp without look-ahead, and (b) a plain 3-way classification of whether a crossover
/// state confirms Pattern A's own expected direction.
/// </summary>
public static class CrossoverResolutionDiagnostics
{
    /// <summary>
    /// The last step in <paramref name="stepsInOrder"/> (chronological, by EndTimestamp) whose
    /// EndTimestamp is at or before <paramref name="targetTimestamp"/> -- i.e. "replay the
    /// alternate clock up to this moment, take its most recently completed state." Null if no
    /// step has completed yet by that time. This is the entire cross-clock alignment rule: it
    /// never looks at a step whose bar completed AFTER the target moment, so it cannot leak
    /// future information from the alternate clock into a signal on the frozen clock.
    /// </summary>
    public static PriceCrossoverEngine.Step? AlignStepAtOrBefore(
        IReadOnlyList<(DateTimeOffset EndTimestamp, PriceCrossoverEngine.Step Step)> stepsInOrder, DateTimeOffset targetTimestamp)
    {
        PriceCrossoverEngine.Step? result = null;
        foreach (var (endTimestamp, step) in stepsInOrder)
        {
            if (endTimestamp > targetTimestamp) { break; }
            result = step;
        }
        return result;
    }

    /// <summary>
    /// Pattern A expects a subsequent DOWNWARD underlying move -- a bearish crossover state
    /// (fast &lt; slow, i.e. DiffFraction &lt; 0) is what "confirms" that expectation.
    /// "DoesNotConfirm" covers both a bullish state and an exact-zero diff; "Unavailable" is the
    /// engine's own still-warming-up null, never fabricated.
    /// </summary>
    public static string ClassifyConfirmation(PriceCrossoverEngine.Step? step) => step?.DiffFraction switch
    {
        null => "Unavailable",
        < 0 => "Confirms",
        _ => "DoesNotConfirm",
    };

    /// <summary>"Fresh" is deliberately defined as the crossing having fired on the single most recently completed bar at/before the signal -- not an arbitrary lookback window.</summary>
    public static bool IsFreshCrossDown(PriceCrossoverEngine.Step? step) => step?.CrossedDown ?? false;

    /// <summary>
    /// 2026-09-24, CE/PE relative-crossover experiment. <see cref="PriceCrossoverEngine"/>'s own
    /// <c>DiffFraction</c> is (fastMa - slowMa) / slowMa -- a safe percentage gap for a PRICE series
    /// (always comfortably away from zero), but numerically unsafe for a zero-centered series like a
    /// CE-minus-PE return spread, whose slowMa can sit arbitrarily close to zero. This classifies
    /// purely from the SIGN of (fastMa - slowMa) -- never divides by slowMa -- so it stays stable for
    /// any series, at the cost of not supporting a magnitude threshold the way <see cref="ClassifyConfirmation"/>
    /// implicitly could. <paramref name="confirmsWhenFastBelowSlow"/> lets the caller state its own
    /// sign convention explicitly (Pattern A's CE/PE relative-return spread confirms when the recent
    /// average is BELOW the longer-run average; a differently-signed series would need the other
    /// convention) rather than this helper guessing one.
    /// </summary>
    public static string ClassifyConfirmationBySign(PriceCrossoverEngine.Step? step, bool confirmsWhenFastBelowSlow) => step switch
    {
        null => "Unavailable",
        { FastMa: null } or { SlowMa: null } => "Unavailable",
        var s when s.Value.FastMa < s.Value.SlowMa => confirmsWhenFastBelowSlow ? "Confirms" : "DoesNotConfirm",
        var s when s.Value.FastMa > s.Value.SlowMa => confirmsWhenFastBelowSlow ? "DoesNotConfirm" : "Confirms",
        _ => "DoesNotConfirm",
    };

    /// <summary>
    /// 2026-09-24, divergence-maturity diagnostic. Sign of (FastMa - SlowMa) as a plain -1/0/+1 --
    /// 0 for warming-up/unavailable OR an exact tie (never fabricated, never forced into a
    /// direction). Exposed publicly so <see cref="CountConsecutiveSameSign"/> and any caller
    /// needing the same-clock crossover STATE (not the ratio-based DiffFraction) share one
    /// definition.
    /// </summary>
    public static int StepSign(PriceCrossoverEngine.Step? step) =>
        step is { FastMa: { } f, SlowMa: { } s } ? Math.Sign(f - s) : 0;

    /// <summary>
    /// How many consecutive events, walking backward from <paramref name="eventId"/> (inclusive),
    /// share the SAME <see cref="StepSign"/> as the event at <paramref name="eventId"/> itself --
    /// i.e. how long the current crossover STATE has already persisted ("maturity"), not whether a
    /// crossing just fired. Returns 0 if the event itself has no defined sign (warming up or an
    /// exact tie) -- a state with no direction has no age to report, never approximated as 1.
    /// </summary>
    public static int CountConsecutiveSameSign(IReadOnlyList<PriceCrossoverEngine.Step?> stepsInOrder, int eventId)
    {
        if (eventId < 0 || eventId >= stepsInOrder.Count)
        {
            return 0;
        }
        var signAtEvent = StepSign(stepsInOrder[eventId]);
        if (signAtEvent == 0)
        {
            return 0;
        }
        var count = 0;
        for (var i = eventId; i >= 0; i--)
        {
            if (StepSign(stepsInOrder[i]) != signAtEvent)
            {
                break;
            }
            count++;
        }
        return count;
    }
}
