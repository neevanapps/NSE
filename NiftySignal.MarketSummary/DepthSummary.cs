namespace NiftySignal.MarketSummary;

/// <summary>
/// One completed bar's EVENT-BASED Top-5 accumulators of ONE instrument (futures or one option contract), from a *-depth5-v2 row. The sums are over the bar's distinct, coherent, valid five-level snapshots, each
/// counted once; no elapsed time is a weight anywhere. A bar without an eligible event has null sums and a zero count: unavailable, never a zero observation.
/// </summary>
public sealed record DepthBar(
    int BarSeq, double? BidNotionalSum, double? AskNotionalSum, int DepthEvents,
    double? MicroDev5Sum = null, double? Tob5Sum = null, int MicroDev5Events = 0,
    long? Ofi5Net = null, long? Ofi5Gross = null)
{
    /// <summary>Depth L5 evidence: at least one eligible event and finite, non-negative notional sums.</summary>
    public bool Usable => DepthEvents > 0 && BidNotionalSum is { } b && AskNotionalSum is { } a && double.IsFinite(b) && double.IsFinite(a) && b >= 0d && a >= 0d;

    public bool MicroDev5Usable => MicroDev5Events > 0 && MicroDev5Sum is { } m && Tob5Sum is { } t && double.IsFinite(m) && double.IsFinite(t);

    public bool Ofi5Usable => Ofi5Net is not null && Ofi5Gross is not null;
}

/// <summary>All attested depth bars of the basket's option tokens, indexed by token and bar.</summary>
public sealed class DepthHistory
{
    readonly Dictionary<string, Dictionary<int, DepthBar>> _byToken = new(StringComparer.Ordinal);

    public DepthHistory(IEnumerable<(string Token, DepthBar Bar)> bars)
    {
        foreach (var (token, bar) in bars)
        {
            if (!_byToken.TryGetValue(token, out var m)) _byToken[token] = m = new();
            m[bar.BarSeq] = bar;
        }
    }

    public DepthBar? Get(string token, int barSeq) => _byToken.TryGetValue(token, out var m) && m.TryGetValue(barSeq, out var b) ? b : null;
}

/// <summary>
/// Futures Top-5 event-based window: Depth Imbalance L5 = (ΣBid − ΣAsk) / (ΣBid + ΣAsk) of the window's pooled event notionals (raw = the fraction, Ratio = ×100); OFI5 = ΣNet / ΣGross; MicroDev5 = ΣMicroDev5 / ΣEvents
/// with ratio mean TOB5 × 100. Bars without eligible events are skipped and reported as PARTIAL; <see cref="DepthEvents"/> is the number of distinct coherent snapshots pooled.
/// </summary>
public sealed record FuturesDepthWindow(
    SummaryWindow Window, int? FromBar, int ToBar, int UsableBars, int Bars, Metric Imbalance,
    Metric Ofi5 = default, Metric MicroDev5 = default, int DepthEvents = 0, int MicroDev5Events = 0)
{
    public CoverageStatus Status => Imbalance.Raw is null ? CoverageStatus.Insufficient : UsableBars == Bars ? CoverageStatus.Full : CoverageStatus.Partial;

    public string Text => Status switch
    {
        CoverageStatus.Insufficient => "—",
        CoverageStatus.Full => $"{UsableBars}/{Bars} · {DepthEvents} ev",
        _ => $"{UsableBars}/{Bars} partial · {DepthEvents} ev",
    };
}

/// <summary>Why contracts of a window did NOT contribute to a Total-grid depth metric (the question "why is Depth L5 Dir often unavailable"): per-contract, first failing reason.</summary>
public sealed record DepthExclusions(int Unresolved, int NoAttestedRow, int NoEligibleEvents)
{
    public static readonly DepthExclusions None = new(0, 0, 0);

    public override string ToString() => $"unresolved {Unresolved}, no attested row {NoAttestedRow}, no coherent depth event in a bar {NoEligibleEvents}";
}

