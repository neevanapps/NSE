using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NiftySignal.AdaptiveObserver;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.PaperEngineV2.Runtime;

/// <summary>
/// The latest observed top of book per option token, fed from the ticks the Observer ALREADY processed (one O(1) step per tick, on the Observer thread; the same ticks the QuoteBook sees). It is deliberately a
/// separate structure from the QuoteBook: it never influences a frame, a fill or a decision. A quote replaces the stored one ONLY if it is newer by (availability time, tick id), so an out-of-order tick can never
/// overwrite a newer quote. Every option-universe token is tracked (so a position that was just filled, or one outside ATM+-2, is covered with no subscription change and no hand-off), the set is bounded by the
/// session's universe, and a token is never substituted for another.
/// </summary>
public sealed class LiveMarkBoard
{
    readonly ConcurrentDictionary<string, MarkQuote> _latest = new(StringComparer.Ordinal);
    readonly HashSet<string> _tokens;
    long _observed, _ignoredOutOfOrder;

    public LiveMarkBoard(IEnumerable<string> optionTokens) => _tokens = new HashSet<string>(optionTokens, StringComparer.Ordinal);

    public long Observed => Interlocked.Read(ref _observed);

    public long IgnoredOutOfOrder => Interlocked.Read(ref _ignoredOutOfOrder);

    public int Tracked => _latest.Count;

    /// <summary>Observer thread (single writer). Never waits, never throws for a normal tick.</summary>
    public void Observe(string token, in CleanObserverTick tick)
    {
        if (!_tokens.Contains(token)) return;
        if (_latest.TryGetValue(token, out var cur) && (tick.AvailableAt < cur.AvailableAt || (tick.AvailableAt == cur.AvailableAt && tick.Id <= cur.SourceTickId)))
        {
            Interlocked.Increment(ref _ignoredOutOfOrder);
            return;
        }

        _latest[token] = new MarkQuote(token, tick.Bid, tick.Ask, tick.Last, tick.AvailableAt, tick.Id);
        Interlocked.Increment(ref _observed);
    }

    public MarkQuote? TryGet(string token) => _latest.TryGetValue(token, out var q) ? q : null;
}

public sealed record MarkHeartbeat(long Seq, DateTimeOffset At, int Positions, long Observed, long IgnoredOutOfOrder, long PublishFailures);

/// <summary>Persists marks. Implementations must be safe to fail: the publisher contains every exception.</summary>
public interface ILiveMarkStore
{
    /// <summary>Upserts the changed marks and the heartbeat in one round trip.</summary>
    Task PublishAsync(IReadOnlyList<LiveMark> changed, MarkHeartbeat heartbeat, CancellationToken ct);
}

/// <summary>
/// Read-only mark publication, about once a second: engine position snapshot + board quotes -> pure calculator -> one batched upsert of what CHANGED (status, quote tick, closed) plus a heartbeat row. It is a SAMPLER,
/// not a queue: nothing accumulates, so there is nothing to bound or overflow, and a slow or failing store only delays the next sample. It never writes to the ledger, never touches the engine, and every failure is
/// contained and counted; the Observer and the decisions never wait for it. The Dashboard (another process) reads the rows and the heartbeat.
/// </summary>
public sealed class LiveMarkPublisher(Func<IReadOnlyList<MarkPosition>> positions, Func<LiveMarkBoard?> boardOf, ILiveMarkStore store, TimeProvider clock, TimeSpan interval, ILogger? log = null)
{
    readonly Dictionary<string, string> _lastKey = new(StringComparer.Ordinal);
    long _seq, _failures;
    DateOnly? _day;

    public long Failures => Interlocked.Read(ref _failures);

    public long Ticks => Interlocked.Read(ref _seq);

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await TickAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>One sample. Returns the number of marks written. Public so tests (and a replay) can drive it deterministically.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow();
            var seq = Interlocked.Increment(ref _seq);
            var snapshot = positions();
            var changed = new List<LiveMark>();
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in snapshot)
            {
                if (_day != p.TradeDate) { _day = p.TradeDate; _lastKey.Clear(); }                           // a new trade date: nothing from yesterday is carried over
                var mark = LiveMarkCalculator.Compute(p, boardOf()?.TryGet(p.Token), now, seq);
                var key = $"{mark.Status}|{mark.QuoteTickId}|{mark.ClosedNet}|{mark.RealizedNetToday}";
                if (_lastKey.TryGetValue(p.PositionId, out var prev) && prev == key && !Due(p.PositionId, now)) continue;
                keys[p.PositionId] = key;                                                                   // remembered only once the store has accepted the batch, so a failed write is retried
                changed.Add(mark);
            }

            await store.PublishAsync(changed, new MarkHeartbeat(seq, now, snapshot.Count, boardOf()?.Observed ?? 0, boardOf()?.IgnoredOutOfOrder ?? 0, Failures), ct);
            foreach (var (id, key) in keys) { _lastKey[id] = key; _written[id] = now; }
            return changed.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _failures) <= 3 || _failures % 60 == 0) log?.LogWarning(ex, "V2 live-mark publication failed ({Failures} so far); decisions and fills are unaffected.", _failures);
            return 0;
        }
    }

    readonly Dictionary<string, DateTimeOffset> _written = new(StringComparer.Ordinal);

    // an unchanged OPEN mark is refreshed at least every 5 s so the Dashboard can tell "no new quote" from "publisher down"
    bool Due(string positionId, DateTimeOffset now) => !_written.TryGetValue(positionId, out var at) || now - at >= TimeSpan.FromSeconds(5);
}
