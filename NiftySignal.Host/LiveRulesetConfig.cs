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
        RulesetVersion: "live-v1-2026-09-04",
        Capital: new CapitalConfig(Total: 50000, LotSize: 65, MaxConcurrentPositions: 3),
        Session: new SessionConfig(
            NoEntryBeforeMinutes: 15,
            NoEntryAfterTime: new TimeOnly(15, 0),
            SquareOffTime: new TimeOnly(15, 15),
            ExpiryDayEnabled: true,
            ExpiryDayNoEntryAfterTime: new TimeOnly(14, 0)),
        Entry: new EntryConfig(
            MinAbsScore: 55,
            MinScoreSustainedSeconds: 45,
            ReEntryGapSameDirectionMinutes: 2,
            MaxTradesPerDay: 7,
            MaxIvRankForEntry: 70),
        StrikeSelection: new StrikeSelectionConfig(
            MinPremium: 150,
            MaxPremium: 200,
            MaxSpreadPctOfMid: 2.0,
            MinOpenInterest: 100_000),
        Exit: new ExitConfig(
            PartialBookAtProfitPct: 30,
            PartialBookFraction: 0.5,
            StopLossPct: 25,
            TrailAfterPartialBook: true,
            ExitOnScoreFlip: true,
            ExitOnScoreBelowAbs: 30,
            MaxHoldMinutes: 120),
        Costs: new CostsConfig(BrokeragePerOrder: 20, SlippageTicks: 2),
        // Profit target deliberately wider than the loss limit (6% vs 3%): the loss breaker
        // exists to stop a bad day compounding, the profit target only to stop giving back an
        // unusually good one, so it should trip far less often. Starting point -- revisit once
        // there's a real distribution of daily P&L to look at.
        RiskLimits: new RiskLimitsConfig(MaxDailyLossPct: 3.0, MaxDailyProfitPct: 6.0, MaxConsecutiveLosses: 4),
        KillSwitch: new KillSwitchOptions { EntriesEnabled = true });
}
