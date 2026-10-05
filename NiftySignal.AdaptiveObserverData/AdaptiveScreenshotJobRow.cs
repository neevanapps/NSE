namespace NiftySignal.AdaptiveObserverData;

public enum AdaptiveScreenshotKind { Initialization = 0, FiveBars = 1, PreLiveTest = 2 }
public enum AdaptiveScreenshotStatus { Pending = 0, Capturing = 1, Sending = 2, Sent = 3, DeliveryUncertain = 4 }

/// <summary>Notification outbox only. Never participates in adaptive calculations.</summary>
public sealed class AdaptiveScreenshotJobRow
{
    public long Id { get; set; }
    public long? SessionId { get; set; }
    public DateOnly TradeDate { get; set; }
    public int TargetBarSeq { get; set; }
    public AdaptiveScreenshotKind Kind { get; set; }
    public AdaptiveScreenshotStatus Status { get; set; }
    public DateTimeOffset TriggeredAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public DateTimeOffset? CapturedAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public string? ImageSha256 { get; set; }
    public string? Caption { get; set; }
    public long? TelegramMessageId { get; set; }
    public string? LastError { get; set; }
    public int Attempts { get; set; }
}
