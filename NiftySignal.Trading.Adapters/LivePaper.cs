using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Trading.Adapters;

public enum PollStatus
{
    Ok = 0,
    Disconnected = 1,
}

/// <summary>
/// The result of one poll of the source. <see cref="PolledAt"/> is the RECORDED source time of the poll (journaled; in a live deployment the poller's
/// own timestamp, in a recorded session the schedule's time) — never read from a wall clock by this library. <see cref="HasMore"/> says the batch was
/// cut short while more rows already existed; the adapter then claims no progress beyond the rows it received, which is what stops a poll batch from
/// splitting an availability group.
/// </summary>
public sealed record PollResult(DateTimeOffset PolledAt, PollStatus Status, IReadOnlyList<SourceTick> Rows, bool HasMore, bool SourceExhausted);

public interface IPollSource
{
    /// <summary>Returns rows with an id above <paramref name="afterId"/> (plus, for an overlapping source, some already delivered rows).</summary>
    PollResult Poll(long afterId);
}

/// <summary>One scripted poll of the fake recorded source.</summary>
/// <param name="Interval">Recorded time since the previous poll.</param>
/// <param name="MaxRows">Batch limit.</param>
/// <param name="Overlap">How many already delivered rows (ids at or below the cursor) are returned again in front of the new ones.</param>
/// <param name="Disconnected">The poll fails (no rows, no progress).</param>
public sealed record PollStep(TimeSpan Interval, int MaxRows, int Overlap = 0, bool Disconnected = false);

/// <summary>
/// Fake poll source over recorded rows: at recorded poll time P it returns the rows with <c>Id &gt; cursor</c> and <c>ReceivedAt &lt;= P</c> that existed then,
/// in id order, up to the step's batch limit. The steps are cycled, so one short script produces a whole day of varied batch sizes, overlaps, delays and
/// disconnects. No wall clock, no threads, no I/O: a run is a pure function of rows and steps.
/// </summary>
public sealed class RecordedPollSource : IPollSource, IDisposable
{
    /// <summary>How many already delivered rows are kept for overlapping polls (the largest supported <see cref="PollStep.Overlap"/>).</summary>
    public const int MaxOverlap = 256;

    private readonly IEnumerator<SourceTick> _rows;
    private readonly Func<int, PollStep> _stepAt;
    private readonly List<SourceTick> _delivered = new();
    private SourceTick? _peek;
    private bool _ended;
    private long _lastId = long.MinValue;
    private int _pollIndex;
    private DateTimeOffset _clock;

    /// <param name="rowsInIdOrder">The recorded rows in strictly increasing id order. Enumerated lazily, once, with one row of look-ahead: a session is never materialised.</param>
    /// <param name="stepAt">The script: the step of the k-th poll (zero-based). Must be deterministic. A script that fails forever never progresses, so callers must let polls succeed eventually.</param>
    public RecordedPollSource(IEnumerable<SourceTick> rowsInIdOrder, Func<int, PollStep> stepAt, DateTimeOffset start)
    {
        ArgumentNullException.ThrowIfNull(rowsInIdOrder);
        ArgumentNullException.ThrowIfNull(stepAt);
        _rows = rowsInIdOrder.GetEnumerator();
        _stepAt = stepAt;
        _clock = start;
    }

    public void Dispose() => _rows.Dispose();

    private SourceTick? Peek()
    {
        if (_peek is null && !_ended)
        {
            if (_rows.MoveNext())
            {
                _peek = _rows.Current;
                if (_peek.Id <= _lastId)
                {
                    throw new InvalidOperationException("Rows must be in strictly increasing id order.");
                }

                _lastId = _peek.Id;
            }
            else
            {
                _ended = true;
            }
        }

        return _peek;
    }

