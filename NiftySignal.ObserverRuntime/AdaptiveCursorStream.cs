using NiftySignal.AdaptiveObserver;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

/// <summary>The day's clean ticks for recovery, released in final (AvailableAt, Id) order: from the start of the day, or continuing from a checkpoint.</summary>
public interface IAdaptiveTickStream
{
    AdaptiveIncrementalTickNormalizer Normalizer { get; }
    long LastRawId { get; }
    long RawRows { get; }
    long CleanRows { get; }
    int PeakBufferedRows { get; }
    /// <summary>Clean ticks later than the cutoff; complete only once the stream is exhausted.</summary>
    IReadOnlyList<ObserverTokenTick> Pending { get; }
    Task<IReadOnlyList<ObserverTokenTick>?> NextAsync(CancellationToken ct);
    /// <summary>Between two batches: where reading must resume, and the ticks already read that have not been released.</summary>
    (AdaptiveSourcePosition Position, List<ObserverTokenTick> Unreleased) Capture();
}

/// <summary>Top-5 depth for recovery: fed far enough ahead of each processed instant, from the start of the day or from a checkpoint.</summary>
public interface IAdaptiveDepthSource
{
    FuturesDepth5Feed Feed { get; }
    long OutOfOrderRows { get; }
    Task AdvanceToAsync(DateTimeOffset instant, CancellationToken ct);
    Task FinishAsync(CancellationToken ct);
    AdaptiveSourcePosition Capture();
}

/// <summary>
/// Continues a day from a checkpoint position with bounded memory: Id-ordered batches after the cursor, exactly as live polling reads them. While more rows remain, a tick is released only once it is more than one
/// read window older than the newest row seen; at the end everything up to the cutoff is released and the rest is pending.
/// </summary>
public sealed class AdaptiveCursorTickStream : IAdaptiveTickStream
{
    readonly AdaptiveSourceTickReader _reader;
    readonly NiftySignalDbContext _source;
    readonly DateOnly _day;
    readonly IReadOnlyCollection<string> _tokens;
    readonly DateTimeOffset _notBefore;
    readonly DateTimeOffset _through;
    readonly TimeSpan _window;
    readonly int _batch;
    readonly Dictionary<string, long> _skip;
    readonly List<ObserverTokenTick> _held;
    List<ObserverTokenTick> _pending = [];
    DateTimeOffset _newest = DateTimeOffset.MinValue;
    long _cursor;
    bool _completed;

    public AdaptiveCursorTickStream(AdaptiveSourceTickReader reader, NiftySignalDbContext source, DateOnly day, IReadOnlyCollection<string> tokens, DateTimeOffset notBefore, DateTimeOffset throughUtc,
        TimeSpan window, int batch, AdaptiveIncrementalTickNormalizer normalizer, AdaptiveSourcePosition position, IEnumerable<ObserverTokenTick> unreleased)
    {
        (_reader, _source, _day, _tokens, _notBefore, _through, _window, _batch, Normalizer) = (reader, source, day, tokens, notBefore, throughUtc, window, batch, normalizer);
        _cursor = position.ResumeAfterId;
        _skip = new(position.SkipThrough, StringComparer.Ordinal);
        _held = unreleased.ToList();
        LastRawId = Math.Max(_cursor, _skip.Values.DefaultIfEmpty(0).Max());
    }

    public AdaptiveIncrementalTickNormalizer Normalizer { get; }
    public long LastRawId { get; private set; }
    public long RawRows { get; private set; }
    public long CleanRows { get; private set; }
    public int PeakBufferedRows { get; private set; }
    public IReadOnlyList<ObserverTokenTick> Pending => _pending;

