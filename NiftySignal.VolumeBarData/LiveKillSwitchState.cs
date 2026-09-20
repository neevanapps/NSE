namespace NiftySignal.VolumeBarData;

/// <summary>
/// Phase G of docs/LIVE_PARITY_PLAN.md: the persisted, cross-process kill switch for THIS pipeline
/// (the volume-bar / <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c> paper strategy), deliberately
/// mirroring <c>NiftySignal.Domain.Entities.KillSwitchState</c>'s own exact shape and semantics --
/// see <c>CLAUDE.md</c>'s "Risk" section and that class's own doc comment for the established
/// precedent this copies rather than reinvents: a single row (Id=1), a DB-backed toggle (not an
/// appsettings value) so the Dashboard/an operator and the Host process can coordinate through the
/// one store they already share, disabling NEW entries only -- bar writing, scoring, and an
/// already-open paper trade's own exit logic (<see cref="TradingDaySession"/>'s
/// ScoreInvalidated/TimeCutoff/EndOfDay rules) all keep running regardless of this flag.
///
/// A SEPARATE row/table from <c>KillSwitchState</c> rather than reusing that exact table, for two
/// reasons: (1) it lives in <see cref="VolumeBarDbContext"/> (the volume-bar database), not
/// <c>NiftySignalDbContext</c> -- the same "own database" architecture decision
/// docs/LIVE_PARITY_PLAN.md made for every other Phase A-D table now applies here too; (2) this
/// pipeline is its own independent strategy from the three engines <c>KillSwitchState</c> already
/// gates (<c>LiveTradingEngine</c>, <c>CoreScoreHysteresisTradingEngine</c>,
/// <c>CoreScoreCrossoverTradingEngine</c>) -- sharing one row would mean flipping the switch for any
/// one of those four independent paper strategies silently halts all four, which is not what an
/// operator reaching for "stop THIS new pipeline specifically" (the task's own framing) would
/// expect. Same mechanism, deliberately separate instance -- not a second different way to do the
/// same thing.
/// </summary>
public sealed class LiveKillSwitchState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// False disables <see cref="LivePaperTradeExecutor.OpenAsync"/> only -- it declines to open a
    /// new paper trade for an entry signal that still fires (the signal itself, and its own
    /// exit-rule lifecycle in <see cref="TradingDaySession"/>, are unaffected, so the offline
    /// backtest/verify-parity comparison keeps seeing the same signals either way -- only whether a
    /// tradeable position was actually opened for one differs). An already-open
    /// <see cref="LivePaperTradeRow"/>'s own exit (<see cref="LivePaperTradeExecutor.CloseAsync"/>)
    /// is NEVER gated by this flag -- see docs/LIVE_PARITY_PLAN.md's Phase G section for the full
    /// reasoning on why "new entries only" was chosen over halting everything.
    /// </summary>
    public bool EntriesEnabled { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who/what last changed it -- free text (e.g. "manual", or an operator's own note) -- no Dashboard UI wires this yet (Phase G's own scope note), so this is set by whatever issues the SQL/tool that flips it.</summary>
    public required string UpdatedBy { get; set; }
}
