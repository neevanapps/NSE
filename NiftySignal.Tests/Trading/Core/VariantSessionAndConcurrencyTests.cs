using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

public class VariantSessionTests
{
    private const string Ce = "CE23900";

    private static Kit NewKit(IBaseStrategy strategy, params VariantSpec[] variants)
        => new(Kit.Config1(variants: variants), Kit.StandardUniverse(), strategy);

    private static DateTimeOffset At(int h, int m, int s = 0, long ticks = 0) => Clock.IstTicks(h, m, s, ticks);

    [Fact]
    public void EntryWindow_OpensInclusiveAt0930_AndAnEarlierEpisodeGetsItsOpportunityAtTheFirstEligibleBar()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Immediate("A"));
        var justBefore = At(9, 29, 59, 9_999_999);
        kit.Feed(kit.Q(justBefore, Ce, 120m), Ev.Bar(justBefore, 1));          // episode begins at 09:29:59.9999999: outside the window

        Assert.Null(kit.Variant("A").Active);
        Assert.Empty(kit.Out<EntryIntent>());
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "SessionNotEligible");

        var open = At(9, 30);
        kit.Feed(kit.Q(open, Ce, 121m), Ev.Bar(open, 2));                      // exactly 09:30:00: eligible; same episode (b1)

        var intent = Assert.Single(kit.Out<EntryIntent>("A"));
        Assert.Equal(open, intent.At);
        Assert.EndsWith("b1", kit.Engine.AllOpportunities.Single().EpisodeId);
        Assert.Equal(121m, intent.QualifyingAsk);
    }

    [Fact]
    public void AtFifteenHundred_WaitingOpportunitiesExpire_EvenInAQuietMarket_AndNothingNewIsCreated()
    {
        Kit Build()
        {
            var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Ma("W", n: 1));
            kit.Feed(kit.Q(At(14, 0), Ce, 130m), Ev.Bar(At(14, 0), 1));        // contract selected; waiting
            Assert.Equal(OpportunityState.Waiting, kit.Variant("W").Active!.State);
            return kit;
        }

        var withHeartbeat = Build();
        withHeartbeat.Loop.AdvanceTo(At(15, 0, 0, 1));                           // quiet market: no market event, only a heartbeat
        var expired = Assert.Single(withHeartbeat.Out<OpportunityTransition>("W"), t => t.To == OpportunityState.Expired);
        Assert.Equal(At(15, 0), expired.At);                                     // processed at its scheduled time
        Assert.Equal("EntryClose", expired.Reason);

        var withoutHeartbeat = Build();
        withoutHeartbeat.Feed(withoutHeartbeat.Q(At(15, 20), Ce, 100m), Ev.Bar(At(15, 20), 2));   // next event only at 15:20

        Assert.Empty(withoutHeartbeat.Out<EntryIntent>());
        var late = Assert.Single(withoutHeartbeat.Out<OpportunityTransition>("W"), t => t.To == OpportunityState.Expired);
        Assert.Equal(At(15, 0), late.At);
        var expiredIndex = withoutHeartbeat.Engine.Outputs.ToList().IndexOf(late);
        Assert.All(withoutHeartbeat.Engine.Outputs.Skip(expiredIndex + 1), o => Assert.True(o.At >= At(15, 0)));
        Assert.Single(withoutHeartbeat.AllOpportunities());                          // the 15:20 bar creates nothing new
        Assert.Equal(Kit.Signature(withHeartbeat.Out<OpportunityTransition>("W")), Kit.Signature(withoutHeartbeat.Out<OpportunityTransition>("W")));
    }

    [Fact]
    public void AtFifteenHundred_PendingEntriesGetACancellationRequest_AndKeepTheirReservationUntilResolved()
    {
        Kit Build(out EntryIntent intent)
        {
            var built = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Immediate("A"));
            built.Feed(built.Q(At(14, 59, 30), Ce, 120m), Ev.Bar(At(14, 59, 30), 1));
            intent = Assert.Single(built.Out<EntryIntent>("A"));
            built.Loop.AdvanceTo(At(15, 0, 0, 1));
            return built;
        }

        var kit = Build(out var pending);
        var cancel = Assert.Single(kit.Out<CancelRequest>("A"));
        Assert.Equal(At(15, 0), cancel.At);
        Assert.Equal(CancelReason.EntryClose, cancel.Reason);
        Assert.Equal(pending.IntentId, cancel.IntentId);
        Assert.Equal(SlotState.Reserved, kit.Variant("A").Slots[0].State);       // reservation retained, nothing freed yet
        Assert.Equal(OpportunityState.OrderPending, kit.Variant("A").Active!.State);

        kit.Feed(kit.Q(At(15, 0, 2), Ce, 110m));                                  // later qualifying quotes issue nothing
        Assert.Single(kit.Out<EntryIntent>("A"));

        kit.At(At(15, 0, 5), s => kit.Outcome(pending, EntryOutcomeKind.Cancelled, s));
        kit.Loop.AdvanceTo(At(15, 0, 6));
        Assert.Equal(SlotState.Free, kit.Variant("A").Slots[0].State);
        Assert.Equal(OpportunityState.Expired, kit.AllOpportunities().Single().State);
        Assert.Equal("EntryClose", kit.AllOpportunities().Single().TerminalReason);   // no re-arm after the cutoff
        Assert.Null(kit.Variant("A").Active);

        // A fill that wins the race with the cancellation still consumes the opportunity.
        var filled = Build(out var pending2);
        filled.At(At(15, 0, 5), s => filled.Outcome(pending2, EntryOutcomeKind.Filled, s));
        filled.Loop.AdvanceTo(At(15, 0, 6));
        Assert.Equal(OpportunityState.Consumed, filled.AllOpportunities().Single().State);
        Assert.Equal(SlotState.Occupied, filled.Variant("A").Slots[0].State);
    }

    [Fact]
    public void QuoteAtExactlyFifteenHundred_CannotEnter_ButOneTickEarlierCan()
    {
        Kit Build()
        {
            var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Ma("W", n: 1));
            kit.Feed(kit.Q(At(14, 0), Ce, 130m), Ev.Bar(At(14, 0), 1));
            return kit;
        }

        var late = Build();
        late.Feed(late.Q(At(15, 0), Ce, 120m));
        Assert.Empty(late.Out<EntryIntent>());

        var early = Build();
        early.Feed(early.Q(At(15, 0).AddTicks(-1), Ce, 120m));
        Assert.Single(early.Out<EntryIntent>("W"));
    }

    [Fact]
    public void ABarAtFifteenHundred_CreatesNoOpportunity()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Immediate("A"));

        kit.Feed(kit.Q(At(15, 0), Ce, 120m), Ev.Bar(At(15, 0), 1));

        Assert.Empty(kit.Out<EntryIntent>());
        Assert.Empty(kit.AllOpportunities());
    }
}

