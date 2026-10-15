using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Tests.Dashboard;

/// <summary>The "raw (ratio)" cells, the direction formulas and the AVG (visible rows) aggregation of the Futures / Options grids.</summary>
public class AdaptiveRatioGridTests
{
    static readonly DateTimeOffset T = new(2026, 10, 14, 4, 0, 0, TimeSpan.Zero);

    static AdaptiveFutureBarRow Bar(int seq, long volume, long strictDelta, long eb, long es, long eu, long enrichedDelta) => new()
    {
        SessionId = 1, BarSeq = seq, StartAvailableAtUtc = T, EndAvailableAtUtc = T.AddMinutes(1), Volume = volume, StrictDelta = strictDelta,
        EnrichedBuyVolume = eb, EnrichedSellVolume = es, EnrichedUnknownVolume = eu, EnrichedDelta = enrichedDelta,
    };

    static AdaptiveFuturesSupplementalRow Sup(double? micro, double? tob, double usable, long? ofi, long? gross) => new()
    {
        SessionId = 1, BarSeq = 1, MetricsVersion = FuturesMicrostructureBar.MetricsVersion, MicroDevTimeWeighted = micro, TobTimeWeighted = tob,
        TobUsableSeconds = usable, MicroDevUsableSeconds = usable, Ofi = ofi, GrossOfi = gross,
    };

    static AdaptiveOptionStrikeBarRow Side(OptionType side, Action<AdaptiveOptionStrikeBarRow>? set = null)
    {
        var r = new AdaptiveOptionStrikeBarRow
        {
            SessionId = 1, BarSeq = 1, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, EndAvailableAtUtc = T, Expiry = new DateOnly(2026, 10, 19), Strike = 25000, Side = side,
            Token = side == OptionType.Call ? "CE" : "PE", TradingSymbol = "X",
        };
        set?.Invoke(r);
        return r;
    }

    // ---- formatting ----

    [Fact]
    public void Cell_ShowsRawAndRatio_AndDashForUnavailable_NeverNanOrZero()
    {
        Assert.Equal("6500 (72.50)", RatioFormat.Cell(RatioPart.Of(6500, 6500, 8965.5172), 0));
        Assert.Equal("-4200 (-51.30)", RatioFormat.Cell(RatioPart.Of(-4200, -4200, 8187.1345)));
        Assert.Equal("0.42 (64.30)", RatioFormat.Cell(new RatioPart(0.42, 0.643, 1d), 2));
        Assert.Equal("—", RatioFormat.Cell(RatioPart.None));
        Assert.Equal("120 (—)", RatioFormat.Cell(RatioPart.Of(120, 120, 0)));                    // zero denominator: ratio unavailable
        Assert.Equal("120 (—)", RatioFormat.Cell(RatioPart.Of(120, 120, null)));
        Assert.Equal("—", RatioFormat.Cell(RatioPart.Of(double.NaN, 1, 1)));
        Assert.DoesNotContain("NaN", RatioFormat.Cell(RatioPart.Of(1, double.NaN, 1)));
        Assert.DoesNotContain("Infinity", RatioFormat.Cell(RatioPart.Of(1, double.PositiveInfinity, 1)));
    }

    // ---- futures ----

    [Fact]
    public void Futures_StrictEnrichedMicroDevAndOfi_RawAndRatio()
    {
        var f = FuturesRatios.Build(Bar(1, 10000, 6500, 4000, 3000, 3000, -2500), Sup(0.42, 0.643, 40, 300, 1200));
        Assert.Equal(65.0, f.Strict.Ratio!.Value, 9);                                  // StrictDelta / Volume
        Assert.Equal(-25.0, f.Enriched.Ratio!.Value, 9);                               // EnrichedDelta / (buy+sell+unknown)
        Assert.Equal(64.3, f.MicroDev.Ratio!.Value, 9);                                // time-weighted TOB * 100
        Assert.Equal(0.42, f.MicroDev.Raw);
        Assert.Equal(25.0, f.Ofi.Ratio!.Value, 9);                                     // NetOFI / GrossOFI
        Assert.Equal(300, f.Ofi.Raw);
    }