/// <summary>
/// Total Options Summary event-based direction metrics of one window. CE and PE are pooled SEPARATELY over the contracts that qualify for the family:
///  - Depth L5 Dir: CE ratio − PE ratio with ratio = (ΣBid − ΣAsk) / (ΣBid + ΣAsk) of each side's pooled event notionals; Den = 1. A contract counts only when EVERY bar of the window has an attested row with at least one eligible event.
///  - MicroDev5 Dir (RAW, not inverted): CE raw − PE raw; raw = ΣMicroDev5 / ΣEvents per side, direction ratio = (CE mean TOB5 − PE mean TOB5) / 2 × 100. Same all-bars rule (a bar needs ≥ 1 eligible event).
///  - OFI5 Dir: (ΣCE net − ΣPE net) over (ΣCE gross + ΣPE gross), the Level-1 OFI rule: an attested row in every bar; bars without a valid transition contribute nothing.
/// A hole is never bridged, never zero-filled, never substituted by another contract, strike or expiry.
/// </summary>
public sealed record DepthDirectionWindow(
    SummaryWindow Window, int? FromBar, int ToBar, int CeContracts, int PeContracts, int Expected, Metric Ce, Metric Pe, Metric Direction,
    Metric Ofi5Dir = default, Metric MicroDev5Dir = default, int Ofi5Contracts = 0, int MicroDev5Contracts = 0, int DepthEvents = 0,
    DepthExclusions? DepthWhy = null)
{
    public int Included => CeContracts + PeContracts;

    public CoverageStatus Status => Direction.Raw is null ? CoverageStatus.Insufficient : Included == Expected ? CoverageStatus.Full : CoverageStatus.Partial;

    public string Text => Status switch
    {
        CoverageStatus.Insufficient => $"— ({Included}/{Expected})",
        CoverageStatus.Full => $"{Included}/{Expected} · {DepthEvents} ev",
        _ => $"{Included}/{Expected} partial · {DepthEvents} ev",
    };

    /// <summary>Per-family contract-count text for OFI5 / MicroDev5 (same style as <see cref="Text"/>).</summary>
    public string FamilyText(int contracts, Metric m) => m.Raw is null ? $"— ({contracts}/{Expected})" : contracts == Expected ? $"{contracts}/{Expected}" : $"{contracts}/{Expected} partial";

    public string Ofi5Text => FamilyText(Ofi5Contracts, Ofi5Dir);

    public string MicroDev5Text => FamilyText(MicroDev5Contracts, MicroDev5Dir);
}

public static class DepthSummaryCalculator
{
    /// <summary>Plain event sums over bars [from, to]. No weights. <paramref name="requireAll"/>: any bar failing <paramref name="ok"/> voids the whole instrument.</summary>
    sealed class Acc
    {
        public double Bid, Ask, Micro, Tob;
        public long Net, Gross;
        public int DepthEvents, MicroEvents, DepthBars, MicroBars, OfiBars;
    }

    static Acc Fold(int from, int to, Func<int, DepthBar?> get)
    {
        var a = new Acc();
        for (var b = from; b <= to; b++)
        {
            if (get(b) is not { } x) continue;
            if (x.Usable) { a.Bid += x.BidNotionalSum!.Value; a.Ask += x.AskNotionalSum!.Value; a.DepthEvents += x.DepthEvents; a.DepthBars++; }
            if (x.MicroDev5Usable) { a.Micro += x.MicroDev5Sum!.Value; a.Tob += x.Tob5Sum!.Value; a.MicroEvents += x.MicroDev5Events; a.MicroBars++; }
            if (x.Ofi5Usable) { a.Net += x.Ofi5Net!.Value; a.Gross += x.Ofi5Gross!.Value; a.OfiBars++; }
        }

        return a;
    }

    static Metric DepthMetric(double bid, double ask, int events)
    {
        var den = bid + ask;
        return events > 0 && den > 0d && double.IsFinite(den) ? new Metric((bid - ask) / den, bid - ask, den) : Metric.None;
    }

    static Metric MicroMetric(double micro, double tob, int events) =>
        events > 0 && double.IsFinite(micro) && double.IsFinite(tob) ? new Metric(micro / events, tob, events) : Metric.None;

    /// <summary>Futures: one row per fixed window, every metric a ratio of SUMS of the bars' underlying event accumulators (never an average of per-bar percentages).</summary>
    public static IReadOnlyList<FuturesDepthWindow> Futures(IReadOnlyDictionary<int, DepthBar> bars, int endBarSeq)
    {
        var rows = new List<FuturesDepthWindow>(SummaryWindow.All.Count);
        foreach (var w in SummaryWindow.All)
        {
            var from = w.Bars is { } n ? endBarSeq - n + 1 : 1;
            if (endBarSeq < 1 || from < 1) { rows.Add(new(w, null, endBarSeq, 0, 0, Metric.None)); continue; }
            var a = Fold(from, endBarSeq, b => bars.GetValueOrDefault(b));
            var ofi = a.OfiBars > 0 ? new Metric(a.Net, a.Net, a.Gross) : Metric.None;
            rows.Add(new(w, from, endBarSeq, a.DepthBars, endBarSeq - from + 1, DepthMetric(a.Bid, a.Ask, a.DepthEvents), ofi, MicroMetric(a.Micro, a.Tob, a.MicroEvents), a.DepthEvents, a.MicroEvents));
        }

        return rows;
    }

