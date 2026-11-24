using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// The ONE decimal -> double conversion for stored depth prices. Live polling and full replay both read the same stored decimal(18,4) values and must obtain bit-identical doubles, so every
/// Top-5 price goes through this function and nowhere else. Trailing zeros are stripped first so the result never depends on the decimal's scale (2000.05m and 2000.0500m are the same double).
/// </summary>
public static class Depth5Price
{
    public static double FromDecimal(decimal value) => (double)(value / 1.0000000000000000000000000000m);
}

/// <summary>One stored depth row as the reader sees it: identity and timestamps plus the depth snapshot (null when the row carried no complete depth).</summary>
public readonly record struct DepthRawRow(string Token, long Id, DateTimeOffset ExchangeTimestamp, DateTimeOffset ReceivedAt, MarketDepth? Depth);

/// <summary>
/// What a channel event says. Only <see cref="Snapshot"/> carries a book; the other two explicitly mark the book as unavailable from that instant:
///  - Snapshot: a depth-originated row (first complete book, or L2-L5 changed versus the previous row). The only way a book ever becomes usable. It may itself be invalid (crossed, non-monotone, empty top).
///  - L1Superseded: the held snapshot's Level 1 (price OR quantity) changed afterwards while levels 2-5 did not. The stored row then pairs a newer touchline with older depth levels, which is not a
///    verified book (in the 2026-10-29 sample 84% of such price changes produced a non-monotone ladder). Nothing is fabricated: the book is unavailable until the next Snapshot.
///  - NoDepth: the row carried no complete depth.
///  - FeedGap: an externally known interruption of the feed (reconnect, halt, session change). Never produced by the row normalizer; supplied by a caller that knows. Breaks every baseline.
/// </summary>
public enum Depth5EventKind { Snapshot = 0, L1Superseded, NoDepth, FeedGap }

/// <summary>A channel event for one token at an availability instant. <see cref="Book"/> is non-null only for <see cref="Depth5EventKind.Snapshot"/>.</summary>
public sealed record Depth5Observation(string Token, long Id, DateTimeOffset ExchangeTimestamp, DateTimeOffset AvailableAt, Depth5Book? Book, Depth5EventKind Kind = Depth5EventKind.Snapshot);

public static class Depth5Mapping
{
    public static Depth5Book FromMarketDepth(MarketDepth d) => new(
        [L(d.Bid1Price, d.Bid1Qty), L(d.Bid2Price, d.Bid2Qty), L(d.Bid3Price, d.Bid3Qty), L(d.Bid4Price, d.Bid4Qty), L(d.Bid5Price, d.Bid5Qty)],
        [L(d.Ask1Price, d.Ask1Qty), L(d.Ask2Price, d.Ask2Qty), L(d.Ask3Price, d.Ask3Qty), L(d.Ask4Price, d.Ask4Qty), L(d.Ask5Price, d.Ask5Qty)]);

    static Depth5Level L(decimal price, long qty) => new(Depth5Price.FromDecimal(price), qty);

    /// <summary>The same availability instant the Level-1 normalizer uses: max(ReceivedAt, ExchangeTimestamp).</summary>
    public static DateTimeOffset AvailableAt(DateTimeOffset exchange, DateTimeOffset received) => received < exchange ? exchange : received;
}

/// <summary>
/// Independent per-token depth normalizer (rows fed in source Id order). Unlike <see cref="AdaptiveIncrementalTickNormalizer"/> (whose payload test ignores L2-L5 and therefore drops depth-only
/// rows) it compares each row with the token's PREVIOUS ROW and emits:
///   - a <see cref="Depth5EventKind.Snapshot"/> when L2-L5 changed (or for the token's first complete book / depth re-appearing). A snapshot row takes Level 1 and levels 2-5 from the same stored row,
///     which is the freshest value of each; in the 2026-10-29 sample every such row had a monotone, uncrossed ladder and Level 1 never moved in the same row as depth;
///   - a single <see cref="Depth5EventKind.L1Superseded"/> when a held valid snapshot's Level 1 (price or quantity) changes while L2-L5 do not: the book is unavailable until the next snapshot
///     (no combined "new L1 + old depth" book is ever produced, and no value is carried over);
///   - <see cref="Depth5EventKind.NoDepth"/> when depth disappears.
/// Rows that change nothing, repeat a snapshot, or only move Level 1 while no valid snapshot is held produce no event, so a repeat can never refresh freshness. State is one small record per
/// token: memory does not grow with the day. The Level-1 cleaner, its payload rule and trade classification are not touched.
/// </summary>
public sealed class AdaptiveDepthNormalizer
{
    sealed class State
    {
        public Depth5Book? Previous;
        public bool HeldValidSnapshot;
    }

