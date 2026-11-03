using System.Security.Cryptography;
using System.Text;
using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Metrics;

namespace NiftySignal.PaperEngineV2.Core.Frames;

/// <summary>
/// The causal identity of a frame. Two DIFFERENT things are kept apart on purpose: what the bar DESCRIBES (market activity between <see cref="BarStartAvailableAt"/> and <see cref="BarEndAvailableAt"/>) and when it
/// BECAME AVAILABLE for evaluation (<see cref="BarEndAvailableAt"/> together with <see cref="LastSourceTickId"/> is the <see cref="DecisionKey"/>; nothing in a frame may be available later than it).
/// </summary>
public sealed record FrameIdentity(
    string Schema, string SourceModel, string AdaptiveModelVersion, string? AtmRuleVersion, long SessionId, DateOnly TradeDate, int BarSeq,
    DateTimeOffset BarStartAvailableAt, DateTimeOffset BarEndAvailableAt, long LastSourceTickId, int SameInstantOrdinal, double DurationSeconds, bool Verified, string? UnverifiedReason)
{
    public const string CurrentSchema = "pev2-frame/1";

    /// <summary>
    /// Frame schema that additionally carries <see cref="MarketStateFrame.HeldQuotes"/>: quotes of contracts held by open positions that are NOT in the signal-time basket. A frame without held quotes keeps
    /// <see cref="CurrentSchema"/> and its exact payload and hash, so existing journals and hashes are unchanged; a Host that does not know /2 rejects such a frame (fail closed).
    /// </summary>
    public const string HeldQuotesSchema = "pev2-frame/2";

    /// <summary>The instant AND source tick from which this frame's data could be used. Ordered by (instant, tick id), the observer's own ordering key.</summary>
    public DecisionKey DecisionKey => new(BarEndAvailableAt, LastSourceTickId);

    /// <summary>A bar produced by the same market update as its predecessor (same availability instant): zero duration, activity metrics unavailable, never eligible as a fill bar for a decision on its sibling.</summary>
    public bool IsZeroDuration => DurationSeconds <= 0d || BarEndAvailableAt <= BarStartAvailableAt;
}

public readonly record struct DecisionKey(DateTimeOffset AvailableAt, long SourceTickId) : IComparable<DecisionKey>
{
    public int CompareTo(DecisionKey other)
    {
        var c = AvailableAt.CompareTo(other.AvailableAt);
        return c != 0 ? c : SourceTickId.CompareTo(other.SourceTickId);
    }
}

public enum Moneyness : byte { Itm = 0, Atm = 1, Otm = 2 }

/// <summary>One of the ten slots of the fixed ATM±2 basket. A slot that could not be resolved has a null token (never substituted).</summary>
public sealed record BasketSlot(int Offset, double? Strike, bool IsCall, string? Token, Moneyness Moneyness);

/// <summary>The exact basket the summary was computed for: one expiry, one centre, the actual listed step and the ten CE/PE slots.</summary>
public sealed record BasketIdentity(DateOnly Expiry, double Centre, double? Step, IReadOnlyList<BasketSlot> Slots, IReadOnlyList<string> Problems);

/// <summary>A two-sided top-of-book quote of a basket contract as available at the bar's end boundary. <see cref="AvailableAt"/> never exceeds the frame's <c>BarEndAvailableAt</c>.</summary>
public sealed record QuoteSnapshot(string Token, double Bid, double Ask, long BidQty, long AskQty, DateTimeOffset AvailableAt, long SourceTickId);

/// <summary>A basket contract whose quote was missing or unusable (never replaced by another contract or an older value).</summary>
public sealed record QuoteIssue(string Token, string Reason);

/// <summary>When the India VIX value used by the frame was observed relative to the boundary (Fresh / Stale / Missing, as the Market Summary defines them).</summary>
public sealed record VixAvailability(DateTimeOffset? ObservedAt, double? AgeSeconds, string Status);

public readonly record struct MetricValue(int Handle, SummaryWindowId Window, double Value);

public sealed class FrameValidationException(string message) : Exception(message);

/// <summary>
/// The immutable market state a strategy sees for ONE verified completed fixed-13,650-volume bar. It owns private copies of everything it exposes (no reference to observer or builder state survives
/// construction), has no setters, and carries a canonical JSON payload and its SHA-256 hash, so the same inputs always give the same frame and a journaled frame can be verified on replay.
/// </summary>
public sealed class MarketStateFrame : IMarketStateFrame
{
    readonly double[] _values;
    readonly bool[] _present;
    readonly Lazy<(string Payload, string Hash)> _canonical;

