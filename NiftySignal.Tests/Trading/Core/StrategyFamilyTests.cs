using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>
/// CP1: the strategy-family contract and the (StrategyId, VariantId) identity. The scenario families are TEST/PLUMBING only: they follow fixed scripts
/// and claim no edge.
/// </summary>
public class StrategyFamilyTests
{
    private const string Ce = "CE23900";
    private const string Pe = "PE23900";

    private static readonly VariantKey X1 = new("X", "X1");
    private static readonly VariantKey X2 = new("X", "X2");
    private static readonly VariantKey X3 = new("X", "X3");

    private static VariantSpec Spec(string strategy, string variant, CapacityBlockedPolicy? capacity = null)
        => new() { StrategyId = strategy, VariantId = variant, EntryCondition = new ImmediateEntrySpec(), OnCapacityBlocked = capacity };

    private static TradingConfig Config(params VariantSpec[] variants) => TradeKit.MakeConfig(variants: variants);

    private static IReadOnlyDictionary<string, IStrategyFamily> Families(params IStrategyFamily[] families) => families.ToDictionary(f => f.StrategyId, f => f);

    private static PlumbingVariantScript Script(IEnumerable<(long From, long To, StrategyDirection Direction)> windows, params long[] exits)
        => new(windows.Select(w => new PlumbingWindow(w.From, w.To, w.Direction)).ToList(), exits.Length == 0 ? null : exits);

    /// <summary>Counts how often the engine asks the family: it must be exactly once per bar, however many variants the family serves.</summary>
    private sealed class CountingFamily(IStrategyFamily inner) : IStrategyFamily
    {
        public int Calls { get; private set; }

        public string StrategyId => inner.StrategyId;

        public string Version => inner.Version;

        public IReadOnlyCollection<string> VariantIds => inner.VariantIds;

        public FamilyDecisions OnBar(BarCompletedEvent bar)
        {
            Calls++;
            return inner.OnBar(bar);
        }
    }

    private sealed class ScriptedFamily(string strategyId, IReadOnlyCollection<string> declared, Func<long, IReadOnlyList<VariantDecision>> script) : IStrategyFamily
    {
        public string StrategyId => strategyId;

        public string Version => "scripted-family";

        public IReadOnlyCollection<string> VariantIds => declared;

        public FamilyDecisions OnBar(BarCompletedEvent bar) => new(script(bar.BarSeq));
    }

    /// <summary>One bar at the given IST time with a CE and a PE quote inside the premium band, then a heartbeat past the order latency.</summary>
    private static void BarAt(TradeKit kit, int hour, int minute, int seq, params EngineEvent[] controls)
    {
        var at = Clock.Ist(hour, minute);
        kit.Feed(controls);
        kit.Bar(at, seq, kit.Q(at, 124m, 125m, token: Ce), kit.Q(at, 124m, 125m, token: Pe));
        kit.Loop.AdvanceTo(at.AddSeconds(1.5));
    }

    private static PlumbingFamily XFamily() => new("X", new Dictionary<string, PlumbingVariantScript>
    {
        ["X1"] = Script(new[] { (1L, 1L, StrategyDirection.Long), (3L, 3L, StrategyDirection.Long), (6L, 6L, StrategyDirection.Long) }, 5),
        ["X2"] = Script(new[] { (2L, 2L, StrategyDirection.Short) }),
        ["X3"] = Script(new[] { (4L, 4L, StrategyDirection.Long) }),
    });

    // ------------------------------------------------------------------ the X / X1 / X2 / X3 scenario

