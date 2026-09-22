namespace NiftySignal.Notifications;

/// <summary>The exact send-on list from plan section 12, plus one addition. Deliberately does not include score changes -- "do not send on every score change."</summary>
public enum NotificationCategory
{
    TradeEntry,
    PartialBook,
    Exit,
    KillSwitchToggle,
    DailyLossCircuitBreakerTripped,
    ConnectionFailure,
    TokenExpiry,
    DailySummary,

    /// <summary>Every 30 minutes during the session (2026-09-04, not in the original plan section 12 list) -- open positions + today's closed-trade stats, distinct from DailySummary's once-a-day cadence (which nothing sends yet).</summary>
    PaperTradeSummary,

    /// <summary>
    /// 2026-09-21 (docs/LIVE_PARITY_PLAN.md "Exception alerting"): an unhandled exception caught by
    /// one of the new live volume-bar/options-score/paper-trade pipeline's own per-poll resilience
    /// catch blocks (<c>LiveVolumeBarWriter</c>, <c>LiveOptionsScoreEngine</c> -- audit findings
    /// F34/F49's "log and continue to next poll" pattern). The poll loop keeps retrying exactly as
    /// before; this category exists purely so a human finds out without reading logs. Also used for
    /// a full Host process crash (<c>Program.cs</c>'s top-level catch), which is strictly more
    /// severe than any single poll failure but shares the same category since both mean "go look at
    /// this pipeline now."
    /// </summary>
    LivePipelineError,
}