    /// <summary>Options: the grid's CE and PE contracts of <paramref name="kind"/> (the Total grids use all ten of the basket).</summary>
    public static IReadOnlyList<DepthDirectionWindow> Direction(Basket basket, GridKind kind, DepthHistory history, int endBarSeq)
    {
        var expected = Basket.Expected(kind);
        var rows = new List<DepthDirectionWindow>(SummaryWindow.All.Count);
        foreach (var w in SummaryWindow.All)
        {
            var from = w.Bars is { } n ? endBarSeq - n + 1 : 1;
            if (endBarSeq < 1 || from < 1) { rows.Add(new(w, null, endBarSeq, 0, 0, expected, Metric.None, Metric.None, Metric.None)); continue; }
            double ceBid = 0d, ceAsk = 0d, peBid = 0d, peAsk = 0d, ceMic = 0d, ceTob = 0d, peMic = 0d, peTob = 0d;
            long ceNet = 0, ceGross = 0, peNet = 0, peGross = 0;
            int ceN = 0, peN = 0, ceEv = 0, peEv = 0, ceMicEv = 0, peMicEv = 0, ceMicN = 0, peMicN = 0, ceOfiN = 0, peOfiN = 0;
            int unresolved = 0, noRow = 0, noEvents = 0;
            foreach (var c in basket.For(kind))
            {
                if (c.Token is null) { unresolved++; continue; }
                var token = c.Token;
                var a = Fold(from, endBarSeq, b => history.Get(token, b));
                var total = endBarSeq - from + 1;
                var rowBars = 0;
                for (var b = from; b <= endBarSeq; b++) if (history.Get(token, b) is not null) rowBars++;
                if (rowBars < total) noRow++;
                else if (a.DepthBars < total) noEvents++;

                var allDepth = a.DepthBars == total;                          // every bar: attested row with >= 1 eligible event
                var allMicro = a.MicroBars == total;
                var allRows = rowBars == total && a.OfiBars > 0;              // OFI: Level-1 rule (row every bar; bars without a valid transition add nothing)
                if (allDepth) { if (c.IsCall) { ceBid += a.Bid; ceAsk += a.Ask; ceN++; ceEv += a.DepthEvents; } else { peBid += a.Bid; peAsk += a.Ask; peN++; peEv += a.DepthEvents; } }
                if (allMicro) { if (c.IsCall) { ceMic += a.Micro; ceTob += a.Tob; ceMicEv += a.MicroEvents; ceMicN++; } else { peMic += a.Micro; peTob += a.Tob; peMicEv += a.MicroEvents; peMicN++; } }
                if (allRows) { if (c.IsCall) { ceNet += a.Net; ceGross += a.Gross; ceOfiN++; } else { peNet += a.Net; peGross += a.Gross; peOfiN++; } }
            }

            var ce = DepthMetric(ceBid, ceAsk, ceEv);
            var pe = DepthMetric(peBid, peAsk, peEv);
            // Depth Direction = CE ratio - PE ratio, each normalized by its OWN side's pooled notional; Den = 1 so Ratio = (CE - PE) x 100.
            var dir = ce.Raw is { } cr && pe.Raw is { } pr ? new Metric(cr - pr, cr - pr, 1d) : Metric.None;

            // MicroDev5 Direction is RAW (not inverted): CE raw - PE raw; ratio = (CE mean TOB5 - PE mean TOB5) / 2 x 100 (MicroDev5 = HalfSpread x TOB5, same shape as the Level-1 direction).
            var cm = MicroMetric(ceMic, ceTob, ceMicEv);
            var pm = MicroMetric(peMic, peTob, peMicEv);
            var mdir = cm.Raw is { } c1 && pm.Raw is { } p1 ? new Metric(c1 - p1, (ceTob / ceMicEv - peTob / peMicEv) / 2d, 1d) : Metric.None;

            // OFI5 Direction: combined-denominator rule, exactly like the Level-1 OFI direction.
            var odir = ceOfiN > 0 && peOfiN > 0 ? new Metric(ceNet - peNet, ceNet - peNet, (double)ceGross + peGross) : Metric.None;

            rows.Add(new(w, from, endBarSeq, ceN, peN, expected, ce, pe, dir, odir, mdir, ceOfiN + peOfiN, ceMicN + peMicN, ceEv + peEv, new DepthExclusions(unresolved, noRow, noEvents)));
        }

        return rows;
    }
}
