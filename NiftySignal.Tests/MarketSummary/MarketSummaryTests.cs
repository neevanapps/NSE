using NiftySignal.MarketSummary;

namespace NiftySignal.Tests.MarketSummary;

public class MarketSummaryTests
{
    static readonly DateOnly Expiry = new(2026, 11, 3);

    static IReadOnlyList<ListedStrike> Listed(double from = 25_800, double to = 26_200, double step = 50) =>
        Enumerable.Range(0, (int)((to - from) / step) + 1).Select(i => from + i * step).Select(s => new ListedStrike(s, $"CE{s:0}", $"PE{s:0}")).ToList();

    static Basket BasketAt(double centre = 26_000) => BasketResolver.Resolve(Expiry, centre, Listed());

    static ContractBar Bar(int seq, string token, bool call, Random r, bool v3 = true) => new(
        seq, token, call,
        PriceStart: 100 + r.Next(0, 50), PriceEnd: 100 + r.Next(0, 50),
        TotalQuantity: r.Next(100, 900), StrictDelta: r.Next(-80, 80), EnrichedBuy: r.Next(10, 100), EnrichedSell: r.Next(10, 100), EnrichedUnknown: r.Next(0, 20), EnrichedDelta: r.Next(-50, 50),
        OiStart: 10_000 + r.Next(0, 500), OiEnd: 10_000 + r.Next(0, 500),
        IvStart: 12 + r.NextDouble(), IvEnd: 12 + r.NextDouble(),
        DeltaStart: v3 ? 0.3 + r.NextDouble() * 0.1 : null, DeltaEnd: v3 ? 0.3 + r.NextDouble() * 0.1 : null, VegaStart: v3 ? 5 + r.NextDouble() : null, VegaEnd: v3 ? 5 + r.NextDouble() : null,
        TobTimeWeighted: v3 ? r.NextDouble() - 0.5 : null, MicroDevTimeWeighted: v3 ? r.NextDouble() - 0.5 : null, TobUsableSeconds: v3 ? 5 + r.NextDouble() * 20 : null,
        NetOfi: v3 ? r.Next(-30, 30) : null, GrossOfi: v3 ? r.Next(40, 90) : null);

    static (ContractHistory History, List<ContractBar> All) Data(Basket b, int bars, int seed = 7, Func<int, string, bool>? skip = null, bool v3 = true)
    {
        var r = new Random(seed);
        var all = new List<ContractBar>();
        for (var seq = 1; seq <= bars; seq++)
        {
            foreach (var c in b.Contracts)
            {
                var bar = Bar(seq, c.Token!, c.IsCall, r, v3);
                if (skip?.Invoke(seq, c.Token!) == true) continue;
                all.Add(bar);
            }
        }

        var dur = Enumerable.Range(1, bars).ToDictionary(i => i, i => 20d + i % 7);
        return (new ContractHistory(all, dur), all);
    }

    [Fact]
    public void Resolve_ClassifiesTenContractsIntoAtmItmOtm()
    {
        var b = BasketAt();
        Assert.Empty(b.Problems);
        Assert.Equal(50d, b.Step);
        Assert.Equal(10, b.Contracts.Count);
        Assert.Equal(new[] { "CE26000", "PE26000" }, b.For(GridKind.Atm).Select(c => c.Token));
        Assert.Equal(new[] { "CE25900", "CE25950", "PE26050", "PE26100" }.OrderBy(x => x), b.For(GridKind.Itm).Select(c => c.Token).OrderBy(x => x));
        Assert.Equal(new[] { "CE26050", "CE26100", "PE25900", "PE25950" }.OrderBy(x => x), b.For(GridKind.Otm).Select(c => c.Token).OrderBy(x => x));
        Assert.Equal(10, b.For(GridKind.Total).Count);
        Assert.Equal(10, b.For(GridKind.Total).Select(c => c.Token).Distinct().Count());
    }