    /// <summary>A source whose script is a short list of steps repeated for the whole day.</summary>
    public static RecordedPollSource Cycle(IEnumerable<SourceTick> rowsInIdOrder, IReadOnlyList<PollStep> steps, DateTimeOffset start)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0 || steps.Any(s => s.Interval <= TimeSpan.Zero || s.MaxRows < 1 || s.Overlap < 0))
        {
            throw new ArgumentException("Poll steps need a positive interval and batch limit and a non-negative overlap.", nameof(steps));
        }

        if (steps.All(s => s.Disconnected))
        {
            throw new ArgumentException("At least one poll step must succeed, otherwise the source never progresses.", nameof(steps));
        }

        return new RecordedPollSource(rowsInIdOrder, k => steps[k % steps.Count], start);
    }

    public PollResult Poll(long afterId)
    {
        var step = _stepAt(_pollIndex++);
        if (step.Interval <= TimeSpan.Zero || step.MaxRows < 1 || step.Overlap < 0)
        {
            throw new InvalidOperationException("A poll step needs a positive interval and batch limit and a non-negative overlap.");
        }

        _clock += step.Interval;
        if (step.Disconnected)
        {
            return new PollResult(_clock, PollStatus.Disconnected, Array.Empty<SourceTick>(), false, false);
        }

        if (step.Overlap > MaxOverlap)
        {
            throw new InvalidOperationException($"Overlap is limited to {MaxOverlap} rows.");
        }

        var batch = new List<SourceTick>();
        if (step.Overlap > 0)
        {
            batch.AddRange(_delivered.Skip(Math.Max(0, _delivered.Count - step.Overlap)));
        }

        var fresh = 0;
        while (Peek() is { } next && fresh < step.MaxRows && next.ReceivedAt <= _clock)
        {
            batch.Add(next);
            _delivered.Add(next);
            if (_delivered.Count > MaxOverlap * 2)
            {
                _delivered.RemoveRange(0, _delivered.Count - MaxOverlap);
            }

            _peek = null;
            fresh++;
        }

        var hasMore = Peek() is { } more && more.ReceivedAt <= _clock;
        var exhausted = Peek() is null;
        return new PollResult(_clock, PollStatus.Ok, batch, hasMore, exhausted);
    }
}

/// <summary>A source that returns a precomputed list of polls (tests of delayed, out-of-order and otherwise unusual deliveries).</summary>
public sealed class ScriptedPollSource : IPollSource
{
    private readonly Queue<PollResult> _polls;

    public ScriptedPollSource(IEnumerable<PollResult> polls) => _polls = new Queue<PollResult>(polls);

    public PollResult Poll(long afterId)
        => _polls.Count > 0 ? _polls.Dequeue() : throw new InvalidOperationException("The scripted poll source has no more polls.");
}

/// <summary>
/// Feed watchdog thresholds. EVERY value is explicit: there are no defaults, because no production value has been approved. Use
/// <see cref="TestOnly"/> for harness runs; its values are labelled and must not be read as recommendations.
/// </summary>
public sealed record WatchdogOptions
{
    /// <summary>Silence (no admitted row from the source) longer than this, observed at a poll, while monitoring, degrades the feed.</summary>
    public required TimeSpan NoMessageTimeout { get; init; }

    /// <summary>Consecutive failed polls before the feed is declared degraded.</summary>
    public required int FailedPollsBeforeDegraded { get; init; }

    /// <summary>Monitoring window (recorded source time). Silence outside it is normal.</summary>
    public required DateTimeOffset MonitorFromUtc { get; init; }

    public required DateTimeOffset MonitorThroughUtc { get; init; }

    /// <summary>Where the values come from, printed with every report. Never empty.</summary>
    public required string Provenance { get; init; }

    public static WatchdogOptions TestOnly(DateTimeOffset monitorFromUtc, DateTimeOffset monitorThroughUtc, TimeSpan? noMessage = null, int failedPolls = 2)
        => new()
        {
            NoMessageTimeout = noMessage ?? TimeSpan.FromSeconds(30),
            FailedPollsBeforeDegraded = failedPolls,
            MonitorFromUtc = monitorFromUtc,
            MonitorThroughUtc = monitorThroughUtc,
            Provenance = "TEST-ONLY harness value: not an approved production threshold",
        };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (NoMessageTimeout <= TimeSpan.Zero)
        {
            errors.Add("NoMessageTimeout must be positive.");
        }

