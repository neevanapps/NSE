using System.Diagnostics;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace NiftySignal.Host;

/// <summary>
/// Operational limits of Observer recovery and live catch-up (section "Adaptive:Recovery"). None of them changes a metric: they bound how much is read at once, how long one live
/// recovery attempt may run and how often progress is published. The numbers are starting values, not measured optima.
/// </summary>
public sealed class AdaptiveRecoveryOptions
{
    public const string SectionName = "Adaptive:Recovery";

    /// <summary>One live recovery attempt is cancelled and reported as failed after this long.</summary>
    public int TimeLimitSeconds { get; set; } = 300;

    /// <summary>Pause before the next attempt after a TIMED OUT attempt (other failures keep the worker's 1 second retry).</summary>
    public int TimeoutRetryBackoffSeconds { get; set; } = 30;

    /// <summary>Received-time slice read per recovery query. A token's rows may be out of Id order by at most one slice; more than that fails the recovery.</summary>
    public int ReadWindowSeconds { get; set; } = 120;

    /// <summary>Minimum interval between recovery progress writes.</summary>
    public int ProgressIntervalMilliseconds { get; set; } = 1000;

    /// <summary>Largest number of rows one live-poll query returns; a larger backlog is drained by consecutive queries within the same poll.</summary>
    public int LiveReadBatchSize { get; set; } = 20000;

    /// <summary>Durable checkpoints of the Observer state, so a restart replays only the rows after the latest usable one.</summary>
    public bool CheckpointsEnabled { get; set; } = true;

    /// <summary>Wall-clock interval between checkpoints, in live processing and during a recovery replay. A starting value; its overhead is to be benchmarked on real data.</summary>
    public int CheckpointIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Usable checkpoints kept per session (the initial value is 3). Rows of another build or configuration are limited to the same number separately. While a replay that started from a checkpoint is unverified,
    /// that checkpoint and older ones are kept in addition.
    /// </summary>
    public int CheckpointRetention { get; set; } = 3;

    public bool IsValid() => CheckpointIntervalSeconds is >= 0 and <= 3600 && CheckpointRetention is >= 1 and <= 20 && TimeLimitSeconds is >= 30 and <= 3600 && TimeoutRetryBackoffSeconds is >= 1 and <= 600 && ReadWindowSeconds is >= 10 and <= 900
        && ProgressIntervalMilliseconds is >= 100 and <= 10000 && LiveReadBatchSize is >= 1000 and <= 200000;
}

public sealed class AdaptiveRecoveryTimeoutException(TimeSpan limit, string reached, Exception inner)
    : TimeoutException($"Adaptive recovery exceeded its {limit.TotalSeconds:0} s limit ({reached}). The attempt was cancelled; nothing it persisted is overwritten by the next attempt.", inner);

/// <summary>
/// Publishes what a running recovery has actually done into the runtime row. It is only ever called from the recovery's own loop, so a heartbeat that moves means the loop moved: there is no timer.
/// </summary>
internal sealed class AdaptiveRecoveryProgress(AdaptiveObserverDbContext db, long sessionId, TimeSpan interval)
{
    readonly Stopwatch _sinceStart = Stopwatch.StartNew();
    readonly Stopwatch _sinceWrite = Stopwatch.StartNew();
    readonly List<(string Phase, TimeSpan EnteredAt)> _phases = [("starting", TimeSpan.Zero)];

    public string Phase => _phases[^1].Phase;
    public long TicksProcessed { get; set; }
    public int BarsReplayed { get; set; }
    public int? PersistedBarsAtStart { get; set; }
    public DateTimeOffset? ReplayedThroughUtc { get; set; }

    public string Describe() => $"phase {Phase}, {TicksProcessed} ticks and {BarsReplayed} bars replayed, {_sinceStart.Elapsed.TotalSeconds:0.0} s elapsed";

    public string Timings() => string.Join(", ", _phases.Select((p, i) =>
        $"{p.Phase}={((i + 1 < _phases.Count ? _phases[i + 1].EnteredAt : _sinceStart.Elapsed) - p.EnteredAt).TotalMilliseconds:0}ms"));