    readonly Dictionary<string, State> _state = new(StringComparer.Ordinal);

    public Depth5Observation? Process(DepthRawRow row)
    {
        var book = row.Depth is { } d ? Depth5Mapping.FromMarketDepth(d) : null;
        var isNew = !_state.TryGetValue(row.Token, out var s);
        s ??= _state[row.Token] = new State();
        var previous = s.Previous;
        s.Previous = book;
        var at = Depth5Mapping.AvailableAt(row.ExchangeTimestamp, row.ReceivedAt);

        Depth5Observation Make(Depth5Book? b, Depth5EventKind kind) => new(row.Token, row.Id, row.ExchangeTimestamp, at, b, kind);

        if (book is null)
        {
            s.HeldValidSnapshot = false;
            return !isNew && previous is not null ? Make(null, Depth5EventKind.NoDepth) : null;     // a first row with no depth says nothing
        }

        if (previous is null || !book.SameDeepLevels(previous))                                      // first book, depth appeared, or L2-L5 changed
        {
            s.HeldValidSnapshot = book.IsValid;
            return Make(book, Depth5EventKind.Snapshot);
        }

        if (s.HeldValidSnapshot && !book.SameLevelOne(previous))                                      // depth identical, touchline moved
        {
            s.HeldValidSnapshot = false;
            return Make(null, Depth5EventKind.L1Superseded);
        }

        return null;
    }

    public void Reset() => _state.Clear();
}

/// <summary>
/// Causal-ordering buffer mirroring the live worker's <c>Pending</c> logic: observations are released in (AvailableAt, Id) order once AvailableAt &lt;= now - <see cref="StableOrderingLag"/>.
/// An observation arriving with AvailableAt older than something already released is "late": it is still released (next call, in order) but counted, exactly the case where the Level-1 path
/// would also process it out of order. Batch replay is <see cref="ReleaseAll"/>.
/// </summary>
public sealed class Depth5OrderingBuffer
{
    public static readonly TimeSpan StableOrderingLag = TimeSpan.FromSeconds(2);

    readonly PriorityQueue<Depth5Observation, (DateTimeOffset, long)> _pending = new();
    DateTimeOffset? _watermark;

    public int Count => _pending.Count;

    public int LateArrivals { get; private set; }

    public void Add(Depth5Observation observation)
    {
        if (_watermark is { } w && observation.AvailableAt < w) LateArrivals++;
        _pending.Enqueue(observation, (observation.AvailableAt, observation.Id));
    }

    public IEnumerable<Depth5Observation> Release(DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc - StableOrderingLag;
        while (_pending.TryPeek(out var next, out _) && next.AvailableAt <= cutoff)
        {
            _pending.Dequeue();
            if (_watermark is not { } w || next.AvailableAt > w) _watermark = next.AvailableAt;
            yield return next;
        }
    }

    public IEnumerable<Depth5Observation> ReleaseAll()
    {
        while (_pending.TryDequeue(out var next, out _))
        {
            if (_watermark is not { } w || next.AvailableAt > w) _watermark = next.AvailableAt;
            yield return next;
        }
    }
}

/// <summary>The hard depth freshness rule: a book is usable only within <see cref="Limit"/> of the observation that produced it. Only observations (real depth changes) carry a timestamp, so repeats cannot extend it.</summary>
public static class Depth5Freshness
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    /// <summary>True when <paramref name="asOf"/> is not before the observation and at most <see cref="Limit"/> after it.</summary>
    public static bool IsFresh(Depth5Observation observation, DateTimeOffset asOf) =>
        observation.Book is { IsValid: true } && asOf >= observation.AvailableAt && asOf - observation.AvailableAt <= Limit;

    /// <summary>The book usable at <paramref name="asOf"/> from a time-ordered observation stream: the latest observation at or before it, if fresh and valid; otherwise null (never a stale or zero book).</summary>
    public static Depth5Book? BookAt(IReadOnlyList<Depth5Observation> ordered, DateTimeOffset asOf)
    {
        Depth5Observation? latest = null;
        foreach (var o in ordered)
        {
            if (o.AvailableAt > asOf) break;
            latest = o;
        }

        return latest is not null && IsFresh(latest, asOf) ? latest.Book : null;
    }
}