    [Fact]
    public void Futures_Ratios_AreUnavailableNotZero_WhenTheDenominatorOrTheSidecarIsMissing()
    {
        var f = FuturesRatios.Build(Bar(1, 0, 0, 0, 0, 0, 0), null);
        Assert.Null(f.Strict.Ratio);
        Assert.Null(f.Enriched.Ratio);
        Assert.Null(f.MicroDev.Raw);
        Assert.Null(f.Ofi.Raw);
        var noOfi = FuturesRatios.Build(Bar(1, 100, 10, 50, 50, 0, 5), Sup(null, null, 0, null, null));
        Assert.Null(noOfi.Ofi.Raw);
        Assert.Null(noOfi.MicroDev.Raw);
        Assert.Null(noOfi.MicroDev.Ratio);
        var v2Fallback = FuturesRatios.Build(Bar(1, 100, 10, 50, 50, 0, 5), Sup(0.1, 0.2, 30, 55, null));    // futures-micro-v2 row: no GrossOfi
        Assert.Equal(55, v2Fallback.Ofi.Raw);
        Assert.Null(v2Fallback.Ofi.Ratio);
        var zeroGross = FuturesRatios.Build(Bar(1, 100, 10, 50, 50, 0, 5), Sup(0.1, 0.2, 30, 0, 0));
        Assert.Null(zeroGross.Ofi.Ratio);
    }

    [Fact]
    public void FuturesAvg_UsesAggregateDenominators_AndSecondsWeighting_NotAveragedPercentages()
    {
        var rows = new[]
        {
            FuturesRatios.Build(Bar(1, 1000, 500, 500, 250, 250, 100), Sup(0.10, 0.20, 10, 100, 400)),     // strict 50%, enriched 10%
            FuturesRatios.Build(Bar(2, 9000, -900, 3000, 3000, 3000, -900), Sup(0.50, 0.60, 30, -200, 200)), // strict -10%, enriched -10%
            FuturesRatios.Build(Bar(3, 0, 0, 0, 0, 0, 0), null),                                            // missing: excluded, never zero
        };
        Assert.Equal(-400d / 10000d * 100d, RatioAverage.Aggregate(rows.Select(r => r.Strict)).Ratio!.Value, 9);          // SUM(delta)/SUM(volume), not (50 + -10)/2
        Assert.Equal((100d - 900d) / (1000d + 9000d) * 100d, RatioAverage.Aggregate(rows.Select(r => r.Enriched)).Ratio!.Value, 9);
        var micro = RatioAverage.Aggregate(rows.Select(r => r.MicroDev));
        Assert.Equal((0.10 * 10 + 0.50 * 30) / 40d, micro.Raw!.Value, 9);                                                  // seconds-weighted
        Assert.Equal((0.20 * 10 + 0.60 * 30) / 40d * 100d, micro.Ratio!.Value, 9);                                         // seconds-weighted TOB * 100
        var ofi = RatioAverage.Aggregate(rows.Select(r => r.Ofi));
        Assert.Equal((100d - 200d) / 2d, ofi.Raw!.Value, 9);                                                               // arithmetic mean of NetOFI
        Assert.Equal((100d - 200d) / (400d + 200d) * 100d, ofi.Ratio!.Value, 9);                                           // SUM(net)/SUM(gross)
        Assert.Null(RatioAverage.Aggregate(Array.Empty<RatioPart>()).Raw);
        Assert.Null(RatioAverage.Mean(new double?[] { null, null }));
        Assert.Equal(2d, RatioAverage.Mean(new double?[] { 1, null, 3 }));
    }

    // ---- options: price / strict / enriched / OI ----

    [Fact]
    public void Options_PriceStrictEnriched_SideRatiosAndCombinedDenominatorDirection()
    {
        var ce = Side(OptionType.Call, r => { r.PriceChange = 5; r.PriceStart = 100; r.StrictDelta = 300; r.TotalQuantity = 1000; r.EnrichedBuy = 500; r.EnrichedSell = 200; r.EnrichedUnknown = 300; r.EnrichedDelta = 300; });
        var pe = Side(OptionType.Put, r => { r.PriceChange = -4; r.PriceStart = 80; r.StrictDelta = -200; r.TotalQuantity = 500; r.EnrichedBuy = 100; r.EnrichedSell = 300; r.EnrichedUnknown = 100; r.EnrichedDelta = -200; });
        var d = OptionDirectionRatios.Core(ce, pe);

        Assert.Equal(5d, d.Price.Ce.Ratio!.Value, 9);
        Assert.Equal(-5d, d.Price.Pe.Ratio!.Value, 9);
        Assert.Equal(9d, d.Price.Direction.Raw);                                                       // CE - PE
        Assert.Equal(9d / 180d * 100d, d.Price.Direction.Ratio!.Value, 9);                              // / (CE start + PE start)

        Assert.Equal(30d, d.Strict.Ce.Ratio!.Value, 9);
        Assert.Equal(-40d, d.Strict.Pe.Ratio!.Value, 9);
        Assert.Equal(500d, d.Strict.Direction.Raw);
        Assert.Equal(500d / 1500d * 100d, d.Strict.Direction.Ratio!.Value, 9);                          // combined denominator, NOT 30 - (-40)
        Assert.NotEqual(70d, d.Strict.Direction.Ratio!.Value, 6);

        Assert.Equal(30d, d.Enriched.Ce.Ratio!.Value, 9);                                              // 300 / 1000
        Assert.Equal(-40d, d.Enriched.Pe.Ratio!.Value, 9);                                             // -200 / 500
        Assert.Equal(500d / 1500d * 100d, d.Enriched.Direction.Ratio!.Value, 9);
    }