    public async Task EnterAsync(string phase, CancellationToken ct)
    {
        _phases.Add((phase, _sinceStart.Elapsed));
        await WriteAsync(ct);
    }

    public Task AdvancedAsync(CancellationToken ct) => _sinceWrite.Elapsed >= interval ? WriteAsync(ct) : Task.CompletedTask;

    async Task WriteAsync(CancellationToken ct)
    {
        var runtime = await db.Runtime.SingleAsync(x => x.SessionId == sessionId, ct);
        var now = DateTimeOffset.UtcNow;
        runtime.LastHeartbeatUtc = now;
        runtime.LastRecoveryProgressUtc = now;
        runtime.RecoveryPhase = Phase;
        runtime.RecoveryTicksProcessed = TicksProcessed;
        runtime.RecoveryBarsReplayed = BarsReplayed;
        runtime.RecoveryPersistedBarsAtStart = PersistedBarsAtStart;
        runtime.RecoveryReplayedThroughUtc = ReplayedThroughUtc;
        await db.SaveChangesAsync(ct);
        _sinceWrite.Restart();
    }
}

/// <summary>
/// The day's Level-1 rows for recovery with bounded memory: received-time slices, one slice of read-ahead, per-token Id order into the duplicate normalizer (the order live polling feeds it), and clean ticks released
/// in global (AvailableAt, Id) order. A tick is released only when every row that could precede it has been normalized (AvailableAt is never earlier than ReceivedAt), so the engine sees exactly the sequence a
/// whole-day read, normalize and sort would produce, without ever holding the day.
/// </summary>
public sealed class AdaptiveRecoveryTickStream : IAdaptiveTickStream
{
    readonly AdaptiveSourceTickReader _reader;
    readonly NiftySignalDbContext _source;
    readonly DateOnly _day;
    readonly IReadOnlyCollection<string> _tokens;
    readonly DateTimeOffset _notBefore;
    readonly DateTimeOffset _through;
    readonly TimeSpan _window;
    readonly DateTimeOffset _dayEnd;
    readonly Dictionary<string, TokenBuffer> _buffers = new(StringComparer.Ordinal);
    readonly List<ObserverTokenTick> _held = [];
    readonly List<ObserverTokenTick> _pending = [];
    DateTimeOffset _windowStart;
    int _buffered;
    // Lowest raw Id of each of the last two slices: no row still unread can have a lower Id than all of them (see Capture).
    long _lowestPreviousSlice = long.MaxValue;
    long _lowestLastSlice = long.MaxValue;

    sealed class TokenBuffer
    {
        public List<ObserverRawTick> Rows = [];
        public long LastReleasedId = long.MinValue;
    }