    [Fact]
    public void Resolve_UnlistedStrike_IsReportedAndNeverSubstituted()
    {
        var listed = Listed().Where(l => l.Strike != 26_100).ToList();
        var b = BasketResolver.Resolve(Expiry, 26_000, listed);
        Assert.NotEmpty(b.Problems);
        Assert.Equal(2, b.Contracts.Count(c => c.Token is null));
        Assert.DoesNotContain(b.Contracts, c => c.Token is { } t && t.EndsWith("26100"));
        var missingCentre = BasketResolver.Resolve(Expiry, 26_010, Listed());
        Assert.All(missingCentre.Contracts, c => Assert.Null(c.Token));
    }

    [Fact]
    public void Compute_WindowBoundaries_AndInsufficientHistory()
    {
        var b = BasketAt();
        var (h, _) = Data(b, 7);
        var g = OptionSummaryCalculator.Compute(b, GridKind.Total, h, 7);
        Assert.Equal(new[] { "5 Bars", "10 Bars", "50 Bars", "100 Bars", "Day" }, g.Rows.Select(r => r.Window.Name));
        Assert.Equal(CoverageStatus.Full, g.Rows[0].Coverage.Status);
        Assert.Equal(3, g.Rows[0].FromBar);
        Assert.Equal("10/10 – FULL", g.Rows[0].Coverage.Text);
        Assert.Equal("— – INSUFFICIENT DATA", g.Rows[1].Coverage.Text);
        Assert.Null(g.Rows[1].Ce);
        Assert.Equal("— – INSUFFICIENT DATA", g.Rows[3].Coverage.Text);
        Assert.Equal(1, g.Rows[4].FromBar);
        Assert.Equal(7, g.Rows[4].ToBar);
        Assert.Equal(5, g.Rows.Count);
    }

    [Fact]
    public void Compute_TotalAllWindows_MatchesIndependentReference()
    {
        var b = BasketAt();
        const int end = 130;
        var (h, all) = Data(b, end);
        var g = OptionSummaryCalculator.Compute(b, GridKind.Total, h, end);
        foreach (var (row, bars) in g.Rows.Zip(new int?[] { 5, 10, 50, 100, null }))
        {
            var from = bars is { } n ? end - n + 1 : 1;
            foreach (var call in new[] { true, false })
            {
                var rows = all.Where(x => x.IsCall == call && x.BarSeq >= from && x.BarSeq <= end).ToList();
                var side = call ? row.Ce! : row.Pe!;
                Assert.Equal(5, side.Contracts);
                // additive + pooled ratios
                Assert.Equal(rows.Sum(x => x.TotalQuantity), side.Volume);
                Assert.Equal(rows.Sum(x => (double)x.StrictDelta) / rows.Sum(x => (double)x.TotalQuantity) * 100d, side.Strict.Ratio!.Value, 9);
                Assert.Equal(rows.Sum(x => (double)x.EnrichedDelta) / rows.Sum(x => (double)(x.EnrichedBuy + x.EnrichedSell + x.EnrichedUnknown)) * 100d, side.Enriched.Ratio!.Value, 9);
                Assert.Equal(rows.Sum(x => (double)x.NetOfi!.Value) / rows.Sum(x => (double)x.GrossOfi!.Value) * 100d, side.Ofi.Ratio!.Value, 9);
                // time-weighted MicroDev
                Assert.Equal(rows.Sum(x => x.MicroDevTimeWeighted!.Value * x.TobUsableSeconds!.Value) / rows.Sum(x => x.TobUsableSeconds!.Value), side.MicroDev.Raw!.Value, 9);
                // price: per-contract end-start averaged; pooled %
                var perContract = rows.GroupBy(x => x.Token).Select(t => (Chg: t.OrderBy(x => x.BarSeq).Last().PriceEnd!.Value - t.OrderBy(x => x.BarSeq).First().PriceStart!.Value, Start: t.OrderBy(x => x.BarSeq).First().PriceStart!.Value)).ToList();
                Assert.Equal(perContract.Average(p => p.Chg), side.Price.Raw!.Value, 9);
                Assert.Equal(perContract.Sum(p => p.Chg) / perContract.Sum(p => p.Start) * 100d, side.Price.Ratio!.Value, 9);
                // OI: sum of window changes over combined start
                var oi = rows.GroupBy(x => x.Token).Select(t => (Chg: (double)(t.OrderBy(x => x.BarSeq).Last().OiEnd!.Value - t.OrderBy(x => x.BarSeq).First().OiStart!.Value), Start: (double)t.OrderBy(x => x.BarSeq).First().OiStart!.Value)).ToList();
                Assert.Equal(oi.Sum(p => p.Chg), side.Oi.Raw!.Value, 9);
                // activity: sum over contracts of qty / elapsed seconds
                var act = rows.GroupBy(x => x.Token).Sum(t => t.Sum(x => (double)x.TotalQuantity) / t.Sum(x => h.BarDurationSeconds[x.BarSeq]));
                Assert.Equal(act, side.Activity!.Value, 9);
                // IV level and change
                var ivEnd = rows.GroupBy(x => x.Token).Average(t => t.OrderBy(x => x.BarSeq).Last().IvEnd!.Value);
                Assert.Equal(ivEnd, side.IvLevel!.Value, 9);
            }

            // directions come from combined numerators / denominators
            var d = row.Direction!;
            var ce = row.Ce!; var pe = row.Pe!;
            Assert.Equal((ce.Oi.Raw!.Value - pe.Oi.Raw!.Value) * -1d, d.Oi.Raw!.Value, 9);
            Assert.Equal(-(ce.Oi.Num!.Value - pe.Oi.Num!.Value) / (ce.Oi.Den!.Value + pe.Oi.Den!.Value) * 100d, d.Oi.Ratio!.Value, 9);
            Assert.Equal((ce.Strict.Num!.Value - pe.Strict.Num!.Value) / (ce.Strict.Den!.Value + pe.Strict.Den!.Value) * 100d, d.Strict.Ratio!.Value, 9);
            Assert.NotEqual(ce.Strict.Ratio!.Value - pe.Strict.Ratio!.Value, d.Strict.Ratio!.Value, 6);   // not a subtraction of displayed percentages
        }
    }

