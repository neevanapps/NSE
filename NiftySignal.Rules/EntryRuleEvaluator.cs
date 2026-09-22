using NiftySignal.Domain.Enums;

namespace NiftySignal.Rules;

/// <summary>
/// Everything EntryRuleEvaluator needs, precomputed by the caller -- deliberately a plain
/// snapshot so the evaluator itself stays pure and every boundary condition in plan section
/// 7.3 is testable by constructing a context, no live wiring required.
/// </summary>
public sealed record EntryContext(
    DateTimeOffset Now,
    double Score,
    TimeSpan ScoreSustainedDuration,
    bool KillSwitchEntriesEnabled,
    bool DailyLossLimitBreached,
    bool AllFeaturesWarmedUp,
    bool HasOpenDataGap,
    int TradesSoFarToday,
    int OpenConcurrentPositions,
    DateTimeOffset? LastEntryTimeSameDirection,
    bool IsExpiryDay,
    bool DailyProfitTargetReached = false,
    // Audit finding F12 (2026-09-08): a feed stall that goes unrecorded before a data gap is
    // formally opened would otherwise let ScoreSustainedDuration alone satisfy the hold
    // requirement on elapsed silence, not a genuinely sustained score -- see
    // ScoreSustainTracker.Observe's own doc comment. Checked in addition to duration, not
    // instead of it.
    int ScoreSustainedCadenceCount = 0,
    // Audit finding F3 (2026-09-08): both wire up config fields that already existed
    // (MaxIvRankForEntry, MaxConsecutiveLosses) but were read by nothing. CurrentIvRank is
    // nullable and simply skips its gate when null (not enough IV history yet to rank against
    // -- see LiveFeatureEngine.ComputeIvRank) rather than blocking on an unknown.
    double? CurrentIvRank = null,
    int ConsecutiveLossesToday = 0,
    // 2026-09-09 external review amendment to F3: CurrentIvRank alone isn't enough to know
    // whether the gate should act -- LiveFeatureEngine.ComputeIvRank falls back to a same-day-
    // only rank (IvRankSessionCount below MinIvRankSessionsForGate) whenever fewer than 5
    // prior sessions of history exist yet, and that same-day rank isn't trustworthy (a
    // genuinely high-vol day would still read "normal" for its own first couple of hours).
    // Zero by default so a caller that hasn't been updated to pass this yet gets the safe
    // (gate-off) behavior rather than an accidentally-live gate.
    int IvRankSessionCount = 0);

/// <summary>Every failed check is included, not just the first -- plan section 7.3: "the rejection data matters as much as the acceptance data."</summary>
public sealed record EntryDecision(bool ShouldEnter, EntryDirection Direction, IReadOnlyList<string> FailedConditions);

/// <summary>
/// The ordered filter-then-threshold checks from plan section 7.3. "Capital available" from
/// that list isn't a separate check here -- plan section 1.2 ties capital directly to
/// MaxConcurrentPositions (fixed lot size near a fixed premium band), so the concurrent-
/// positions check below already covers it. "Strike selection returns a valid candidate"
/// (step 5) is also not here -- that's IStrikeSelector's job, run only after this method
/// says entry conditions are otherwise met, since it needs live quotes this evaluator has
/// no business depending on.
/// </summary>
public static class EntryRuleEvaluator
{
    public static readonly TimeOnly MarketOpen = new(9, 15);

    // 2026-09-09 external review amendment to F3 -- must match LiveFeatureEngine's
    // MinPriorSessionsForIvRank exactly (that's the threshold IvRankSessionCount is measured
    // against); duplicated as a literal here rather than shared, same as every other config
    // duplicate already in this codebase (see RulesetConfigOptions' own doc comment).
    const int MinIvRankSessionsForGate = 5;