    [Fact]
    public void FamilyX_RunsThreeIndependentVariantsConcurrently_EachWithItsOwnSlotAndPosition()
    {
        var family = new CountingFamily(XFamily());
        var kit = new TradeKit(Config(Spec("X", "X1"), Spec("X", "X2"), Spec("X", "X3")), Families(family));

        BarAt(kit, 9, 45, 1);                                                    // X1 enters long -> CE
        Assert.Equal(PositionState.Open, kit.Position(X1).State);
        Assert.Equal(Ce, kit.Position(X1).Contract.Token);
        Assert.Empty(kit.Engine.Execution.PositionsOf(X2));

        BarAt(kit, 9, 52, 2);                                                    // X2 enters short -> PE while X1 is still open
        Assert.Equal(PositionState.Open, kit.Position(X1).State);
        Assert.Equal(PositionState.Open, kit.Position(X2).State);
        Assert.Equal(Pe, kit.Position(X2).Contract.Token);

        BarAt(kit, 10, 0, 3);                                                    // X1 becomes valid again while it still owns its slot
        var x1First = Assert.Single(kit.Engine.Execution.PositionsOf(X1));
        Assert.Equal(PositionState.Open, x1First.State);
        var rejected = kit.Out<OpportunityTransition>(X1).Last(t => t.To == OpportunityState.Rejected);
        Assert.Equal("CapacityBusy", rejected.Reason);                           // X1's own capacity policy handled it ...
        Assert.DoesNotContain(kit.Out<OpportunityTransition>(X2), t => t.To == OpportunityState.Rejected);   // ... and X2 / X3 were not touched
        Assert.Empty(kit.Out<OpportunityTransition>(X3));
        Assert.Single(kit.Engine.Variants.GetVariant(X1).Slots);                 // no second slot appeared

        BarAt(kit, 10, 5, 4);                                                    // X3 enters while X1 and X2 are both open
        Assert.Equal(Ce, kit.Position(X3).Contract.Token);
        foreach (var key in new[] { X1, X2, X3 })
        {
            var slot = Assert.Single(kit.Engine.Variants.GetVariant(key).Slots);
            Assert.Equal(SlotState.Occupied, slot.State);
            var position = Assert.Single(kit.Engine.Execution.PositionsOf(key));
            Assert.Equal(PositionState.Open, position.State);
            Assert.Equal(key, position.Variant);
            Assert.Equal(650, position.OpenQuantity);
        }

        var ids = new[] { X1, X2, X3 }.Select(k => kit.Position(k).Id).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        var x2Id = kit.Position(X2).Id;
        var x3Id = kit.Position(X3).Id;

        BarAt(kit, 10, 15, 5);                                                   // X1 asks for its own exit: only X1 closes
        Assert.Equal(PositionState.Closed, kit.Position(X1).State);
        Assert.Equal(SlotState.Free, kit.Engine.Variants.GetVariant(X1).Slots[0].State);
        Assert.Equal(PositionState.Open, kit.Position(X2).State);
        Assert.Equal(PositionState.Open, kit.Position(X3).State);
        Assert.All(kit.Out<ExitOrderCreated>(X1), e => Assert.Equal(ExitReason.StrategyExit, e.Reason));
        Assert.Empty(kit.Out<ExitOrderCreated>(X2));
        Assert.Empty(kit.Out<ExitOrderCreated>(X3));

        BarAt(kit, 10, 20, 6);                                                   // a new X1 validity episode: X1 may enter again, X2 / X3 are unaffected
        var x1Positions = kit.Engine.Execution.PositionsOf(X1);
        Assert.Equal(2, x1Positions.Count);
        Assert.Equal(PositionState.Closed, x1Positions[0].State);
        Assert.Equal(PositionState.Open, x1Positions[1].State);
        Assert.NotEqual(x1Positions[0].Id, x1Positions[1].Id);
        Assert.Equal(x2Id, kit.Position(X2).Id);
        Assert.Equal(x3Id, kit.Position(X3).Id);
        Assert.Single(kit.Engine.Execution.PositionsOf(X2));
        Assert.Single(kit.Engine.Execution.PositionsOf(X3));

        Assert.Equal(6, family.Calls);                                           // evaluated once per bar, not once per variant
    }