    public AdaptiveRecoveryTickStream(AdaptiveSourceTickReader reader, NiftySignalDbContext source, DateOnly day, IReadOnlyCollection<string> tokens,
        DateTimeOffset notBefore, DateTimeOffset throughUtc, TimeSpan window)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        (_reader, _source, _day, _tokens, _notBefore, _through, _window) = (reader, source, day, tokens, notBefore, throughUtc, window);
        _windowStart = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime().AddMinutes(-1);
        _dayEnd = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUniversalTime();
    }

    public AdaptiveIncrementalTickNormalizer Normalizer { get; } = new();
    public bool Completed { get; private set; }
    public long LastRawId { get; private set; }
    public long RawRows { get; private set; }
    public long CleanRows { get; private set; }

    /// <summary>Largest number of rows (raw, awaiting release, or released but not yet orderable) held at once; excludes <see cref="Pending"/>.</summary>
    public int PeakBufferedRows { get; private set; }

    /// <summary>Clean ticks later than the cutoff, in raw Id order; complete only once <see cref="Completed"/>. The id cursor has passed them, so live polling must receive them.</summary>
    public IReadOnlyList<ObserverTokenTick> Pending => _pending;

    /// <summary>The next ordered batch of clean ticks at or before the cutoff (possibly empty); null when the day is exhausted.</summary>
    public async Task<IReadOnlyList<ObserverTokenTick>?> NextAsync(CancellationToken ct)
    {
        if (Completed) return null;
        // Slices wholly after the cutoff only feed Pending, so they are read as one final query.
        var end = _windowStart > _through || _dayEnd - _windowStart <= _window ? _dayEnd : _windowStart + _window;
        var final = end >= _dayEnd;
        var rows = await _reader.ReadReceivedWindowAsync(_source, _tokens, _windowStart, end, _day, ct);
        _windowStart = end;
        RawRows += rows.Count;
        _lowestPreviousSlice = _lowestLastSlice;
        _lowestLastSlice = rows.Count == 0 ? long.MaxValue : rows[0].Tick.Id;
        foreach (var (token, tick) in rows)
        {
            LastRawId = Math.Max(LastRawId, tick.Id);
            if (!_buffers.TryGetValue(token, out var b)) _buffers[token] = b = new TokenBuffer();
            if (b.Rows.Count > 0 && b.Rows[^1].Id > tick.Id)
            {
                var at = b.Rows.Count - 1;
                while (at > 0 && b.Rows[at - 1].Id > tick.Id) at--;
                b.Rows.Insert(at, tick);
            }
            else b.Rows.Add(tick);
            _buffered++;
        }

        PeakBufferedRows = Math.Max(PeakBufferedRows, _buffered + _held.Count);
        var releaseLimit = final ? DateTimeOffset.MaxValue : end - _window;
        foreach (var (token, b) in _buffers)
        {
            // Everything up to the last row received before the limit must pass the normalizer now, including any lower-Id row received later.
            var last = b.Rows.Count - 1;
            if (!final) while (last >= 0 && b.Rows[last].ReceivedAt >= releaseLimit) last--;
            for (var i = 0; i <= last; i++)
            {
                var raw = b.Rows[i];
                if (raw.Id < b.LastReleasedId)
                    throw new InvalidOperationException($"Adaptive recovery: token {token} row {raw.Id} arrived more than one read window after a higher row id; increase {AdaptiveRecoveryOptions.SectionName}:ReadWindowSeconds.");
                b.LastReleasedId = raw.Id;
                if (Normalizer.Process(token, raw) is not { } tick || tick.AvailableAt < _notBefore) continue;
                if (tick.AvailableAt > _through) _pending.Add(new ObserverTokenTick(token, tick));
                else _held.Add(new ObserverTokenTick(token, tick));
            }

            if (last >= 0) { b.Rows.RemoveRange(0, last + 1); _buffered -= last + 1; }
        }

        _held.Sort(static (a, b) =>
        {
            var byTime = a.Tick.AvailableAt.CompareTo(b.Tick.AvailableAt);
            return byTime != 0 ? byTime : a.Tick.Id.CompareTo(b.Tick.Id);
        });
        var count = final ? _held.Count : _held.FindIndex(x => x.Tick.AvailableAt >= releaseLimit);
        if (count < 0) count = _held.Count;
        var ready = _held.GetRange(0, count);
        _held.RemoveRange(0, count);
        CleanRows += ready.Count;
        if (final)
        {
            _pending.Sort(static (a, b) => a.Tick.Id.CompareTo(b.Tick.Id));
            Completed = true;
        }

        return ready;
    }

    /// <summary>
    /// The same state expressed as an Id position: resume below every buffered row and below every row of the last two slices (a row received later is never more than one slice behind in Id), and skip, per token,
    /// the rows already normalized.
    /// </summary>
    public (AdaptiveSourcePosition Position, List<ObserverTokenTick> Unreleased) Capture()
    {
        var lowest = Math.Min(_lowestPreviousSlice, _lowestLastSlice);
        foreach (var b in _buffers.Values)
            if (b.Rows.Count > 0) lowest = Math.Min(lowest, b.Rows[0].Id);
        var resume = lowest == long.MaxValue ? LastRawId : lowest - 1;
        var skip = _buffers.Where(x => x.Value.LastReleasedId > resume).ToDictionary(x => x.Key, x => x.Value.LastReleasedId, StringComparer.Ordinal);
        return (new AdaptiveSourcePosition(resume, skip), _held.Concat(_pending).ToList());
    }
}
