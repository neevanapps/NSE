namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// One Level-1 futures book state. Pure value logic for the 08-Oct plan, sections 3.4-3.5 and 6-8.
/// </summary>
public readonly record struct FuturesBookState(DateTimeOffset At, long Id, double Bid, double Ask, long BidQty, long AskQty)
{
    /// <summary>Usable book (plan 3.5): positive prices, not crossed (ask >= bid), non-negative quantities.</summary>
    public bool IsValid => double.IsFinite(Bid) && double.IsFinite(Ask) && Bid > 0d && Ask > 0d && Ask >= Bid && BidQty >= 0 && AskQty >= 0;

    long TotalQty => BidQty + AskQty;

    /// <summary>TOB and microprice need a non-zero quantity denominator (plan 3.5).</summary>
    public bool HasQuantity => IsValid && TotalQty > 0;

    /// <summary>TOB = (BidQty - AskQty) / (BidQty + AskQty), in [-1, +1].</summary>
    public double? Tob => HasQuantity ? (double)(BidQty - AskQty) / TotalQty : null;

    /// <summary>
    /// MicroDev = MicroPrice - Mid with MicroPrice = (Ask*BidQty + Bid*AskQty) / (BidQty + AskQty) (cross-weighted) and
    /// Mid = (Bid + Ask) / 2. At Level 1 this equals (Spread / 2) * TOB.
    /// </summary>
    public double? MicroDev => HasQuantity
        ? ((Ask * BidQty + Bid * AskQty) / TotalQty) - ((Bid + Ask) / 2d)
        : null;

    public bool SameBook(FuturesBookState other) =>
        Bid == other.Bid && Ask == other.Ask && BidQty == other.BidQty && AskQty == other.AskQty;
}

/// <summary>Per-completed-bar Level-1 microstructure observations (metrics contract <see cref="MetricsVersion"/>).</summary>
public sealed record FuturesMicrostructureBar(
    double? TobStart,
    double? TobTimeWeighted,
    double? TobEnd,
    double? TobChange,
    double? TobMin,
    double? TobMax,
    double? MicroDevStart,
    double? MicroDevTimeWeighted,
    double? MicroDevEnd,
    double? MicroDevChange,
    long? Ofi,
    int OfiTransitions,
    int BookStateChanges,
    int InvalidBookEvents,
    double ValidBookSeconds,
    double InvalidBookSeconds)
{
    /// <summary>Identifies the calculation contract. Change it whenever any formula or boundary rule changes.</summary>
    public const string MetricsVersion = "futures-micro-v1";
}

/// <summary>
/// Accumulates unique futures book states in causal processing order and summarizes them over a completed adaptive bar.
/// Deterministic: the result depends only on the ordered states seen so far and the bar boundaries, so live processing and
/// full replay (same engine, same order) produce identical values. It owns no wall clock and shares no state with the bar builder.
///
/// Boundary convention for a bar (start, end]: the reference state is the last unique state with At &lt;= start; states with
/// start &lt; At &lt;= end are the bar's own book changes. A state is effective from its At until the next unique state, clipped to
/// the bar. Repeated identical snapshots are collapsed (plan 3.4) and time weighting is interval-based, never row-count based.
/// </summary>
public sealed class FuturesMicrostructureTracker
{
    readonly List<FuturesBookState> _states = [];

    public int UniqueStateCount => _states.Count;

    public void Observe(CleanObserverTick tick)
    {
        var state = new FuturesBookState(tick.AvailableAt, tick.Id, tick.Bid, tick.Ask, tick.BidQty, tick.AskQty);
        if (_states.Count > 0 && _states[^1].SameBook(state))
        {
            return; // carry-forward duplicate: not independent book evidence
        }

        _states.Add(state);
    }

    public FuturesMicrostructureBar Complete(DateTimeOffset start, DateTimeOffset end)
    {
        if (end < start) throw new ArgumentException("A bar cannot end before it starts.", nameof(end));

        var firstInside = UpperBound(start);
        var lastInside = UpperBound(end);
        FuturesBookState? current = firstInside > 0 ? _states[firstInside - 1] : null;
        var reference = current;

        double validSeconds = 0d, invalidSeconds = 0d;
        double tobSeconds = 0d, tobSum = 0d, microSeconds = 0d, microSum = 0d;
        double? tobMin = null, tobMax = null;

        void Extent(FuturesBookState? s)
        {
            if (s?.Tob is { } t)
            {
                tobMin = tobMin is null ? t : Math.Min(tobMin.Value, t);
                tobMax = tobMax is null ? t : Math.Max(tobMax.Value, t);
            }
        }

        void Account(FuturesBookState? state, DateTimeOffset from, DateTimeOffset to)
        {
            var seconds = (to - from).TotalSeconds;
            if (seconds <= 0d) return;
            if (state is not { IsValid: true } valid)
            {
                invalidSeconds += seconds;
                return;
            }

            validSeconds += seconds;
            if (valid.Tob is { } tob) { tobSeconds += seconds; tobSum += tob * seconds; }
            if (valid.MicroDev is { } micro) { microSeconds += seconds; microSum += micro * seconds; }
        }

        Extent(reference);
        var cursor = start;
        var baseline = reference is { IsValid: true } ? reference : null;
        var established = baseline is not null;
        long ofi = 0;
        var transitions = 0;
        var invalidEvents = 0;
        for (var i = firstInside; i < lastInside; i++)
        {
            var next = _states[i];
            Account(current, cursor, next.At);
            Extent(next);

            if (!next.IsValid)
            {
                // Plan 8.5: never bridge an invalid/crossed book; re-baseline when a valid book returns.
                baseline = null;
                invalidEvents++;
            }
            else
            {
                if (baseline is { } previous)
                {
                    ofi += Contribution(previous, next);
                    transitions++;
                }

                baseline = next;
                established = true;
            }

            current = next;
            cursor = next.At;
        }

        Account(current, cursor, end);

        var tobStart = reference?.Tob;
        var tobEnd = current?.Tob;
        var microStart = reference?.MicroDev;
        var microEnd = current?.MicroDev;
        return new FuturesMicrostructureBar(
            tobStart,
            tobSeconds > 0d ? tobSum / tobSeconds : null,
            tobEnd,
            tobStart.HasValue && tobEnd.HasValue ? tobEnd - tobStart : null,
            tobMin,
            tobMax,
            microStart,
            microSeconds > 0d ? microSum / microSeconds : null,
            microEnd,
            microStart.HasValue && microEnd.HasValue ? microEnd - microStart : null,
            established ? ofi : null,
            transitions,
            lastInside - firstInside,
            invalidEvents,
            validSeconds,
            invalidSeconds);
    }

    /// <summary>
    /// Standard Level-1 order-flow-imbalance event contribution (plan 8.3):
    /// e = I(Pb_n >= Pb_p) qb_n - I(Pb_n &lt;= Pb_p) qb_p - I(Pa_n &lt;= Pa_p) qa_n + I(Pa_n >= Pa_p) qa_p.
    /// </summary>
    public static long Contribution(FuturesBookState previous, FuturesBookState next)
    {
        long e = 0;
        if (next.Bid >= previous.Bid) e += next.BidQty;
        if (next.Bid <= previous.Bid) e -= previous.BidQty;
        if (next.Ask <= previous.Ask) e -= next.AskQty;
        if (next.Ask >= previous.Ask) e += previous.AskQty;
        return e;
    }

    // First index whose At is strictly greater than the instant (states are in non-decreasing At order).
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