    [Fact]
    public void EveryOutputOfTheScenario_CarriesBothPartsOfItsOwnersIdentity()
    {
        var kit = new TradeKit(Config(Spec("X", "X1"), Spec("X", "X2"), Spec("X", "X3")), Families(XFamily()));
        BarAt(kit, 9, 45, 1);
        BarAt(kit, 9, 52, 2);
        BarAt(kit, 10, 5, 4);

        var variantOutputs = kit.Engine.Outputs.Where(o => !o.Variant.IsGlobal).ToList();
        Assert.NotEmpty(variantOutputs);
        Assert.All(variantOutputs, o =>
        {
            Assert.Equal("X", o.StrategyId);
            Assert.Contains(o.VariantId, new[] { "X1", "X2", "X3" });
            Assert.Equal(new VariantKey(o.StrategyId, o.VariantId), o.Variant);
        });
        Assert.All(kit.Engine.AuditTrailEntries().Where(e => e.VariantId != "*"), e => Assert.Equal("X", e.StrategyId));
        Assert.All(kit.Engine.DailySummaries(Clock.Ist(11, 0)), s => Assert.Equal("X", s.StrategyId));
        Assert.Equal(new[] { "X1", "X2", "X3" }, kit.Engine.DailySummaries(Clock.Ist(11, 0)).Select(s => s.VariantId));
    }

    [Fact]
    public void TheScenario_IsDeterministic_AndIndependentOfDeclarationOrder()
    {
        static string Run(IEnumerable<VariantSpec> specs)
        {
            var kit = new TradeKit(Config(specs.ToArray()), Families(XFamily()));
            BarAt(kit, 9, 45, 1);
            BarAt(kit, 9, 52, 2);
            BarAt(kit, 10, 0, 3);
            BarAt(kit, 10, 5, 4);
            BarAt(kit, 10, 15, 5);
            BarAt(kit, 10, 20, 6);
            return TradeKit.Signature(kit.Engine.Outputs);
        }

        var forward = new[] { Spec("X", "X1"), Spec("X", "X2"), Spec("X", "X3") };
        var baseline = Run(forward);

        Assert.Equal(baseline, Run(forward));
        Assert.Equal(baseline, Run(forward.Reverse()));
    }

    [Fact]
    public void AVariantNotInTheConfiguration_IsSimplyNotTraded()
    {
        var kit = new TradeKit(Config(Spec("X", "X2")), Families(XFamily()));          // X1 and X3 are declared by the family but not configured

        BarAt(kit, 9, 45, 1);
        BarAt(kit, 9, 52, 2);

        Assert.Equal(PositionState.Open, kit.Position(X2).State);
        Assert.DoesNotContain(kit.Engine.Outputs, o => o.Variant == X1 || o.Variant == X3);
    }

    // ------------------------------------------------------------------ same VariantId under different strategies

    private static TradeKit TwoStrategiesWithTheSameVariantId(out CountingFamily x, out CountingFamily y)
    {
        x = new CountingFamily(new PlumbingFamily("X", new Dictionary<string, PlumbingVariantScript> { ["A"] = Script(new[] { (1L, 1L, StrategyDirection.Long) }) }));
        y = new CountingFamily(new PlumbingFamily("Y", new Dictionary<string, PlumbingVariantScript> { ["A"] = Script(new[] { (1L, 1L, StrategyDirection.Short) }) }));
        return new TradeKit(Config(Spec("X", "A"), Spec("Y", "A")), Families(x, y));
    }