        if (FailedPollsBeforeDegraded < 1)
        {
            errors.Add("FailedPollsBeforeDegraded must be at least 1.");
        }

        if (MonitorThroughUtc <= MonitorFromUtc)
        {
            errors.Add("The monitoring window must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(Provenance))
        {
            errors.Add("Provenance of the watchdog thresholds must be stated.");
        }

        return errors;
    }
}

/// <summary>One journaled observation of a poll: everything the watchdog decided from, so the health events can be re-derived without the source.</summary>
public sealed record HeartbeatEntry(long Ordinal, DateTimeOffset PolledAt, PollStatus Status, int AdmittedRows, DateTimeOffset? LatestReceivedAt, bool HasMore, FeedHealthEvent? Health);

/// <summary>
/// Deterministic feed watchdog over recorded source time (contract section A.16.5). It is a pure state machine over <see cref="HeartbeatEntry"/>
/// inputs; it never reads a clock. Degrades on consecutive failed polls or on silence beyond the threshold inside the monitoring window; recovers on
/// the first successful poll after a disconnect, or when rows resume after a silence. Health events are stamped with the poll's recorded time.
/// </summary>
public sealed class FeedWatchdog
{
    private readonly WatchdogOptions _options;
    private FeedFaultReason _faultReason = FeedFaultReason.None;
    private int _failedStreak;
    private DateTimeOffset _lastMessageAt;
    private long _sequence;

    public FeedWatchdog(WatchdogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid watchdog options: " + string.Join(" ", errors), nameof(options));
        }

        _options = options;
        _lastMessageAt = options.MonitorFromUtc;
    }

    public WatchdogOptions Options => _options;

    public bool Degraded => _faultReason != FeedFaultReason.None;

    /// <summary>Feeds one observation; returns the health event it produced, if any.</summary>
    public FeedHealthEvent? Observe(DateTimeOffset polledAt, PollStatus status, int admittedRows, DateTimeOffset? latestReceivedAt)
    {
        if (status == PollStatus.Disconnected)
        {
            _failedStreak++;
            if (!Degraded && _failedStreak >= _options.FailedPollsBeforeDegraded)
            {
                _faultReason = FeedFaultReason.SocketDisconnected;
                return new FeedHealthEvent(polledAt, ++_sequence, FeedHealthState.Degraded, FeedFaultReason.SocketDisconnected, $"{_failedStreak} consecutive failed polls ({_options.Provenance})");
            }

            return null;
        }

        _failedStreak = 0;
        if (admittedRows > 0 && latestReceivedAt is { } latest && latest > _lastMessageAt)
        {
            _lastMessageAt = latest;
        }

        if (_faultReason == FeedFaultReason.SocketDisconnected)
        {
            _faultReason = FeedFaultReason.None;
            return new FeedHealthEvent(polledAt, ++_sequence, FeedHealthState.Healthy, FeedFaultReason.None, "first successful poll after the disconnect");
        }

        if (_faultReason == FeedFaultReason.NoMessageWatchdog)
        {
            if (admittedRows > 0)
            {
                _faultReason = FeedFaultReason.None;
                return new FeedHealthEvent(polledAt, ++_sequence, FeedHealthState.Healthy, FeedFaultReason.None, "rows resumed after the silence");
            }

            return null;
        }

        var monitored = polledAt >= _options.MonitorFromUtc && polledAt <= _options.MonitorThroughUtc;
        if (monitored && polledAt - _lastMessageAt > _options.NoMessageTimeout)
        {
            _faultReason = FeedFaultReason.NoMessageWatchdog;
            return new FeedHealthEvent(polledAt, ++_sequence, FeedHealthState.Degraded, FeedFaultReason.NoMessageWatchdog, $"no row since {_lastMessageAt:O} ({_options.Provenance})");
        }

        return null;
    }

