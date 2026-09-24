using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, mark-to-market/signal-quality follow-up to the Trade-Lifecycle Diagnostics (see
/// docs/VolumeCandle_0DTE_Findings.md's "Mark-to-Market Signal-Quality Diagnostics" section).
/// Pure, deterministic helpers only -- no new signal/entry/exit logic, no change to the frozen
/// simulator. All bucket boundaries below are either specified verbatim by the task itself
/// (the 4 holding-time groups, the 6 MFE buckets) or derived from the day's own actual listed
/// strikes (the strike-step size), never invented.
/// </summary>
public static class MarkToMarketDiagnostics
{
    /// <summary>
    /// 2026-09-24, exit-asymmetry validation. One trade's full record for the session/DTE/day
    /// breakdowns and the exit-opportunity-cost measurement -- a superset of the fields the
    /// signal-quality pass (<see cref="MtmRow"/>) already captured, plus MFE/MAE measured in the
    /// window AFTER the actual exit (bounded at EntryTimestamp+15min, the same ceiling used
    /// throughout, never an unbounded "look as far as you like" window) and the per-horizon
    /// opportunity-cost values. Purely a data-carrying record.
    /// </summary>
    public sealed record ExitAsymmetryRow(
        DateOnly Date, string Pattern, OptionType OptionType, decimal Strike, decimal AtmStrike, decimal StrikeDistance, int Dte, string SessionBucket,
        decimal EntryPrice, decimal FuturesAtEntry, decimal IntrinsicAtEntry, decimal ExtrinsicAtEntry,
        decimal? CeChange1AtSignal, decimal? PeChange1AtSignal, decimal? PriorMovement3,
        decimal?[] EventReturns, decimal?[] MinuteReturns,
        TimeSpan HoldingDuration, decimal ActualGrossPnl, decimal ActualNetPnl,
        decimal MfeBeforeExit, decimal MaeBeforeExit, decimal? MfeAfterExit, decimal? MaeAfterExit,
        decimal?[] OpportunityCostAtMinuteHorizon);

    /// <summary>
    /// One trade's full mark-to-market/signal-quality diagnostic record (see
    /// docs/VolumeCandle_0DTE_Findings.md's "Mark-to-Market Signal-Quality Diagnostics" section).
    /// <see cref="EventReturns"/>/<see cref="EventIntrinsicChanges"/>/<see cref="EventExtrinsicChanges"/>
    /// are indexed in the same order as the caller's own event-horizon array (1/3/5/10 events);
    /// <see cref="MinuteReturns"/> likewise for the minute-horizon array (1/2/3/5/10/15 minutes).
    /// Purely a data-carrying record -- no calculation lives here, everything is computed by the
    /// orchestrating command from already-frozen simulation output.
    /// </summary>
    public sealed record MtmRow(
        DateOnly Date, string Pattern, OptionType OptionType, decimal Strike, decimal AtmStrike, decimal StrikeDistance, string StrikeStepBucket, int Dte,
        decimal EntryPrice, decimal FuturesAtEntry, decimal IntrinsicAtEntry, decimal ExtrinsicAtEntry, decimal? IntrinsicPctOfPremium,
        decimal?[] EventReturns, decimal?[] EventIntrinsicChanges, decimal?[] EventExtrinsicChanges,
        decimal?[] MinuteReturns, TimeSpan HoldingDuration, string HoldingGroup,
        decimal Mae, decimal MaePercent, decimal Mfe, decimal MfePercent, string MfeBucket,
        TimeSpan? TimeToMfe, TimeSpan? TimeToMae, decimal NetPnl, string ExitReason, decimal? PriorMovement3, string SessionBucket);

    /// <summary>Task's own 4 groups for Part 3 (NOT the 8-bucket Part-4 scheme from the prior diagnostic -- a coarser, task-specified split).</summary>
    public static string BucketByHoldingTimeCoarse(TimeSpan holding) => holding.TotalMinutes switch
    {
        < 1 => "Group1 (<1 min)",
        < 2 => "Group2 (1-2 min)",
        < 5 => "Group3 (2-5 min)",
        _ => "Group4 (>5 min)",
    };

    /// <summary>Task's own 6 MFE buckets for Part 4.</summary>
    public static string BucketByMfePercent(decimal mfePercent) => mfePercent switch
    {
        < 0 => "MFE<0%",
        <= 1 => "0-1%",
        <= 2 => "1-2%",
        <= 5 => "2-5%",
        <= 10 => "5-10%",
        _ => ">10%",
    };

    /// <summary>Task's own strike-step buckets for Part 6 -- steps derived from <paramref name="strikeStepSize"/>, the day's own minimum gap between listed strikes, never a hardcoded constant.</summary>
    public static string BucketByStrikeSteps(decimal strike, decimal atmStrike, decimal strikeStepSize)
    {
        if (strikeStepSize <= 0) { return "Unavailable"; }
        var steps = (int)Math.Round(Math.Abs(strike - atmStrike) / strikeStepSize, MidpointRounding.AwayFromZero);
        return steps switch
        {
            0 => "ATM/closest strike",
            1 => "1 step away",
            2 => "2 steps away",
            3 => "3 steps away",
            _ => "4+ steps away",
        };
    }

    /// <summary>
    /// Elapsed time from <paramref name="entryTimestamp"/> to the tick in <paramref name="pathAfterEntry"/>
    /// (already filtered to strictly after entry) at which price first reaches its own maximum
    /// (time to MFE) and minimum (time to MAE). Null when the path is empty.
    /// </summary>
    public static (TimeSpan? TimeToMfe, TimeSpan? TimeToMae) ComputeExcursionTiming(
        DateTimeOffset entryTimestamp, IReadOnlyList<(DateTimeOffset Timestamp, decimal Price)> pathAfterEntry)
    {
        if (pathAfterEntry.Count == 0) { return (null, null); }
        var maxPrice = pathAfterEntry.Max(p => p.Price);
        var minPrice = pathAfterEntry.Min(p => p.Price);
        var firstMaxTimestamp = pathAfterEntry.First(p => p.Price == maxPrice).Timestamp;
        var firstMinTimestamp = pathAfterEntry.First(p => p.Price == minPrice).Timestamp;
        return (firstMaxTimestamp - entryTimestamp, firstMinTimestamp - entryTimestamp);
    }

    /// <summary>
    /// 2026-09-24, exit-asymmetry validation. Positive = the hypothetical (holding to that later
    /// horizon) would have realized MORE than the actual frozen exit; negative = less. Gross-to-
    /// gross by construction (see the orchestrating command's own note on why STT is not
    /// subtracted from a horizon that was never actually executed) -- this function does no cost
    /// arithmetic itself, purely the comparison.
    /// </summary>
    public static decimal ComputeOpportunityCost(decimal hypotheticalGrossPnl, decimal actualGrossPnl)
        => hypotheticalGrossPnl - actualGrossPnl;

    /// <summary>The day's own minimum gap between distinct, sorted strikes -- used as the strike-step size instead of a hardcoded constant.</summary>
    public static decimal InferStrikeStepSize(IReadOnlyList<decimal> distinctSortedStrikes)
    {
        if (distinctSortedStrikes.Count < 2) { return 0m; }
        var gaps = new List<decimal>();
        for (var i = 1; i < distinctSortedStrikes.Count; i++)
        {
            gaps.Add(distinctSortedStrikes[i] - distinctSortedStrikes[i - 1]);
        }
        return gaps.Min();
    }
}