public class VariantConcurrencyTests
{
    private static readonly string[] AllVariants = { "A", "A2", "A3", "B", "B2" };

    private static IEnumerable<VariantSpec> Specs() => new[]
    {
        Kit.Immediate("A"),
        Kit.Ma("A2", n: 3),
        Kit.Ma("A3", n: 2, source: CandlePriceSource.BidClose),
        Kit.Immediate("B", strategy: "P"),
        Kit.Ma("B2", n: 2, strategy: "P"),
    };

    private static Kit Scenario(IEnumerable<VariantSpec> specs, Random? heartbeatRng = null)
    {
        var kit = new Kit(
            Kit.Config1(variants: specs.ToArray()),
            Kit.StandardUniverse(),
            ScriptedStrategy.Valid("S", 4, 8, OptionSide.Call),
            ScriptedStrategy.Valid("P", 4, 8, OptionSide.Put));
        var ce = new[] { 130m, 130m, 130m, 128m, 127m, 126m, 125m, 124m };
        var previous = Kit.Bar(0);
        for (var k = 1; k <= 8; k++)
        {
            var t = Kit.Bar(k);
            Heartbeats(kit, heartbeatRng, previous, t);
            kit.BarGroup(k, kit.Q(t, "CE23900", ce[k - 1]), kit.Q(t, "PE23900", ce[k - 1] + 10m));
            kit.Feed(kit.Q(t.AddSeconds(5), "CE23900", ce[k - 1] - 1m));
            kit.Feed(kit.Q(t.AddSeconds(10), "PE23900", ce[k - 1] + 9m));
            previous = t.AddSeconds(10);
        }

        Heartbeats(kit, heartbeatRng, previous, Kit.Bar(9));
        kit.Loop.Finish(kit.Engine.Schedule.SessionEndUtc);
        return kit;
    }

