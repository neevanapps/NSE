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
}