    // NSE trading hours are always IST regardless of where this process runs or what offset
    // context.Now happens to carry (UtcNow throughout this codebase, per Npgsql's timestamptz
    // convention). Same value LiveTradingEngine already uses for its own IST-midnight calc.
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public static EntryDecision Evaluate(EntryContext context, RulesetConfig config)
    {
        var failures = new List<string>();

        // Live-caught 2026-09-07: context.Now is UTC, and .DateTime returns that UTC instant's
        // own wall-clock time, not a timezone conversion. Comparing that raw UTC clock reading
        // against IST-intended cutoffs (MarketOpen 9:15, NoEntryAfterTime 15:00) meant "entries
        // start at 9:30" was actually only satisfied once the clock read 9:30 UTC = 15:00 IST --
        // at or past NSE's close. This single bug meant no entry had ever been possible during
        // the actual trading session, for the entire life of this project.
        var nowTime = TimeOnly.FromDateTime(context.Now.ToOffset(IstOffset).DateTime);

        var noEntryBefore = MarketOpen.AddMinutes(config.Session.NoEntryBeforeMinutes);
        if (nowTime < noEntryBefore)
        {
            failures.Add($"Before session open window (NoEntryBeforeMinutes: entries start {noEntryBefore})");
        }

        var noEntryAfter = config.Session.ExpiryDayEnabled && context.IsExpiryDay
            ? config.Session.ExpiryDayNoEntryAfterTime
            : config.Session.NoEntryAfterTime;
        if (nowTime >= noEntryAfter)
        {
            failures.Add($"After session close window (entries stop at {noEntryAfter})");
        }

        if (!context.KillSwitchEntriesEnabled)
        {
            failures.Add("KillSwitch.EntriesEnabled is false");
        }

        if (context.DailyLossLimitBreached)
        {
            failures.Add("Daily loss circuit breaker has tripped");
        }

        // The upside mirror of the loss breaker (2026-09-05): once the day's realised profit
        // clears the target, stop opening new risk rather than giving it back. Deliberately
        // blocks entries only -- open positions still run their normal exit rules, and
        // ingestion is untouched, same separation the kill switch already draws.
        if (context.DailyProfitTargetReached)
        {
            failures.Add("Daily profit target has been reached");
        }

        // Audit finding F3 (2026-09-08): buying premium when IV is already rich relative to its
        // own recent range is how a directionally-correct trade still loses to a vol crush.
        // Skipped, not blocked, while there isn't yet enough IV history to rank against --
        // amended 2026-09-09 (external review): "enough history" means IvRankSessionCount has
        // reached MinIvRankSessionsForGate, not merely CurrentIvRank being non-null.
        // LiveFeatureEngine.ComputeIvRank can and does return a non-null same-day-only rank
        // well before 5 sessions exist; acting on that rank here would be gating on a value
        // that hasn't earned trust yet (a genuinely high-vol day still reads "normal" for its
        // own first couple of hours under a same-day-only rank).
        if (context.IvRankSessionCount >= MinIvRankSessionsForGate
            && context.CurrentIvRank is { } ivRank && ivRank > config.Entry.MaxIvRankForEntry)
        {
            failures.Add($"IV rank ({ivRank:F1}) above MaxIvRankForEntry ({config.Entry.MaxIvRankForEntry})");
        }

        // The other half of F3: was dead config (RiskLimitsConfig.MaxConsecutiveLosses existed,
        // nothing read it). A cooldown after a losing streak, same "blocks new entries only"
        // separation as the daily loss/profit breakers above.
        if (context.ConsecutiveLossesToday >= config.RiskLimits.MaxConsecutiveLosses)
        {
            failures.Add($"MaxConsecutiveLosses ({config.RiskLimits.MaxConsecutiveLosses}) already reached ({context.ConsecutiveLossesToday} in a row)");
        }

        if (!context.AllFeaturesWarmedUp)
        {
            failures.Add("Not all contributing features are warmed up yet");
        }

        if (context.HasOpenDataGap)
        {
            failures.Add("An open data gap is in progress");
        }

        if (context.TradesSoFarToday >= config.Entry.MaxTradesPerDay)
        {
            failures.Add($"MaxTradesPerDay ({config.Entry.MaxTradesPerDay}) already reached");
        }

        if (context.OpenConcurrentPositions >= config.Capital.MaxConcurrentPositions)
        {
            failures.Add($"MaxConcurrentPositions ({config.Capital.MaxConcurrentPositions}) already reached");
        }

        var direction = Math.Sign(context.Score) switch
        {
            > 0 => EntryDirection.Bullish,
            < 0 => EntryDirection.Bearish,
            _ => EntryDirection.None,
        };

        if (Math.Abs(context.Score) < config.Entry.MinAbsScore)
        {
            failures.Add($"|score| ({Math.Abs(context.Score):F1}) below MinAbsScore ({config.Entry.MinAbsScore})");
        }

        var requiredSustain = TimeSpan.FromSeconds(config.Entry.MinScoreSustainedSeconds);
        if (context.ScoreSustainedDuration < requiredSustain)
        {
            failures.Add($"Score not sustained for MinScoreSustainedSeconds ({requiredSustain.TotalSeconds}s, held for {context.ScoreSustainedDuration.TotalSeconds}s)");
        }

        if (context.ScoreSustainedCadenceCount < config.Entry.MinScoreSustainedCadences)
        {
            failures.Add($"Score not sustained for MinScoreSustainedCadences ({config.Entry.MinScoreSustainedCadences}, held for {context.ScoreSustainedCadenceCount} cadence(s))");
        }

        if (context.LastEntryTimeSameDirection is { } lastEntry)
        {
            var gap = context.Now - lastEntry;
            var requiredGap = TimeSpan.FromMinutes(config.Entry.ReEntryGapSameDirectionMinutes);
            if (gap < requiredGap)
            {
                failures.Add($"Within ReEntryGapSameDirectionMinutes ({requiredGap.TotalMinutes}min) of the last same-direction entry");
            }
        }

        var shouldEnter = failures.Count == 0 && direction != EntryDirection.None;
        return new EntryDecision(shouldEnter, shouldEnter ? direction : EntryDirection.None, failures);
    }
}
