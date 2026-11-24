namespace NiftySignal.AdaptiveObserver;

/// <summary>Net / gross OFI5 and the number of valid transitions that produced them. Net and Gross are null (never 0) when there was no valid transition: a baseline alone is not evidence.</summary>
public readonly record struct Ofi5Totals(long? Net, long? Gross, int Transitions);

/// <summary>
/// Pure Top-5 book calculations (S2 of the Top-5 microstructure design). Every method returns null when its inputs are unavailable (null book, invalid book, zero denominator) and never substitutes zero.
/// All values are raw and signed; any display inversion is a later Dashboard concern. Nothing here reads a clock, a feed or a database, and nothing here changes the Level-1 calculations in
/// <see cref="FuturesMicrostructureTracker"/> / <see cref="FuturesBookState"/>.
///
/// Contracts (a "level" is a non-empty level; sums run over levels 1..5 in index order):
///   Qb = SUM bid qty, Qa = SUM ask qty.
///   MicroDev5Proxy  = ((Ask1 - Bid1) / 2) * ((Qb - Qa) / (Qb + Qa))        usable iff the book is valid and Qb + Qa &gt; 0.
///   BidN = SUM(price_i * qty_i) over bids, AskN likewise over asks.
///   DepthN5         = (BidN - AskN) / (BidN + AskN)                         in [-1, +1]; usable iff the book is valid and BidN + AskN &gt; 0.
///   OFI5 transition = SUM_{p >= L}(qB_N(p) - qB_P(p)) - SUM_{p &lt;= U}(qA_N(p) - qA_P(p)), price-aligned, see <see cref="Ofi5Contribution"/>.
/// </summary>
public static class Depth5Metrics
{
    public static long? TotalBidQuantity(Depth5Book? book) => book is { IsValid: true } b ? Sum(b.NonEmpty(bids: true)) : null;

    public static long? TotalAskQuantity(Depth5Book? book) => book is { IsValid: true } b ? Sum(b.NonEmpty(bids: false)) : null;

    /// <summary>
    /// ((Ask1 - Bid1) / 2) * ((Qb - Qa) / (Qb + Qa)). At one level this is exactly the Level-1 MicroDev (Spread / 2) * TOB. Raw signed: positive means the bid side holds more quantity.
    /// Null for an unavailable/invalid book or when Qb + Qa is not positive. (Qb + Qa is always positive for a valid book because both best levels are non-empty; the guard is kept explicit.)
    /// </summary>
    public static double? MicroDev5Proxy(Depth5Book? book)
    {
        if (book is not { IsValid: true } b) return null;
        var qb = Sum(b.NonEmpty(bids: true));
        var qa = Sum(b.NonEmpty(bids: false));
        var total = checked(qb + qa);
        if (total <= 0) return null;
        var halfSpread = (b.Asks[0].Price - b.Bids[0].Price) / 2d;
        return halfSpread * ((double)(qb - qa) / total);
    }

    /// <summary>DepthN5 = (BidN - AskN) / (BidN + AskN) with BidN = SUM price * qty. Raw signed in [-1, +1]; null when the book is unavailable/invalid or the denominator is not positive.</summary>
    public static double? NotionalImbalance(Depth5Book? book)
    {
        if (book is not { IsValid: true } b) return null;
        var bidN = Notional(b.NonEmpty(bids: true));
        var askN = Notional(b.NonEmpty(bids: false));
        var denominator = bidN + askN;
        if (!(denominator > 0d) || !double.IsFinite(denominator)) return null;
        return (bidN - askN) / denominator;
    }

    public static double? BidNotional(Depth5Book? book) => book is { IsValid: true } b ? Notional(b.NonEmpty(bids: true)) : null;

    public static double? AskNotional(Depth5Book? book) => book is { IsValid: true } b ? Notional(b.NonEmpty(bids: false)) : null;

