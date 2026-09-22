using NiftySignal.Rules;

namespace NiftySignal.Tests.Rules;

/// <summary>
/// Batch 5 (2026-09-13) test defaults for the two new Core-score strategies -- matches the
/// backtest's own locked-in values (EntryScoreThreshold=30, FastWindowMinutes=10/SlowWindowMinutes=30).
/// <c>shadowMode</c> defaults to <c>false</c> here -- the OPPOSITE of the production appsettings.json
/// default (Batch 6, 2026-09-14) -- because every existing Batch 5 test in
/// <c>CoreScoreTradingEngineTests</c> asserts against real <c>db.PaperTrades</c> rows; those tests
/// would silently stop seeing any rows at all if this factory defaulted to shadow mode. Tests that
/// specifically exercise ShadowMode pass <c>shadowMode: true</c> explicitly.
/// </summary>
public static class TestCoreScoreConfigs
{
    public static CoreScoreHysteresisConfig Hysteresis(double maxDailyLossPct = 20.0, int maxConsecutiveLosses = 4, int maxTradesPerDay = 30, bool shadowMode = false) => new(
        EntryScoreThreshold: 30.0,
        RiskLimits: new CoreScoreRiskLimitsConfig(maxDailyLossPct, maxConsecutiveLosses, maxTradesPerDay),
        ShadowMode: shadowMode);

    public static CoreScoreCrossoverConfig Crossover(double maxDailyLossPct = 20.0, int maxConsecutiveLosses = 4, int maxTradesPerDay = 30, bool shadowMode = false) => new(
        FastWindowMinutes: 10,
        SlowWindowMinutes: 30,
        RiskLimits: new CoreScoreRiskLimitsConfig(maxDailyLossPct, maxConsecutiveLosses, maxTradesPerDay),
        ShadowMode: shadowMode);
}
