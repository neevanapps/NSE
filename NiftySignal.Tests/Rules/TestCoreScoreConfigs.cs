using NiftySignal.Rules;

namespace NiftySignal.Tests.Rules;

/// <summary>Batch 5 (2026-09-13) test defaults for the two new Core-score strategies -- matches the backtest's own locked-in values (EntryScoreThreshold=30, FastWindowMinutes=10/SlowWindowMinutes=30).</summary>
public static class TestCoreScoreConfigs
{
    public static CoreScoreHysteresisConfig Hysteresis(double maxDailyLossPct = 20.0, int maxConsecutiveLosses = 4, int maxTradesPerDay = 30) => new(
        EntryScoreThreshold: 30.0,
        RiskLimits: new CoreScoreRiskLimitsConfig(maxDailyLossPct, maxConsecutiveLosses, maxTradesPerDay));

    public static CoreScoreCrossoverConfig Crossover(double maxDailyLossPct = 20.0, int maxConsecutiveLosses = 4, int maxTradesPerDay = 30) => new(
        FastWindowMinutes: 10,
        SlowWindowMinutes: 30,
        RiskLimits: new CoreScoreRiskLimitsConfig(maxDailyLossPct, maxConsecutiveLosses, maxTradesPerDay));
}
