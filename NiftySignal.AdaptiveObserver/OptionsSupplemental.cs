using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Per-completed-bar supplemental options observations (08-Oct plan sections 19-26; metrics contract <see cref="MetricsVersion"/>).
/// All center-contract values refer to the center CE and PE frozen for the bar by the existing ATM±2 band selection at the bar's start.
/// Every value is nullable: unavailable inputs never become zero.
/// </summary>
public sealed record OptionsSupplementalBar(
    double? CenterStrike,
    string? UnavailableReason,
    long? CeOiStart, long? CeOiEnd, long? CeOiDelta,
    long? PeOiStart, long? PeOiEnd, long? PeOiDelta,
    double? CeMidStart, double? CeMidEnd, double? CeMidDelta,
    double? PeMidStart, double? PeMidEnd, double? PeMidDelta,
    string? CePosition, string? PePosition,
    double? CeIvStart, double? CeIvEnd, double? CeDeltaIv,
    double? PeIvStart, double? PeIvEnd, double? PeDeltaIv,
    double? IvSkewStart, double? IvSkewEnd, double? DeltaSkew,
    long? BarCeQuantity, long? BarPeQuantity, double? VolPcr,
    long? RollCeQuantity, long? RollPeQuantity, double? RollVolPcr,
    long DayCeVolume, long DayPeVolume, int UniverseTokenCount, int TokensObserved,
    double? CeMicroDevTimeWeighted, long? CeOfi,
    double? PeMicroDevTimeWeighted, long? PeOfi,
    double? CeActivityPerSecond, double? PeActivityPerSecond,
    double? StraddleMidStart, double? StraddleMidEnd, double? StraddleDelta)
{
    /// <summary>Identifies the calculation contract. Change it whenever any formula, boundary or freshness rule changes.</summary>
    public const string MetricsVersion = "options-supp-v1";

    /// <summary>
    /// Option quote freshness, REUSED from the existing adaptive convention (<see cref="AdaptiveOptionBandCalculator.Select"/>'s
    /// <c>maxQuoteAgeSeconds</c> default and the residual quote age): 5 seconds. A guard test pins this to the band calculator's default so
    /// a second threshold cannot appear unnoticed.
    /// </summary>
    public const double QuoteFreshnessSeconds = 5d;

    public static OptionsSupplementalBar Unavailable(string reason, double? centerStrike = null) => new(
        centerStrike, reason,
        null, null, null, null, null, null,          // center OI (CE, PE)
        null, null, null, null, null, null,          // center mids (CE, PE)
        null, null,                                  // positions
        null, null, null, null, null, null,          // center IV (CE, PE)
        null, null, null,                            // IV skew
        null, null, null,                            // bar volume PCR
        null, null, null,                            // rolling volume PCR
        0, 0, 0, 0,                                  // subscribed-universe day volumes
        null, null, null, null,                      // center book (MicroDev, OFI)
        null, null,                                  // activity per second
        null, null, null);                           // straddle
}