    [Fact]
    public void Options_OiDirectionIsPutMinusCall_WithCombinedStartOiDenominator()
    {
        var ce = Side(OptionType.Call, r => { r.OiDelta = 1000; r.OiStart = 10000; });
        var pe = Side(OptionType.Put, r => { r.OiDelta = 3000; r.OiStart = 20000; });
        var oi = OptionDirectionRatios.Core(ce, pe).Oi;
        Assert.Equal(10d, oi.Ce.Ratio!.Value, 9);
        Assert.Equal(15d, oi.Pe.Ratio!.Value, 9);
        Assert.Equal(2000d, oi.Direction.Raw);                                                         // PE - CE (the only reversed direction)
        Assert.Equal(2000d / 30000d * 100d, oi.Direction.Ratio!.Value, 9);

        var noStart = OptionDirectionRatios.Core(Side(OptionType.Call, r => { r.OiDelta = 5; r.OiStart = 0; }), pe).Oi;
        Assert.Null(noStart.Ce.Ratio);                                                                 // non-positive start OI is not a valid denominator
        Assert.Null(noStart.Direction.Ratio);
        Assert.Equal(3000d - 5d, noStart.Direction.Raw);
    }

    [Fact]
    public void Options_MissingSide_NeverBecomesZero_OrAnotherStrike()
    {
        var pe = Side(OptionType.Put, r => { r.PriceChange = 2; r.PriceStart = 50; r.StrictDelta = 10; r.TotalQuantity = 100; });
        var d = OptionDirectionRatios.Core(null, pe);
        Assert.Null(d.Price.Ce.Raw);
        Assert.Null(d.Price.Direction.Raw);
        Assert.Null(d.Strict.Direction.Raw);
        Assert.Equal(2d, d.Price.Pe.Raw);
        Assert.Equal("—", RatioFormat.Cell(d.Strict.Direction));
        Assert.Equal("—", RatioFormat.Cell(d.Oi.Direction));
    }

    // ---- options: MicroDev / OFI ----

    [Fact]
    public void Options_MicroDevRatiosAreTobBased_AndDirectionIsHalfTheTobDifference()
    {
        var ce = Side(OptionType.Call, r => { r.MicroDevTimeWeighted = 0.30; r.TobTimeWeighted = 0.60; r.TobUsableSeconds = 40; });
        var pe = Side(OptionType.Put, r => { r.MicroDevTimeWeighted = 0.10; r.TobTimeWeighted = -0.20; r.TobUsableSeconds = 20; });
        var m = OptionDirectionRatios.Core(ce, pe).MicroDev;
        Assert.Equal(60d, m.Ce.Ratio!.Value, 9);
        Assert.Equal(-20d, m.Pe.Ratio!.Value, 9);
        Assert.Equal(0.30 - 0.10, m.Direction.Raw!.Value, 9);                                           // CE - PE
        Assert.Equal((0.60 - -0.20) / 2d * 100d, m.Direction.Ratio!.Value, 9);                          // bounded in [-100, +100]

        var noBook = OptionDirectionRatios.Core(Side(OptionType.Call), pe).MicroDev;                    // a v1 row / a book with no usable quantity
        Assert.Null(noBook.Ce.Raw);
        Assert.Null(noBook.Direction.Raw);
    }

