using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptiveCausalClockTests
{
    [Fact]
    public void SameAvailability_OptionIdAfterFutureIdStillBelongsToClosingBoundary()
    {
        var setup=Setup();var engine=setup.Engine;var at=setup.At;
        engine.ProcessAvailabilityGroup([( "FUT",Future(1,at,1000) )]);
        var first=setup.Quotes.Select((q,i)=>(q.Token,Quote(i+3,at.AddSeconds(1),q,1000))).ToList();
        first.Add(("FUT",Future(2,at.AddSeconds(1),1050)));
        Assert.Empty(engine.ProcessAvailabilityGroup(first));
        var call=setup.Quotes.Single(x=>x.Token=="C23000");
        var changed=call with { AvailableAt=at.AddSeconds(2),Last=call.Last+1,Bid=call.Bid+1,Ask=call.Ask+1 };
        var package=Assert.Single(engine.ProcessAvailabilityGroup([
            ("FUT",Future(13,at.AddSeconds(2),1100)),(call.Token,Quote(14,at.AddSeconds(2),changed,1010)) ]));
        Assert.NotNull(package.OptionBand.Call);
        Assert.Equal(10,package.OptionBand.Call!.ContractTotalQuantity);
        Assert.Equal(10,package.OptionBand.Call.ContractStrictBuy);
        Assert.Equal(at.AddSeconds(2),package.FutureBar.EndAvailableAtUtc);
        Assert.Throws<InvalidOperationException>(()=>engine.ProcessAvailabilityGroup([(call.Token,Quote(15,at.AddSeconds(2),changed,1020))]));
    }

    [Fact]
    public void DiagnosticMilliseconds_DoNotUseQuoteFromLaterFractionOfSameMillisecond()
    {
        var setup=Setup();var engine=setup.Engine;var at=setup.At;
        engine.ProcessAvailabilityGroup([("FUT",Future(1,at,1000))]);
        var first=setup.Quotes.Select((q,i)=>(q.Token,Quote(i+3,at.AddSeconds(1),q,1000))).ToList();
        first.Add(("FUT",Future(2,at.AddSeconds(1),1050)));
        engine.ProcessAvailabilityGroup(first);
        var call=setup.Quotes.Single(x=>x.Token=="C23000");
        var quoteTime=at.AddSeconds(2).AddTicks(1000);
        var changed=call with { AvailableAt=quoteTime,Last=call.Last+100,Bid=call.Bid+100,Ask=call.Ask+100 };
        engine.ProcessAvailabilityGroup([(call.Token,Quote(14,quoteTime,changed,1010))]);
        var closeTime=at.AddSeconds(2).AddTicks(2000);
        var package=Assert.Single(engine.ProcessAvailabilityGroup([("FUT",Future(13,closeTime,1100))]));
        Assert.Equal(closeTime,package.FutureBar.EndAvailableAtUtc); // Exact clock is preserved.
        var actual=package.Residuals.Single(x=>x.Variant==ResidualVariant.Atm).Reading!;
        var expected=OptionResidualModel.Evaluate(setup.Anchor,at.AddSeconds(2),23000,
            setup.Quotes.ToDictionary(x=>x.Token,x=>x with { AvailableAt=at.AddSeconds(1) }),.065)
            .Single(x=>x.Variant==ResidualVariant.Atm);
        Assert.Equal(at.AddSeconds(2),actual.AsOfUtc);
        Assert.InRange(Math.Abs(actual.CEResidual-expected.CEResidual),0,1e-9);
    }

    static (AdaptiveObserverEngine Engine,DateTimeOffset At,OptionQuoteSnapshot[] Quotes,OptionResidualAnchor Anchor) Setup()
    {
        var at=new DateTimeOffset(2026,9,25,4,0,0,TimeSpan.Zero);var day=new DateOnly(2026,9,25);var expiry=day.AddDays(4);
        var chain=new List<ObserverOptionInstrument>();
        foreach(var strike in new double[] { 22900,22950,23000,23050,23100 })
            foreach(var side in new[] { OptionType.Call,OptionType.Put })chain.Add(new((side==OptionType.Call?"C":"P")+strike,"OPTION",side,strike,expiry,65));
        var quotes=chain.Select(i=> { var p=AdaptiveResearchPricing.Price(i.OptionType,23000,i.Strike,TimeToExpiry.YearsUntilExpiry(expiry,at),.065,.2);
            return new OptionQuoteSnapshot(i.Token,at,p,p-.01,p+.01,1000); }).ToArray();
        var anchor=OptionResidualModel.BuildAnchor(at,expiry,23000,chain,quotes.ToDictionary(x=>x.Token),.065)!;
        Assert.NotNull(anchor);
        return (new(new(day,"FUT","FUT",expiry,65,100),expiry,.065,null,at,chain,anchor),at,quotes,anchor);
    }
    static CleanObserverTick Future(long id,DateTimeOffset at,long volume)=>new(id,at,at,23000,22999,23001,1,1,volume,1000);
    static CleanObserverTick Quote(long id,DateTimeOffset at,OptionQuoteSnapshot q,long volume)=>new(id,at,at,q.Last,q.Bid,q.Ask,1,1,volume,1000);
}
