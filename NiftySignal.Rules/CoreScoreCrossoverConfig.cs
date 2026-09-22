namespace NiftySignal.Rules;

/// <summary>
/// Config for <c>CoreScoreCrossoverTradingEngine</c> (Batch 5, A9). <see cref="FastWindowMinutes"/>/
/// <see cref="SlowWindowMinutes"/> are the backtest's own locked-in values (10/30, from
/// <c>CoreScoreOptionSimulator.CoreScoreCrossoverOptions</c>) -- documentation/validation fields
/// ONLY at this stage, deliberately NOT wired back into <c>LiveFeatureEngine</c>'s own
/// <c>CoreScoreFastWindow</c>/<c>CoreScoreSlowWindow</c> constants (hardcoded to the same 10/30
/// values there, already validated against the backtest by Batch 3's own exact-match replay diff).
/// Rewiring those constants to read from this config would be a genuinely new piece of plumbing
/// this plan section never asked for, and would risk disturbing an already-tight Batch 3 parity
/// for no live-trading benefit -- <c>CoreScoreCrossoverTradingEngine</c> only ever reads the
/// already-smoothed <c>CoreScoreSnapshot.CoreScoreFast</c>/<c>CoreScoreSlow</c> values, never the
/// window lengths themselves, so these two fields exist here purely so a config reader (a human,
/// or a future validation check) can see what window the currently-running engine is actually
/// built against without cross-referencing source code.
/// <see cref="RiskLimits"/> is this strategy's own independent risk box -- see
/// <see cref="CoreScoreRiskLimitsConfig"/>'s own doc comment. Every other input
/// (Capital/Session/StrikeSelection/Costs/KillSwitch) continues reading the EXISTING shared
/// <see cref="RulesetConfig"/> unchanged (Assumption 1 in docs/replication_plan.md).
///
/// <see cref="ShadowMode"/> (Batch 6, 2026-09-14) -- see <see cref="CoreScoreHysteresisConfig.ShadowMode"/>'s
/// own doc comment; identical meaning and default here.
/// </summary>
public sealed record CoreScoreCrossoverConfig(int FastWindowMinutes, int SlowWindowMinutes, CoreScoreRiskLimitsConfig RiskLimits, bool ShadowMode = true);