    [Fact]
    public void Compute_AtmItmOtm_UseOnlyTheirOwnContracts()
    {
        var b = BasketAt();
        var (h, all) = Data(b, 12);
        foreach (var (kind, ceN, peN, tokens) in new[]
        {
            (GridKind.Atm, 1, 1, new[] { "CE26000", "PE26000" }),
            (GridKind.Itm, 2, 2, new[] { "CE25900", "CE25950", "PE26050", "PE26100" }),
            (GridKind.Otm, 2, 2, new[] { "CE26050", "CE26100", "PE25900", "PE25950" }),
        })
        {
            var row = OptionSummaryCalculator.Compute(b, kind, h, 12).Rows[1];
            Assert.Equal(ceN, row.Ce!.Contracts);
            Assert.Equal(peN, row.Pe!.Contracts);
            var expectedQty = all.Where(x => tokens.Contains(x.Token) && x.BarSeq >= 3).Sum(x => x.TotalQuantity);
            Assert.Equal(expectedQty, row.Ce.Volume + row.Pe.Volume);
        }
    }

    [Fact]
    public void Compute_MissingBarOfOneContract_ExcludesItAndReportsPartial()
    {
        var b = BasketAt();
        var (h, _) = Data(b, 30, skip: (seq, tok) => tok == "CE25900" && seq == 28);
        var total = OptionSummaryCalculator.Compute(b, GridKind.Total, h, 30);
        Assert.Equal("9/10 – PARTIAL", total.Rows[0].Coverage.Text);
        Assert.Equal(new[] { "CE25900" }, total.Rows[0].Coverage.MissingTokens);
        Assert.Equal(4, total.Rows[0].Ce!.Contracts);
        var atm = OptionSummaryCalculator.Compute(b, GridKind.Atm, h, 30);
        Assert.All(atm.Rows.Take(2), r => Assert.Equal(CoverageStatus.Full, r.Coverage.Status));   // other grids unaffected
        var itm = OptionSummaryCalculator.Compute(b, GridKind.Itm, h, 30);
        Assert.Equal("3/4 – PARTIAL", itm.Rows[0].Coverage.Text);
        // a contract with no rows at all in a one-contract grid is INSUFFICIENT, not zero
        var (h2, _) = Data(b, 30, skip: (_, tok) => tok == "CE26000" || tok == "PE26000");
        var atm2 = OptionSummaryCalculator.Compute(b, GridKind.Atm, h2, 30);
        Assert.All(atm2.Rows, r => { Assert.Equal("— – INSUFFICIENT DATA", r.Coverage.Text); Assert.Null(r.Ce); });
    }

