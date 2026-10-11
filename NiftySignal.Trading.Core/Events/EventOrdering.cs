namespace NiftySignal.Trading.Core.Events;

/// <summary>
/// The total order of input events: (AvailableAt, kind rank, SourceId, token ordinal). Events with equal AvailableAt form
/// one atomic availability group that is processed in this order. Culture-independent (ordinal string comparison).
/// </summary>
public sealed class EventOrdering : IComparer<EngineEvent>
{
    public static readonly EventOrdering Instance = new();

    private EventOrdering()
    {
    }

    public int Compare(EngineEvent? x, EngineEvent? y)
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

        var c = x.AvailableAt.UtcTicks.CompareTo(y.AvailableAt.UtcTicks);
        if (c != 0)
        {
            return c;
        }

        c = ((int)x.Kind).CompareTo((int)y.Kind);
        if (c != 0)
        {
            return c;
        }

        c = x.SourceId.CompareTo(y.SourceId);
        if (c != 0)
        {
            return c;
        }

        return string.CompareOrdinal(x.Token, y.Token);
    }
}