    /// <summary>
    /// Price-aligned multi-level order-flow-imbalance contribution of one transition previous -> next of the SAME instrument (null when either book is unavailable or invalid, or
    /// <paramref name="levels"/> is outside 1..5: the caller must then re-baseline, exactly like the Level-1 rule).
    ///
    /// Quantities are compared at the SAME PRICE, never at the same rank, so a price that merely slides down a rank contributes nothing unless its quantity changed. The only prices comparable
    /// in both snapshots are those inside the observable depth window of both:
    ///   bids: L = max(lowB(previous), lowB(next)), lowB(S) = price of the last (levels-th) bid when the side has <paramref name="levels"/> levels, else 0 (book exhausted: everything below is known empty);
    ///   asks: U = min(highA(previous), highA(next)), highA(S) = price of the last ask when the side has <paramref name="levels"/> levels, else +infinity.
    /// contribution = SUM_{bid p >= L}(qty_next - qty_previous) - SUM_{ask p &lt;= U}(qty_next - qty_previous), summed over the levels present in each snapshot (a price missing from a snapshot
    /// inside the window has quantity 0). Queue additions on the bid / removals on the ask are positive (buy pressure); removals on the bid / additions on the ask are negative.
    ///
    /// With <paramref name="levels"/> = 1 this equals the Level-1 formula <see cref="FuturesMicrostructureTracker.Contribution"/> for books whose best levels have positive quantity
    /// (verified by the unit tests). Prices are compared by exact equality; callers must build them deterministically from the same source values (the unit tests do).
    /// </summary>
    public static long? Ofi5Contribution(Depth5Book? previous, Depth5Book? next, int levels = Depth5Book.Levels)
    {
        if (levels is < 1 or > Depth5Book.Levels) return null;
        if (previous is not { IsValid: true } p || next is not { IsValid: true } n) return null;

        var pb = p.NonEmpty(bids: true).Take(levels).ToArray();
        var nb = n.NonEmpty(bids: true).Take(levels).ToArray();
        var pa = p.NonEmpty(bids: false).Take(levels).ToArray();
        var na = n.NonEmpty(bids: false).Take(levels).ToArray();

        var floor = Math.Max(BidFloor(pb, levels), BidFloor(nb, levels));
        var ceiling = Math.Min(AskCeiling(pa, levels), AskCeiling(na, levels));

        long bid = 0, ask = 0;
        foreach (var l in nb) if (l.Price >= floor) bid = checked(bid + l.Qty);
        foreach (var l in pb) if (l.Price >= floor) bid = checked(bid - l.Qty);
        foreach (var l in na) if (l.Price <= ceiling) ask = checked(ask + l.Qty);
        foreach (var l in pa) if (l.Price <= ceiling) ask = checked(ask - l.Qty);
        return checked(bid - ask);
    }

    /// <summary>
    /// Folds an ordered sequence of snapshots (null = unavailable) into net/gross OFI5 and the transition count. A valid book following an unavailable/invalid one only re-baselines (no transition
    /// across a gap). Two identical consecutive valid snapshots (exact value equality, see <see cref="Depth5Book.SameBook"/>) are a repeat, NOT a transition: they add nothing to the
    /// transition count. A genuinely changed book whose net contribution happens to be 0 still counts. Net/Gross are null when there was no valid transition.
    /// </summary>
    public static Ofi5Totals Ofi5(IEnumerable<Depth5Book?> sequence, int levels = Depth5Book.Levels)
    {
        Depth5Book? baseline = null;
        long net = 0, gross = 0;
        var transitions = 0;
        foreach (var book in sequence)
        {
            if (book is not { IsValid: true })
            {
                baseline = null;
                continue;
            }

            // An identical complete-book snapshot is a repeat of stored state, not a new book event: never a transition; the baseline is unchanged.
            if (baseline is not null && baseline.SameBook(book)) continue;

            if (baseline is not null && Ofi5Contribution(baseline, book, levels) is { } e)
            {
                net = checked(net + e);
                gross = checked(gross + Math.Abs(e));
                transitions++;
            }

            baseline = book;
        }

        return transitions > 0 ? new Ofi5Totals(net, gross, transitions) : new Ofi5Totals(null, null, 0);
    }

    static double BidFloor(Depth5Level[] side, int levels) => side.Length == levels ? side[^1].Price : 0d;

    static double AskCeiling(Depth5Level[] side, int levels) => side.Length == levels ? side[^1].Price : double.PositiveInfinity;

    static long Sum(IEnumerable<Depth5Level> levels)
    {
        long total = 0;
        foreach (var l in levels) total = checked(total + l.Qty);
        return total;
    }

    static double Notional(IEnumerable<Depth5Level> levels)
    {
        var total = 0d;
        foreach (var l in levels) total += l.Price * l.Qty;   // fixed level order: the result is bit-for-bit deterministic
        return total;
    }
}