    [Fact]
    public void Options_OfiRatiosUseGrossOfi_AndDirectionUsesCombinedGross()
    {
        var ce = Side(OptionType.Call, r => { r.NetOfi = 300; r.GrossOfi = 1000; });
        var pe = Side(OptionType.Put, r => { r.NetOfi = -100; r.GrossOfi = 500; });
        var o = OptionDirectionRatios.Core(ce, pe).Ofi;
        Assert.Equal(30d, o.Ce.Ratio!.Value, 9);
        Assert.Equal(-20d, o.Pe.Ratio!.Value, 9);
        Assert.Equal(400d, o.Direction.Raw);
        Assert.Equal(400d / 1500d * 100d, o.Direction.Ratio!.Value, 9);

        var zeroGross = OptionDirectionRatios.Core(Side(OptionType.Call, r => { r.NetOfi = 0; r.GrossOfi = 0; }), pe).Ofi;
        Assert.Equal(0d, zeroGross.Ce.Raw);
        Assert.Null(zeroGross.Ce.Ratio);                                                                // zero gross: unavailable, not zero
        Assert.Equal(100d / 500d * 100d, zeroGross.Direction.Ratio!.Value, 9);                        // the combined gross (0 + 500) is still positive
        var bothZero = OptionDirectionRatios.Core(Side(OptionType.Call, r => { r.NetOfi = 0; r.GrossOfi = 0; }), Side(OptionType.Put, r => { r.NetOfi = 0; r.GrossOfi = 0; })).Ofi;
        Assert.Null(bothZero.Direction.Ratio);                                                        // combined gross 0: unavailable
        Assert.Null(OptionDirectionRatios.Core(Side(OptionType.Call), pe).Ofi.Ce.Raw);                  // no transitions: unavailable, not zero
    }

    // ---- options context ----

    [Fact]
    public void Context_IvUsesStartIv_DirectionUsesAverageStartIv_ActivityIsAShare()
    {
        var ce = Side(OptionType.Call, r => { r.IvChange = 1.5; r.IvStart = 15; r.ActivityPerSecond = 30; });
        var pe = Side(OptionType.Put, r => { r.IvChange = -0.5; r.IvStart = 25; r.ActivityPerSecond = 10; });
        var c = OptionDirectionRatios.Context(ce, pe);
        Assert.Equal(10d, c.Iv.Ce.Ratio!.Value, 9);
        Assert.Equal(-2d, c.Iv.Pe.Ratio!.Value, 9);
        Assert.Equal(2d, c.Iv.Direction.Raw);
        Assert.Equal(2d / ((15d + 25d) / 2d) * 100d, c.Iv.Direction.Ratio!.Value, 9);

        Assert.Equal(75d, c.Activity.Ce.Ratio!.Value, 9);
        Assert.Equal(25d, c.Activity.Pe.Ratio!.Value, 9);
        Assert.Equal(20d, c.Activity.Direction.Raw);
        Assert.Equal(50d, c.Activity.Direction.Ratio!.Value, 9);

        var none = OptionDirectionRatios.Context(Side(OptionType.Call, r => { r.ActivityPerSecond = 0; r.IvChange = 1; r.IvStart = 0; }), Side(OptionType.Put, r => { r.ActivityPerSecond = 0; r.IvChange = 1; r.IvStart = 20; }));
        Assert.Null(none.Activity.Ce.Ratio);                                                           // total activity <= 0: every activity ratio unavailable
        Assert.Null(none.Activity.Direction.Ratio);
        Assert.Null(none.Iv.Ce.Ratio);                                                                 // non-positive start IV
        Assert.Null(none.Iv.Direction.Ratio);
    }

    // ---- AVG rows ----

    [Fact]
    public void OptionAvg_UsesAggregateNumeratorsAndDenominators_AndExcludesMissing()
    {
        var rows = new[]
        {
            OptionDirectionRatios.Core(
                Side(OptionType.Call, r => { r.StrictDelta = 100; r.TotalQuantity = 200; r.OiDelta = 10; r.OiStart = 100; r.NetOfi = 50; r.GrossOfi = 100; }),
                Side(OptionType.Put, r => { r.StrictDelta = 0; r.TotalQuantity = 100; r.OiDelta = 40; r.OiStart = 200; r.NetOfi = -10; r.GrossOfi = 50; })),
            OptionDirectionRatios.Core(
                Side(OptionType.Call, r => { r.StrictDelta = -50; r.TotalQuantity = 800; r.OiDelta = -10; r.OiStart = 300; r.NetOfi = 20; r.GrossOfi = 100; }),
                Side(OptionType.Put, r => { r.StrictDelta = 30; r.TotalQuantity = 300; r.OiDelta = 0; r.OiStart = 100; })),     // PE OFI missing
            OptionDirectionRatios.Core(null, null),                                                                            // whole bar missing
        };
        var strict = OptionDirectionRatios.Average(rows.Select(r => r.Strict));
        Assert.Equal(50d / 1000d * 100d, strict.Ce.Ratio!.Value, 9);                                    // SUM(delta)/SUM(total quantity), not the mean of 50% and -6.25%
        Assert.Equal(30d / 400d * 100d, strict.Pe.Ratio!.Value, 9);
        Assert.Equal(((100d - 0d) + (-50d - 30d)) / (300d + 1100d) * 100d, strict.Direction.Ratio!.Value, 9);

        var oi = OptionDirectionRatios.Average(rows.Select(r => r.Oi));
        Assert.Equal(0d / 400d * 100d, oi.Ce.Ratio!.Value, 9);
        Assert.Equal(((40d - 10d) + (0d - -10d)) / (300d + 400d) * 100d, oi.Direction.Ratio!.Value, 9);                 // SUM(PE delta - CE delta) / SUM(PE start + CE start)

        var ofi = OptionDirectionRatios.Average(rows.Select(r => r.Ofi));
        Assert.Equal(70d / 200d * 100d, ofi.Ce.Ratio!.Value, 9);                                        // SUM(net)/SUM(gross)
        Assert.Equal((50d + 10d) / (100d + 50d) * 100d, ofi.Direction.Ratio!.Value, 9);                 // only the bar where both sides exist
        Assert.Equal(35d, ofi.Ce.Raw!.Value, 9);                                                        // arithmetic mean of available NetOFI
    }

