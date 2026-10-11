using NiftySignal.Tests.Trading.Core;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Signals;

/// <summary>The plumbing strategy exists only to drive engine machinery. These tests make no statement about trading edge.</summary>
public class PlumbingStrategyTests
{
    [Fact]
    public void Validity_FollowsThePeriodWindowAndAlternatesSides()
    {
        var strategy = new PlumbingEveryNthBarStrategy("P", period: 4, validBars: 2);

        var actual = Enumerable.Range(1, 12)
            .Select(k => strategy.OnBar(Ev.Bar(Kit.Bar(k), k)))
            .Select(v => v.IsValid ? v.Side.ToString() : "-")
            .ToArray();

        Assert.Equal(new[] { "Call", "-", "-", "Put", "Put", "-", "-", "Call", "Call", "-", "-", "Put" }, actual);
    }

    [Fact]
    public void Strategy_IsPure_AndRejectsBadParameters()
    {
        var strategy = new PlumbingEveryNthBarStrategy("P", 5, 5);
        var bar = Ev.Bar(Kit.Bar(7), 7);

        Assert.Equal(strategy.OnBar(bar), strategy.OnBar(bar));
        Assert.False(strategy.OnBar(Ev.Bar(Kit.Bar(1), 0)).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlumbingEveryNthBarStrategy("P", 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlumbingEveryNthBarStrategy("P", 4, 5));
        Assert.Throws<ArgumentException>(() => new PlumbingEveryNthBarStrategy("", 4, 2));
        Assert.Equal("plumbing-v1", strategy.Version);
    }

    [Fact]
    public void DrivenThroughTheEngine_ItProducesOneOpportunityPerEpisode_WithAlternatingSides()
    {
        var kit = new Kit(
            Kit.Config1(variants: Kit.Immediate("P1", strategy: "P")),
            Kit.StandardUniverse(),
            new PlumbingEveryNthBarStrategy("P", 4, 2));

        for (var k = 1; k <= 12; k++)
        {
            var before = kit.Out<EntryIntent>().Count();
            kit.BarGroup(k, kit.Q(Kit.Bar(k), "CE23900", 120m), kit.Q(Kit.Bar(k), "PE23900", 120m));
            if (kit.Out<EntryIntent>().Count() > before)
            {
                var intent = kit.Out<EntryIntent>().Last();
                var t = Kit.Bar(k);
                kit.At(t.AddSeconds(5), s => kit.Outcome(intent, EntryOutcomeKind.Filled, s));
                kit.At(t.AddSeconds(10), s => kit.Engine.ReportPositionClosed(intent.VariantId, intent.OpportunityId, s));
                kit.Loop.AdvanceTo(t.AddSeconds(11));
            }
        }

        var intents = kit.Out<EntryIntent>().ToList();
        Assert.Equal(new[] { OptionSide.Call, OptionSide.Put, OptionSide.Call, OptionSide.Put }, intents.Select(i => i.Contract.Side).ToArray());
        Assert.Equal(new[] { Kit.Bar(1), Kit.Bar(4), Kit.Bar(8), Kit.Bar(12) }, intents.Select(i => i.At).ToArray());
        Assert.Equal(4, kit.Engine.AllOpportunities.Select(o => o.Id).Distinct().Count());
        Assert.Equal(new[] { "b1", "b4", "b8", "b12" }, kit.Engine.AllOpportunities.Select(o => o.EpisodeId).ToArray());
        Assert.All(kit.Engine.AllOpportunities, o => Assert.Equal(OpportunityState.Consumed, o.State));
    }
}