    [Fact]
    public void Compute_UnresolvedSlot_NeverSubstituted()
    {
        var b = BasketResolver.Resolve(Expiry, 26_000, Listed().Where(l => l.Strike != 26_100).ToList());
        var (h, _) = Data(BasketAt(), 12);
        var g = OptionSummaryCalculator.Compute(b, GridKind.Total, h, 12);
        Assert.Equal("8/10 – PARTIAL", g.Rows[0].Coverage.Text);
        Assert.Contains(g.Rows[0].Coverage.MissingTokens, m => m.StartsWith("unresolved"));
    }

    [Fact]
    public void Compute_NoFutureLeakage_BarsAfterTheTargetAreIgnored()
    {
        var b = BasketAt();
        var (h, all) = Data(b, 40);
        var at30 = OptionSummaryCalculator.ComputeAll(b, h, 30);
        var shifted = all.Select(x => x.BarSeq > 30 ? x with { TotalQuantity = 9_999_999, PriceEnd = 1, NetOfi = 77, OiEnd = 1 } : x).ToList();
        var h2 = new ContractHistory(shifted, h.BarDurationSeconds);
        var at30b = OptionSummaryCalculator.ComputeAll(b, h2, 30);
        Assert.Equivalent(at30, at30b, strict: true);
    }

    [Fact]
    public void Compute_V1Rows_LeaveBookAndGreekMetricsUnavailableNotZero()
    {
        var b = BasketAt();
        var (h, _) = Data(b, 12, v3: false);
        var row = OptionSummaryCalculator.Compute(b, GridKind.Total, h, 12).Rows[0];
        Assert.Null(row.Ce!.Ofi.Raw);
        Assert.Null(row.Ce.MicroDev.Raw);
        Assert.Null(row.Ce.Delta.Raw);
        Assert.Null(row.Ce.Vega.Raw);
        Assert.Null(row.Direction!.Ofi.Raw);
        Assert.NotNull(row.Ce.Strict.Raw);
    }

    [Fact]
    public void Compute_ZeroDenominators_GiveNoRatio()
    {
        var b = BasketAt();
        var (_, all) = Data(b, 6);
        var zeroed = all.Select(x => x with { TotalQuantity = 0, StrictDelta = 0, PriceStart = 0, IvStart = 0, OiStart = 0, GrossOfi = 0, NetOfi = 0 }).ToList();
        var row = OptionSummaryCalculator.Compute(b, GridKind.Atm, new ContractHistory(zeroed, Enumerable.Range(1, 6).ToDictionary(i => i, _ => 30d)), 6).Rows[0];
        Assert.Null(row.Ce!.Strict.Ratio);
        Assert.Null(row.Ce.Price.Raw);        // non-positive start price: unavailable, not zero
        Assert.Null(row.Ce.Iv.Raw);           // zero start IV unavailable
        Assert.Null(row.Ce.Oi.Raw);
        Assert.Null(row.Ce.Ofi.Ratio);
        Assert.Equal(0d, row.Ce.Activity);    // genuinely zero traded quantity over valid elapsed time
    }