    [Fact]
    public void MicroDevDirectionAvg_IsWeightedByTheUsableOverlap_OnlyWhereBothSidesExist()
    {
        RatioPart Dir(double ceTob, double peTob, double ceSec, double peSec) => OptionDirectionRatios.Core(
            Side(OptionType.Call, r => { r.MicroDevTimeWeighted = ceTob / 2; r.TobTimeWeighted = ceTob; r.TobUsableSeconds = ceSec; }),
            Side(OptionType.Put, r => { r.MicroDevTimeWeighted = peTob / 2; r.TobTimeWeighted = peTob; r.TobUsableSeconds = peSec; })).MicroDev.Direction;
        var rows = new[] { Dir(0.8, 0.0, 60, 30), Dir(-0.2, 0.2, 10, 40) };
        var avg = RatioAverage.Aggregate(rows);
        Assert.Equal((((0.8 - 0.0) / 2d) * 30d + ((-0.2 - 0.2) / 2d) * 10d) / 40d * 100d, avg.Ratio!.Value, 9);      // overlap = min(CE seconds, PE seconds)
        Assert.InRange(avg.Ratio!.Value, -100d, 100d);
    }

    [Fact]
    public void FixedStrikeProjection_PrefersV2Rows_AndFallsBackToV1_ForTheSameExactTokens()
    {
        var expiry = new DateOnly(2026, 10, 19);
        var catalog = new FixedStrikeCatalog(expiry, [new FixedStrikeContract(25000, "CE", "CE25000", "PE", "PE25000")]);
        var contract = catalog.Strikes[0];
        AdaptiveOptionStrikeBarRow Row(int bar, OptionType side, string version, string token = "", double strike = 25000) => new()
        {
            SessionId = 1, BarSeq = bar, MetricsVersion = version, EndAvailableAtUtc = T, Expiry = expiry, Strike = strike, Side = side,
            Token = token.Length > 0 ? token : side == OptionType.Call ? "CE" : "PE", TradingSymbol = "X", NetOfi = version == "option-strike-v2" ? 7 : null,
        };
        var stored = new[]
        {
            Row(1, OptionType.Call, "option-strike-v1"), Row(1, OptionType.Call, "option-strike-v2"), Row(1, OptionType.Put, "option-strike-v1"),
            Row(2, OptionType.Call, "option-strike-v1"),
            Row(2, OptionType.Put, "option-strike-v2", token: "OTHER"),            // wrong token: never shown
            Row(2, OptionType.Put, "option-strike-v2", strike: 25050),             // wrong strike: never shown
            Row(2, OptionType.Put, "some-other-version"),
        };
        var h = FixedStrikeProjection.Assemble(catalog, contract, [(1, T), (2, T)], stored);
        var bar1 = h.Rows.Single(r => r.BarSeq == 1);
        Assert.Equal(7, bar1.Ce!.NetOfi);                                         // v2 wins
        Assert.Equal("option-strike-v1", bar1.Pe!.MetricsVersion);                // v1 fallback when no v2 row exists
        var bar2 = h.Rows.Single(r => r.BarSeq == 2);
        Assert.Equal("option-strike-v1", bar2.Ce!.MetricsVersion);
        Assert.Null(bar2.Pe);                                                     // no substitution from another token / strike / version
    }
}
