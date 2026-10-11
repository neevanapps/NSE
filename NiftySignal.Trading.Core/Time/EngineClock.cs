namespace NiftySignal.Trading.Core.Time;

/// <summary>
/// The only time source Trading.Core may read: engine time. It is never wall-clock time; it is the
/// availability time of the last processed event, fired deadline or watermark (contract section 4.4).
/// </summary>
public interface ITradingClock
{
    DateTimeOffset Now { get; }
}

/// <summary>
/// Monotonic, manually advanced <see cref="ITradingClock"/>. Only the engine loop advances it. All instants are
/// UTC <see cref="DateTimeOffset"/> values (offset zero), the same axis as the persisted <c>AvailableAt</c>.
/// </summary>
public sealed class EngineClock : ITradingClock
{
    public EngineClock(DateTimeOffset start)
    {
        Utc.Require(start, nameof(start));
        Now = start;
    }

    public DateTimeOffset Now { get; private set; }

    public void AdvanceTo(DateTimeOffset time)
    {
        Utc.Require(time, nameof(time));
        if (time < Now)
        {
            throw new InvalidOperationException($"Engine time cannot move backwards: {Now:O} -> {time:O}.");
        }

        Now = time;
    }
}

internal static class Utc
{
    /// <summary>Engine instants must carry offset zero so that equality, hashing and formatting never depend on a local offset.</summary>
    public static void Require(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} must be a UTC instant (offset zero), got offset {value.Offset}.", name);
        }
    }
}
