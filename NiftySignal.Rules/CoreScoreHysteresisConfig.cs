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
///
/// <see cref="ShadowMode"/> (Batch 6, 2026-09-14) -- when true, the engine runs the FULL entry/exit
/// decision logic (including the hysteresis band, all risk gates, strike selection) and logs what
/// it would have done, but never touches <c>db.PaperTrades</c> -- "purely computing and logging
/// what each strategy would do, with zero trading side effects" (docs/replication_plan.md
/// Assumption 4). Defaults to <c>true</c> deliberately: a fresh deploy of either new strategy must
/// require an explicit config change to start writing real rows, not silently go live the moment
/// this ships. See <c>CoreScoreHysteresisTradingEngine</c>'s own doc comment for how the
/// hypothetical open position is tracked (in-memory, not persisted) while shadow mode is on.
/// </summary>
public sealed record CoreScoreHysteresisConfig(double EntryScoreThreshold, CoreScoreRiskLimitsConfig RiskLimits, bool ShadowMode = true);
