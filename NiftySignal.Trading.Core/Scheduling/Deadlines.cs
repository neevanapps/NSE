namespace NiftySignal.Trading.Core.Scheduling;

/// <summary>
/// Deadline class. The numeric value is the tie-break rank among deadlines with the same time (contract section A.2):
/// execution maturities first, then session boundaries, then risk/exit retries, then strategy timers.
/// </summary>
public enum DeadlineClass
{
    ExecutionMaturity = 0,
    SessionBoundary = 1,
    RiskRetry = 2,
    StrategyTimer = 3,
}

/// <summary>Opaque handle for cancelling a scheduled deadline. Equal to the deadline's creation sequence.</summary>
public readonly record struct DeadlineHandle(long Id);

/// <summary>
/// A deadline queued in the engine. Fires with decision time == <see cref="Time"/>, never later, regardless of when the
/// loop is driven. <see cref="VariantOrdinal"/> is the variant's position in the sorted VariantId list
/// (<see cref="GlobalOrdinal"/> for engine-wide deadlines). <see cref="CreationSeq"/> is the engine's own counter.
/// </summary>
public sealed record ScheduledDeadline(DateTimeOffset Time, DeadlineClass Class, int VariantOrdinal, string Key, long CreationSeq)
{
    /// <summary>Ordinal for deadlines that belong to no variant; sorts before every variant ordinal.</summary>
    public const int GlobalOrdinal = -1;

    public DeadlineHandle Handle => new(CreationSeq);
}

/// <summary>
/// Total order of deadlines: (Time, Class, VariantOrdinal, Key ordinal, CreationSeq). Culture-independent; the final
/// component is unique, so the order is strict.
/// </summary>
public sealed class DeadlineOrdering : IComparer<ScheduledDeadline>
{
    public static readonly DeadlineOrdering Instance = new();

    private DeadlineOrdering()
    {
    }

    public int Compare(ScheduledDeadline? x, ScheduledDeadline? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var c = x.Time.UtcTicks.CompareTo(y.Time.UtcTicks);
        if (c != 0)
        {
            return c;
        }

        c = ((int)x.Class).CompareTo((int)y.Class);
        if (c != 0)
        {
            return c;
        }

        c = x.VariantOrdinal.CompareTo(y.VariantOrdinal);
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(x.Key, y.Key);
        if (c != 0)
        {
            return c;
        }

        return x.CreationSeq.CompareTo(y.CreationSeq);
    }
}

/// <summary>Priority queue of deadlines with cancellation. Not thread-safe (the engine is single-threaded by contract).</summary>
public sealed class DeadlineQueue
{
    private readonly SortedSet<ScheduledDeadline> _set = new(DeadlineOrdering.Instance);
    private readonly Dictionary<long, ScheduledDeadline> _byId = new();

    public int Count => _set.Count;

    public ScheduledDeadline? Peek() => _set.Count == 0 ? null : _set.Min;

    public void Add(ScheduledDeadline deadline)
    {
        if (!_byId.TryAdd(deadline.CreationSeq, deadline))
        {
            throw new InvalidOperationException($"Duplicate deadline creation sequence {deadline.CreationSeq}.");
        }

        _set.Add(deadline);
    }

    public ScheduledDeadline Pop()
    {
        var min = _set.Min ?? throw new InvalidOperationException("Deadline queue is empty.");
        _set.Remove(min);
        _byId.Remove(min.CreationSeq);
        return min;
    }

    public bool Cancel(DeadlineHandle handle)
    {
        if (!_byId.Remove(handle.Id, out var deadline))
        {
            return false;
        }

        _set.Remove(deadline);
        return true;
    }

    /// <summary>Snapshot in firing order (diagnostics and tests).</summary>
    public IReadOnlyList<ScheduledDeadline> Snapshot() => _set.ToArray();
}
