using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Trading.Core.Scheduling;

/// <summary>Outcome of a replay. <see cref="StoppedAtHorizon"/> is true when the input contained events after the horizon (they were not delivered).</summary>
public sealed record ReplayResult(EngineLoopStats Stats, bool StoppedAtHorizon);

/// <summary>
/// Replays an ordered event stream through an <see cref="EngineLoop"/> with an <b>explicit horizon</b> (contract section A.2).
/// The horizon is required: after the last event, every deadline up to and including the horizon still fires (so an exit,
/// cancellation or close deadline is not skipped because the final market event preceded it). Events after the horizon are not
/// delivered; replay never extends past what the caller asked for.
/// <para>
/// Input must be non-decreasing in AvailableAt; within one AvailableAt the order is irrelevant (the group is sorted by
/// <see cref="EventOrdering"/>). The driver buffers one availability group at a time, so no event is visible to a handler before
/// the deadlines at or before its time have fired. Ordering is validated up to the horizon only.
/// </para>
/// </summary>
public static class ReplayDriver
{
    public static ReplayResult Run(EngineLoop loop, IEnumerable<EngineEvent> events, DateTimeOffset horizon)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(events);

        var group = new List<EngineEvent>();
        DateTimeOffset groupTime = default;
        var stopped = false;
        foreach (var e in events)
        {
            if (e.AvailableAt > horizon)
            {
                stopped = true;
                break;
            }

            if (group.Count > 0 && e.AvailableAt != groupTime)
            {
                if (e.AvailableAt < groupTime)
                {
                    throw new LateEventException(
                        $"Replay input is not ordered: event {e.Identity} at {e.AvailableAt:O} follows a group at {groupTime:O}.");
                }

                loop.ProcessGroup(group);
                group.Clear();
            }

            if (group.Count == 0)
            {
                groupTime = e.AvailableAt;
            }

            group.Add(e);
        }

        if (group.Count > 0)
        {
            loop.ProcessGroup(group);
        }

        loop.Finish(horizon);
        return new ReplayResult(loop.Stats, stopped);
    }
}
