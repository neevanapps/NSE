using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Time;

namespace NiftySignal.Trading.Core.Scheduling;

/// <summary>Scheduling surface handed to handlers. Handlers can read engine time and schedule/cancel deadlines, nothing else.</summary>
public interface IEngineScheduler
{
    /// <summary>Engine time: the deadline time while a deadline callback runs, the group's AvailableAt while an event callback runs.</summary>
    DateTimeOffset Now { get; }

    /// <summary>Schedules a deadline at <paramref name="time"/> (must be at or after <see cref="Now"/>).</summary>
    DeadlineHandle Schedule(DateTimeOffset time, DeadlineClass deadlineClass, int variantOrdinal, string key);

    bool Cancel(DeadlineHandle handle);
}

/// <summary>The consumer of the ordered stream. Implemented by the (later) strategy/risk/execution host; CP1 only defines the seam.</summary>
public interface IEngineHandler
{
    /// <summary>Called for each event in <see cref="EventOrdering"/> order, with the clock at the event's AvailableAt.</summary>
    void OnEvent(EngineEvent engineEvent, IEngineScheduler scheduler);

    /// <summary>Called when a deadline fires, with the clock at exactly the deadline's time.</summary>
    void OnDeadline(ScheduledDeadline deadline, IEngineScheduler scheduler);
}

public sealed class LateEventException(string message) : InvalidOperationException(message);

public sealed class ConflictingDuplicateEventException(string message) : InvalidOperationException(message);

public sealed class DeadlineInPastException(string message) : InvalidOperationException(message);

public sealed class EngineFinishedException(string message) : InvalidOperationException(message);

/// <summary>Counters reported by the loop. All are deterministic functions of the input.</summary>
public sealed record EngineLoopStats(
    long EventsDelivered,
    long DuplicatesIgnored,
    long GroupsProcessed,
    long DeadlinesFired,
    int PendingDeadlines,
    DateTimeOffset Now,
    bool Finished);

/// <summary>
/// The single-threaded deterministic driver (contract sections 4.3, 4.4 and A.2).
/// <list type="bullet">
/// <item><b>Groups.</b> <see cref="ProcessGroup"/> takes all events of one AvailableAt T, orders them by
/// <see cref="EventOrdering"/> and delivers them atomically.</item>
/// <item><b>Deadlines before events.</b> Before a group at T is delivered, every queued deadline with time &lt;= T fires
/// in <see cref="DeadlineOrdering"/> order (clock = the deadline's time), including deadlines created by earlier
/// deadline callbacks. A deadline equal to T therefore fires before the events at T. After the group, deadlines that
/// became due at or before T (created by its handlers) drain before the call returns.</item>
/// <item><b>Quiet markets.</b> <see cref="AdvanceTo"/> (exclusive watermark, used by live heartbeats) fires deadlines with
/// time &lt; the watermark; <see cref="Finish"/> (explicit horizon, inclusive) fires the rest. Because a deadline callback
/// always sees the clock at the deadline time, heartbeats only change when in wall time a deadline is processed, never what it does.</item>
/// <item><b>No future data.</b> An event is not visible to any handler before its group is delivered, and a group is only
/// delivered after all deadlines at or before its time have fired.</item>
/// </list>
/// There is no wall clock, randomness or threading in this class. Re-entrant or concurrent use throws.
/// </summary>
public sealed class EngineLoop : IEngineScheduler
{
    private readonly IEngineHandler _handler;
    private readonly EngineClock _clock;
    private readonly DeadlineQueue _queue = new();
    private readonly Dictionary<EventIdentity, EngineEvent> _seen = new();
    private DateTimeOffset? _lastGroupTime;
    private long _nextCreationSeq = 1;
    private long _eventsDelivered;
    private long _duplicatesIgnored;
    private long _groupsProcessed;
    private long _deadlinesFired;
    private bool _finished;
    private bool _busy;

