namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// One price level of a Top-5 book side. A level is EMPTY when its price is not a positive finite number or its quantity is not positive; empty levels are the padding
/// (price 0, quantity 0) that a thin book reports below its last real level. Pure value type: no I/O, no clock, no state.
/// </summary>
public readonly record struct Depth5Level(double Price, long Qty)
{
    public bool IsEmpty => !(double.IsFinite(Price) && Price > 0d) || Qty <= 0;
}

/// <summary>Why a Top-5 snapshot cannot be used. Anything other than <see cref="Valid"/> means "no usable book": callers must treat it as unavailable (never zero) and must not bridge across it.</summary>
public enum Depth5Validity
{
    Valid = 0,
    /// <summary>A side does not have exactly five level slots.</summary>
    WrongLevelCount,
    /// <summary>A price is NaN/infinite/negative.</summary>
    InvalidPrice,
    /// <summary>A quantity is negative.</summary>
    NegativeQuantity,
    /// <summary>Level 1 of the bid or ask side is empty (no best bid / best ask with quantity).</summary>
    EmptyTopOfBook,
    /// <summary>Best ask is strictly below best bid. A locked book (equal prices) is NOT crossed, matching the Level-1 rule <c>Ask &gt;= Bid</c>.</summary>
    Crossed,
    /// <summary>A non-empty level follows an empty level on the same side (a hole inside the book).</summary>
    NonContiguous,
    /// <summary>Bid prices are not strictly decreasing, or ask prices not strictly increasing, among the non-empty levels.</summary>
    NonMonotone,
}

/// <summary>
/// An immutable Top-5 book snapshot: exactly five bid slots (best first, descending price) and five ask slots (best first, ascending price). Validity is decided once at construction by the
/// rules in <see cref="Depth5Validity"/>; this type does not know how a snapshot was obtained (no broker or feed semantics are assumed).
/// </summary>
public sealed class Depth5Book
{
    public const int Levels = 5;

    readonly Depth5Level[] _bids;
    readonly Depth5Level[] _asks;

    public Depth5Book(IEnumerable<Depth5Level> bids, IEnumerable<Depth5Level> asks)
    {
        _bids = bids.ToArray();
        _asks = asks.ToArray();
        Validity = Validate(_bids, _asks);
    }

    public IReadOnlyList<Depth5Level> Bids => _bids;

    public IReadOnlyList<Depth5Level> Asks => _asks;

    public Depth5Validity Validity { get; }

    public bool IsValid => Validity == Depth5Validity.Valid;

    static Depth5Validity Validate(Depth5Level[] bids, Depth5Level[] asks)
    {
        if (bids.Length != Levels || asks.Length != Levels) return Depth5Validity.WrongLevelCount;
        foreach (var l in bids.Concat(asks))
        {
            if (!double.IsFinite(l.Price) || l.Price < 0d) return Depth5Validity.InvalidPrice;
            if (l.Qty < 0) return Depth5Validity.NegativeQuantity;
        }

        if (bids[0].IsEmpty || asks[0].IsEmpty) return Depth5Validity.EmptyTopOfBook;
        if (asks[0].Price < bids[0].Price) return Depth5Validity.Crossed;
        if (HasHole(bids) || HasHole(asks)) return Depth5Validity.NonContiguous;
        if (!StrictlyMonotone(bids, descending: true) || !StrictlyMonotone(asks, descending: false)) return Depth5Validity.NonMonotone;
        return Depth5Validity.Valid;
    }

    static bool HasHole(Depth5Level[] side)
    {
        var seenEmpty = false;
        foreach (var l in side)
        {
            if (l.IsEmpty) seenEmpty = true;
            else if (seenEmpty) return true;
        }

        return false;
    }

    static bool StrictlyMonotone(Depth5Level[] side, bool descending)
    {
        double? previous = null;
        foreach (var l in side)
        {
            if (l.IsEmpty) continue;
            if (previous is { } p && (descending ? l.Price >= p : l.Price <= p)) return false;
            previous = l.Price;
        }

        return true;
    }

    /// <summary>Exact value equality of all ten level slots (price bits and quantity). Two snapshots built from the same stored decimals are always equal, never "almost equal".</summary>
    public bool SameBook(Depth5Book? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_bids.Length != other._bids.Length || _asks.Length != other._asks.Length) return false;
        for (var i = 0; i < _bids.Length; i++) if (_bids[i] != other._bids[i]) return false;
        for (var i = 0; i < _asks.Length; i++) if (_asks[i] != other._asks[i]) return false;
        return true;
    }

    /// <summary>True when Level 1 (price and quantity, both sides) is identical.</summary>
    public bool SameLevelOne(Depth5Book other) => _bids[0] == other._bids[0] && _asks[0] == other._asks[0];

    /// <summary>True when levels 2-5 of both sides are identical slot by slot (Level 1 is ignored): "no depth-originated change".</summary>
    public bool SameDeepLevels(Depth5Book other)
    {
        for (var i = 1; i < Levels && i < _bids.Length && i < other._bids.Length; i++) if (_bids[i] != other._bids[i]) return false;
        for (var i = 1; i < Levels && i < _asks.Length && i < other._asks.Length; i++) if (_asks[i] != other._asks[i]) return false;
        return true;
    }

    /// <summary>The non-empty levels of a side, best first (valid books only have them contiguous from level 1).</summary>
    internal IEnumerable<Depth5Level> NonEmpty(bool bids) => (bids ? _bids : _asks).Where(l => !l.IsEmpty);
}