    MarketStateFrame(FrameIdentity identity, BasketIdentity basket, double[] values, bool[] present, IReadOnlyList<QuoteSnapshot> quotes, IReadOnlyList<QuoteIssue> issues, VixAvailability vix, IReadOnlyList<QuoteSnapshot> heldQuotes, Lazy<(string, string)> canonical)
    {
        Identity = identity; Basket = basket; _values = values; _present = present; Quotes = quotes; QuoteIssues = issues; Vix = vix; HeldQuotes = heldQuotes; _canonical = canonical;
    }

    public FrameIdentity Identity { get; }

    public BasketIdentity Basket { get; }

    public IReadOnlyList<QuoteSnapshot> Quotes { get; }

    public IReadOnlyList<QuoteIssue> QuoteIssues { get; }

    /// <summary>
    /// Genuinely observed two-sided quotes (with their own availability instant and source tick) of contracts held by open V2 positions that are outside this bar's basket. They exist only so a held position can be
    /// marked and exited after the dynamic ATM moved; they are never an input of any Market Summary metric or entry decision, never replace a missing basket quote and never stand in for another contract.
    /// </summary>
    public IReadOnlyList<QuoteSnapshot> HeldQuotes { get; }

    public VixAvailability Vix { get; }

    /// <summary>
    /// Canonical JSON of the frame (the journal payload). Deterministic: fixed field order, UTC instants, shortest round-trip numbers. Derived purely from the immutable fields and computed on FIRST USE (normally on the
    /// consumer's thread), so building a frame on the observer thread costs only the validated copy of its values.
    /// </summary>
    public string PayloadJson => _canonical.Value.Payload;

    /// <summary>Lower-case hex SHA-256 of the UTF-8 payload (computed together with it, thread-safe, exactly once).</summary>
    public string Hash => _canonical.Value.Hash;

    public int PresentMetricCount => _present.Count(x => x);

    public bool TryRead(int metricHandle, SummaryWindowId window, out double value)
    {
        var i = (metricHandle * SummaryWindows.Count) + (int)window;
        if ((uint)i >= (uint)_values.Length) { value = 0d; return false; }
        value = _values[i];
        return _present[i];
    }

    /// <summary>The present metrics in (handle, window) order - what the payload serialises.</summary>
    public IEnumerable<MetricValue> Metrics()
    {
        for (var i = 0; i < _values.Length; i++)
            if (_present[i]) yield return new MetricValue(i / SummaryWindows.Count, (SummaryWindowId)(i % SummaryWindows.Count), _values[i]);
    }

    /// <summary>
    /// Whether this frame could carry a fill for a decision taken on <paramref name="decision"/>: it must END strictly later than the decision's availability instant (a sibling bar of the same market update, a
    /// zero-duration split bar or an earlier bar never qualifies). The quote that fills still has to be strictly newer than the decision (checked by the execution layer).
    /// </summary>
    public bool IsEligibleFillBarFor(MarketStateFrame decision) => Identity.BarEndAvailableAt > decision.Identity.BarEndAvailableAt;

    /// <summary>Creates and validates a frame. Inputs are copied; any look-ahead (a quote or VIX observation available after the bar's end), a foreign source model, an invalid metric value or an inconsistent identity throws <see cref="FrameValidationException"/>.</summary>
    public static MarketStateFrame Create(
        FrameIdentity identity, BasketIdentity basket, IEnumerable<MetricValue> metrics, IEnumerable<QuoteSnapshot> quotes, IEnumerable<QuoteIssue> quoteIssues, VixAvailability vix, IEnumerable<QuoteSnapshot>? heldQuotes = null)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(basket); ArgumentNullException.ThrowIfNull(metrics); ArgumentNullException.ThrowIfNull(quotes); ArgumentNullException.ThrowIfNull(quoteIssues); ArgumentNullException.ThrowIfNull(vix);
        var heldCopy = (heldQuotes ?? []).Select(q => q with { }).OrderBy(q => q.Token, StringComparer.Ordinal).ToArray();
        if (identity.Schema != FrameIdentity.CurrentSchema && identity.Schema != FrameIdentity.HeldQuotesSchema) throw new FrameValidationException($"unsupported frame schema '{identity.Schema}'");
        if ((heldCopy.Length > 0) != (identity.Schema == FrameIdentity.HeldQuotesSchema)) throw new FrameValidationException($"schema '{identity.Schema}' and {heldCopy.Length} held quote(s) are inconsistent: held quotes require {FrameIdentity.HeldQuotesSchema} and only then");
        if (identity.SourceModel != MetricRegistry.SourceModel) throw new FrameValidationException($"source model '{identity.SourceModel}' is not '{MetricRegistry.SourceModel}': the fixed-13,650 clock is never mixed with another clock");
        if (identity.BarSeq < 1) throw new FrameValidationException("BarSeq must be >= 1");
        if (identity.SameInstantOrdinal < 0) throw new FrameValidationException("SameInstantOrdinal must be >= 0");
        if (identity.BarEndAvailableAt < identity.BarStartAvailableAt) throw new FrameValidationException("a bar cannot end before it starts");
        if (!double.IsFinite(identity.DurationSeconds) || identity.DurationSeconds < 0d) throw new FrameValidationException("DurationSeconds must be finite and >= 0");
        if (!identity.Verified && string.IsNullOrWhiteSpace(identity.UnverifiedReason)) throw new FrameValidationException("an unverified frame must state why");
        if (identity.Verified && identity.UnverifiedReason is not null) throw new FrameValidationException("a verified frame has no unverified reason");