/// <summary>Everything the calculator needs, supplied explicitly so the formulas are pure and unit-testable.</summary>
public sealed class OptionsSupplementalInput
{
    public required OptionBandSelection? Selection { get; init; }
    public required string? UnavailableReason { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required double DurationSeconds { get; init; }
    public required double RiskFreeRate { get; init; }
    /// <summary>Synthetic weekly underlying at <see cref="Start"/> (the band selection's own value).</summary>
    public required double? StartUnderlying { get; init; }
    /// <summary>Synthetic weekly underlying at <see cref="End"/>, from the next band selection at the same instant; null when unavailable.</summary>
    public required double? EndUnderlying { get; init; }
    public required Func<string, InstrumentFlowSnapshot?> StartSnapshot { get; init; }
    public required Func<string, InstrumentFlowSnapshot> EndSnapshot { get; init; }
    public required OptionBandSideMetrics? CallBand { get; init; }
    public required OptionBandSideMetrics? PutBand { get; init; }
    public required IReadOnlyList<OptionBandSideMetrics?> CallHistory { get; init; }
    public required IReadOnlyList<OptionBandSideMetrics?> PutHistory { get; init; }
    public required int RollingWindowBars { get; init; }
    public required IReadOnlyList<ObserverOptionInstrument> Universe { get; init; }
    public required Func<string, FuturesMicrostructureBar?> CenterBook { get; init; }
}

public static class OptionsSupplementalCalculator
{
    public static OptionsSupplementalBar Build(OptionsSupplementalInput x)
    {
        // Day (subscribed-universe) and band-level volume figures do not need a center contract, so they are filled whenever possible.
        var day = DayVolumes(x);
        if (x.Selection is not { } sel)
        {
            return OptionsSupplementalBar.Unavailable(x.UnavailableReason ?? "Band context unavailable.") with
            {
                DayCeVolume = day.Ce, DayPeVolume = day.Pe, UniverseTokenCount = day.Tokens, TokensObserved = day.Observed,
            };
        }

        var ci = Array.IndexOf(sel.Strikes.ToArray(), sel.CenterStrike);
        if (ci < 2 || ci + 2 >= sel.Calls.Count || sel.Calls.Count != sel.Puts.Count)
        {
            return OptionsSupplementalBar.Unavailable("Band does not contain the symmetric ATM±2 contracts.", sel.CenterStrike) with
            {
                DayCeVolume = day.Ce, DayPeVolume = day.Pe, UniverseTokenCount = day.Tokens, TokensObserved = day.Observed,
            };
        }

        var ce = sel.Calls[ci];
        var pe = sel.Puts[ci];
        var ceStart = x.StartSnapshot(ce.Token); var ceEnd = x.EndSnapshot(ce.Token);
        var peStart = x.StartSnapshot(pe.Token); var peEnd = x.EndSnapshot(pe.Token);

        var ceMidStart = FreshMid(ceStart, x.Start); var ceMidEnd = FreshMid(ceEnd, x.End);
        var peMidStart = FreshMid(peStart, x.Start); var peMidEnd = FreshMid(peEnd, x.End);
        var ceMidDelta = Delta(ceMidStart, ceMidEnd); var peMidDelta = Delta(peMidStart, peMidEnd);
        var ceOiDelta = ceStart?.OpenInterest is { } a0 && ceEnd.OpenInterest is { } a1 ? a1 - a0 : (long?)null;
        var peOiDelta = peStart?.OpenInterest is { } b0 && peEnd.OpenInterest is { } b1 ? b1 - b0 : (long?)null;

        double? Iv(ObserverOptionInstrument i, InstrumentFlowSnapshot? s, DateTimeOffset at, double? underlying)
        {
            if (s is null || underlying is not { } u || FreshMid(s, at) is not { } mid) return null;
            var t = TimeToExpiry.YearsUntilExpiry(i.ExpiryDate, at);
            return AdaptiveResearchPricing.SolveIv(i.OptionType, mid, u, i.Strike, t, x.RiskFreeRate) is { } iv ? iv * 100d : null; // IV points
        }

        double? CeIv(ObserverOptionInstrument i, bool atEnd) => atEnd
            ? Iv(i, x.EndSnapshot(i.Token), x.End, x.EndUnderlying)
            : Iv(i, x.StartSnapshot(i.Token), x.Start, x.StartUnderlying);

        var ceIvStart = CeIv(ce, false); var ceIvEnd = CeIv(ce, true);
        var peIvStart = CeIv(pe, false); var peIvEnd = CeIv(pe, true);

        // Skew = [(IV_PE-1 - IV_CE+1) + (IV_PE-2 - IV_CE+2)] / 2 over the symmetric OTM wings; every one of the four IVs is required.
        double? Skew(bool atEnd)
        {
            var p1 = CeIv(sel.Puts[ci - 1], atEnd); var p2 = CeIv(sel.Puts[ci - 2], atEnd);
            var c1 = CeIv(sel.Calls[ci + 1], atEnd); var c2 = CeIv(sel.Calls[ci + 2], atEnd);
            return p1 is { } && p2 is { } && c1 is { } && c2 is { } ? ((p1.Value - c1.Value) + (p2.Value - c2.Value)) / 2d : null;
        }

        var skewStart = Skew(false); var skewEnd = Skew(true);

        long? ceQty = x.CallBand?.ContractTotalQuantity; long? peQty = x.PutBand?.ContractTotalQuantity;
        var window = x.RollingWindowBars;
        long? rollCe = null, rollPe = null;
        if (window > 0 && x.CallHistory.Count >= window && x.PutHistory.Count >= window)
        {
            var calls = x.CallHistory.Skip(x.CallHistory.Count - window).ToArray();
            var puts = x.PutHistory.Skip(x.PutHistory.Count - window).ToArray();
            // Same all-bars-present rule as the existing rolling option metrics: one missing band blanks the window.
            if (calls.All(c => c is not null) && puts.All(p => p is not null))
            {
                rollCe = calls.Sum(c => c!.ContractTotalQuantity);
                rollPe = puts.Sum(p => p!.ContractTotalQuantity);
            }
        }

        var ceBook = x.CenterBook(ce.Token); var peBook = x.CenterBook(pe.Token);
        var straddleStart = ceMidStart is { } && peMidStart is { } ? ceMidStart + peMidStart : null;
        var straddleEnd = ceMidEnd is { } && peMidEnd is { } ? ceMidEnd + peMidEnd : null;
        return new OptionsSupplementalBar(
            sel.CenterStrike, null,
            ceStart?.OpenInterest, ceEnd.OpenInterest, ceOiDelta,
            peStart?.OpenInterest, peEnd.OpenInterest, peOiDelta,
            ceMidStart, ceMidEnd, ceMidDelta,
            peMidStart, peMidEnd, peMidDelta,
            Position(OptionType.Call, ceOiDelta, ceMidDelta), Position(OptionType.Put, peOiDelta, peMidDelta),
            ceIvStart, ceIvEnd, Delta(ceIvStart, ceIvEnd),
            peIvStart, peIvEnd, Delta(peIvStart, peIvEnd),
            skewStart, skewEnd, Delta(skewStart, skewEnd),
            ceQty, peQty, Ratio(peQty, ceQty),
            rollCe, rollPe, Ratio(rollPe, rollCe),
            day.Ce, day.Pe, day.Tokens, day.Observed,
            ceBook?.MicroDevTimeWeighted, ceBook?.Ofi,
            peBook?.MicroDevTimeWeighted, peBook?.Ofi,
            x.DurationSeconds > 0d && ceQty is { } cq ? cq / x.DurationSeconds : null,
            x.DurationSeconds > 0d && peQty is { } pq ? pq / x.DurationSeconds : null,
            straddleStart, straddleEnd, Delta(straddleStart, straddleEnd));
    }

