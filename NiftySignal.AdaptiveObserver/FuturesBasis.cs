namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Per-completed-bar futures-minus-spot basis observation (08-Oct plan, section 9; metrics contract <see cref="MetricsVersion"/>).
///
/// Freshness rule (project-owner decision, final): a basis state is usable only when the spot it is built on is at most
/// <see cref="SpotFreshnessSeconds"/> seconds old, i.e. <c>0 &lt;= SpotAge &lt;= 5 s</c> with
/// <c>SpotAge = BasisStateTime - LastSpotAvailableAt</c>. There is NO late-session exception and NO nearest-future spot selection.
/// A stale or missing boundary state makes that boundary (and therefore <see cref="DeltaBasis"/>) unavailable (null) - never zero.
/// Spot ages are still recorded as diagnostics.
/// </summary>
public sealed record FuturesBasisBar(
    double? BasisStart,
    double? BasisTimeWeighted,
    double? BasisEnd,
    double? DeltaBasis,
    double? SpotAgeStartSeconds,
    double? SpotAgeEndSeconds,
    double? SpotAgeMaxSeconds,
    int BasisStateChanges,
    double CoveredSeconds,
    double UncoveredSeconds,
    string Status)
{
    /// <summary>v2 = v1 raw values + the approved 5-second freshness rule applied at the stored boundaries and in the time-weighted average, plus a boundary status.</summary>
    public const string MetricsVersion = "futures-basis-v2";

    /// <summary>Approved spot freshness threshold in seconds (inclusive). Not configurable and not relaxed late in the session.</summary>
    public const double SpotFreshnessSeconds = 5d;

    public const string StatusUsable = "Usable";
    public const string StatusNoState = "NoState";
    public const string StatusStaleStart = "StaleStart";
    public const string StatusStaleEnd = "StaleEnd";
    public const string StatusStaleBoth = "StaleBoth";
}

/// <summary>
/// Maintains the joined causal futures/spot state and summarizes basis over a completed adaptive bar. It is fed spot and futures ticks
/// separately from, and shares no state with, the exact-volume bar builder, so adding spot input cannot alter any core adaptive output.
///
/// Basis_t = FuturePrice_t - SpotPrice_t using the latest future and spot ticks available at or before t (never a later observation).
/// A new basis state is recorded whenever either price updates once both exist, so the series is not sampled only on futures ticks.
/// Bar convention is the same as <see cref="FuturesMicrostructureTracker"/>: reference = last state with At &lt;= start; changes inside
/// are the states with start &lt; At &lt;= end. The state in force at an instant t is the latest state with At &lt;= t; it is USABLE at t only while
/// t - SpotAt &lt;= <see cref="FuturesBasisBar.SpotFreshnessSeconds"/>.
/// </summary>
public sealed class FuturesBasisTracker
{
    readonly record struct State(DateTimeOffset At, double Basis, DateTimeOffset SpotAt);

    static readonly TimeSpan Freshness = TimeSpan.FromSeconds(FuturesBasisBar.SpotFreshnessSeconds);

    readonly List<State> _states = [];
    double? _futurePrice;
    double? _spotPrice;
    DateTimeOffset _spotAt;
    DateTimeOffset? _lastSpotAt;
    long _lastSpotId;

    /// <summary>Spot ticks that arrived older than an already-seen spot tick and were ignored (live/replay divergence signal; replay is always ordered).</summary>
    public int LateSpotTicks { get; private set; }

    public int StateCount => _states.Count;

    public void ObserveFuture(CleanObserverTick tick)
    {
        if (!(tick.Last > 0d) || !double.IsFinite(tick.Last)) return;
        _futurePrice = tick.Last;
        Record(tick.AvailableAt);
    }