        var values = new double[MetricRegistry.Count * SummaryWindows.Count];
        var present = new bool[values.Length];
        foreach (var m in metrics)
        {
            if ((uint)m.Handle >= (uint)MetricRegistry.Count) throw new FrameValidationException($"unknown metric handle {m.Handle}");
            if ((uint)m.Window >= (uint)SummaryWindows.Count) throw new FrameValidationException($"unknown window {(int)m.Window}");
            if (!double.IsFinite(m.Value)) throw new FrameValidationException($"metric {MetricRegistry.Specs[m.Handle].Id} is not finite (an unavailable value must be omitted, never NaN/Infinity)");
            var i = (m.Handle * SummaryWindows.Count) + (int)m.Window;
            if (present[i]) throw new FrameValidationException($"metric {MetricRegistry.Specs[m.Handle].Id} window {m.Window} given twice");
            values[i] = m.Value; present[i] = true;
        }

        var tokens = new HashSet<string>(basket.Slots.Where(s => s.Token is not null).Select(s => s.Token!), StringComparer.Ordinal);
        var quoteCopy = quotes.Select(q => q with { }).OrderBy(q => q.Token, StringComparer.Ordinal).ToArray();
        foreach (var q in quoteCopy)
        {
            if (!tokens.Contains(q.Token)) throw new FrameValidationException($"quote token {q.Token} is not in the basket (no substitution)");
            if (!double.IsFinite(q.Bid) || !double.IsFinite(q.Ask) || q.Bid <= 0d || q.Ask < q.Bid || q.BidQty < 0 || q.AskQty < 0) throw new FrameValidationException($"quote of {q.Token} is not a valid two-sided quote");
            if (q.AvailableAt > identity.BarEndAvailableAt) throw new FrameValidationException($"look-ahead: the quote of {q.Token} became available at {q.AvailableAt:O}, after the bar end {identity.BarEndAvailableAt:O}");
        }

        if (quoteCopy.Select(q => q.Token).Distinct(StringComparer.Ordinal).Count() != quoteCopy.Length) throw new FrameValidationException("two quotes for one token");
        foreach (var q in heldCopy)
        {
            if (tokens.Contains(q.Token)) throw new FrameValidationException($"held quote token {q.Token} is a basket token: a basket contract is quoted once, in Quotes");
            if (!double.IsFinite(q.Bid) || !double.IsFinite(q.Ask) || q.Bid <= 0d || q.Ask < q.Bid || q.BidQty < 0 || q.AskQty < 0) throw new FrameValidationException($"held quote of {q.Token} is not a valid two-sided quote");
            if (q.AvailableAt > identity.BarEndAvailableAt) throw new FrameValidationException($"look-ahead: the held quote of {q.Token} became available at {q.AvailableAt:O}, after the bar end {identity.BarEndAvailableAt:O}");
        }

        if (heldCopy.Select(q => q.Token).Distinct(StringComparer.Ordinal).Count() != heldCopy.Length) throw new FrameValidationException("two held quotes for one token");
        if (vix.ObservedAt is { } obs && obs > identity.BarEndAvailableAt) throw new FrameValidationException($"look-ahead: the VIX observation is from {obs:O}, after the bar end {identity.BarEndAvailableAt:O}");

        var basketCopy = new BasketIdentity(basket.Expiry, basket.Centre, basket.Step, Array.AsReadOnly(basket.Slots.Select(s => s with { }).ToArray()), Array.AsReadOnly(basket.Problems.ToArray()));     // read-only wrappers: a caller cannot cast back to a mutable array
        var issues = quoteIssues.Select(i => i with { }).OrderBy(i => i.Token, StringComparer.Ordinal).ToArray();
        var vixCopy = vix with { };
        var identityCopy = identity with { };
        var canonical = new Lazy<(string, string)>(() =>
        {
            var payload = FrameSerializer.Write(identityCopy, basketCopy, values, present, quoteCopy, issues, vixCopy, heldCopy);
            return (payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant());
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        return new MarketStateFrame(identityCopy, basketCopy, values, present, Array.AsReadOnly(quoteCopy), Array.AsReadOnly(issues), vixCopy, Array.AsReadOnly(heldCopy), canonical);
    }
}
