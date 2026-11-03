using NiftySignal.MarketSummary;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Metrics;
using CoreMoneyness = NiftySignal.PaperEngineV2.Core.Frames.Moneyness;
using SummaryMoneyness = NiftySignal.MarketSummary.Moneyness;

namespace NiftySignal.PaperEngineV2.Frames;

/// <summary>The completed fixed-13,650-volume bar a frame is built for. <see cref="AdaptiveModelVersion"/> must be the fixed-clock model; the NPT1 M1 clock and the opening-volume model are refused.</summary>
public sealed record BarInput(
    long SessionId, DateOnly TradeDate, string AdaptiveModelVersion, string? AtmRuleVersion, int BarSeq, DateTimeOffset StartAvailableAt, DateTimeOffset EndAvailableAt,
    long LastSourceTickId, double DurationSeconds, bool Verified, string? UnverifiedReason);

/// <summary>A top-of-book observation of a basket contract (as available at or before the bar's end; anything later is look-ahead and refuses the frame).</summary>
public sealed record QuoteInput(string Token, double Bid, double Ask, long BidQty, long AskQty, DateTimeOffset AvailableAt, long SourceTickId);

/// <summary>
/// Maps ONE bar's already-computed Market Summary results (futures, the four option grids, India VIX) plus the basket's quotes into an immutable <see cref="MarketStateFrame"/>. Pure: no clock, no I/O, no observer
/// state. A metric that the summary reports as unavailable, insufficient or non-finite is OMITTED (Unknown), never written as zero. The builder keeps only the tiny per-session ordinal state needed to number sibling bars
/// of the same market update.
/// </summary>
public sealed class MarketStateFrameBuilder
{
    public const string FixedClockModelVersion = "adaptive-fixed13650-v1";

    long? _session;
    int _lastSeq;
    DateTimeOffset _lastEnd;
    int _lastOrdinal;

    static readonly string[] Families = ["price", "roll", "strict", "enriched", "oi", "microdev", "ofi", "iv", "delta", "vega", "activity"];
    static readonly string[] GridNames = ["atm", "itm", "otm", "total"];
    static readonly string[] SideNames = ["ce", "pe", "dir"];

    // handle tables resolved once from the registry: [grid][family][side][kind], -1 = the registry has no such metric
    static readonly int[][][][] OptionHandles = BuildOptionHandles();
    static readonly int[] FutHandles = new[] { "price_change", "volume", "strict", "enriched", "oi_change", "microdev", "ofi", "activity" }.SelectMany(f => new[] { Handle($"fut.{f}:raw"), Handle($"fut.{f}:pct") }).ToArray();
    static readonly int FutCoveragePct = Handle("fut.coverage_pct", required: true), FutCoverageFull = Handle("fut.coverage_full", required: true);
    static readonly int[] GridCoveragePct = GridNames.Select(g => Handle($"opt.{g}.coverage_pct", required: true)).ToArray();
    static readonly int[] GridCoverageFull = GridNames.Select(g => Handle($"opt.{g}.coverage_full", required: true)).ToArray();
    // the Dashboard's inverted MicroDev direction (opt.total only): the ONLY place a MicroDev sign is flipped for a strategy
    static readonly int TotalMicroDevInvRaw = Handle("opt.total.microdev_inv.dir:raw", required: true), TotalMicroDevInvPct = Handle("opt.total.microdev_inv.dir:pct", required: true);
    static readonly int VixLevel = Handle("vix.level:raw", required: true), VixChange = Handle("vix.change:raw", required: true), VixChangePct = Handle("vix.change:pct", required: true), VixFresh = Handle("vix.fresh", required: true);

    static int Handle(string id, bool required = false) => MetricRegistry.TryGet(id, out var s) ? s.Handle : required ? throw new InvalidOperationException($"registry lacks {id}") : -1;

    static int[][][][] BuildOptionHandles() =>
        GridNames.Select(g => Families.Select(f => SideNames.Select(s => new[] { Handle($"opt.{g}.{f}.{s}:raw"), Handle($"opt.{g}.{f}.{s}:pct") }).ToArray()).ToArray()).ToArray();

