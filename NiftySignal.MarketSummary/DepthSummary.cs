namespace NiftySignal.MarketSummary;

/// <summary>
/// One completed bar's Top-5 depth notionals of ONE instrument (futures or one option contract): the bar's time-weighted bid / ask notional over its <see cref="UsableSeconds"/> (valid snapshot, within the 5 s freshness rule).
/// A bar with no usable seconds (or a null notional) is an unavailable observation, never a zero.
/// </summary>
public sealed record DepthBar(int BarSeq, double UsableSeconds, double? BidNotional, double? AskNotional)
{
    public bool Usable => UsableSeconds > 0d && double.IsFinite(UsableSeconds) && BidNotional is { } b && AskNotional is { } a && double.IsFinite(b) && double.IsFinite(a) && b >= 0d && a >= 0d;
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

/// <summary>Futures Notional Depth Imbalance L5 of one window: (ΣBid − ΣAsk) / (ΣBid + ΣAsk), the bars' time-weighted notionals pooled with their usable seconds as weights. Raw = the fraction; Ratio = ×100.</summary>
public sealed record FuturesDepthWindow(SummaryWindow Window, int? FromBar, int ToBar, int UsableBars, int Bars, Metric Imbalance)
{
    public CoverageStatus Status => Imbalance.Raw is null ? CoverageStatus.Insufficient : UsableBars == Bars ? CoverageStatus.Full : CoverageStatus.Partial;

    public string Text => Status switch
    {
        CoverageStatus.Insufficient => "—",
        CoverageStatus.Full => $"{UsableBars}/{Bars}",
        _ => $"{UsableBars}/{Bars} partial",
    };
}

/// <summary>
/// Total Options Summary "Notional Depth L5 Direction": CE and PE are normalized SEPARATELY (premium-notional pooled across the side's included contracts, time-weighted by usable seconds),
/// and the Direction is CE ratio − PE ratio. <see cref="CeContracts"/> / <see cref="PeContracts"/> count the contracts included: a contract is included only when EVERY bar of the window has an attested row with usable depth
/// (the same all-bars discipline as the other flow families; a hole is never bridged, never zero-filled, never substituted by another contract).
/// </summary>
public sealed record DepthDirectionWindow(SummaryWindow Window, int? FromBar, int ToBar, int CeContracts, int PeContracts, int Expected, Metric Ce, Metric Pe, Metric Direction)
{
    public int Included => CeContracts + PeContracts;

    public CoverageStatus Status => Direction.Raw is null ? CoverageStatus.Insufficient : Included == Expected ? CoverageStatus.Full : CoverageStatus.Partial;

    public string Text => Status switch
    {
        CoverageStatus.Insufficient => $"— ({Included}/{Expected})",
        CoverageStatus.Full => $"{Included}/{Expected}",
        _ => $"{Included}/{Expected} partial",
    };
}

public static class DepthSummaryCalculator
{
    /// <summary>Pooled imbalance numerator / denominator for one instrument over bars [from, to]; null when any bar of the window is not usable and <paramref name="requireAll"/> is true, or when nothing is usable.</summary>
    static (double Num, double Den, int Bars)? Pool(int from, int to, Func<int, DepthBar?> get, bool requireAll)
    {
        double num = 0d, den = 0d;
        var n = 0;
        for (var b = from; b <= to; b++)
        {
            var x = get(b);
            if (x is null || !x.Usable)
            {
                if (requireAll) return null;
                continue;
            }

            num += (x.BidNotional!.Value - x.AskNotional!.Value) * x.UsableSeconds;
            den += (x.BidNotional.Value + x.AskNotional.Value) * x.UsableSeconds;
            n++;
        }

        return n == 0 ? null : (num, den, n);
    }

    static Metric ToMetric((double Num, double Den, int Bars)? p) =>
        p is { } v && v.Den > 0d && double.IsFinite(v.Num) && double.IsFinite(v.Den) ? new Metric(v.Num / v.Den, v.Num, v.Den) : Metric.None;

    /// <summary>Futures: one row per fixed window. Bars without usable depth are skipped (the time weighting already accounts for the usable seconds that exist) and reported in the window's coverage.</summary>
    public static IReadOnlyList<FuturesDepthWindow> Futures(IReadOnlyDictionary<int, DepthBar> bars, int endBarSeq)
    {
        var rows = new List<FuturesDepthWindow>(SummaryWindow.All.Count);
        foreach (var w in SummaryWindow.All)
        {
            var from = w.Bars is { } n ? endBarSeq - n + 1 : 1;
            if (endBarSeq < 1 || from < 1) { rows.Add(new(w, null, endBarSeq, 0, 0, Metric.None)); continue; }
            var pool = Pool(from, endBarSeq, b => bars.GetValueOrDefault(b), requireAll: false);
            rows.Add(new(w, from, endBarSeq, pool?.Bars ?? 0, endBarSeq - from + 1, ToMetric(pool)));
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
            double ceNum = 0d, ceDen = 0d, peNum = 0d, peDen = 0d;
            int ceN = 0, peN = 0;
            foreach (var c in basket.For(kind))
            {
                if (c.Token is null) continue;
                var token = c.Token;
                if (Pool(from, endBarSeq, b => history.Get(token, b), requireAll: true) is not { } p) continue;
                if (c.IsCall) { ceNum += p.Num; ceDen += p.Den; ceN++; } else { peNum += p.Num; peDen += p.Den; peN++; }
            }

            var ce = ToMetric(ceN == 0 ? null : (ceNum, ceDen, ceN));
            var pe = ToMetric(peN == 0 ? null : (peNum, peDen, peN));
            // Direction = CE ratio - PE ratio, each normalized by its OWN side's pooled notional; Den = 1 so Ratio = (CE - PE) x 100.
            var dir = ce.Raw is { } cr && pe.Raw is { } pr ? new Metric(cr - pr, cr - pr, 1d) : Metric.None;
            rows.Add(new(w, from, endBarSeq, ceN, peN, expected, ce, pe, dir));
        }

        return rows;
    }
}
