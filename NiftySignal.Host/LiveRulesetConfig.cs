using NiftySignal.Domain.Configuration;
using NiftySignal.Rules;

namespace NiftySignal.Host;

/// <summary>
/// The live ruleset -- same values as the plan's own section 7.2 example (LotSize
/// corrected to the confirmed-current 65, matching NiftySignal.Tests.Rules.TestRulesetConfigs).
/// Hardcoded rather than config-bound for now: RulesetConfig's records don't bind cleanly
/// through the classic IOptions reflection binder, and getting that working isn't worth
/// blocking today's live test over -- move this to appsettings + IOptionsMonitor (the
/// project's established hot-reload pattern, see KillSwitchOptions) once someone actually
/// needs to retune a value without a redeploy.
/// </summary>
public static class LiveRulesetConfig
{
    public static RulesetConfig Default() => new(
        RulesetVersion: "live-v2-2026-09-07",
        // LotsPerTrade: 2 (2026-09-07) -- 1 lot made PartialBookFraction's 50% land on 32.5
        // units, not a valid multiple of LotSize. 2 lots means the partial book is exactly 1
        // clean lot and the remainder is exactly 1 clean lot too.
        Capital: new CapitalConfig(Total: 50000, LotSize: 65, MaxConcurrentPositions: 3, LotsPerTrade: 2),
        Session: new SessionConfig(
            NoEntryBeforeMinutes: 15,
            NoEntryAfterTime: new TimeOnly(15, 0),
            SquareOffTime: new TimeOnly(15, 15),
            ExpiryDayEnabled: true,
            ExpiryDayNoEntryAfterTime: new TimeOnly(14, 0)),
        Entry: new EntryConfig(
            // MinAbsScore and MinScoreSustainedSeconds (audit finding F13, 2026-09-08): both
            // tuned watching a score that sat at +/-99 roughly a third of the day under the old
            // dynamic-k mechanism (see F1 / CompositeScoreCalculator.DefaultK's doc comment) --
            // left unchanged here rather than guessed at, since a new number picked before k's
            // corrected scale exists would just be tuning against a distribution about to shift
            // again. Revisit both from a live session's qualification-rate data on the fixed-k,
            // Batch-3-corrected score (target "top 5-10% of cadences" per the audit) once that
            // data exists.
            MinAbsScore: 55,
            MinScoreSustainedSeconds: 45,
            ReEntryGapSameDirectionMinutes: 2,
            // Raised from 7 to 30 (2026-09-08) -- the 7 cap got spent by 10:48 today, well
            // before a later well-sustained bearish move (score to -96.7) could be evaluated,
            // so the cap itself was the binding constraint rather than the scoring/rules being
            // wrong. 30 is deliberately loose for now to observe unconstrained behaviour for a
            // session or two before picking a real steady-state number.
            MaxTradesPerDay: 30,
            MaxIvRankForEntry: 70,
            MinScoreSustainedCadences: 3),
        // Narrowed 2026-09-07 (from 150-200) after the first live session -- cheaper premium
        // means a smaller capital commitment per lot and generally higher gamma/more strikes
        // to choose from near the money.
        StrikeSelection: new StrikeSelectionConfig(
            MinPremium: 100,
            MaxPremium: 150,
            MaxSpreadPctOfMid: 2.0,
            MinOpenInterest: 100_000),
        Exit: new ExitConfig(
            // Tightened 2026-09-07 after the first day of live paper trading: 25%/30% let a
            // position round-trip a lot of premium before anything protected it -- today's
            // score reversed from +99 to ~0 in about a minute, and a position sitting at only
            // +15% would still have had nothing booked under the old 30% partial-book level.
            PartialBookAtProfitPct: 15,
            PartialBookFraction: 0.5,
            StopLossPct: 10,
            TrailAfterPartialBook: true,
            ExitOnScoreFlip: true,
            ExitOnScoreBelowAbs: 30,
            MaxHoldMinutes: 120),
        Costs: new CostsConfig(BrokeragePerOrder: 20, SlippageTicks: 2),
        // Widened 2026-09-07, deliberately -- this is paper trading, no real capital is at
        // risk, and the 3%/6% starting point cut the very first live session short (tripped
        // after -6.1%, before a later -99 score move could even be considered). At this early,
        // exploratory stage, seeing a fuller day's range of outcomes is more valuable than a
        // tight breaker -- revisit both numbers downward once there's a real distribution of
        // daily P&L to calibrate against, before any real capital is involved.
        RiskLimits: new RiskLimitsConfig(MaxDailyLossPct: 20.0, MaxDailyProfitPct: 30.0, MaxConsecutiveLosses: 4),
        KillSwitch: new KillSwitchOptions { EntriesEnabled = true });
}
