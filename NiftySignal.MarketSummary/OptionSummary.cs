namespace NiftySignal.MarketSummary;

/// <summary>One side (CE or PE) of one grid / window: the aggregate of that side's INCLUDED contracts. See docs/MARKET_SUMMARY_METRICS.md for every rule.</summary>
public sealed record SideSummary(
    int Contracts, long Volume,
    Metric Strict, Metric Enriched, Metric Ofi, Metric MicroDev,
    Metric Price, Metric Roll,
    double? IvLevel, Metric Iv,
    double? DeltaLevel, Metric Delta, double? VegaLevel, Metric Vega,
    Metric Oi, double? Activity);

/// <summary>Direction block: CE minus PE everywhere except OI (PE minus CE); every ratio is built from the COMBINED numerators / denominators, never by subtracting displayed percentages.</summary>
public sealed record DirectionSummary(Metric Price, Metric Roll, Metric Strict, Metric Enriched, Metric Oi, Metric MicroDev, Metric Ofi, Metric Iv, Metric Activity, Metric Delta, Metric Vega);

public sealed record WindowSummary(SummaryWindow Window, int? FromBar, int ToBar, Coverage Coverage, SideSummary? Ce, SideSummary? Pe, DirectionSummary? Direction);

public sealed record GridSummary(GridKind Kind, int Expected, IReadOnlyList<WindowSummary> Rows);

/// <summary>
/// Pure aggregation of the exact ten contracts of ONE fixed basket over the five windows, for completed bars through <c>endBarSeq</c> only. The same basket (same centre, expiry, tokens) is used for every
/// window - history is never re-classified by each bar's own centre. A contract is INCLUDED in a window only when a stored row exists for EVERY bar of the window (no bridging, no substitution).
/// </summary>
public static class OptionSummaryCalculator
{
    /// <summary>The length of the exact-strike rolling price window (equals AdaptiveFixedClockV1.RollingWindowBars; the existing fixed-strike Roll ΔPx uses the same ten bars).</summary>
    public const int RollBars = 10;

    public static IReadOnlyList<GridSummary> ComputeAll(Basket basket, ContractHistory history, int endBarSeq) =>
        new[] { GridKind.Atm, GridKind.Itm, GridKind.Otm, GridKind.Total }.Select(k => Compute(basket, k, history, endBarSeq)).ToList();

    public static GridSummary Compute(Basket basket, GridKind kind, ContractHistory history, int endBarSeq)
    {
        var expected = Basket.Expected(kind);
        var contracts = basket.For(kind);
        var rows = new List<WindowSummary>(5);
        foreach (var w in SummaryWindow.All)
        {
            if (endBarSeq < 1) { rows.Add(new(w, null, endBarSeq, Coverage.Insufficient(expected, "no completed bar"), null, null, null)); continue; }
            var from = w.Bars is { } n ? endBarSeq - n + 1 : 1;
            if (from < 1) { rows.Add(new(w, null, endBarSeq, Coverage.Insufficient(expected, $"only {endBarSeq} completed bars, {w.Name} needs {w.Bars}"), null, null, null)); continue; }

            var ce = new List<ContractWindow>();
            var pe = new List<ContractWindow>();
            var missing = new List<string>();
            foreach (var c in contracts)
            {
                if (c.Token is null) { missing.Add($"unresolved {(c.IsCall ? "CE" : "PE")} offset {c.Offset:+0;-0;0}"); continue; }
                var cw = ContractWindow.Build(c.Token, from, endBarSeq, history);
                if (cw is null) { missing.Add(c.Token); continue; }
                (c.IsCall ? ce : pe).Add(cw);
            }

            var included = ce.Count + pe.Count;
            if (included == 0) { rows.Add(new(w, from, endBarSeq, Coverage.Insufficient(expected, "no contract has every bar of the window", missing), null, null, null)); continue; }
            var status = included == expected ? CoverageStatus.Full : CoverageStatus.Partial;
            var cov = new Coverage(included, expected, status, status == CoverageStatus.Full ? "" : "contracts missing rows for the window", missing);
            var ceS = Side(ce);
            var peS = Side(pe);
            rows.Add(new(w, from, endBarSeq, cov, ceS, peS, ceS is not null && peS is not null ? Direction(ceS, peS) : null));
        }

        return new GridSummary(kind, expected, rows);
    }

    // ---- per-contract window accumulation -------------------------------------------------------------------------------------------------------------------------------------------

