using NiftySignal.MarketSummary;

namespace NiftySignal.Tests.MarketSummary;

/// <summary>
/// Metric semantics checked against the producer definitions (AdaptiveStrikeProjection / FuturesMicrostructureTracker): a null COLUMN is "no evidence in that bar", a missing ROW is an integrity gap;
/// level metrics telescope because End[n] == Start[n+1]; OFI is net over gross-of-absolute-contributions; MicroDev is weighted by usable seconds.
/// </summary>
public class MarketSummarySemanticsTests
{
    static readonly DateOnly Expiry = new(2026, 11, 3);

    static Basket Basket26000() => BasketResolver.Resolve(Expiry, 26_000, Enumerable.Range(0, 9).Select(i => 25_800d + i * 50).Select(s => new ListedStrike(s, $"CE{s:0}", $"PE{s:0}")).ToList());

    /// <summary>A chained series: PriceStart[n+1] == PriceEnd[n] (and the same for IV, Delta, Vega, OI) exactly as the producer snapshots guarantee.</summary>
    static List<ContractBar> Chain(string token, bool call, int bars, double p0 = 100, Func<int, long>? qty = null)
    {
        var rows = new List<ContractBar>();
        double p = p0, iv = 12, d = call ? 0.45 : -0.45, v = 6;
        long oi = 10_000;
        for (var n = 1; n <= bars; n++)
        {
            var pe = p + (n % 3) - 1 + 0.5; var ive = iv + 0.1 * (n % 4); var de = d + 0.01 * (n % 5); var ve = v + 0.05 * (n % 3); var oie = oi + 7 * n % 13 + 1;
            rows.Add(new ContractBar(n, token, call, p, pe, qty?.Invoke(n) ?? 100, 10, 40, 40, 20, 5, oi, oie, iv, ive, d, de, v, ve, 0.1, 0.1, 5, 5, 10));
            p = pe; iv = ive; d = de; v = ve; oi = oie;
        }

        return rows;
    }

    static ContractHistory History(IEnumerable<ContractBar> rows, int bars) => new(rows, Enumerable.Range(1, bars).ToDictionary(i => i, _ => 30d));

    [Fact]
    public void LevelMetrics_AreIdenticalWithAndWithoutAnInteriorHole_BecauseEndEqualsNextStart()
    {
        var b = Basket26000();
        var all = b.Contracts.SelectMany(c => Chain(c.Token!, c.IsCall, 60)).ToList();
        var full = OptionSummaryCalculator.Compute(b, GridKind.Total, History(all, 60), 60);
        var holed = OptionSummaryCalculator.Compute(b, GridKind.Total, History(all.Where(x => !(x.Token == "CE26000" && x.BarSeq is 40 or 41) && !(x.Token == "PE25950" && x.BarSeq == 55)), 60), 60);
        foreach (var i in new[] { 0, 1, 2 })       // 5, 10, 50 bars contain holes; level metrics must be identical
        {
            var f = full.Rows[i]; var h = holed.Rows[i];
            Assert.Equal(f.Ce!.Price, h.Ce!.Price); Assert.Equal(f.Ce.Iv, h.Ce.Iv); Assert.Equal(f.Ce.Delta, h.Ce.Delta); Assert.Equal(f.Ce.Vega, h.Ce.Vega); Assert.Equal(f.Ce.Oi, h.Ce.Oi);
            Assert.Equal(f.Pe!.Price, h.Pe!.Price); Assert.Equal(f.Pe.Iv, h.Pe.Iv); Assert.Equal(f.Pe.Delta, h.Pe.Delta); Assert.Equal(f.Pe.Vega, h.Pe.Vega); Assert.Equal(f.Pe.Oi, h.Pe.Oi);
        }

        // and the endpoint difference equals the SUM of the per-bar stored changes when the chain is continuous (the producer identity the shortcut relies on)
        var ce = all.Where(x => x.Token == "CE26000" && x.BarSeq >= 11).ToList();
        Assert.Equal(ce.Sum(x => x.PriceEnd!.Value - x.PriceStart!.Value), ce[^1].PriceEnd!.Value - ce[0].PriceStart!.Value, 9);
        Assert.Equal(ce.Sum(x => x.DeltaEnd!.Value - x.DeltaStart!.Value), ce[^1].DeltaEnd!.Value - ce[0].DeltaStart!.Value, 9);
        // flow metrics DO need the hole: the holed contract is excluded from them, never zero-filled
        Assert.Equal(4, holed.Rows[2].Ce!.Contracts);                                 // 50-bar window 11..60 contains CE26000's holes (40, 41)
        Assert.Equal(5, holed.Rows[2].Ce!.LevelContracts);
        Assert.Equal(5, holed.Rows[1].Ce!.Contracts);                                 // 10-bar window 51..60 does not
        Assert.Equal(full.Rows[2].Ce!.Volume - 50 * 100, holed.Rows[2].Ce!.Volume);     // exactly the one excluded contract's 50 bars x 100
    }

