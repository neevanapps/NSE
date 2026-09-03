namespace NiftySignal.Ingestion;

/// <summary>
/// Exponential backoff with jitter, capped at 30s, alerting after 5 consecutive failures
/// (plan section 3.4). The base-delay schedule is exposed separately from jitter so it can
/// be asserted exactly in tests without fighting randomness.
/// </summary>
public sealed class ReconnectionPolicy(Random? random = null)
{
    static readonly TimeSpan[] Steps =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    ];

    public const int AlertThreshold = 5;

    readonly Random _random = random ?? Random.Shared;

    public int ConsecutiveFailures { get; private set; }

    public bool ShouldAlert => ConsecutiveFailures >= AlertThreshold;

    public void RecordFailure() => ConsecutiveFailures++;

    public void RecordSuccess() => ConsecutiveFailures = 0;

    /// <summary>The un-jittered backoff step for a given failure count. 0 before any failure.</summary>
    public static TimeSpan BaseDelayFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        var index = Math.Min(consecutiveFailures - 1, Steps.Length - 1);
        return Steps[index];
    }

    /// <summary>The delay to wait before the next reconnect attempt, with +/-20% jitter applied.</summary>
    public TimeSpan NextDelay()
    {
        var baseDelay = BaseDelayFor(ConsecutiveFailures);
        var jitterFactor = 0.8 + (_random.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * jitterFactor);
    }
}
