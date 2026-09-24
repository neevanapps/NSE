namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, forward validation of the Underlying-Spot-CE-PE relationship patterns (see
/// docs/VolumeCandle_0DTE_Findings.md's "Forward Validation of Underlying-CE-PE Relationship
/// Patterns" section). Pure, reusable calculators only -- ALL forward-change computation itself
/// is the EXISTING, unmodified <see cref="UnderlyingOptionRelationshipSummary.ComputeFuturesChange"/>/
/// <see cref="UnderlyingOptionRelationshipSummary.ComputeSpotChange"/>/
/// <see cref="UnderlyingOptionRelationshipSummary.ComputeCeChange"/>/
/// <see cref="UnderlyingOptionRelationshipSummary.ComputePeChange"/> (same-contract/missing/stale
/// rejection already built and tested there -- not re-implemented here). This file adds only the
/// NEW pieces those four functions don't already provide: descriptive metrics over a set of
/// changes, same/opposite-direction classification, and a deterministic bootstrap CI.
/// </summary>
public static class ForwardValidationAnalysis
{
    /// <summary>The four named relationship states (user's own section 5) -- each maps EXACTLY onto an existing, unmodified <see cref="RelationshipObservation.RelationshipCategory"/> string. No new classification logic; these are the same labels <see cref="UnderlyingOptionRelationshipRecorder"/> already produces.</summary>
    public const string State1_NormalBullishConfirmation = "UnderlyingUp_CEUp_PEDown";
    public const string State2_BullishDivergence_PatternA = "UnderlyingUp_CEDown_PEUp";
    public const string State3_NormalBearishConfirmation = "UnderlyingDown_CEDown_PEUp";
    public const string State4_BearishDivergence_PatternB = "UnderlyingDown_CEUp_PEDown";

    public static readonly string[] AllFourStates =
    [
        State1_NormalBullishConfirmation, State2_BullishDivergence_PatternA,
        State3_NormalBearishConfirmation, State4_BearishDivergence_PatternB,
    ];

    public sealed record ForwardMetrics(int N, decimal? Mean, decimal? Median, double PositivePct, double NegativePct, double ZeroPct, decimal? MeanAbsolute);

    /// <summary>Mean/median/positive-negative-zero percentage/mean-absolute over the non-null values only -- a null (unavailable) reading is excluded, never treated as zero (same discipline every calculator in this project already follows).</summary>
    public static ForwardMetrics ComputeForwardMetrics(IEnumerable<decimal?> values)
    {
        var real = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (real.Count == 0)
        {
            return new ForwardMetrics(0, null, null, 0, 0, 0, null);
        }

        var sorted = real.Order().ToList();
        var mid = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
        var positive = real.Count(v => v > 0);
        var negative = real.Count(v => v < 0);
        var zero = real.Count(v => v == 0);

        return new ForwardMetrics(
            real.Count, real.Average(), median,
            100.0 * positive / real.Count, 100.0 * negative / real.Count, 100.0 * zero / real.Count,
            real.Select(Math.Abs).Average());
    }

    /// <summary>
    /// Section 7's explicit classification -- based ENTIRELY on the existing exact-zero-vs-nonzero
    /// <see cref="RelationshipDirection"/> convention, no new threshold. "Flat" covers either the
    /// pre-event OR the forward reading landing on an exact-zero change; "Unavailable" covers a
    /// pre-event reading that was itself Unavailable, or a forward change that could not be
    /// computed (missing/stale/contract-transition -- already excluded upstream by the Compute*Change
    /// functions returning a null AbsoluteChange).
    /// </summary>
    public static string ClassifyForwardDirection(RelationshipDirection preDirection, decimal? forwardChange)
    {
        if (preDirection == RelationshipDirection.Unavailable || forwardChange is null)
        {
            return "Unavailable";
        }
        var forwardDirection = RelationshipDirectionExtensions.Classify(forwardChange);
        if (preDirection == RelationshipDirection.Flat || forwardDirection == RelationshipDirection.Flat)
        {
            return "Flat";
        }
        return preDirection == forwardDirection ? "SameDirection" : "OppositeDirection";
    }

    /// <summary>Terciles (bottom/middle/top third) of the given ABSOLUTE values -- a standard, describable data-derived split (section 14's own explicit instruction: a documented quantile, never chosen to produce a favourable result). Nearest-rank method, same convention <see cref="EventBarDurationStats"/> already uses.</summary>
    public static (decimal Low33, decimal High67) ComputeTerciles(IReadOnlyList<decimal> absoluteValues)
    {
        if (absoluteValues.Count == 0)
        {
            return (0, 0);
        }
        var sorted = absoluteValues.Order().ToList();
        // Decimal (not double) arithmetic throughout -- avoids a double floating-point rounding
        // edge case (e.g. 100.0/3*9/100.0 landing at 3.0000000000000004 instead of exactly 3.0)
        // that would silently shift the nearest-rank index by one.
        decimal Percentile(decimal fraction)
        {
            var rank = (int)Math.Ceiling(fraction * sorted.Count) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
        }
        return (Percentile(1m / 3), Percentile(2m / 3));
    }

    /// <summary>Compares two ALREADY-COMPUTED directions (e.g. forward futures vs forward spot) -- distinct from <see cref="ClassifyForwardDirection"/>, which compares a pre-event direction against a forward one.</summary>
    public static string ClassifyPairwiseDirection(RelationshipDirection a, RelationshipDirection b)
    {
        if (a == RelationshipDirection.Unavailable || b == RelationshipDirection.Unavailable)
        {
            return "Unavailable";
        }
        if (a == RelationshipDirection.Flat && b == RelationshipDirection.Flat)
        {
            return "BothFlat";
        }
        if (a == RelationshipDirection.Flat || b == RelationshipDirection.Flat)
        {
            return "Mixed";
        }
        return a == b ? "SameDirection" : "OppositeDirection";
    }

    /// <summary>
    /// Deterministic bootstrap 95%% CI for the MEDIAN, seeded so re-running produces an identical
    /// result -- descriptive uncertainty only (section 13's own explicit instruction: NOT a
    /// significance test, NOT machine learning). Resamples with replacement
    /// <paramref name="resamples"/> times, takes the 2.5th/97.5th percentile of the resampled
    /// medians.
    /// </summary>
    public static (decimal Lower, decimal Upper) BootstrapMedianCi(IReadOnlyList<decimal> values, int seed = 42, int resamples = 1000)
    {
        if (values.Count == 0)
        {
            return (0, 0);
        }
        var random = new Random(seed);
        var resampledMedians = new List<decimal>(resamples);
        for (var i = 0; i < resamples; i++)
        {
            var sample = new decimal[values.Count];
            for (var j = 0; j < values.Count; j++)
            {
                sample[j] = values[random.Next(values.Count)];
            }
            Array.Sort(sample);
            var mid = sample.Length / 2;
            resampledMedians.Add(sample.Length % 2 == 1 ? sample[mid] : (sample[mid - 1] + sample[mid]) / 2m);
        }
        resampledMedians.Sort();
        decimal Percentile(double p)
        {
            var rank = (int)Math.Ceiling(p / 100.0 * resampledMedians.Count) - 1;
            return resampledMedians[Math.Clamp(rank, 0, resampledMedians.Count - 1)];
        }
        return (Percentile(2.5), Percentile(97.5));
    }
}