    [Fact]
    public void TheSameVariantId_UnderTwoStrategies_CoexistsWithoutAnyCollision()
    {
        var kit = TwoStrategiesWithTheSameVariantId(out _, out _);
        var xa = new VariantKey("X", "A");
        var ya = new VariantKey("Y", "A");

        BarAt(kit, 9, 45, 1);

        Assert.Equal(2, kit.Engine.Variants.Variants.Count);
        Assert.NotSame(kit.Engine.Variants.GetVariant(xa), kit.Engine.Variants.GetVariant(ya));
        var px = Assert.Single(kit.Engine.Execution.PositionsOf(xa));
        var py = Assert.Single(kit.Engine.Execution.PositionsOf(ya));
        Assert.Equal(Ce, px.Contract.Token);                                     // X/A went long
        Assert.Equal(Pe, py.Contract.Token);                                     // Y/A went short
        Assert.NotEqual(px.Id, py.Id);                                           // no id collision
        Assert.NotEqual(kit.Out<EntryIntent>(xa).Single().IntentId, kit.Out<EntryIntent>(ya).Single().IntentId);
        Assert.NotEqual(kit.Engine.Variants.GetVariant(xa).VariantHash, kit.Engine.Variants.GetVariant(ya).VariantHash);
        Assert.Equal("X/A", kit.Engine.Variants.GetVariant(xa).Spec.ResolveAccountId());           // accounts do not collide either
        Assert.Equal("Y/A", kit.Engine.Variants.GetVariant(ya).Spec.ResolveAccountId());
        Assert.NotEqual(kit.Engine.Execution.BookLabel(xa), kit.Engine.Execution.BookLabel(ya));
        Assert.True(kit.Engine.Execution.CashOf(xa) != 0m);
        Assert.All(kit.Out<FillRecord>(xa), f => Assert.Equal("X", f.StrategyId));
        Assert.All(kit.Out<FillRecord>(ya), f => Assert.Equal("Y", f.StrategyId));
        Assert.Equal(new[] { "X/A", "Y/A" }, kit.Engine.DailySummaries(Clock.Ist(11, 0)).Select(s => $"{s.StrategyId}/{s.VariantId}"));
    }

    [Fact]
    public void AKillOfOneVariantIdentity_DoesNotTouchTheSameVariantIdUnderAnotherStrategy()
    {
        var kit = TwoStrategiesWithTheSameVariantId(out _, out _);

        BarAt(kit, 9, 45, 1, new OperatorCommandEvent(Clock.Ist(9, 44), 1, OperatorCommand.Kill, "X/A", "tester", "kill one identity"));

        Assert.Empty(kit.Engine.Execution.PositionsOf(new VariantKey("X", "A")));
        Assert.Single(kit.Engine.Execution.PositionsOf(new VariantKey("Y", "A")));
    }

    [Fact]
    public void AKillScopeThatIsNotACanonicalIdentity_IsRejected()
    {
        var kit = TwoStrategiesWithTheSameVariantId(out _, out _);

        kit.Feed(new OperatorCommandEvent(Clock.Ist(9, 44), 1, OperatorCommand.Kill, "A", "tester", "ambiguous bare id"));

        Assert.Equal("Rejected:UnknownVariant", kit.Engine.Outputs.OfType<NiftySignal.Trading.Core.Integrity.KillSwitchChanged>().Single().Effect);
        Assert.False(kit.Engine.Integrity.IsKilled(new VariantKey("X", "A")));
        Assert.False(kit.Engine.Integrity.IsKilled(new VariantKey("Y", "A")));
    }

    // ------------------------------------------------------------------ capacity is per variant