    public EngineLoop(IEngineHandler handler, DateTimeOffset start)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
        _clock = new EngineClock(start);
    }

    public DateTimeOffset Now => _clock.Now;

    public EngineLoopStats Stats => new(_eventsDelivered, _duplicatesIgnored, _groupsProcessed, _deadlinesFired, _queue.Count, _clock.Now, _finished);

    public IReadOnlyList<ScheduledDeadline> PendingDeadlines => _queue.Snapshot();

    public DeadlineHandle Schedule(DateTimeOffset time, DeadlineClass deadlineClass, int variantOrdinal, string key)
    {
        Utc.Require(time, nameof(time));
        ArgumentNullException.ThrowIfNull(key);
        if (!Enum.IsDefined(deadlineClass))
        {
            throw new ArgumentOutOfRangeException(nameof(deadlineClass));
        }

        if (_finished)
        {
            throw new EngineFinishedException("Cannot schedule after Finish.");
        }

        if (time < _clock.Now)
        {
            throw new DeadlineInPastException($"Deadline '{key}' at {time:O} is before engine time {_clock.Now:O}.");
        }

        var deadline = new ScheduledDeadline(time, deadlineClass, variantOrdinal, key, _nextCreationSeq++);
        _queue.Add(deadline);
        return deadline.Handle;
    }

    public bool Cancel(DeadlineHandle handle) => _queue.Cancel(handle);

    /// <summary>
    /// Delivers one availability group. All events must share one AvailableAt; their order in the argument is irrelevant.
    /// Exact re-deliveries (same identity and equal content) are ignored; same identity with different content, or an event
    /// at or before an already processed group, is a fault.
    /// </summary>
    public void ProcessGroup(IReadOnlyList<EngineEvent> group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Count == 0)
        {
            return;
        }

        Enter();
        try
        {
            var time = group[0].AvailableAt;
            foreach (var e in group)
            {
                if (e.AvailableAt != time)
                {
                    throw new ArgumentException("A group must contain events of exactly one AvailableAt.", nameof(group));
                }
            }

            var ordered = group.OrderBy(e => e, EventOrdering.Instance).ToList();
            var fresh = new List<EngineEvent>(ordered.Count);
            var inGroup = new Dictionary<EventIdentity, EngineEvent>();
            foreach (var e in ordered)
            {
                if (_seen.TryGetValue(e.Identity, out var previous) || inGroup.TryGetValue(e.Identity, out previous))
                {
                    if (!previous.Equals(e))
                    {
                        throw new ConflictingDuplicateEventException($"Event {e.Identity} was delivered twice with different content.");
                    }

                    _duplicatesIgnored++;
                    continue;
                }

                inGroup.Add(e.Identity, e);
                fresh.Add(e);
            }

            if (fresh.Count == 0)
            {
                return;
            }

            if ((_lastGroupTime is { } last && time <= last) || time < _clock.Now)
            {
                throw new LateEventException(
                    $"Group at {time:O} is not after the processed boundary (last group {_lastGroupTime:O}, engine time {_clock.Now:O}).");
            }

            RunDeadlines(time, inclusive: true);
            _clock.AdvanceTo(time);
            foreach (var e in fresh)
            {
                _seen.Add(e.Identity, e);
                _eventsDelivered++;
                _handler.OnEvent(e, this);
            }

            _lastGroupTime = time;
            _groupsProcessed++;
            RunDeadlines(time, inclusive: true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Live heartbeat / watermark: asserts that no undelivered event has AvailableAt strictly before <paramref name="watermark"/>.
    /// Fires deadlines with time &lt; watermark and moves the clock to the watermark. A watermark at or before engine time is a no-op.
    /// </summary>
    public void AdvanceTo(DateTimeOffset watermark)
    {
        Utc.Require(watermark, nameof(watermark));
        Enter();
        try
        {
            if (watermark <= _clock.Now)
            {
                return;
            }

            RunDeadlines(watermark, inclusive: false);
            _clock.AdvanceTo(watermark);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Ends the run at an explicit horizon (inclusive): fires every remaining deadline with time &lt;= horizon, including
    /// chained ones, then moves the clock to the horizon. Deadlines after the horizon stay pending and are reported.
    /// </summary>
    public void Finish(DateTimeOffset horizon)
    {
        Utc.Require(horizon, nameof(horizon));
        Enter();
        try
        {
            if (horizon < _clock.Now)
            {
                throw new ArgumentException($"Horizon {horizon:O} is before engine time {_clock.Now:O}.", nameof(horizon));
            }

            RunDeadlines(horizon, inclusive: true);
            _clock.AdvanceTo(horizon);
            _finished = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private void Enter()
    {
        if (_finished)
        {
            throw new EngineFinishedException("The engine loop has finished.");
        }

        if (_busy)
        {
            throw new InvalidOperationException("Re-entrant or concurrent use of EngineLoop is not allowed.");
        }

        _busy = true;
    }

    private void RunDeadlines(DateTimeOffset limit, bool inclusive)
    {
        while (_queue.Peek() is { } next && (next.Time < limit || (inclusive && next.Time == limit)))
        {
            var deadline = _queue.Pop();
            _clock.AdvanceTo(deadline.Time);
            _deadlinesFired++;
            _handler.OnDeadline(deadline, this);
        }
    }
}