    /// <summary>
    /// Center-contract position label from open-interest change and quote-midpoint change (never LTP). Missing inputs => null (unavailable);
    /// a zero change on either leg => "Neutral" (no inference about who initiated). These are behaviour-compatible labels, not actor identity.
    /// </summary>
    public static string? Position(OptionType side, long? oiDelta, double? midDelta)
    {
        if (oiDelta is not { } oi || midDelta is not { } mid) return null;
        if (oi == 0L || mid == 0d) return "Neutral";
        var prefix = side == OptionType.Call ? "Call" : "Put";
        return (oi > 0L, mid > 0d) switch
        {
            (true, false) => prefix + "Writing",
            (true, true) => prefix + "LongBuild",
            (false, true) => prefix + "ShortCover",
            _ => prefix + "LongUnwind",
        };
    }

    /// <summary>Two-sided mid (Bid &gt; 0, Ask &gt;= Bid) whose quote is available at or before <paramref name="at"/> and at most 5 s old.</summary>
    public static double? FreshMid(InstrumentFlowSnapshot? s, DateTimeOffset at)
    {
        if (s?.LastAvailableAt is not { } available || available > at) return null;
        if ((at - available).TotalSeconds > OptionsSupplementalBar.QuoteFreshnessSeconds) return null;
        if (s.Bid is not { } bid || s.Ask is not { } ask || !double.IsFinite(bid) || !double.IsFinite(ask) || !(bid > 0d) || ask < bid) return null;
        return (bid + ask) / 2d;
    }

    static double? Ratio(long? numerator, long? denominator) =>
        numerator is { } n && denominator is { } d && d > 0 ? (double)n / d : null;

    static double? Delta(double? start, double? end) => start is { } s && end is { } e ? e - s : null;

    static (long Ce, long Pe, int Tokens, int Observed) DayVolumes(OptionsSupplementalInput x)
    {
        long ce = 0, pe = 0; var observed = 0;
        foreach (var i in x.Universe)
        {
            var s = x.EndSnapshot(i.Token);
            if (s.LastAvailableAt is not null) observed++;
            var traded = s.StrictBuyQuantity + s.StrictSellQuantity + s.StrictUnknownQuantity;
            if (i.OptionType == OptionType.Call) ce += traded; else if (i.OptionType == OptionType.Put) pe += traded;
        }

        return (ce, pe, x.Universe.Count, observed);
    }
}
