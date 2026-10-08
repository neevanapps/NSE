using System.Text;

namespace NiftySignal.AdaptiveObserver.Commentary;

/// <summary>The most recent notification already queued for the session, used only for the same-event cooldown.</summary>
public sealed record LastCommentaryNotification(CommentaryEventType EventType, EventBias Bias, int BarSeq);

/// <summary>
/// Operational notification throttle and message text (08-Oct plan sections 60-61). The throttle is an operational convention, not a market
/// parameter, and is not tuned against outcomes. Telegram eligibility itself is decided by <see cref="CommentaryEvaluator.TelegramPolicy"/>.
/// </summary>
public static class CommentaryNotificationPolicy
{
    /// <summary>Default same-event cooldown, in completed bars. Applies to a repeated EventType + Bias.</summary>
    public const int MinimumBarsBetweenSameEventTelegram = 5;

    /// <summary>
    /// Reasons that are materially new and therefore bypass the cooldown. In V1 every Telegram-eligible class (Confirmed, Flipped,
    /// BiasChanged) is in this set, so the cooldown currently has no practical effect; it exists so that enabling more eligible classes later
    /// cannot flood Telegram.
    /// </summary>
    static readonly HashSet<string> BypassReasons = new(StringComparer.Ordinal) { "Confirmed", "Flipped", "BiasChanged" };

    public static bool ShouldEnqueue(CommentaryEvent e, LastCommentaryNotification? last, int cooldownBars = MinimumBarsBetweenSameEventTelegram)
    {
        if (!e.ShouldNotifyTelegram) return false;
        if (e.NotificationReason is { } reason && BypassReasons.Contains(reason)) return true;
        return last is null || last.EventType != e.EventType || last.Bias != e.EventBias || e.BarSeq - last.BarSeq >= cooldownBars;
    }

    /// <summary>Telegram text for an event: the deterministic rendered commentary plus identification. Never exceeds the Bot API text limit.</summary>
    public static string FormatMessage(DateOnly tradeDate, int barSeq, DateTimeOffset occurredAtUtc, string renderedCommentary)
    {
        var ist = occurredAtUtc.ToOffset(TimeSpan.FromHours(5.5));
        var sb = new StringBuilder();
        sb.Append("NIFTY adaptive commentary | ").Append(tradeDate.ToString("yyyy-MM-dd")).Append(" | bar ").Append(barSeq)
            .Append(" | ").Append(ist.ToString("HH:mm:ss")).Append(" IST").Append('\n');
        sb.Append(renderedCommentary).Append('\n');
        sb.Append("Interpretation hypotheses; EvidenceAgreement is not a probability or a trade signal.");
        var text = sb.ToString();
        return text.Length <= 4000 ? text : text[..3997] + "...";
    }
}