    [Fact]
    public void NullColumn_IsNotAMissingRow()
    {
        var b = Basket26000();
        var rows = b.Contracts.SelectMany(c => Chain(c.Token!, c.IsCall, 12)).Select(x => x with { NetOfi = null, GrossOfi = null, TobTimeWeighted = null, MicroDevTimeWeighted = null, TobUsableSeconds = null, DeltaStart = null, DeltaEnd = null, VegaStart = null, VegaEnd = null }).ToList();
        var w = OptionSummaryCalculator.Compute(b, GridKind.Total, History(rows, 12), 12).Rows[0];
        Assert.Equal(CoverageStatus.Full, w.Coverage.Status);            // every ROW exists
        Assert.Equal("10/10 – FULL", w.Coverage.Text);
        Assert.Equal(0, w.Coverage.FamilyCount("Ofi"));                  // but no COLUMN evidence
        Assert.Equal(0, w.Coverage.FamilyCount("MicroDev"));
        Assert.Equal(0, w.Coverage.FamilyCount("Delta"));
        Assert.Equal(10, w.Coverage.FamilyCount("Strict"));
        Assert.Equal(10, w.Coverage.FamilyCount("Price"));
        Assert.Null(w.Ce!.Ofi.Raw); Assert.Null(w.Ce.MicroDev.Raw); Assert.Null(w.Ce.Delta.Raw);
        Assert.NotNull(w.Ce.Strict.Raw);
    }

    [Fact]
    public void Ofi_UsesNetOverGrossOfAbsoluteContributions_NotAbsNet()
    {
        var b = Basket26000();
        // two bars: net +10 / -10, gross 30 / 30 -> window net 0, gross 60 -> ratio 0 (|net| would give 20/60 = 33.3)
        var rows = b.Contracts.SelectMany(c => Chain(c.Token!, c.IsCall, 2).Select(x => x with { NetOfi = x.BarSeq == 1 ? 10 : -10, GrossOfi = 30 })).ToList();
        var w = OptionSummaryCalculator.Compute(b, GridKind.Atm, History(rows, 2), 2).Rows[0];     // 5-bar needs 5 bars -> use Day
        var day = OptionSummaryCalculator.Compute(b, GridKind.Atm, History(rows, 2), 2).Rows[4];
        Assert.Equal(CoverageStatus.Insufficient, w.Coverage.Status);
        Assert.Equal(0d, day.Ce!.Ofi.Num);
        Assert.Equal(60d, day.Ce.Ofi.Den);
        Assert.Equal(0d, day.Ce.Ofi.Ratio);
        // a bar with no valid transitions (null net+gross) contributes nothing; it does not zero the window
        var partial = rows.Select(x => x.BarSeq == 2 ? x with { NetOfi = null, GrossOfi = null } : x).ToList();
        var d2 = OptionSummaryCalculator.Compute(b, GridKind.Atm, History(partial, 2), 2).Rows[4];
        Assert.Equal(100d / 3d, d2.Ce!.Ofi.Ratio!.Value, 9);                                        // 10 / 30
    }

    [Fact]
    public void MicroDev_IsWeightedByUsableSeconds_NotByBarCount()
    {
        var b = Basket26000();
        var rows = b.Contracts.SelectMany(c => Chain(c.Token!, c.IsCall, 2).Select(x => x with
        {
            MicroDevTimeWeighted = x.BarSeq == 1 ? 1.0 : 3.0, TobTimeWeighted = x.BarSeq == 1 ? 0.2 : 0.6, TobUsableSeconds = x.BarSeq == 1 ? 10 : 30,
        })).ToList();
        var day = OptionSummaryCalculator.Compute(b, GridKind.Atm, History(rows, 2), 2).Rows[4];
        Assert.Equal((1.0 * 10 + 3.0 * 30) / 40d, day.Ce!.MicroDev.Raw!.Value, 12);                 // 2.5, not the simple mean 2.0
        Assert.Equal((0.2 * 10 + 0.6 * 30) / 40d * 100d, day.Ce.MicroDev.Ratio!.Value, 9);          // TOB x 100, time weighted
        // a bar with zero usable seconds carries no weight
        var zero = rows.Select(x => x.BarSeq == 2 ? x with { TobUsableSeconds = 0 } : x).ToList();
        Assert.Equal(1.0, OptionSummaryCalculator.Compute(b, GridKind.Atm, History(zero, 2), 2).Rows[4].Ce!.MicroDev.Raw!.Value, 12);
    }

    [Fact]
    public void AllFiveWindows_UseTheSameBasketTokens_ForeignContractsAreIgnored()
    {
        var b = Basket26000();
        var mine = b.Contracts.SelectMany(c => Chain(c.Token!, c.IsCall, 120)).ToList();
        var foreign = Chain("CE26150", true, 120, qty: _ => 9_999_999).Concat(Chain("PE25750", false, 120, qty: _ => 9_999_999)).ToList();   // outside K±2Δ
        var plain = OptionSummaryCalculator.ComputeAll(b, History(mine, 120), 120);
        var withForeign = OptionSummaryCalculator.ComputeAll(b, History(mine.Concat(foreign), 120), 120);
        Assert.Equivalent(plain, withForeign, strict: true);
        // a missing basket token is reported as missing; the adjacent listed strike is never used instead
        var noCe26050 = mine.Where(x => x.Token != "CE26050").Concat(foreign);
        var g = OptionSummaryCalculator.Compute(b, GridKind.Total, History(noCe26050, 120), 120);
        Assert.All(g.Rows, r => { Assert.Equal(9, r.Coverage.Included); Assert.Contains("CE26050", r.Coverage.MissingTokens); Assert.Equal(4, r.Ce!.Contracts); });
    }
}
