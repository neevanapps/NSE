namespace NiftySignal.Rules;

/// <summary>
/// Per-strategy risk limits for the two new Core-score trading engines (2026-09-13, live-wiring
/// plan Batch 5, A9) -- deliberately a DIFFERENT shape from the existing <see cref="RiskLimitsConfig"/>
/// (which has its own <c>MaxDailyProfitPct</c>, not carried over here; A9's own wording lists only
/// <c>MaxDailyLossPct</c>/<c>MaxConsecutiveLosses</c>/<c>MaxTradesPerDay</c> as the independent
/// fields each strategy needs), not a reuse of it -- reusing the existing type would either drag
/// along an unused MaxDailyProfitPct or require a breaking change to every existing caller.
/// <see cref="MaxTradesPerDay"/> moves here from the legacy engine's own <c>EntryConfig</c> shape
/// since these two strategies have no other use for the rest of that record (session/sustain
/// timing, IV-rank gate -- none of which either backtested strategy was validated against).
/// </summary>
public sealed record CoreScoreRiskLimitsConfig(double MaxDailyLossPct, int MaxConsecutiveLosses, int MaxTradesPerDay);

/// <summary>
/// Config for <c>CoreScoreHysteresisTradingEngine</c> (Batch 5, A9) -- <see cref="EntryScoreThreshold"/>
/// is the backtest's own locked-in value (30, from <c>CoreScoreOptionSimulator.CoreScoreSimulationOptions</c>).
/// <see cref="RiskLimits"/> is this strategy's own independent risk box; every other input
/// (Capital/Session/StrikeSelection/Costs/KillSwitch) continues reading the EXISTING shared
/// <see cref="RulesetConfig"/> unchanged (Assumption 1 in docs/replication_plan.md -- one shared
/// capital pool, one shared session window, across both new strategies and the legacy engine).
/// </summary>
public sealed record CoreScoreHysteresisConfig(double EntryScoreThreshold, CoreScoreRiskLimitsConfig RiskLimits);