    private static void Heartbeats(Kit kit, Random? rng, DateTimeOffset from, DateTimeOffset to)
    {
        if (rng is null)
        {
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            var span = (to - from).Ticks;
            kit.Loop.AdvanceTo(from.AddTicks(span <= 0 ? 0 : rng.NextInt64(0, span + 1)));
        }

        kit.Loop.AdvanceTo(to);                                                  // watermark exactly at the next group's instant
    }

    [Fact]
    public void Scenario_ProducesIntentsForEveryVariant()
    {
        var kit = Scenario(Specs());

        foreach (var id in AllVariants)
        {
            Assert.NotEmpty(kit.Out<EntryIntent>(id));
        }

        Assert.All(kit.Out<EntryIntent>("A").Concat(kit.Out<EntryIntent>("A2")), i => Assert.Equal(OptionSide.Call, i.Contract.Side));
        Assert.All(kit.Out<EntryIntent>("B").Concat(kit.Out<EntryIntent>("B2")), i => Assert.Equal(OptionSide.Put, i.Contract.Side));
    }

    [Fact]
    public void EachVariant_BehavesIdentically_WhetherOrNotOthersRunBesideIt()
    {
        var together = Scenario(Specs());

        foreach (var id in AllVariants)
        {
            var alone = Scenario(Specs().Where(s => s.VariantId == id));
            Assert.Equal(Kit.Signature(alone.Out<EngineOutput>(id)), Kit.Signature(together.Out<EngineOutput>(id)));
            Assert.NotEmpty(together.Out<EngineOutput>(id));
        }
    }

    [Fact]
    public void DeclarationOrder_NeverChangesTheHash_OrAnyOutput()
    {
        var forward = Scenario(Specs());
        var backward = Scenario(Specs().Reverse());

        Assert.Equal(forward.Config.ComputeHash(), backward.Config.ComputeHash());
        Assert.Equal(Kit.Signature(forward.Engine.Outputs), Kit.Signature(backward.Engine.Outputs));
        Assert.Equal(Specs().OrderBy(s => s.GetKey()).Select(s => s.VariantId), forward.Engine.Variants.Select(v => v.VariantId));   // ascending (StrategyId, VariantId)
        Assert.Equal(Enumerable.Range(0, 5), forward.Engine.Variants.Select(v => v.Ordinal));
    }

    [Fact]
    public void ARepeatedRun_IsIdentical_AlsoInParallelAndUnderOtherCultures()
    {
        var baseline = Kit.Signature(Scenario(Specs()).Engine.Outputs);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(baseline, Kit.Signature(Scenario(Specs()).Engine.Outputs));
        }

        var cultures = new[] { "tr-TR", "ar-SA", "de-DE", "th-TH", "fr-FR", "ja-JP", "hi-IN", "en-US" };
        var results = new string[cultures.Length * 2];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            results[i] = Culture.Under(cultures[i % cultures.Length], () => Kit.Signature(Scenario(Specs()).Engine.Outputs)));

        Assert.All(results, r => Assert.Equal(baseline, r));
    }

    [Fact]
    public void Heartbeats_NeverChangeAnyOutput()
    {
        var baseline = Kit.Signature(Scenario(Specs()).Engine.Outputs);
        var rng = new Random(77);

        for (var run = 0; run < 25; run++)
        {
            Assert.Equal(baseline, Kit.Signature(Scenario(Specs(), rng).Engine.Outputs));
        }
    }

    [Fact]
    public void Ids_DoNotDependOnWhichOtherVariantsExist()
    {
        var together = Scenario(Specs());
        var alone = Scenario(Specs().Where(s => s.VariantId == "A2"));

        var a = together.Out<EntryIntent>("A2").Select(i => (i.DecisionId, i.IntentId, i.OpportunityId)).ToList();
        var b = alone.Out<EntryIntent>("A2").Select(i => (i.DecisionId, i.IntentId, i.OpportunityId)).ToList();

        Assert.Equal(b, a);
        Assert.NotEqual(together.Config.ComputeHash(), alone.Config.ComputeHash());   // the whole-config hash differs, the variant's ids do not
    }
}