/// <summary>Why a stretch of time has no usable Top-5 book.</summary>
public enum Depth5GapReason
{
    None = 0,
    /// <summary>Before the token's first event.</summary>
    NotYetObserved,
    /// <summary>The last snapshot is older than the 5 s freshness limit and nothing newer arrived.</summary>
    Stale,
    /// <summary>Level 1 moved after the last snapshot; waiting for a depth update that restores a coherent book.</summary>
    L1Superseded,
    /// <summary>The latest snapshot failed validation (see <see cref="Depth5Validity"/>), e.g. crossed or non-monotone.</summary>
    InvalidBook,
    /// <summary>The rows carried no complete depth.</summary>
    NoDepth,
    /// <summary>An externally known feed interruption.</summary>
    FeedGap,
}

/// <summary>A half-open interval [From, To) of one token with a book available, or the reason it is not.</summary>
public readonly record struct Depth5CoverageSegment(DateTimeOffset From, DateTimeOffset To, bool Available, Depth5GapReason Reason, Depth5Validity? Validity = null);

public static class Depth5Coverage
{
    /// <summary>
    /// Explicit coverage of one token's time axis from an (AvailableAt, Id)-ordered event list: every instant in [<paramref name="from"/>, <paramref name="to"/>) is either inside an Available segment
    /// (a valid snapshot at most <see cref="Depth5Freshness.Limit"/> old with no later event) or inside a gap with its reason. Gaps are never filled with a carried-over book.
    /// </summary>
    public static IReadOnlyList<Depth5CoverageSegment> Segments(IReadOnlyList<Depth5Observation> ordered, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<Depth5CoverageSegment>();
        void Add(DateTimeOffset a, DateTimeOffset b, bool ok, Depth5GapReason reason, Depth5Validity? v = null)
        {
            if (a < from) a = from;
            if (b > to) b = to;
            if (b <= a) return;
            if (result.Count > 0 && result[^1] is { } last && last.Available == ok && last.Reason == reason && last.Validity == v && last.To == a)
                result[^1] = last with { To = b };
            else result.Add(new Depth5CoverageSegment(a, b, ok, reason, v));
        }

        var cursor = from;
        for (var i = 0; i < ordered.Count; i++)
        {
            var o = ordered[i];
            var next = i + 1 < ordered.Count ? ordered[i + 1].AvailableAt : to;
            if (o.AvailableAt > cursor) Add(cursor, o.AvailableAt, false, i == 0 ? Depth5GapReason.NotYetObserved : Depth5GapReason.None);
            var start = o.AvailableAt > cursor ? o.AvailableAt : cursor;
            switch (o.Kind)
            {
                case Depth5EventKind.Snapshot when o.Book is { IsValid: true }:
                    var expiry = o.AvailableAt + Depth5Freshness.Limit;
                    Add(start, expiry < next ? expiry : next, true, Depth5GapReason.None);
                    if (expiry < next) Add(expiry, next, false, Depth5GapReason.Stale);
                    break;
                case Depth5EventKind.Snapshot:
                    Add(start, next, false, Depth5GapReason.InvalidBook, o.Book?.Validity);
                    break;
                case Depth5EventKind.L1Superseded:
                    Add(start, next, false, Depth5GapReason.L1Superseded);
                    break;
                case Depth5EventKind.FeedGap:
                    Add(start, next, false, Depth5GapReason.FeedGap);
                    break;
                default:
                    Add(start, next, false, Depth5GapReason.NoDepth);
                    break;
            }

            if (next > cursor) cursor = next;
        }

        if (ordered.Count == 0) Add(from, to, false, Depth5GapReason.NotYetObserved);
        return result;
    }
}

public static class Depth5Flow
{
    /// <summary>
    /// OFI5 over an ordered event stream of ONE token. Only snapshots form transitions. A <see cref="Depth5EventKind.NoDepth"/> event or an invalid snapshot interrupts the baseline (no bridging);
    /// a <see cref="Depth5EventKind.L1Superseded"/> marker interrupts it only when <paramref name="breakOnL1Superseded"/> is set (default: false, the two coherent endpoint snapshots are differenced
    /// exactly like consecutive Level-1 snapshots, the Level-1 changes in between being part of that difference).
    /// </summary>
    public static Ofi5Totals Ofi5(IEnumerable<Depth5Observation> ordered, bool breakOnL1Superseded = false, int levels = Depth5Book.Levels) =>
        Depth5Metrics.Ofi5(Sequence(ordered, breakOnL1Superseded), levels);

    static IEnumerable<Depth5Book?> Sequence(IEnumerable<Depth5Observation> ordered, bool breakOnL1Superseded)
    {
        foreach (var o in ordered)
        {
            switch (o.Kind)
            {
                case Depth5EventKind.Snapshot: yield return o.Book; break;
                case Depth5EventKind.NoDepth:
                case Depth5EventKind.FeedGap: yield return null; break;
                case Depth5EventKind.L1Superseded when breakOnL1Superseded: yield return null; break;
            }
        }
    }
}