    public MarketStateFrame Build(
        BarInput bar, Basket basket, IReadOnlyList<FuturesWindowSummary> futures, IReadOnlyList<GridSummary> grids, IReadOnlyList<VixWindowSummary>? vix, IReadOnlyList<QuoteInput> quotes, IReadOnlyList<QuoteInput>? heldQuotes = null)
    {
        ArgumentNullException.ThrowIfNull(bar); ArgumentNullException.ThrowIfNull(basket); ArgumentNullException.ThrowIfNull(futures); ArgumentNullException.ThrowIfNull(grids); ArgumentNullException.ThrowIfNull(quotes);
        if (bar.AdaptiveModelVersion != FixedClockModelVersion)
            throw new FrameValidationException($"model '{bar.AdaptiveModelVersion}' is not the fixed-13,650 clock ('{FixedClockModelVersion}'): the NPT1 M1 clock and the opening-volume model are never mixed into a V2 frame");
        if (futures.Count != SummaryWindows.Count) throw new FrameValidationException($"{SummaryWindows.Count} futures window rows are required, got {futures.Count}");
        if (futures.Any(f => f.ToBar != bar.BarSeq)) throw new FrameValidationException("a futures summary row was computed for a different bar than the frame's bar (no look-ahead, no lag)");
        if (grids.Count != GridNames.Length || grids.Any(g => g.Rows.Count != SummaryWindows.Count || g.Rows.Any(r => r.ToBar != bar.BarSeq))) throw new FrameValidationException("the four option grids with five rows each, computed for this bar, are required");
        if (vix is not null && vix.Count != SummaryWindows.Count) throw new FrameValidationException($"{SummaryWindows.Count} VIX window rows are required when the VIX is supplied");

        // ordinal of this bar among bars that share its availability instant (zero-duration siblings of one market update)
        var sameSession = _session == bar.SessionId;
        var ordinal = sameSession && bar.BarSeq == _lastSeq + 1 && bar.EndAvailableAt == _lastEnd ? _lastOrdinal + 1 : 0;

        var metrics = new List<MetricValue>(1_024);
        MapFutures(metrics, futures);
        for (var g = 0; g < GridNames.Length; g++) MapGrid(metrics, g, grids[g]);
        var vixAvailability = MapVix(metrics, vix);

        var slots = basket.Contracts.Select(c => new BasketSlot(c.Offset, c.Strike, c.IsCall, c.Token, c.Moneyness switch
        {
            SummaryMoneyness.Itm => CoreMoneyness.Itm, SummaryMoneyness.Atm => CoreMoneyness.Atm, _ => CoreMoneyness.Otm,
        })).ToList();
        var basketId = new BasketIdentity(basket.Expiry, basket.Center, basket.Step, slots, basket.Problems.ToList());

        var usable = new List<QuoteSnapshot>();
        var issues = new List<QuoteIssue>();
        var byToken = quotes.GroupBy(q => q.Token, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var token in slots.Where(s => s.Token is not null).Select(s => s.Token!))
        {
            if (!byToken.TryGetValue(token, out var list)) { issues.Add(new QuoteIssue(token, "Missing")); continue; }
            if (list.Count > 1) { issues.Add(new QuoteIssue(token, "Ambiguous")); continue; }
            var q = list[0];
            if (q.AvailableAt > bar.EndAvailableAt) throw new FrameValidationException($"look-ahead: the quote of {token} became available at {q.AvailableAt:O}, after the bar end {bar.EndAvailableAt:O}");
            if (!double.IsFinite(q.Bid) || !double.IsFinite(q.Ask) || q.Bid <= 0d || q.Ask < q.Bid || q.BidQty < 0 || q.AskQty < 0) { issues.Add(new QuoteIssue(token, "NotTwoSided")); continue; }
            usable.Add(new QuoteSnapshot(token, q.Bid, q.Ask, q.BidQty, q.AskQty, q.AvailableAt, q.SourceTickId));
        }

        if (quotes.Any(q => !slots.Any(s => s.Token == q.Token))) throw new FrameValidationException("a quote was supplied for a token outside the basket (no substitution)");

        // held-contract quotes: observed quotes of contracts held by open positions OUTSIDE this basket (the caller passes only those); a missing or unusable one is simply absent (never inferred, never another strike)
        var held = new List<QuoteSnapshot>();
        foreach (var q in heldQuotes ?? [])
        {
            if (slots.Any(s => s.Token == q.Token)) throw new FrameValidationException($"held quote of {q.Token} supplied for a basket token: a basket contract is quoted once");
            if (q.AvailableAt > bar.EndAvailableAt) throw new FrameValidationException($"look-ahead: the held quote of {q.Token} became available at {q.AvailableAt:O}, after the bar end {bar.EndAvailableAt:O}");
            if (!double.IsFinite(q.Bid) || !double.IsFinite(q.Ask) || q.Bid <= 0d || q.Ask < q.Bid || q.BidQty < 0 || q.AskQty < 0) continue;
            held.Add(new QuoteSnapshot(q.Token, q.Bid, q.Ask, q.BidQty, q.AskQty, q.AvailableAt, q.SourceTickId));
        }

        var identity = new FrameIdentity(held.Count > 0 ? FrameIdentity.HeldQuotesSchema : FrameIdentity.CurrentSchema, MetricRegistry.SourceModel, bar.AdaptiveModelVersion, bar.AtmRuleVersion, bar.SessionId, bar.TradeDate, bar.BarSeq,
            bar.StartAvailableAt, bar.EndAvailableAt, bar.LastSourceTickId, ordinal, bar.DurationSeconds, bar.Verified, bar.Verified ? null : bar.UnverifiedReason ?? "unverified");
        var frame = MarketStateFrame.Create(identity, basketId, metrics, usable, issues, vixAvailability, held);
        _session = bar.SessionId; _lastSeq = bar.BarSeq; _lastEnd = bar.EndAvailableAt; _lastOrdinal = ordinal;      // only after the frame validated
        return frame;
    }

