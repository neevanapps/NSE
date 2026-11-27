namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Per-completed-bar Futures Top-5 observations (S4 of the Top-5 design, contract <see cref="MetricsVersion"/>). Sidecar only: never part of the parity-protected core rows.
/// Every second of the bar is accounted in exactly one of Usable / Stale / Invalid / Superseded / NoDepth / Unobserved, so a zero-duration split bar has all of them 0 and every
/// time-weighted value null (nothing is fabricated for a bar with no time).
/// </summary>
public sealed record FuturesDepth5Bar(
    double BarSeconds,
    // OFI5 (D10 conditional bridging). Net/Gross are null (never 0) when no valid transition happened inside the bar.
    long? Ofi5,
    long? GrossOfi5,
    int Ofi5Transitions,
    int Ofi5DirectTransitions,
    int Ofi5BridgedTransitions,
    long? Ofi5BridgedNet,
    double Ofi5BridgedGapSeconds,
    int Ofi5BaselineResets,
    // Time-weighted over Depth5UsableSeconds (null when that is 0).
    double? Tob5TimeWeighted,
    double? MicroDev5TimeWeighted,
    double? DepthImb5TimeWeighted,
    double? BidNotionalTimeWeighted,
    double? AskNotionalTimeWeighted,
    double Depth5UsableSeconds,
    double Depth5StaleSeconds,
    double Depth5InvalidSeconds,
    double Depth5SupersededSeconds,
    double Depth5NoDepthSeconds,
    double Depth5UnobservedSeconds,
    int SnapshotEvents,
    int SupersededMarkers,
    int InvalidSnapshots,
    // ---- futures-depth5-v2 EVENT-BASED accumulators (no elapsed time anywhere): every distinct, coherent, valid five-level snapshot delivered inside the bar contributes exactly once. ----
    // Sums are null (never 0) when the bar has no eligible event.
    double? BidNotionalEventSum = null,
    double? AskNotionalEventSum = null,
    int ValidDepthEvents = 0,
    double? MicroDev5EventSum = null,
    double? Tob5EventSum = null,
    int ValidMicroDev5Events = 0,
    int RepeatSnapshotsIgnored = 0,
    int NoDepthEvents = 0,
    int FeedGapEvents = 0)
{
    /// <summary>
    /// futures-depth5-v2 (EVENT-BASED): per-bar SumBidNotional / SumAskNotional / ValidDepthEvents and SumMicroDev5 / SumTob5 / ValidMicroDev5Events over the bar's distinct coherent snapshots, plus OFI5 (price-aligned,
    /// D10 conditional bridging across L1Superseded markers; both endpoints valid, at most 5 s apart; invalid/no-depth/feed-gap/stale break the baseline; identical snapshots are not transitions).
    /// The time-weighted members of this record (…TimeWeighted, Depth5*Seconds) are v1 legacy / diagnostics only: they are NOT persisted into v2 rows (those columns stay null) and never used as weights.
    /// futures-depth5-v1 rows (time-weighted) are never rewritten or reinterpreted. A different rule is a new version.
    /// </summary>
    public const string MetricsVersion = "futures-depth5-v2";

    /// <summary>The v1 version string, kept only so readers can recognise (and never serve) historical time-weighted rows.</summary>
    public const string LegacyTimeWeightedVersion = "futures-depth5-v1";
}

/// <summary>
/// Deterministic Futures depth tracker: a pure function of the ordered event stream and the bar boundaries (no clock, no I/O, fixed arithmetic order), so live processing and full replay
/// produce identical values. Events must be delivered in (AvailableAt, Id) order; an event older than the last one delivered is dropped and counted in <see cref="LateEvents"/> (diagnostic only, not part
/// of any persisted value: a replay includes it, which the sidecar verification then reports as a mismatch rather than hiding it).
///
/// Boundary convention, identical to the Level-1 tracker: a bar is (start, end]; the reference state is the last event with At &lt;= start; events with start &lt; At &lt;= end are the bar's own.
/// </summary>
public sealed class FuturesDepth5Tracker
{
    sealed record Entry(Depth5Observation Event, double? Tob5, double? MicroDev5, double? DepthImb5, double? BidNotional, double? AskNotional)
    {
        public bool Usable => Event.Kind == Depth5EventKind.Snapshot && Event.Book is { IsValid: true };
    }