    public async Task<IReadOnlyList<ObserverTokenTick>?> NextAsync(CancellationToken ct)
    {
        if (_completed) return null;
        long? after = _cursor > 0 ? _cursor : await AdaptiveSourceTickReader.FindStartCursorAsync(_source, _tokens, _day, _window, ct);
        IReadOnlyList<(string Token, ObserverRawTick Tick)> rows = after is { } from
            ? await _reader.ReadRawAfterIdAsync(_source, _tokens, from, ct, _day, _batch)
            : [];
        RawRows += rows.Count;
        foreach (var (token, raw) in rows)
        {
            _cursor = Math.Max(_cursor, raw.Id);
            if (raw.ReceivedAt > _newest) _newest = raw.ReceivedAt;
            if (_skip.TryGetValue(token, out var consumed) && raw.Id <= consumed) continue;
            if (Normalizer.Process(token, raw) is { } tick && tick.AvailableAt >= _notBefore) _held.Add(new ObserverTokenTick(token, tick));
        }

        LastRawId = Math.Max(LastRawId, _cursor);
        PeakBufferedRows = Math.Max(PeakBufferedRows, _held.Count);
        var final = rows.Count < _batch;
        _held.Sort(static (a, b) =>
        {
            var byTime = a.Tick.AvailableAt.CompareTo(b.Tick.AvailableAt);
            return byTime != 0 ? byTime : a.Tick.Id.CompareTo(b.Tick.Id);
        });
        var limit = final ? DateTimeOffset.MaxValue : _newest - _window;
        var count = 0;
        while (count < _held.Count && _held[count].Tick.AvailableAt <= _through && (final || _held[count].Tick.AvailableAt < limit)) count++;
        var ready = _held.GetRange(0, count);
        _held.RemoveRange(0, count);
        CleanRows += ready.Count;
        if (final)
        {
            _pending = _held.OrderBy(x => x.Tick.Id).ToList();
            _held.Clear();
            _completed = true;
        }

        return ready;
    }

    /// <summary>The cursor has passed every tick read, so those not yet released (including, after the final batch, those later than the cutoff) travel with the state.</summary>
    public (AdaptiveSourcePosition Position, List<ObserverTokenTick> Unreleased) Capture() =>
        (new AdaptiveSourcePosition(_cursor, _skip.Where(x => x.Value > _cursor).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)), _held.Concat(_pending).ToList());
}

/// <summary>Continues a Top-5 depth feed from a checkpoint position: Id-ordered batches until a row received one read window after the instant has been fed.</summary>
public sealed class AdaptiveCursorDepthSource(AdaptiveDepthTickReader reader, NiftySignalDbContext source, IReadOnlyCollection<string> tokens, DateOnly day, DateTimeOffset notBefore,
    TimeSpan window, int batch, FuturesDepth5Feed feed, AdaptiveSourcePosition position) : IAdaptiveDepthSource
{
    readonly Dictionary<string, long> _skip = new(position.SkipThrough, StringComparer.Ordinal);
    long _cursor = position.ResumeAfterId;
    DateTimeOffset _newest = DateTimeOffset.MinValue;
    bool _exhausted;

    public FuturesDepth5Feed Feed => feed;
    public long OutOfOrderRows => 0;

    public async Task AdvanceToAsync(DateTimeOffset instant, CancellationToken ct)
    {
        var cover = instant > DateTimeOffset.MaxValue - window ? DateTimeOffset.MaxValue : instant + window;
        while (!_exhausted && _newest < cover)
        {
            long? after = _cursor > 0 ? _cursor : await AdaptiveSourceTickReader.FindStartCursorAsync(source, tokens, day, window, ct);
            IReadOnlyList<DepthRawRow> rows = after is { } from ? await reader.ReadAfterIdAsync(source, tokens, from, day, ct, batch) : [];
            foreach (var row in rows)
            {
                _cursor = Math.Max(_cursor, row.Id);
                if (row.ReceivedAt > _newest) _newest = row.ReceivedAt;
            }

            feed.Ingest(rows.Where(r => !(_skip.TryGetValue(r.Token, out var consumed) && r.Id <= consumed)), notBefore);
            if (rows.Count < batch) _exhausted = true;
        }
    }

    public Task FinishAsync(CancellationToken ct) => AdvanceToAsync(DateTimeOffset.MaxValue, ct);

    public AdaptiveSourcePosition Capture() =>
        new(_cursor, _skip.Where(x => x.Value > _cursor).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
}
