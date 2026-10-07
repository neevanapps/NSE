namespace NiftySignal.Domain.Entities;

public sealed class UpstoxSession
{
    public const int SingletonId = 1;
    public int Id { get; set; } = SingletonId;
    public string? Token { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public bool IsAnalyticsToken { get; set; }
    public bool IsValidAt(DateTimeOffset now) => !string.IsNullOrWhiteSpace(Token) && ExpiresAtUtc > now;
}