    readonly record struct Transition(DateTimeOffset At, long Contribution, bool Bridged, double GapSeconds);

    readonly List<Entry> _events = [];
    readonly List<Transition> _transitions = [];
    readonly List<DateTimeOffset> _resets = [];
    readonly List<DateTimeOffset> _repeatAts = [];      // repeats ignored, attributed to their bar at Complete (pruned there; bounded by one bar)

    // Incremental OFI state (a pure function of the delivered events).
    Depth5Book? _baseBook;
    DateTimeOffset _baseAt;
    DateTimeOffset? _markerAt;
    (DateTimeOffset At, long Id)? _last;

    Depth5Book? _heldBook;
    bool _heldSuperseded;

    public int LateEvents { get; private set; }

    /// <summary>Identical snapshots ignored because they repeated the held book (never renew freshness; diagnostic).</summary>
    public int RepeatsIgnored { get; private set; }

    public int RetainedEvents => _events.Count;

    public void Observe(Depth5Observation o)
    {
        if (_last is { } last && (o.AvailableAt, o.Id).CompareTo(last) < 0)
        {
            LateEvents++;
            return;
        }

        // A snapshot identical to the held, un-superseded book is a repeat of stored state: it is not evidence, adds no event and cannot renew the freshness clock (the row normalizer never emits one;
        // this is the defence for any other producer). After a superseding marker an identical snapshot is a genuine fresh depth observation and is processed normally.
        if (o.Kind == Depth5EventKind.Snapshot && o.Book is { IsValid: true } repeat && _heldBook is not null && !_heldSuperseded && repeat.SameBook(_heldBook))
        {
            _last = (o.AvailableAt, o.Id);
            RepeatsIgnored++;
            _repeatAts.Add(o.AvailableAt);
            return;
        }

        _last = (o.AvailableAt, o.Id);
        if (o.Kind == Depth5EventKind.Snapshot)
        {
            _heldBook = o.Book is { IsValid: true } ? o.Book : null;
            _heldSuperseded = false;
        }
        else if (o.Kind == Depth5EventKind.L1Superseded) _heldSuperseded = true;
        else _heldBook = null;

        var book = o.Kind == Depth5EventKind.Snapshot ? o.Book : null;
        var usable = book is { IsValid: true };
        double? tob = null;
        if (usable)
        {
            var qb = Depth5Metrics.TotalBidQuantity(book)!.Value;
            var qa = Depth5Metrics.TotalAskQuantity(book)!.Value;
            if (checked(qb + qa) > 0) tob = (double)(qb - qa) / (qb + qa);
        }

        _events.Add(new Entry(o, tob, usable ? Depth5Metrics.MicroDev5Proxy(book) : null, usable ? Depth5Metrics.NotionalImbalance(book) : null,
            usable ? Depth5Metrics.BidNotional(book) : null, usable ? Depth5Metrics.AskNotional(book) : null));
        Advance(o);
    }

    void Reset(DateTimeOffset at)
    {
        if (_baseBook is not null) _resets.Add(at);
        _baseBook = null;
        _markerAt = null;
    }

    void Advance(Depth5Observation o)
    {
        var at = o.AvailableAt;
        switch (o.Kind)
        {
            case Depth5EventKind.L1Superseded:
                if (_baseBook is not null) _markerAt ??= at;       // unavailable from here; a second marker before the next snapshot changes nothing
                return;
            case Depth5EventKind.NoDepth:
            case Depth5EventKind.FeedGap:
                Reset(at);
                return;
        }

        if (o.Book is not { IsValid: true } book)
        {
            Reset(at);                                              // malformed / crossed / non-monotone / empty top: never bridged
            return;
        }

        if (_baseBook is null)
        {
            _baseBook = book;
            _baseAt = at;
            _markerAt = null;
            return;
        }

        if (at - _baseAt > Depth5Freshness.Limit)
        {
            Reset(at);                                              // freshness expired between the endpoints: no transition, new baseline
            _baseBook = book;
            _baseAt = at;
            return;
        }

        var bridged = _markerAt is not null;
        var gap = _markerAt is { } m ? (at - m).TotalSeconds : 0d;
        _markerAt = null;
        if (book.SameBook(_baseBook))
        {
            // An identical snapshot is a repeat, never a transition. It only restores coverage when a superseding marker intervened (a fresh depth message after the Level-1 move);
            // it never extends the clock of a snapshot that was not superseded.
            if (bridged) _baseAt = at;
            return;
        }

        var e = Depth5Metrics.Ofi5Contribution(_baseBook, book)!.Value;
        _transitions.Add(new Transition(at, e, bridged, gap));
        _baseBook = book;
        _baseAt = at;
    }

