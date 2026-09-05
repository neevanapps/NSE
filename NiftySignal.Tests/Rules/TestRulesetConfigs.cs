using NiftySignal.Domain.Configuration;
using NiftySignal.Rules;

namespace NiftySignal.Tests.Rules;

/// <summary>Matches the plan's own section 7.2 JSON example (LotSize corrected to the confirmed-current 65).</summary>
public static class TestRulesetConfigs
{
    public static RulesetConfig Default() => new(
        RulesetVersion: "test-ruleset-1",
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
        RiskLimits: new RiskLimitsConfig(MaxDailyLossPct: 3.0, MaxDailyProfitPct: 6.0, MaxConsecutiveLosses: 4),
        KillSwitch: new KillSwitchOptions { EntriesEnabled = true });
}
