namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, forensic validation of the Pattern A/Pattern B forward-futures-direction finding
/// (see docs/VolumeCandle_0DTE_Findings.md's "Forensic Validation" section). Pure calculators
/// only -- every forward change/state classification this reads from is the EXISTING, unmodified
/// <see cref="RelationshipObservation"/>/<see cref="UnderlyingOptionRelationshipSummary"/>/
/// <see cref="ForwardValidationAnalysis"/> machinery. Nothing here alters the 1300-contract
/// threshold, 0-DTE filter, dynamic ATM, 3/10 crossover, direction classification, or any
/// existing command.
/// </summary>
public static class ForensicValidationAnalysis
{
    public sealed record PercentileResult(int N, decimal Min, decimal P10, decimal P25, decimal Median, decimal P75, decimal P90, decimal Max);

    /// <summary>Nearest-rank percentiles, same convention <see cref="EventBarDurationStats"/> already uses -- decimal arithmetic throughout (see <see cref="ForwardValidationAnalysis.ComputeTerciles"/>'s own doc comment for why double arithmetic is avoided here).</summary>
    public static PercentileResult ComputePercentiles(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
        {
            return new PercentileResult(0, 0, 0, 0, 0, 0, 0, 0);
        }
        var sorted = values.Order().ToList();
        decimal Percentile(decimal fraction)
        {
            var rank = (int)Math.Ceiling(fraction * sorted.Count) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
        }
        return new PercentileResult(sorted.Count, sorted[0], Percentile(0.10m), Percentile(0.25m), Percentile(0.50m), Percentile(0.75m), Percentile(0.90m), sorted[^1]);
    }

    /// <summary>
    /// What fraction of the TOTAL (signed sum, which the mean is proportional to) comes from the
    /// top <paramref name="topFraction"/> largest-ABSOLUTE-magnitude observations -- e.g.
    /// topFraction=0.01 answers "do the largest 1% of moves account for most of the mean." A
    /// small count of large moves dominating close to 100% of the total would flag the mean (but
    /// not necessarily the median) as fragile.
    /// </summary>
    public static decimal ContributionOfLargestToTotal(IReadOnlyList<decimal> values, double topFraction)
    {
        if (values.Count == 0)
        {
            return 0m;
        }
        var total = values.Sum();
        if (total == 0)
        {
            return 0m;
        }
        var topCount = Math.Max(1, (int)Math.Ceiling(values.Count * topFraction));
        var topSum = values.OrderByDescending(Math.Abs).Take(topCount).Sum();
        return topSum / total * 100m;
    }

    /// <summary>
    /// Section 11's permutation sanity check -- NOT a significance test (per the user's own
    /// explicit instruction). Draws <paramref name="permutations"/> random subsets of
    /// <paramref name="sampleSize"/> (without replacement) from <paramref name="population"/>,
    /// computes each subset's own median, and returns the sorted distribution of those medians --
    /// the same operation as "shuffle which events carry the Pattern A/B label, keeping the real
    /// forward returns fixed, and see how often a same-sized random subset produces a median this
    /// extreme by chance in this specific 3-day dataset."
    /// </summary>
    public static List<decimal> PermutationMedianDistribution(IReadOnlyList<decimal> population, int sampleSize, int seed, int permutations)
    {
        if (population.Count == 0 || sampleSize <= 0 || sampleSize > population.Count)
        {
            return [];
        }
        var random = new Random(seed);
        var medians = new List<decimal>(permutations);
        var indices = Enumerable.Range(0, population.Count).ToArray();
        for (var p = 0; p < permutations; p++)
        {
            // Fisher-Yates partial shuffle -- deterministic given the seeded Random, draws
            // sampleSize distinct indices without replacement each permutation.
            for (var i = 0; i < sampleSize; i++)
            {
                var j = random.Next(i, indices.Length);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }
            var sample = indices.Take(sampleSize).Select(idx => population[idx]).Order().ToList();
            var mid = sample.Count / 2;
            medians.Add(sample.Count % 2 == 1 ? sample[mid] : (sample[mid - 1] + sample[mid]) / 2m);
        }
        medians.Sort();
        return medians;
    }

    /// <summary>Where the ACTUAL observed value falls within a sorted permutation distribution -- 0% means smaller than every permutation draw, 100% means larger than every one. Purely descriptive placement, not a p-value (the user's own explicit instruction: this is a sanity check, not a significance claim).</summary>
    public static double PercentileRankOf(IReadOnlyList<decimal> sortedDistribution, decimal observedValue)
    {
        if (sortedDistribution.Count == 0)
        {
            return double.NaN;
        }
        var countBelow = sortedDistribution.Count(v => v < observedValue);
        return 100.0 * countBelow / sortedDistribution.Count;
    }
}