    sealed class ContractWindow
    {
        public long Qty, StrictDelta, EnrichedDelta, EnrichedTotal;
        public long? OfiNet, OfiGross;
        public double MdNum, TobNum, MdW;
        public double? PriceStart, PriceEnd, IvStart, IvEnd, DeltaStart, DeltaEnd, VegaStart, VegaEnd;
        public long? OiStart, OiEnd;
        public double ActQty, ActDur;
        public double RollSum, RollStartSum;
        public int RollN;

        public double? ActivityRate => ActDur > 0d ? ActQty / ActDur : null;

        public static ContractWindow? Build(string token, int from, int to, ContractHistory h)
        {
            var cw = new ContractWindow();
            ContractBar? first = null, last = null;
            for (var b = from; b <= to; b++)
            {
                var bar = h.Get(token, b);
                if (bar is null) return null;
                first ??= bar;
                last = bar;
                cw.Qty += bar.TotalQuantity;
                cw.StrictDelta += bar.StrictDelta;
                cw.EnrichedDelta += bar.EnrichedDelta;
                cw.EnrichedTotal += bar.EnrichedBuy + bar.EnrichedSell + bar.EnrichedUnknown;
                if (bar.NetOfi is { } n && bar.GrossOfi is { } g) { cw.OfiNet = (cw.OfiNet ?? 0) + n; cw.OfiGross = (cw.OfiGross ?? 0) + g; }
                if (bar.MicroDevTimeWeighted is { } md && bar.TobTimeWeighted is { } tob && bar.TobUsableSeconds is { } w && w > 0d && double.IsFinite(md) && double.IsFinite(tob))
                {
                    cw.MdNum += md * w; cw.TobNum += tob * w; cw.MdW += w;
                }

                if (h.BarDurationSeconds.TryGetValue(b, out var dur) && dur > 0d) { cw.ActQty += bar.TotalQuantity; cw.ActDur += dur; }
                if (b - RollBars + 1 >= 1 && Roll(token, b, h) is { } r) { cw.RollSum += r.Delta; cw.RollStartSum += r.Start; cw.RollN++; }
            }

            cw.PriceStart = first!.PriceStart; cw.PriceEnd = last!.PriceEnd;
            cw.IvStart = first.IvStart; cw.IvEnd = last.IvEnd;
            cw.DeltaStart = first.DeltaStart; cw.DeltaEnd = last.DeltaEnd;
            cw.VegaStart = first.VegaStart; cw.VegaEnd = last.VegaEnd;
            cw.OiStart = first.OiStart; cw.OiEnd = last.OiEnd;
            return cw;
        }

        /// <summary>Exact-strike roll: PriceEnd of bar b minus PriceStart of bar b-9. Valid only when every bar of those ten has a stored row (no bridging) and the start price is positive.</summary>
        static (double Delta, double Start)? Roll(string token, int b, ContractHistory h)
        {
            for (var k = b - RollBars + 1; k <= b; k++) if (h.Get(token, k) is null) return null;
            var s = h.Get(token, b - RollBars + 1)!.PriceStart;
            var e = h.Get(token, b)!.PriceEnd;
            return s is > 0d && e is { } ev && double.IsFinite(ev) && double.IsFinite(s.Value) ? (ev - s.Value, s.Value) : null;
        }
    }

    // ---- side aggregation (cross-contract) ------------------------------------------------------------------------------------------------------------------------------------------

    static SideSummary? Side(List<ContractWindow> cs)
    {
        if (cs.Count == 0) return null;
        var qty = cs.Sum(c => c.Qty);
        var strict = new Metric(cs.Sum(c => c.StrictDelta), cs.Sum(c => c.StrictDelta), qty);
        var enriched = new Metric(cs.Sum(c => c.EnrichedDelta), cs.Sum(c => c.EnrichedDelta), cs.Sum(c => c.EnrichedTotal));

        var ofiV = cs.Where(c => c.OfiNet is not null).ToList();
        var ofi = ofiV.Count == 0 ? Metric.None : new Metric(ofiV.Sum(c => c.OfiNet!.Value), ofiV.Sum(c => c.OfiNet!.Value), ofiV.Sum(c => c.OfiGross!.Value));

        var micro = cs.Where(c => c.MdW > 0d).ToList();
        var mw = micro.Sum(c => c.MdW);
        var microDev = micro.Count == 0 ? Metric.None : new Metric(micro.Sum(c => c.MdNum) / mw, micro.Sum(c => c.TobNum), mw);

        var price = Change(cs, c => c.PriceStart, c => c.PriceEnd, start => start > 0d);
        var iv = Change(cs, c => c.IvStart, c => c.IvEnd, start => start > 0d);
        var delta = Change(cs, c => c.DeltaStart, c => c.DeltaEnd, start => start != 0d);
        var vega = Change(cs, c => c.VegaStart, c => c.VegaEnd, start => start != 0d);

        var rollC = cs.Where(c => c.RollN > 0).ToList();
        var roll = rollC.Count == 0 ? Metric.None : new Metric(rollC.Average(c => c.RollSum / c.RollN), rollC.Sum(c => c.RollSum), rollC.Sum(c => c.RollStartSum));

        var oiC = cs.Where(c => c.OiStart is > 0 && c.OiEnd is not null).ToList();
        var oi = oiC.Count == 0 ? Metric.None : new Metric(oiC.Sum(c => c.OiEnd!.Value - c.OiStart!.Value), oiC.Sum(c => c.OiEnd!.Value - c.OiStart!.Value), oiC.Sum(c => c.OiStart!.Value));

        var act = cs.Where(c => c.ActivityRate is not null).ToList();
        return new SideSummary(cs.Count, qty, strict, enriched, ofi, microDev, price, roll,
            Average(cs.Select(c => c.IvEnd)), iv,
            Average(cs.Select(c => c.DeltaEnd)), delta, Average(cs.Select(c => c.VegaEnd)), vega,
            oi, act.Count == 0 ? null : act.Sum(c => c.ActivityRate!.Value));
    }