    [Fact]
    public void Compute_Roll_AveragesValidObservations_AndNeverBridgesAGap()
    {
        var b = BasketAt();
        const int end = 25;
        var (h, all) = Data(b, end);
        var row = OptionSummaryCalculator.Compute(b, GridKind.Atm, h, end).Rows[0];   // 5-bar window: observations at bars 21..25, each needs bars b-9..b
        var ceRows = all.Where(x => x.Token == "CE26000").ToDictionary(x => x.BarSeq);
        var obs = Enumerable.Range(21, 5).Select(k => ceRows[k].PriceEnd!.Value - ceRows[k - 9].PriceStart!.Value).ToList();
        Assert.Equal(obs.Average(), row.Ce!.Roll.Raw!.Value, 9);
        // a hole at bar 14 lies outside the 5-bar window (21..25) so flows are unchanged, but it sits inside the ten bars of observations 14..23: only 24 and 25 stay valid (no bridging)
        var (hGap, _) = Data(b, end, skip: (seq, tok) => tok == "CE26000" && seq == 14);
        var gap = OptionSummaryCalculator.Compute(b, GridKind.Atm, hGap, end).Rows[0];
        Assert.Equal(row.Ce.Strict.Raw, gap.Ce!.Strict.Raw);
        var obsGap = new[] { 24, 25 }.Select(k => ceRows[k].PriceEnd!.Value - ceRows[k - 9].PriceStart!.Value).ToList();
        Assert.Equal(obsGap.Average(), gap.Ce.Roll.Raw!.Value, 9);
    }

    [Fact]
    public void DisplayScale_Times100_IsDisplayOnly()
    {
        Assert.Equal(42d, DisplayScale.Times100(0.42)!.Value, 12);
        Assert.Equal(1.8d, DisplayScale.Times100(0.018)!.Value, 12);
        Assert.Null(DisplayScale.Times100(null));
        var b = BasketAt();
        var (h, all) = Data(b, 12);
        var row = OptionSummaryCalculator.Compute(b, GridKind.Atm, h, 12).Rows[0];
        var end = all.Where(x => x.Token == "CE26000" && x.BarSeq == 12).Single().DeltaEnd!.Value;
        Assert.Equal(end, row.Ce!.DeltaLevel!.Value, 12);          // the calculator never scales
        Assert.Equal(end * 100d, DisplayScale.Times100(row.Ce.DeltaLevel)!.Value, 12);
    }

    [Fact]
    public void FuturesSummary_WindowsAndCoverage()
    {
        var r = new Random(3);
        var bars = Enumerable.Range(1, 60).ToDictionary(i => i, i => new FuturesBar(i, 30 + i % 5, 26_000 + i, 26_000 + i + r.Next(-5, 6), 13_650, r.Next(-500, 500),
            1000, 1100, 100, r.Next(-300, 300), 1_000_000 + i * 10, 1_000_000 + (i + 1) * 10, r.NextDouble() - 0.5, r.NextDouble() - 0.5, 20, 22, r.Next(-100, 100), r.Next(150, 400)));
        var rows = FuturesSummaryCalculator.Compute(bars, 60);
        Assert.Equal(5, rows.Count);
        Assert.Equal(CoverageStatus.Full, rows[2].Coverage.Status);
        Assert.Equal(CoverageStatus.Insufficient, rows[3].Coverage.Status);   // 100 bars needed, 60 completed
        Assert.Equal(60, rows[4].Coverage.Included);
        Assert.Equal(13_650L * 5, rows[0].Volume);
        Assert.Equal(bars[60].Close - bars[56].Open, rows[0].Price.Raw!.Value, 9);
        Assert.Equal(bars[60].OiClose!.Value - bars[56].OiOpen!.Value, rows[0].Oi.Raw!.Value);
        var strict = Enumerable.Range(56, 5).Sum(i => (double)bars[i].StrictDelta) / (13_650d * 5) * 100d;
        Assert.Equal(strict, rows[0].Strict.Ratio!.Value, 9);
        var withHole = bars.Where(kv => kv.Key != 58).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("4/5 – PARTIAL", FuturesSummaryCalculator.Compute(withHole, 60)[0].Coverage.Text);
    }
}
