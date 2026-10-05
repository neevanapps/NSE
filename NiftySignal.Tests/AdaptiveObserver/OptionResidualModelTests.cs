using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class OptionResidualModelTests
{
    [Fact]
    public void FixedBand_ConstantIvRepricing_HasNearZeroResidual_WhenMarketEqualsModel()
    {
        var at = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero); // 09:30 IST
        var expiry = new DateOnly(2026, 9, 29);
        const double spot = 100d;
        const double rate = 0.05d;
        const double vol = 0.20d;
        var chain = BuildChain(expiry);
        var t0 = TimeToExpiry.YearsUntilExpiry(expiry, at);
        var quotes0 = BuildQuotes(chain, at, spot, t0, rate, vol);

        var anchor = OptionResidualModel.BuildAnchor(at, expiry, spot, chain, quotes0, rate);

        Assert.NotNull(anchor);
        Assert.Equal(100d, anchor!.CenterStrike);

        var later = at.AddMinutes(20);
        var modeled = 101.25d;
        var t1 = TimeToExpiry.YearsUntilExpiry(expiry, later);
        var quotes1 = BuildQuotes(chain, later, modeled, t1, rate, vol);

        var readings = OptionResidualModel.Evaluate(anchor, later, modeled, quotes1, rate);

        Assert.Equal(2, readings.Count);
        Assert.All(readings, x =>
        {
            Assert.InRange(Math.Abs(x.CEResidual), 0d, 1e-6);
            Assert.InRange(Math.Abs(x.PEResidual), 0d, 1e-6);
            Assert.InRange(Math.Abs(x.DirectionalResidualPct), 0d, 1e-6);
        });
    }

    [Fact]
    public void FixedBand_CallRichness_MakesDirectionalResidualPositive()
    {
        var at = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);
        var expiry = new DateOnly(2026, 9, 29);
        const double rate = 0.05d;
        const double vol = 0.20d;
        var chain = BuildChain(expiry);
        var t0 = TimeToExpiry.YearsUntilExpiry(expiry, at);
        var anchor = OptionResidualModel.BuildAnchor(at, expiry, 100d, chain, BuildQuotes(chain, at, 100d, t0, rate, vol), rate)!;

        var later = at.AddMinutes(10);
        var t1 = TimeToExpiry.YearsUntilExpiry(expiry, later);
        var quotes = BuildQuotes(chain, later, 100d, t1, rate, vol)
            .ToDictionary(x => x.Key, x => x.Value);

        foreach (var call in chain.Where(x => x.OptionType == OptionType.Call))
        {
            var q = quotes[call.Token];
            quotes[call.Token] = q with { Bid = q.Bid + 1d, Ask = q.Ask + 1d, Last = q.Last + 1d };
        }

        var band = OptionResidualModel.Evaluate(anchor, later, 100d, quotes, rate)
            .Single(x => x.Variant == ResidualVariant.AtmPlusMinus2);

        Assert.True(band.CEResidual > 4.9d);
        Assert.InRange(Math.Abs(band.PEResidual), 0d, 1e-6);
        Assert.True(band.DirectionalResidualPct > 0d);
    }

    static IReadOnlyList<ObserverOptionInstrument> BuildChain(DateOnly expiry)
    {
        var result = new List<ObserverOptionInstrument>();
        foreach (var strike in new[] { 90d, 95d, 100d, 105d, 110d, 115d, 120d })
        {
            result.Add(new ObserverOptionInstrument($"C{strike}", $"C{strike}", OptionType.Call, strike, expiry, 65));
            result.Add(new ObserverOptionInstrument($"P{strike}", $"P{strike}", OptionType.Put, strike, expiry, 65));
        }

        return result;
    }

    static IReadOnlyDictionary<string, OptionQuoteSnapshot> BuildQuotes(
        IReadOnlyList<ObserverOptionInstrument> chain,
        DateTimeOffset at,
        double underlying,
        double t,
        double rate,
        double vol)
    {
        var result = new Dictionary<string, OptionQuoteSnapshot>();
        foreach (var i in chain)
        {
            var price = BlackScholes.Calculate(i.OptionType, underlying, i.Strike, t, rate, vol).Price;
            result[i.Token] = new OptionQuoteSnapshot(i.Token, at, price, price - 0.01d, price + 0.01d, 1000);
        }

        return result;
    }
}