    [Fact]
    public void ASecondEntryOfAnOccupiedVariant_FollowsItsOwnCapacityPolicy_RejectOrWait()
    {
        static (TradeKit Kit, VariantKey Key) Run(CapacityBlockedPolicy policy)
        {
            var family = new PlumbingFamily("X", new Dictionary<string, PlumbingVariantScript> { ["X1"] = Script(new[] { (1L, 1L, StrategyDirection.Long), (3L, 4L, StrategyDirection.Long) }) });
            var kit = new TradeKit(Config(Spec("X", "X1", policy)), Families(family));
            BarAt(kit, 9, 45, 1);
            BarAt(kit, 9, 50, 2);
            BarAt(kit, 10, 0, 3);
            return (kit, X1);
        }

        var (rejecting, key) = Run(CapacityBlockedPolicy.Reject);
        Assert.Single(rejecting.Engine.Execution.PositionsOf(key));
        Assert.Contains(rejecting.Out<OpportunityTransition>(key), t => t.To == OpportunityState.Rejected && t.Reason == "CapacityBusy");

        var (waiting, _) = Run(CapacityBlockedPolicy.Wait);
        Assert.Single(waiting.Engine.Execution.PositionsOf(key));
        Assert.DoesNotContain(waiting.Out<OpportunityTransition>(key), t => t.To == OpportunityState.Rejected);
        Assert.Equal(OpportunityState.Waiting, waiting.Engine.Variants.GetVariant(key).Active!.State);   // keeps waiting until the slot frees
    }

    // ------------------------------------------------------------------ the family contract

    [Fact]
    public void OneFamily_CanAnswerDifferentlyForEachOfItsVariants_OnOneBar()
    {
        var family = new PlumbingFamily("X", new Dictionary<string, PlumbingVariantScript>
        {
            ["X1"] = Script(new[] { (7L, 7L, StrategyDirection.Long) }),
            ["X2"] = Script(Array.Empty<(long, long, StrategyDirection)>()),
            ["X3"] = Script(new[] { (7L, 7L, StrategyDirection.Short) }, 9),
        });

        var atSeven = family.OnBar(Ev.Bar(Clock.Ist(10, 0), 7)).Decisions.ToDictionary(d => d.VariantId);
        var atNine = family.OnBar(Ev.Bar(Clock.Ist(10, 0), 9)).Decisions.ToDictionary(d => d.VariantId);

        Assert.Equal((StrategyAction.Enter, StrategyDirection.Long), (atSeven["X1"].Action, atSeven["X1"].Direction));
        Assert.Equal((StrategyAction.Wait, StrategyDirection.Neutral), (atSeven["X2"].Action, atSeven["X2"].Direction));
        Assert.Equal((StrategyAction.Enter, StrategyDirection.Short), (atSeven["X3"].Action, atSeven["X3"].Direction));
        Assert.Equal(StrategyAction.Exit, atNine["X3"].Action);
        Assert.All(atSeven.Values, d => Assert.False(string.IsNullOrWhiteSpace(d.ReasonCode)));
    }