    static void Put(List<MetricValue> list, int handle, SummaryWindowId window, double? value)
    {
        if (handle >= 0 && value is { } v && double.IsFinite(v)) list.Add(new MetricValue(handle, window, v));
    }

    static void PutMetric(List<MetricValue> list, int rawHandle, int pctHandle, SummaryWindowId w, NiftySignal.MarketSummary.Metric m)
    {
        Put(list, rawHandle, w, m.Raw);
        Put(list, pctHandle, w, m.Ratio);
    }

    static void MapFutures(List<MetricValue> list, IReadOnlyList<FuturesWindowSummary> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var w = (SummaryWindowId)i;
            if (r.Coverage.Status == CoverageStatus.Insufficient) continue;
            PutMetric(list, FutHandles[0], FutHandles[1], w, r.Price);
            Put(list, FutHandles[2], w, r.Volume);
            PutMetric(list, FutHandles[4], FutHandles[5], w, r.Strict);
            PutMetric(list, FutHandles[6], FutHandles[7], w, r.Enriched);
            PutMetric(list, FutHandles[8], FutHandles[9], w, r.Oi);
            PutMetric(list, FutHandles[10], FutHandles[11], w, r.MicroDev);
            PutMetric(list, FutHandles[12], FutHandles[13], w, r.Ofi);
            Put(list, FutHandles[14], w, r.Activity);
            if (r.Coverage.Expected > 0) Put(list, FutCoveragePct, w, r.Coverage.Included * 100d / r.Coverage.Expected);
            Put(list, FutCoverageFull, w, r.Coverage.Status == CoverageStatus.Full ? 1d : 0d);
        }
    }

    static void MapGrid(List<MetricValue> list, int g, GridSummary grid)
    {
        var h = OptionHandles[g];
        for (var i = 0; i < grid.Rows.Count; i++)
        {
            var r = grid.Rows[i]; var w = (SummaryWindowId)i;
            if (r.Coverage.Status == CoverageStatus.Insufficient) continue;
            if (r.Ce is { } ce) MapSide(list, h, 0, w, ce);
            if (r.Pe is { } pe) MapSide(list, h, 1, w, pe);
            if (r.Direction is { } d)
            {
                PutMetric(list, h[0][2][0], h[0][2][1], w, d.Price); PutMetric(list, h[1][2][0], h[1][2][1], w, d.Roll);
                PutMetric(list, h[2][2][0], h[2][2][1], w, d.Strict); PutMetric(list, h[3][2][0], h[3][2][1], w, d.Enriched);
                PutMetric(list, h[4][2][0], h[4][2][1], w, d.Oi); PutMetric(list, h[5][2][0], h[5][2][1], w, d.MicroDev);
                PutMetric(list, h[6][2][0], h[6][2][1], w, d.Ofi); PutMetric(list, h[7][2][0], h[7][2][1], w, d.Iv);
                PutMetric(list, h[8][2][0], h[8][2][1], w, d.Delta); PutMetric(list, h[9][2][0], h[9][2][1], w, d.Vega);
                PutMetric(list, h[10][2][0], h[10][2][1], w, d.Activity);
            }

            if (g == TotalGridIndex && r.Direction is { } inv)
            {
                Put(list, TotalMicroDevInvRaw, w, Invert(inv.MicroDev.Raw));         // same sign convention as the Dashboard's MicroDev (Inv) column: value = -raw, ratio flips with it, zero stays zero
                Put(list, TotalMicroDevInvPct, w, Invert(inv.MicroDev.Ratio));
            }

            if (r.Coverage.Expected > 0) Put(list, GridCoveragePct[g], w, r.Coverage.Included * 100d / r.Coverage.Expected);
            Put(list, GridCoverageFull[g], w, r.Coverage.Status == CoverageStatus.Full ? 1d : 0d);
        }
    }

    const int TotalGridIndex = 3;

    static double? Invert(double? v) => v is { } x && double.IsFinite(x) ? (x == 0d ? 0d : -x) : null;

    static void MapSide(List<MetricValue> list, int[][][] h, int side, SummaryWindowId w, SideSummary s)
    {
        PutMetric(list, h[0][side][0], h[0][side][1], w, s.Price); PutMetric(list, h[1][side][0], h[1][side][1], w, s.Roll);
        PutMetric(list, h[2][side][0], h[2][side][1], w, s.Strict); PutMetric(list, h[3][side][0], h[3][side][1], w, s.Enriched);
        PutMetric(list, h[4][side][0], h[4][side][1], w, s.Oi); PutMetric(list, h[5][side][0], h[5][side][1], w, s.MicroDev);
        PutMetric(list, h[6][side][0], h[6][side][1], w, s.Ofi); PutMetric(list, h[7][side][0], h[7][side][1], w, s.Iv);
        PutMetric(list, h[8][side][0], h[8][side][1], w, s.Delta); PutMetric(list, h[9][side][0], h[9][side][1], w, s.Vega);
        Put(list, h[10][side][0], w, s.Activity);                              // per-side activity has a raw form only
    }

    static VixAvailability MapVix(List<MetricValue> list, IReadOnlyList<VixWindowSummary>? vix)
    {
        for (var i = 0; i < SummaryWindows.Count; i++)
        {
            var w = (SummaryWindowId)i;
            var row = vix?.FirstOrDefault(v => v.Window.Name == SummaryWindow.All[i].Name);
            Put(list, VixFresh, w, row is not null && row.End.Status == VixStatus.Fresh ? 1d : 0d);     // a missing / stale VIX is 0, never Unknown
            if (row is null) continue;
            Put(list, VixLevel, w, row.End.Value); Put(list, VixChange, w, row.Change); Put(list, VixChangePct, w, row.ChangePct);
        }

        var end = vix?.FirstOrDefault()?.End;
        return end is null ? new VixAvailability(null, null, "Unavailable") : new VixAvailability(end.ObservedAt, end.AgeSeconds, end.Status.ToString());
    }
}
