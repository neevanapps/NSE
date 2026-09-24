namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, A-side exit-delay sensitivity experiment (see
/// docs/VolumeCandle_0DTE_Findings.md's "A-Side Exit-Delay Sensitivity Experiment" section).
/// Pure, deterministic helpers ONLY -- this file computes a COUNTERFACTUAL/diagnostic
/// alternative exit timestamp and outcome classification over an ALREADY-FROZEN baseline trade;
/// it never changes the actual simulated trade lifecycle, never touches Pattern B, and never
/// re-triggers the relationship/signal-generation logic during the hypothetical delay window
/// (a stated, deliberate simplification -- see the orchestrating command's own note). This is
/// the "diagnostic layer instead of modifying production strategy behaviour" approach the task
/// itself suggested when a true counterfactual is architecturally difficult to simulate live.
/// </summary>
public static class DelayedExitCounterfactual
{
    /// <summary>
    /// One Pattern-A trade's baseline result plus, for each tested delay, the counterfactual
    /// alternative-exit outcome. <see cref="AlternativeExitTimestamp"/>/<see cref="AlternativeExitPrice"/>/
    /// <see cref="AlternativeGrossPnl"/>/<see cref="AlternativeNetPnl"/>/<see cref="AlternativeMae"/>/
    /// <see cref="AlternativeMfe"/>/<see cref="ExitedBy"/>/<see cref="MaxFavorableInWaitWindow"/>/
    /// <see cref="MaxAdverseInWaitWindow"/> are all indexed by delay (same order as the caller's
    /// own delay array). Null entries mean no valid tick existed for that delay -- reported as
    /// missing, never fabricated. Purely a data-carrying record.
    /// </summary>
    public sealed record DelayExperimentRow(
        DateOnly Date, decimal Strike, decimal AtmStrike, DateTimeOffset EntryTimestamp, string SessionBucket,
        decimal EntryPrice, int Quantity,
        DateTimeOffset BaselineExitTimestamp, decimal BaselineExitPrice, decimal BaselineGrossPnl, decimal BaselineNetPnl,
        decimal BaselineMae, decimal BaselineMfe, TimeSpan BaselineHolding,
        DateTimeOffset?[] AlternativeExitTimestamp, decimal?[] AlternativeExitPrice,
        decimal?[] AlternativeGrossPnl, decimal?[] AlternativeNetPnl, decimal?[] AlternativeMae, decimal?[] AlternativeMfe,
        string[] ExitedBy, decimal?[] MaxFavorableInWaitWindow, decimal?[] MaxAdverseInWaitWindow);


    /// <summary>
    /// The hypothetical exit timestamp if Pattern A's exit had been delayed by
    /// <paramref name="delay"/> past the ACTUAL (baseline) exit -- capped at
    /// <paramref name="mandatoryCutoff"/> (15:15 IST), the same forced-close ceiling the frozen
    /// simulator already enforces. Never later than the mandatory cutoff, by construction.
    /// </summary>
    public static DateTimeOffset ComputeAlternativeExitTarget(DateTimeOffset baselineExitTimestamp, TimeSpan delay, DateTimeOffset mandatoryCutoff)
    {
        var target = baselineExitTimestamp + delay;
        return target < mandatoryCutoff ? target : mandatoryCutoff;
    }

    /// <summary>
    /// 2026-09-24, A-continuation-vs-reversal diagnostic. Deterministic, zero-threshold sign
    /// split of a post-exit PE return: positive = the price kept moving in the direction that
    /// would have favoured continuing to hold ("Continuation"); negative = it moved against that
    /// direction ("Reversal", i.e. the exit was validated); exactly zero = "Flat"; null (no
    /// tick available) = "Unavailable". No magnitude threshold is introduced -- the task's own
    /// requirement that a classification only be used when it needs no arbitrary cutoff.
    /// </summary>
    public static string ClassifyContinuationVsReversal(decimal? postExitReturn) => postExitReturn switch
    {
        null => "Unavailable",
        0m => "Flat",
        > 0m => "Continuation",
        _ => "Reversal",
    };

    /// <summary>Elapsed minutes from the 09:15 IST session open to <paramref name="timestamp"/> -- a signal-time-only feature, never derived from anything after the signal.</summary>
    public static double MinutesSinceSessionOpen(DateTimeOffset timestamp, TimeSpan istOffset)
    {
        var istTime = timestamp.ToOffset(istOffset);
        var sessionOpen = new DateTimeOffset(istTime.Year, istTime.Month, istTime.Day, 9, 15, 0, istOffset);
        return (istTime - sessionOpen).TotalMinutes;
    }

    /// <summary>
    /// 2026-09-24, A-continuation-vs-reversal diagnostic. One Pattern-A trade's signal-time
    /// features (known at/before the signal, used as candidate predictors) plus its post-EXIT
    /// outcome (the price path strictly AFTER the actual baseline exit, at 1/3/5/10/15 minutes --
    /// the OUTCOME, never mixed into the feature set). Purely a data-carrying record.
    /// </summary>
    public sealed record ContinuationRow(
        DateOnly Date, string SessionBucket, DateTimeOffset SignalTimestamp, double MinutesSinceSessionOpen,
        decimal? CeChange1, decimal? PeChange1, string CePeDivergence, decimal? FuturesChange1, decimal? PriorMovement3,
        decimal EntryPrice, decimal Strike, decimal AtmStrike, decimal StrikeDistance, decimal FuturesAtEntry,
        decimal Moneyness, decimal IntrinsicAtEntry, decimal ExtrinsicAtEntry, int Dte,
        decimal?[] PostExitReturn, string[] ContinuationLabel, decimal? MfeAfterExit, decimal? MaeAfterExit);

    /// <summary>Win = NetPnl &gt; 0, Loss = NetPnl &lt;= 0 -- same convention used throughout this diagnostic chain.</summary>
    public static string ClassifyOutcomeTransition(decimal baselineNetPnl, decimal alternativeNetPnl)
    {
        var baselineWin = baselineNetPnl > 0;
        var alternativeWin = alternativeNetPnl > 0;
        return (baselineWin, alternativeWin) switch
        {
            (false, true) => "LossToWin",
            (false, false) => "LossToLoss",
            (true, true) => "WinToWin",
            (true, false) => "WinToLoss",
        };
    }
}