    [Theory]
    [InlineData(StrategyAction.Enter, StrategyDirection.Long, true)]
    [InlineData(StrategyAction.Enter, StrategyDirection.Short, true)]
    [InlineData(StrategyAction.Enter, StrategyDirection.Neutral, false)]
    [InlineData(StrategyAction.Hold, StrategyDirection.Neutral, true)]
    [InlineData(StrategyAction.Hold, StrategyDirection.Long, false)]
    [InlineData(StrategyAction.Exit, StrategyDirection.Neutral, true)]
    [InlineData(StrategyAction.Exit, StrategyDirection.Short, false)]
    [InlineData(StrategyAction.Wait, StrategyDirection.Neutral, true)]
    [InlineData(StrategyAction.Wait, StrategyDirection.Long, false)]
    public void ActionAndDirection_OnlyValidCombinationsAreAccepted(StrategyAction action, StrategyDirection direction, bool valid)
    {
        if (valid)
        {
            var decision = new VariantDecision("X1", action, direction, "why");
            Assert.Empty(decision.Validate());
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new VariantDecision("X1", action, direction, "why"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void EveryDecision_NeedsAReason_AndAValidVariantId(string reason)
    {
        Assert.Throws<ArgumentException>(() => VariantDecision.Enter("X1", StrategyDirection.Long, reason));
        Assert.Throws<ArgumentException>(() => VariantDecision.Wait("bad/id", "why"));
        Assert.Throws<ArgumentException>(() => VariantDecision.Hold(" ", "why"));
    }

    [Fact]
    public void AVariantTheFamilyDoesNotAnswerFor_IsTreatedAsWait_NeverAsHoldOrEnter()
    {
        var silent = new ScriptedFamily("X", new[] { "X1", "X2" }, seq => seq == 1 ? new[] { VariantDecision.Enter("X1", StrategyDirection.Long, "go") } : Array.Empty<VariantDecision>());
        var kit = new TradeKit(Config(Spec("X", "X1"), Spec("X", "X2")), Families(silent));

        BarAt(kit, 9, 45, 1);

        Assert.Single(kit.Engine.Execution.PositionsOf(X1));
        Assert.Empty(kit.Engine.Execution.PositionsOf(X2));
        Assert.Equal("NoDecision", kit.Engine.Variants.GetVariant(X2).LastDecision!.ReasonCode);
        Assert.Equal(StrategyAction.Wait, kit.Engine.Variants.GetVariant(X2).LastDecision!.Action);
    }

    [Fact]
    public void EveryDecision_IsAttributedToItsStrategyVariantAndSourceBar()
    {
        var kit = new TradeKit(Config(Spec("X", "X1"), Spec("X", "X2"), Spec("X", "X3")), Families(XFamily()));

        BarAt(kit, 9, 45, 1);

        var d1 = kit.Engine.Variants.GetVariant(X1).LastDecision!;
        Assert.Equal(X1, d1.Variant);
        Assert.Equal((StrategyAction.Enter, StrategyDirection.Long, "PlumbingWindow"), (d1.Action, d1.Direction, d1.ReasonCode));
        Assert.Equal(1, d1.BarSeq);
        Assert.Equal(Clock.Ist(9, 45), d1.BarAvailableAt);
        Assert.Equal("session:2026-09-22", d1.SourceSessionId);                  // the default source session label; the Host passes the Adaptive session id
        var opportunity = kit.Engine.Variants.AllOpportunities.Single(o => o.Variant == X1);
        Assert.Equal(d1.CanonicalText(), opportunity.EntryDecision!.CanonicalText());
        Assert.Equal(StrategyAction.Wait, kit.Engine.Variants.GetVariant(X2).LastDecision!.Action);
    }

    [Fact]
    public void TheEngine_FailsLoudly_OnADecisionForAnUndeclaredVariant_OrTwoDecisionsForOne()
    {
        var undeclared = new ScriptedFamily("X", new[] { "X1" }, _ => new[] { VariantDecision.Wait("ZZ", "why") });
        var kit = new TradeKit(Config(Spec("X", "X1")), Families(undeclared));
        Assert.Throws<InvalidOperationException>(() => BarAt(kit, 9, 45, 1));

        var twice = new ScriptedFamily("X", new[] { "X1" }, _ => new[] { VariantDecision.Wait("X1", "a"), VariantDecision.Wait("X1", "b") });
        var kit2 = new TradeKit(Config(Spec("X", "X1")), Families(twice));
        Assert.Throws<InvalidOperationException>(() => BarAt(kit2, 9, 45, 1));
    }

    [Fact]
    public void TheEngine_RefusesAConfigurationWhoseVariantTheFamilyDoesNotDeclare_OrWhoseFamilyIsMissing()
    {
        var family = new ScriptedFamily("X", new[] { "X1" }, _ => Array.Empty<VariantDecision>());

        Assert.Throws<ArgumentException>(() => new TradeKit(Config(Spec("X", "X9")), Families(family)));
        Assert.Throws<ArgumentException>(() => new TradeKit(Config(Spec("Q", "X1")), Families(family)));
    }

    // ------------------------------------------------------------------ the single-case compatibility path

    [Fact]
    public void ALegacySingleCaseStrategy_RunsThroughTheCanonicalFamilyPath()
    {
        var legacy = ScriptedStrategy.Valid("S", 1, null, OptionSide.Put);
        var family = new LegacyBaseStrategyFamily(legacy, new[] { "A", "B" });

        var decisions = family.OnBar(Ev.Bar(Clock.Ist(10, 0), 1)).Decisions;

        Assert.Equal(new[] { "A", "B" }, decisions.Select(d => d.VariantId));
        Assert.All(decisions, d => Assert.Equal((StrategyAction.Enter, StrategyDirection.Short), (d.Action, d.Direction)));
        Assert.Equal(StrategyAction.Wait, new LegacyBaseStrategyFamily(ScriptedStrategy.Valid("S", 5, null), new[] { "A" }).OnBar(Ev.Bar(Clock.Ist(10, 0), 1)).Decisions.Single().Action);

        // and through a whole engine, both variants of the one strategy see the same decision and trade independently
        var kit = new TradeKit(TradeKit.MakeConfig(variants: new[] { TradeKit.Immediate("A"), TradeKit.Immediate("B") }), new Dictionary<string, IStrategyFamily> { ["S"] = new LegacyBaseStrategyFamily(ScriptedStrategy.Valid("S", 1, null), new[] { "A", "B" }) });
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Loop.AdvanceTo(kit.At(1.5));
        Assert.Single(kit.Engine.Execution.PositionsOf(Vk.S("A")));
        Assert.Single(kit.Engine.Execution.PositionsOf(Vk.S("B")));
    }

    // ------------------------------------------------------------------ identity type and configuration

    [Fact]
    public void VariantKey_FormatsParsesCompares_AndRejectsAmbiguousParts()
    {
        var key = new VariantKey("X", "X1");

        Assert.Equal("X/X1", key.ToString());
        Assert.True(VariantKey.TryParse("X/X1", out var parsed));
        Assert.Equal(key, parsed);
        Assert.NotEqual(new VariantKey("X", "A"), new VariantKey("Y", "A"));
        Assert.True(new VariantKey("X", "Z").CompareTo(new VariantKey("Y", "A")) < 0);           // StrategyId first
        Assert.True(new VariantKey("X", "A").CompareTo(new VariantKey("X", "B")) < 0);
        Assert.Equal(new[] { "A/Z", "B/A", "B/B" }, new[] { new VariantKey("B", "B"), new VariantKey("A", "Z"), new VariantKey("B", "A") }.OrderBy(k => k).Select(k => k.ToString()));

        foreach (var bad in new[] { "", " ", "a/b", "a b", "-x", ".x", "a\"b", "é" })
        {
            Assert.False(VariantKey.IsValidPart(bad), bad);
            Assert.Throws<ArgumentException>(() => new VariantKey(bad, "A"));
            Assert.Throws<ArgumentException>(() => new VariantKey("X", bad));
        }

        foreach (var bad in new[] { "X", "X/", "/A", "X/A/B", "X//A", "X /A", null })
        {
            Assert.False(VariantKey.TryParse(bad, out _), bad);
        }

        Assert.True(VariantKey.TryParse("*/*", out var global));
        Assert.True(global.IsGlobal);
        Assert.False(new VariantKey("X", "A").IsGlobal);
    }

    [Fact]
    public void Configuration_KeysVariantsByTheirFullIdentity_AndValidatesIt()
    {
        var good = Config(Spec("X", "A"), Spec("Y", "A"));
        Assert.Empty(good.Validate());
        Assert.Equal(new[] { "X/A", "Y/A" }, good.Variants.Keys.Order(StringComparer.Ordinal));

        var wrongKey = good with { Variants = new Dictionary<string, VariantSpec> { ["A"] = Spec("X", "A") } };
        Assert.NotEmpty(wrongKey.Validate());                                                      // a bare VariantId is no longer a key

        var badId = good with { Variants = new Dictionary<string, VariantSpec> { ["X/a b"] = Spec("X", "a b") } };
        Assert.NotEmpty(badId.Validate());

        Assert.NotEqual(good.ComputeVariantHash(new VariantKey("X", "A")), good.ComputeVariantHash(new VariantKey("Y", "A")));
    }
}
