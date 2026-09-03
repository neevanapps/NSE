namespace NiftySignal.Notifications;

/// <summary>The exact send-on list from plan section 12. Deliberately does not include score changes -- "do not send on every score change."</summary>
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
}
