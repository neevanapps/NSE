using NiftySignal.Domain.ValueObjects;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class VolumeBar6500OptionTranslationAnalysisTests
{
    static readonly DateTimeOffset T0 =
        new(2026, 9, 8, 9, 30, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void ExpandingAbsolutePercentiles_UsesOnlyCurrentAndPriorValues()
    {
        double?[] values = [10, 30, 20, -40, null, -20];

        var ranks = VolumeBar6500OptionTranslationAnalysis.ExpandingAbsolutePercentiles(values);

        Assert.Equal(1.0, ranks[0]!.Value, 12);
        Assert.Equal(1.0, ranks[1]!.Value, 12);
        Assert.Equal(2.0 / 3.0, ranks[2]!.Value, 12);
        Assert.Equal(1.0, ranks[3]!.Value, 12);
        Assert.Null(ranks[4]);

        // Magnitudes seen are 10,30,20,40,20. For the final 20:
        // 1 less, 2 equal -> average rank position = 1 + 1.5 = 2.5 of 5.
        Assert.Equal(0.5, ranks[5]!.Value, 12);
    }

    [Fact]
    public void SelectDecisionContract_UsesFreshDecisionTimeQuotesAndSmallestRelativeSpread()
    {
        var near = Manifest("A", "Call", 24000m);
        var fartherButTighter = Manifest("B", "Call", 24100m);
        var stale = Manifest("C", "Call", 23950m);
        var put = Manifest("D", "Put", 24000m);

        var series = new Dictionary<string, VolumeBar6500OptionTranslationAnalysis.TokenSeries>
        {
            ["A"] = Series(Tick(1, T0.AddSeconds(-1), 118m, 120m)),
            ["B"] = Series(Tick(2, T0.AddSeconds(-1), 119.5m, 120m)),
            ["C"] = Series(Tick(3, T0.AddSeconds(-30), 119.9m, 120m)),
            ["D"] = Series(Tick(4, T0.AddSeconds(-1), 119.9m, 120m)),
        };

        var selected = VolumeBar6500OptionTranslationAnalysis.SelectDecisionContract(
            [near, fartherButTighter, stale, put],
            "Call",
            24000m,
            T0,
            token => series[token]);

        Assert.NotNull(selected);
        Assert.Equal("B", selected!.Value.Instrument.Token);
    }

    [Fact]
    public void SelectDecisionContract_RejectsQuoteOutsidePremiumBand()
    {
        var call = Manifest("A", "Call", 24000m);
        var series = Series(Tick(1, T0.AddSeconds(-1), 149m, 151m));

        var selected = VolumeBar6500OptionTranslationAnalysis.SelectDecisionContract(
            [call],
            "Call",
            24000m,
            T0,
            _ => series);

        Assert.Null(selected);
    }

    [Fact]
    public void TokenSeries_FirstValidQuoteAtOrAfter_RespectsLatencyBoundaryAndDeadline()
    {
        var series = Series(
            Tick(1, T0.AddMilliseconds(500), 119m, 120m),
            Tick(2, T0.AddMilliseconds(1200), 119m, 120m),
            Tick(3, T0.AddSeconds(20), 119m, 120m));

        var quote = series.FirstValidQuoteAtOrAfter(
            T0.AddSeconds(1),
            lotSize: 65,
            noLaterThan: T0.AddSeconds(15));

        Assert.NotNull(quote);
        Assert.Equal(2, quote!.Value.Id);
    }

    [Fact]
    public void TokenSeries_RequiresDisplayedQuantityForOneLot()
    {
        var lowQty = Tick(1, T0, 119m, 120m, qty: 10);
        var good = Tick(2, T0.AddSeconds(1), 119m, 120m, qty: 65);
        var series = Series(lowQty, good);

        var quote = series.FirstValidQuoteAtOrAfter(T0, lotSize: 65);

        Assert.NotNull(quote);
        Assert.Equal(2, quote!.Value.Id);
    }

    [Theory]
    [InlineData(true, true, "FuturesCorrectOptionProfit")]
    [InlineData(true, false, "FuturesCorrectOptionLoss")]
    [InlineData(false, true, "FuturesWrongOptionProfit")]
    [InlineData(false, false, "FuturesWrongOptionLoss")]
    public void ClassifyOutcome_ProducesFourTranslationCategories(
        bool futuresCorrect,
        bool optionProfitable,
        string expected)
    {
        Assert.Equal(
            expected,
            VolumeBar6500OptionTranslationAnalysis.ClassifyOutcome(
                futuresCorrect,
                optionProfitable));
    }

    [Fact]
    public void ClassifyOutcome_KeepsFlatUnderlyingSeparate()
    {
        Assert.Equal(
            "UnderlyingFlat",
            VolumeBar6500OptionTranslationAnalysis.ClassifyOutcome(
                null,
                true));
    }

    static OptionTickReaderV2.ManifestRow Manifest(
        string token,
        string optionType,
        decimal strike) =>
        new(
            token,
            "Nfo",
            $"TEST-{token}",
            "Option",
            optionType,
            strike,
            new DateOnly(2026, 9, 8),
            "NIFTY",
            65,
            0.05m);

    static VolumeBar6500OptionTranslationAnalysis.TokenSeries Series(
        params OptionTickV2[] ticks) =>
        VolumeBar6500OptionTranslationAnalysis.TokenSeries.FromTicks(ticks);

    static OptionTickV2 Tick(
        long id,
        DateTimeOffset availableAt,
        decimal bid,
        decimal ask,
        long qty = 65,
        decimal ltp = 120m)
    {
        var depth = new MarketDepth(
            bid, qty,
            0, 0,
            0, 0,
            0, 0,
            0, 0,
            ask, qty,
            0, 0,
            0, 0,
            0, 0,
            0, 0);

        return new OptionTickV2(
            id,
            availableAt,
            availableAt,
            ltp,
            depth,
            id * 100,
            null);
    }
}