    public FuturesDepth5Bar Complete(DateTimeOffset start, DateTimeOffset end)
    {
        if (end < start) throw new ArgumentException("A bar cannot end before it starts.", nameof(end));

        var firstInside = UpperBound(start);
        var lastInside = UpperBound(end);
        var current = firstInside > 0 ? _events[firstInside - 1] : null;

        double usable = 0d, stale = 0d, invalid = 0d, superseded = 0d, noDepth = 0d, unobserved = 0d;
        double tobSum = 0d, microSum = 0d, imbSum = 0d, bidSum = 0d, askSum = 0d;
        double tobSec = 0d, microSec = 0d, imbSec = 0d, bidSec = 0d, askSec = 0d;

        void Account(Entry? state, DateTimeOffset from, DateTimeOffset to)
        {
            var seconds = (to - from).TotalSeconds;
            if (seconds <= 0d) return;
            if (state is null) { unobserved += seconds; return; }
            switch (state.Event.Kind)
            {
                case Depth5EventKind.Snapshot when state.Usable:
                    var expiry = state.Event.AvailableAt + Depth5Freshness.Limit;
                    var usableTo = expiry < to ? expiry : to;
                    var u = usableTo > from ? (usableTo - from).TotalSeconds : 0d;
                    usable += u;
                    stale += seconds - u;
                    if (u > 0d)
                    {
                        if (state.Tob5 is { } t) { tobSum += t * u; tobSec += u; }
                        if (state.MicroDev5 is { } m) { microSum += m * u; microSec += u; }
                        if (state.DepthImb5 is { } d) { imbSum += d * u; imbSec += u; }
                        if (state.BidNotional is { } b) { bidSum += b * u; bidSec += u; }
                        if (state.AskNotional is { } a) { askSum += a * u; askSec += u; }
                    }

                    break;
                case Depth5EventKind.Snapshot: invalid += seconds; break;
                case Depth5EventKind.L1Superseded: superseded += seconds; break;
                default: noDepth += seconds; break;
            }
        }

        var cursor = start;
        for (var i = firstInside; i < lastInside; i++)
        {
            var next = _events[i];
            Account(current, cursor, next.Event.AvailableAt);
            current = next;
            cursor = next.Event.AvailableAt;
        }

        Account(current, cursor, end);

        int snapshots = 0, markers = 0, invalidSnapshots = 0, noDepthEvents = 0, feedGaps = 0;
        // v2 event accumulators: fixed event order (the delivery order), so live and replay sums are bit-for-bit identical. No weight of any kind.
        double bidEv = 0d, askEv = 0d, microEv = 0d, tobEv = 0d;
        int depthEvents = 0, microEvents = 0;
        for (var i = firstInside; i < lastInside; i++)
        {
            var en = _events[i];
            var e = en.Event;
            if (e.Kind == Depth5EventKind.Snapshot)
            {
                snapshots++;
                if (!en.Usable) invalidSnapshots++;
                else
                {
                    if (en.BidNotional is { } bn && en.AskNotional is { } an && double.IsFinite(bn) && double.IsFinite(an) && bn + an > 0d) { bidEv += bn; askEv += an; depthEvents++; }
                    if (en.MicroDev5 is { } md && en.Tob5 is { } tb && double.IsFinite(md) && double.IsFinite(tb)) { microEv += md; tobEv += tb; microEvents++; }
                }
            }
            else if (e.Kind == Depth5EventKind.L1Superseded) markers++;
            else if (e.Kind == Depth5EventKind.NoDepth) noDepthEvents++;
            else if (e.Kind == Depth5EventKind.FeedGap) feedGaps++;
        }

        var repeats = 0;
        foreach (var r in _repeatAts) if (r > start && r <= end) repeats++;

        long net = 0, gross = 0, bridgedNet = 0;
        int transitions = 0, bridgedCount = 0;
        double bridgedGap = 0d;
        foreach (var t in _transitions)
        {
            if (t.At <= start || t.At > end) continue;
            net += t.Contribution;
            gross += Math.Abs(t.Contribution);
            transitions++;
            if (t.Bridged) { bridgedCount++; bridgedNet += t.Contribution; bridgedGap += t.GapSeconds; }
        }

        var resets = 0;
        foreach (var r in _resets) if (r > start && r <= end) resets++;

        // Prune what no later bar can need: events before the last one at or before this end (the next bar's reference), transitions and resets already attributed.
        if (lastInside > 1) _events.RemoveRange(0, lastInside - 1);
        _transitions.RemoveAll(t => t.At <= end);
        _resets.RemoveAll(r => r <= end);
        _repeatAts.RemoveAll(r => r <= end);

        return new FuturesDepth5Bar(
            (end - start).TotalSeconds,
            transitions > 0 ? net : null,
            transitions > 0 ? gross : null,
            transitions,
            transitions - bridgedCount,
            bridgedCount,
            bridgedCount > 0 ? bridgedNet : null,
            bridgedGap,
            resets,
            tobSec > 0d ? tobSum / tobSec : null,
            microSec > 0d ? microSum / microSec : null,
            imbSec > 0d ? imbSum / imbSec : null,
            bidSec > 0d ? bidSum / bidSec : null,
            askSec > 0d ? askSum / askSec : null,
            usable, stale, invalid, superseded, noDepth, unobserved,
            snapshots, markers, invalidSnapshots,
            depthEvents > 0 ? bidEv : null, depthEvents > 0 ? askEv : null, depthEvents,
            microEvents > 0 ? microEv : null, microEvents > 0 ? tobEv : null, microEvents,
            repeats, noDepthEvents, feedGaps);
    }