    static double? Average(IEnumerable<double?> xs)
    {
        var v = xs.Where(x => x is { } d && double.IsFinite(d)).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : v.Average();
    }

    /// <summary>start→end change per contract (valid when both are finite and the start passes <paramref name="startOk"/>); group raw = arithmetic mean of the valid changes, ratio = Σchange / Σ|start|.</summary>
    static Metric Change(List<ContractWindow> cs, Func<ContractWindow, double?> start, Func<ContractWindow, double?> end, Func<double, bool> startOk)
    {
        double sum = 0d, den = 0d; var n = 0;
        foreach (var c in cs)
        {
            if (start(c) is { } s && end(c) is { } e && double.IsFinite(s) && double.IsFinite(e) && startOk(s)) { sum += e - s; den += Math.Abs(s); n++; }
        }

        return n == 0 ? Metric.None : new Metric(sum / n, sum, den);
    }

    // ---- direction ------------------------------------------------------------------------------------------------------------------------------------------------------------------

    static DirectionSummary Direction(SideSummary ce, SideSummary pe) => new(
        Price: Dir(ce.Price, pe.Price, 1d), Roll: Dir(ce.Roll, pe.Roll, 1d),
        Strict: Dir(ce.Strict, pe.Strict, 1d), Enriched: Dir(ce.Enriched, pe.Enriched, 1d),
        Oi: Dir(ce.Oi, pe.Oi, -1d),
        MicroDev: MicroDir(ce.MicroDev, pe.MicroDev),
        Ofi: Dir(ce.Ofi, pe.Ofi, 1d),
        Iv: Dir(ce.Iv, pe.Iv, 1d, 2d),
        Activity: ce.Activity is { } a && pe.Activity is { } b ? new Metric(a - b, a - b, a + b) : Metric.None,
        Delta: Dir(ce.Delta, pe.Delta, 1d, 2d), Vega: Dir(ce.Vega, pe.Vega, 1d, 2d));

    /// <summary>sign × (CE − PE); ratio numerator sign × (ΣCE − ΣPE) over the combined denominator (÷ <paramref name="denDivisor"/>, 2 for IV / Greeks = the existing average-start convention).</summary>
    static Metric Dir(Metric ce, Metric pe, double sign, double denDivisor = 1d)
    {
        if (ce.Raw is not { } c || pe.Raw is not { } p) return Metric.None;
        var raw = sign * (c - p);
        return ce.Num is { } cn && pe.Num is { } pn && ce.Den is > 0d && pe.Den is > 0d ? new Metric(raw, sign * (cn - pn), (ce.Den.Value + pe.Den.Value) / denDivisor) : new Metric(raw);
    }

    /// <summary>MicroDev = (Spread/2)·TOB, so the dimensionless ratio is TOB·100 (time-weighted); direction ratio = (CE TOB − PE TOB)/2·100.</summary>
    static Metric MicroDir(Metric ce, Metric pe)
    {
        if (ce.Raw is not { } c || pe.Raw is not { } p) return Metric.None;
        return ce.Num is { } cn && pe.Num is { } pn && ce.Den is > 0d && pe.Den is > 0d ? new Metric(c - p, (cn / ce.Den.Value - pn / pe.Den.Value) / 2d, 1d) : new Metric(c - p);
    }
}