    public void ObserveSpot(CleanObserverTick tick)
    {
        if (_lastSpotAt is { } last && (tick.AvailableAt < last || (tick.AvailableAt == last && tick.Id < _lastSpotId)))
        {
            LateSpotTicks++;
            return;
        }

        _lastSpotAt = tick.AvailableAt;
        _lastSpotId = tick.Id;
        if (!(tick.Last > 0d) || !double.IsFinite(tick.Last)) return;
        _spotPrice = tick.Last;
        _spotAt = tick.AvailableAt;
        Record(tick.AvailableAt);
    }

    void Record(DateTimeOffset at)
    {
        if (_futurePrice is not { } future || _spotPrice is not { } spot) return;
        _states.Add(new State(at, future - spot, _spotAt));
    }

    static bool Usable(State state, DateTimeOffset at)
    {
        var age = at - state.SpotAt;
        return age >= TimeSpan.Zero && age <= Freshness;
    }

    public FuturesBasisBar Complete(DateTimeOffset start, DateTimeOffset end)
    {
        if (end < start) throw new ArgumentException("A bar cannot end before it starts.", nameof(end));

        var firstInside = UpperBound(start);
        var lastInside = UpperBound(end);
        State? current = firstInside > 0 ? _states[firstInside - 1] : null;
        var reference = current;

        double coveredSeconds = 0d, uncoveredSeconds = 0d, weighted = 0d;
        double? ageMax = null;

        // The state in force over [from, to) is usable only until its spot ages past the threshold: the covered part is
        // [from, min(to, SpotAt + 5 s)] and the rest of the interval is uncovered (stale spot), never carried forward as fresh.
        void Account(State? state, DateTimeOffset from, DateTimeOffset to)
        {
            var seconds = (to - from).TotalSeconds;
            if (seconds <= 0d) return;
            if (state is not { } s) { uncoveredSeconds += seconds; return; }
            var freshUntil = s.SpotAt + Freshness;
            var coveredTo = freshUntil < to ? freshUntil : to;
            var covered = coveredTo > from ? (coveredTo - from).TotalSeconds : 0d;
            coveredSeconds += covered;
            weighted += s.Basis * covered;
            uncoveredSeconds += seconds - covered;
        }

        void Age(State? state, DateTimeOffset at)
        {
            if (state is not { } s) return;
            var age = Math.Max(0d, (at - s.SpotAt).TotalSeconds);
            ageMax = ageMax is null ? age : Math.Max(ageMax.Value, age);
        }

        Age(reference, start);
        var cursor = start;
        for (var i = firstInside; i < lastInside; i++)
        {
            var next = _states[i];
            Account(current, cursor, next.At);
            current = next;
            cursor = next.At;
            Age(next, next.At);
        }

        Account(current, cursor, end);
        Age(current, end);

        double? basisStart = reference is { } rs && Usable(rs, start) ? rs.Basis : null;
        double? basisEnd = current is { } cs && Usable(cs, end) ? cs.Basis : null;
        var status = (reference, current) switch
        {
            (null, _) or (_, null) => FuturesBasisBar.StatusNoState,
            _ => (basisStart.HasValue, basisEnd.HasValue) switch
            {
                (true, true) => FuturesBasisBar.StatusUsable,
                (false, true) => FuturesBasisBar.StatusStaleStart,
                (true, false) => FuturesBasisBar.StatusStaleEnd,
                _ => FuturesBasisBar.StatusStaleBoth,
            },
        };
        return new FuturesBasisBar(
            basisStart,
            coveredSeconds > 0d ? weighted / coveredSeconds : null,
            basisEnd,
            basisStart.HasValue && basisEnd.HasValue ? basisEnd - basisStart : null,
            reference is { } r0 ? Math.Max(0d, (start - r0.SpotAt).TotalSeconds) : null,
            current is { } r1 ? Math.Max(0d, (end - r1.SpotAt).TotalSeconds) : null,
            ageMax,
            lastInside - firstInside,
            coveredSeconds,
            uncoveredSeconds,
            status);
    }

    int UpperBound(DateTimeOffset instant)
    {
        int lo = 0, hi = _states.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_states[mid].At <= instant) lo = mid + 1; else hi = mid;
        }

        return lo;
    }
}