    /// <summary>Re-derives the health events of a journal (replay of a recorded run without the source).</summary>
    public static IReadOnlyList<FeedHealthEvent> Replay(WatchdogOptions options, IEnumerable<HeartbeatEntry> journal)
    {
        var watchdog = new FeedWatchdog(options);
        var events = new List<FeedHealthEvent>();
        foreach (var entry in journal)
        {
            if (watchdog.Observe(entry.PolledAt, entry.Status, entry.AdmittedRows, entry.LatestReceivedAt) is { } e)
            {
                events.Add(e);
            }
        }

        return events;
    }
}

public sealed record LivePaperOptions
{
    /// <summary>Optional watchdog. Null: health events come only from the scripted controls handed to the run.</summary>
    public WatchdogOptions? Watchdog { get; init; }
}

public sealed record LivePaperResult(RunResult Run, IReadOnlyList<HeartbeatEntry> Journal);

/// <summary>
/// Non-hosted live-paper adapter library (contract section A.16.5). It polls an <see cref="IPollSource"/> (a fake recorded source here; a database
/// poller in a later hosted deployment), feeds the rows to the shared translator, claims source progress only when a poll was not cut short, journals
/// every heartbeat and health decision, delivers the released groups to the engine and advances the engine clock to the stable boundary after each
/// poll (a heartbeat, so quiet-market deadlines fire). Duplicates and overlapping polls are absorbed by the translator; a failed poll claims no progress.
/// Real-order execution does not exist here: the engine behind it is the simulated one.
/// </summary>
public static class LivePaperAdapter
{
    public static LivePaperResult Run(TradingRun run, IPollSource source, LivePaperOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(source);
        var watchdog = options?.Watchdog is { } w ? new FeedWatchdog(w) : null;
        var journal = new List<HeartbeatEntry>();
        var cursor = long.MinValue;
        long ordinal = 0;
        while (true)
        {
            var poll = source.Poll(cursor);
            if (poll.Status == PollStatus.Disconnected)
            {
                run.NotePoll(0, failed: true);
                var failedHealth = watchdog?.Observe(poll.PolledAt, PollStatus.Disconnected, 0, null);
                journal.Add(new HeartbeatEntry(ordinal++, poll.PolledAt, PollStatus.Disconnected, 0, null, false, failedHealth));
                if (failedHealth is not null)
                {
                    run.AddControl(failedHealth);
                }

                run.Pump(heartbeat: false);
                if (poll.SourceExhausted)
                {
                    break;
                }

                continue;
            }

            var admitted = 0;
            DateTimeOffset? latest = null;
            foreach (var row in poll.Rows)
            {
                var disposition = run.Offer(row);
                if (row.Id > cursor)
                {
                    cursor = row.Id;
                }

                if (disposition is RowDisposition.Accepted or RowDisposition.PayloadDuplicate or RowDisposition.OutsideWindow)
                {
                    admitted++;
                    if (latest is null || row.ReceivedAt > latest)
                    {
                        latest = row.ReceivedAt;
                    }
                }
            }

            run.NotePoll(poll.Rows.Count, failed: false);
            if (!poll.HasMore)
            {
                run.Translator.AdvanceSource(poll.PolledAt);
            }

            var health = watchdog?.Observe(poll.PolledAt, PollStatus.Ok, admitted, latest);
            journal.Add(new HeartbeatEntry(ordinal++, poll.PolledAt, PollStatus.Ok, admitted, latest, poll.HasMore, health));
            if (health is not null)
            {
                run.AddControl(health);
            }

            run.Pump(heartbeat: true);
            if (poll.SourceExhausted)
            {
                break;
            }
        }

        return new LivePaperResult(run.Finish(), journal);
    }
}