    int UpperBound(DateTimeOffset instant)
    {
        int lo = 0, hi = _events.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_events[mid].Event.AvailableAt <= instant) lo = mid + 1; else hi = mid;
        }

        return lo;
    }
}

/// <summary>
/// The Futures depth event feed shared by live polling and full recovery, so both derive the observations from the same rows by the same code: per-token normalization in source Id order, a
/// pending set ordered by (AvailableAt, Id), and delivery of every event with AvailableAt at or before a group's instant just before that group is processed. The feed owns the Id cursor.
/// It holds only the future token's rows, so its memory is a small constant per session.
/// </summary>
public sealed class FuturesDepth5Feed
{
    readonly AdaptiveDepthNormalizer _normalizer = new();
    readonly PriorityQueue<Depth5Observation, (DateTimeOffset, long)> _pending = new();

    public long LastFetchedId { get; private set; }

    /// <summary>Latest received time among the rows fed so far; a live backlog uses it to read depth only as far as the Level-1 rows it is about to process.</summary>
    public DateTimeOffset LastFetchedReceivedAt { get; private set; } = DateTimeOffset.MinValue;

    public int PendingCount => _pending.Count;

    /// <summary>Feeds source rows (Id order). Events before <paramref name="notBefore"/> are dropped exactly as the Level-1 path drops clean ticks before the observation start; the normalizer still sees every row.</summary>
    public void Ingest(IEnumerable<DepthRawRow> rows, DateTimeOffset notBefore)
    {
        foreach (var row in rows)
        {
            if (row.Id > LastFetchedId) LastFetchedId = row.Id;
            if (row.ReceivedAt > LastFetchedReceivedAt) LastFetchedReceivedAt = row.ReceivedAt;
            if (_normalizer.Process(row) is { } o && o.AvailableAt >= notBefore)
                _pending.Enqueue(o, (o.AvailableAt, o.Id));
        }
    }

    /// <summary>All pending events with AvailableAt &lt;= <paramref name="instant"/>, in (AvailableAt, Id) order.</summary>
    public IEnumerable<Depth5Observation> DrainThrough(DateTimeOffset instant)
    {
        while (_pending.TryPeek(out var next, out _) && next.AvailableAt <= instant)
        {
            _pending.Dequeue();
            yield return next;
        }
    }
}
