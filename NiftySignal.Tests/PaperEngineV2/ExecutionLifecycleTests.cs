using System.Text.Json.Nodes;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Decisions A2, A5, A6 and A7: reversal on the same signal, the Avg5 pending intent, the frozen partial target, protective flatten, session liquidation and replay determinism. Synthetic inputs only.</summary>
public partial class ExecutionLifecycleTests
{
    const string Policy = """
        { "enabled": true, "exitQuantityPercent": 50, "vixThreshold": 16, "maxVixAgeSeconds": 60, "freezeAtEntry": true, "missingVixPolicy": "DisablePartialTargetForPosition",
          "targets": [ { "dte": [6, 5], "belowVixPercent": 20, "atOrAboveVixPercent": 25 }, { "dte": [0], "belowVixPercent": 40, "atOrAboveVixPercent": 50 } ] }
        """;

    static CompiledStrategy Strategy(bool reverse = true, bool avg5 = false, bool partial = false, Action<JsonObject>? edit = null) => ExecFixtures.Strat(edit: o =>
    {
        if (reverse) o["exit"]!["onOppositeEntrySignal"] = "ExitThenEnterOpposite";
        if (avg5) o["entryTiming"] = "Avg5Pullback";
        if (partial) o["partialProfit"] = JsonNode.Parse(Policy);
        edit?.Invoke(o);
    });

    sealed class Sim(PaperEngine engine)
    {
        public PaperEngine Engine { get; } = engine;
        public List<ExecutionInput> Journal { get; } = [];
        MarketStateFrame? _previous;

        public bool Bar(int seq, double dir = 0, SafetyState? safety = null, ContractAverage[]? averages = null, bool entriesAllowed = true)
        {
            var frame = ExecFixtures.Fr(seq, dir);
            var input = ExecutionInput.FromFrame(frame, _previous, entriesAllowed ? ExecFixtures.Allowed : ExecFixtures.Blocked) with { Safety = safety ?? SafetyState.Healthy, Averages = averages ?? [] };
            _previous = frame;
            return Apply(input);
        }

        public bool Tick(DateTimeOffset now, SafetyState? safety = null, double? vix = null, ContractAverage[]? averages = null, params ExecFixtures.Q[] quotes)
        {
            var qs = quotes.Select((q, i) => new QuoteSnapshot(q.Token, q.Bid, q.Ask, q.Qty, q.Qty, now.AddMilliseconds(-q.AgeMs), 9_000 + i)).ToList();
            return Apply(new ExecutionInput(now, FrameFixtures.Day, 1, qs, vix is { } v ? new VixReading(v, now.AddSeconds(-1)) : null, averages ?? [], safety ?? SafetyState.Healthy));
        }

        bool Apply(ExecutionInput input)
        {
            var (changed, _) = Engine.Step(input);
            if (changed) Journal.Add(input);
            return changed;
        }

        public List<T> Of<T>() where T : PaperEvent => ExecFixtures.Of<T>(Engine);
        public StrategyStatus Status => Engine.Statuses().Single();
    }

    static Sim New(CompiledStrategy s) => new(ExecFixtures.Engine([s]));

    static DateTimeOffset At(int seq, double seconds = 1) => FrameFixtures.End(seq).AddSeconds(seconds);

    static ExecFixtures.Q Ce(double bid = 125, double ask = 126, int ageMs = 100) => new("CE26000", bid, ask, ageMs);
    static ExecFixtures.Q Pe(double bid = 125, double ask = 126, int ageMs = 100) => new("PE26000", bid, ask, ageMs);

    /// <summary>A held call position filled at ask 127 + 0.5 slippage.</summary>
    static Sim HoldingCall(CompiledStrategy s, double? vix = 14)
    {
        var sim = New(s);
        sim.Bar(5, dir: 10);
        sim.Tick(At(5), vix: vix, quotes: [Ce(125.5, 127)]);
        Assert.Single(sim.Of<EntryFilled>());
        return sim;
    }

    // ---- A5: reversal on the same signal ----

    [Fact]
    public void OppositeSignal_ExitsThenEntersOpposite_FromTheSameSignal_OnAQuoteNewerThanTheConfirmedExit()
    {
        var sim = HoldingCall(Strategy());
        sim.Bar(7, dir: -10);
        Assert.Equal(ExitReason.OppositeSignal, Assert.Single(sim.Of<ExitRequested>()).Reason);
        Assert.True(Assert.Single(sim.Of<EntryIntentCreated>()).AwaitingFlat);
        Assert.Single(sim.Of<EntryOrderPlaced>());                                           // nothing for the put while the call is not confirmed closed

        sim.Tick(At(7), quotes: [Ce(), Pe()]);
        Assert.Single(sim.Of<ExitFilled>());
        var put = sim.Of<EntryOrderPlaced>()[1];
        Assert.Equal((OptionRight.Put, At(7)), (put.Right, put.At));
        Assert.Single(sim.Of<EntryFilled>());                                                // the quote that existed at the exit confirmation can never fill the new order

        sim.Tick(At(7, 1.5), quotes: [Pe(ageMs: 600)]);                                       // available before the order decision
        Assert.Single(sim.Of<EntryFilled>());
        sim.Tick(At(7, 2), quotes: [Pe()]);
        Assert.Equal(2, sim.Of<EntryFilled>().Count);
        Assert.Single(sim.Engine.Runtimes.Single().Positions(), p => p.State == PositionState.Open && p.Right == OptionRight.Put);
    }

    [Fact]
    public void UnconfirmedExit_NeverOpensTheOpposite_AndItIsConsumedAtTheEntryCutoff()
    {
        var sim = HoldingCall(Strategy());
        sim.Bar(7, dir: -10);
        sim.Tick(At(7, 30), quotes: [Pe()]);                                                  // no call quote: the exit cannot be confirmed
        Assert.Empty(sim.Of<ExitFilled>());
        Assert.Single(sim.Of<EntryOrderPlaced>());
        sim.Tick(At(330), quotes: [Pe()]);                                                    // 15:00
        Assert.Single(sim.Of<EntryIntentCancelled>());
        sim.Tick(At(330, 5), quotes: [Ce(), Pe()]);
        Assert.Single(sim.Of<ExitFilled>());                                                  // the exit is not subject to the cut-off
        Assert.Single(sim.Of<EntryOrderPlaced>());
        Assert.False(sim.Engine.Runtimes.Single().HasPendingEntry);
    }

    // ---- A6: protective flatten and entry blocks ----

    [Fact]
    public void ProtectiveFlatten_ExitsOnFreshQuotes_ConsumesTheIntent_AndNeverReopens()
    {
        var sim = HoldingCall(Strategy());
        sim.Bar(7, dir: -10);                                                                  // exit pending with an opposite intent
        var halt = new SafetyState(Flatten: "observer heartbeat stale");
        sim.Tick(At(7, 40), safety: halt);
        Assert.Single(sim.Of<EntryIntentCancelled>());
        sim.Tick(At(7, 41), safety: halt, quotes: [Ce(), Pe()]);
        Assert.Single(sim.Of<ExitFilled>());
        sim.Tick(At(7, 50), quotes: [Pe()]);                                                   // healthy again: the consumed intent does not come back
        Assert.Single(sim.Of<EntryOrderPlaced>());
    }

    [Fact]
    public void ProtectiveFlatten_WithoutAnyBar_RequestsOneExit_AndInventsNoFill()
    {
        var sim = HoldingCall(Strategy());
        var halt = new SafetyState(Flatten: "observer failed");
        sim.Tick(At(6, 40), safety: halt);
        sim.Tick(At(6, 50), safety: halt);
        sim.Tick(At(6, 55), safety: halt, quotes: [Ce(ageMs: 9_000)]);                          // only a stale quote
        Assert.Equal(ExitReason.Protective, Assert.Single(sim.Of<ExitRequested>()).Reason);
        Assert.Empty(sim.Of<ExitFilled>());
        Assert.Equal(PositionState.ExitPending, sim.Engine.Runtimes.Single().Positions().Single().State);
    }

    [Fact]
    public void EntryBlock_NeverBlocksAnExit_AndNoOppositeIntentIsCreated()
    {
        var sim = HoldingCall(Strategy());
        var blocked = new SafetyState(EntryBlock: "observer rebuilding");
        sim.Bar(7, dir: -10, safety: blocked);
        sim.Tick(At(7), safety: blocked, quotes: [Ce(), Pe()]);
        Assert.Single(sim.Of<ExitFilled>());
        Assert.Empty(sim.Of<EntryIntentCreated>());
        Assert.Contains(sim.Of<EntryRejected>(), r => r.Reason == RejectionReason.EntriesBlocked);
    }

    [Fact]
    public void EntryBlock_AppearingBeforeTheFill_CancelsThePendingOrder()
    {
        var sim = New(Strategy());
        sim.Bar(5, dir: 10);
        sim.Tick(At(5), safety: new SafetyState(EntryBlock: "state unreconciled"), quotes: [Ce(125.5, 127)]);
        Assert.Empty(sim.Of<EntryFilled>());
        Assert.Equal(RejectionReason.EntriesBlocked, Assert.Single(sim.Of<EntryRejected>()).Reason);
    }

    // ---- A5: Avg5 ----

    static ContractAverage[] Average(double value, int bar = 5, string token = "CE26000") => [new(token, value, bar)];

    [Fact]
    public void Avg5_WaitsForTheReferenceMidStrictlyBelowItsAverage_BeyondTheOrderLifetime_AndRecordsEvidence()
    {
        var sim = New(Strategy(avg5: true));
        sim.Bar(5, dir: 10);
        var intent = Assert.Single(sim.Of<EntryIntentCreated>());
        Assert.Equal("CE26000", intent.ReferenceToken);
        sim.Tick(At(5), quotes: [Ce()]);                                                       // no average: wait
        sim.Tick(At(5, 2), averages: Average(125.5), quotes: [Ce()]);                           // mid 125.5 is not strictly below
        sim.Tick(At(5, 3), averages: Average(200, bar: 4), quotes: [Ce()]);                     // an average of an older bar is stale
        sim.Tick(At(5, 4), averages: Average(200), quotes: [Ce(ageMs: 9_000)]);                 // stale quote
        Assert.Empty(sim.Of<EntryOrderPlaced>());
        Assert.Empty(sim.Of<EntryRejected>());

        sim.Tick(At(5, 600), averages: Average(126), quotes: [Ce()]);                           // ten minutes later
        Assert.Single(sim.Of<EntryOrderPlaced>());
        sim.Tick(At(5, 601), averages: Average(126), quotes: [Ce()]);
        var evidence = Assert.Single(sim.Of<Avg5EntryEvidence>());
        Assert.Equal(("CE26000", 125.5, 126d, 5, "CE26000"), (evidence.ReferenceToken, evidence.ReferenceMid, evidence.Average, evidence.AverageBarSeq, evidence.ExecutionToken));
        Assert.Equal(601d, evidence.EntryDelaySeconds);                                           // signal instant to fill instant
        Assert.False(sim.Engine.Runtimes.Single().HasPendingEntry);
    }

    [Fact]
    public void Avg5_ConditionLostAtTheFill_KeepsWaiting_WithANewOrderIdentity()
    {
        var sim = New(Strategy(avg5: true));
        sim.Bar(5, dir: 10);
        sim.Tick(At(5), averages: Average(130), quotes: [Ce()]);
        sim.Tick(At(5, 2), averages: Average(120), quotes: [Ce()]);
        Assert.Equal(RejectionReason.Avg5NotMet, Assert.Single(sim.Of<EntryRejected>()).Reason);
        Assert.True(sim.Engine.Runtimes.Single().HasPendingEntry);
        sim.Tick(At(5, 3), averages: Average(130), quotes: [Ce()]);
        sim.Tick(At(5, 4), averages: Average(130), quotes: [Ce()]);
        var placed = sim.Of<EntryOrderPlaced>();
        Assert.Equal(2, placed.Select(p => p.IntentId).Distinct().Count());
        Assert.Single(sim.Of<EntryFilled>());
    }

    [Theory]
    [InlineData("opposite")] [InlineData("cutoff")] [InlineData("flatten")]
    public void Avg5_Intent_IsCancelled(string how)
    {
        var sim = New(Strategy(avg5: true));
        sim.Bar(5, dir: 10);
        if (how == "opposite") sim.Bar(6, dir: -10);
        else if (how == "cutoff") sim.Tick(At(330));
        else sim.Tick(At(5), safety: new SafetyState(Flatten: "observer failed"));
        Assert.Equal(OptionRight.Call, Assert.Single(sim.Of<EntryIntentCancelled>()).Right);
        sim.Tick(At(331), averages: Average(200, bar: how == "opposite" ? 6 : 5), quotes: [Ce()]);
        Assert.Empty(sim.Of<EntryOrderPlaced>());
        if (how == "opposite") Assert.Equal(OptionRight.Put, sim.Of<EntryIntentCreated>()[1].Right);      // the opposite signal has its own intent
    }

    [Fact]
    public void Avg5_RepeatedSignal_NeverDuplicatesTheIntent()
    {
        var sim = New(Strategy(avg5: true));
        sim.Bar(5, dir: 10);
        sim.Bar(6, dir: 10);
        Assert.Single(sim.Of<EntryIntentCreated>());
    }

    // ---- A7: partial profit ----

    [Fact]
    public void PartialTarget_IsFrozenAtEntry_FilledOnALaterQuote_AndAccountedOnce()
    {
        var sim = HoldingCall(Strategy(partial: true));
        var assigned = Assert.Single(sim.Of<PartialTargetAssigned>());
        Assert.Equal((6, 20d, 153.0m, 325), (assigned.Dte, assigned.TargetPercent!.Value, assigned.TargetPrice!.Value, assigned.Quantity));

        sim.Tick(At(6), vix: 30, quotes: [Ce(152.95, 154)]);                                    // below target; a later VIX changes nothing
        Assert.Empty(sim.Of<PartialExitRequested>());
        sim.Tick(At(6, 2), quotes: [Ce(153, 154)]);
        Assert.Single(sim.Of<PartialExitRequested>());
        Assert.Empty(sim.Of<PartialExitFilled>());                                              // never the triggering quote
        sim.Tick(At(6, 3), quotes: [Ce(154, 155)]);
        sim.Tick(At(6, 4), quotes: [Ce(160, 161)]);
        var partial = Assert.Single(sim.Of<PartialExitFilled>());
        Assert.Equal((325, 325, 153.5m), (partial.Quantity, partial.RemainingQuantity, partial.FillPrice));
        Assert.Single(sim.Of<PartialExitRequested>());

        sim.Bar(8, dir: -10);
        sim.Tick(At(8), quotes: [Ce(140, 141)]);
        var final = Assert.Single(sim.Of<ExitFilled>());
        Assert.Equal(325, final.Quantity);
        var position = sim.Engine.Runtimes.Single().Positions().Single();
        var entry = sim.Of<EntryFilled>().Single();
        Assert.Equal(((153.5m - 127.5m) * 325) + ((139.5m - 127.5m) * 325), final.GrossPnl);
        Assert.Equal(final.GrossPnl - entry.Fees - partial.Fees - final.Fees, final.NetPnl);
        Assert.Equal(final.NetPnl, sim.Engine.Runtimes.Single().RealizedToday);
        Assert.Equal(0, position.RemainingQuantity);
        Assert.Equal(1, sim.Status.TradesToday);
    }

    [Theory]
    [InlineData(null, "VIX")]
    [InlineData(15.0, null)]
    [InlineData(16.0, null)]
    public void PartialTarget_UsesTheVixBucket_OrDisablesWithAReason(double? vix, string? reason)
    {
        var sim = HoldingCall(Strategy(partial: true), vix);
        var a = Assert.Single(sim.Of<PartialTargetAssigned>());
        if (reason is null) Assert.Equal(vix >= 16 ? 25d : 20d, a.TargetPercent);
        else
        {
            Assert.Null(a.TargetPrice);
            Assert.Contains(reason, a.DisabledReason, StringComparison.Ordinal);
            sim.Tick(At(6), vix: 14, quotes: [Ce(400, 401)]);                                    // never assigned later
            Assert.Empty(sim.Of<PartialExitRequested>());
        }
    }

    [Fact]
    public void PartialTarget_UnmappedDte_DisablesIt()
    {
        var sim = HoldingCall(Strategy(partial: true, edit: o => o["partialProfit"]!["targets"] = JsonNode.Parse("""[ { "dte": [0], "belowVixPercent": 40, "atOrAboveVixPercent": 50 } ]""")));
        Assert.Contains("DTE 6", Assert.Single(sim.Of<PartialTargetAssigned>()).DisabledReason, StringComparison.Ordinal);
    }

    // ---- A2: session liquidation ----

    [Fact]
    public void Liquidation_SupersedesAPendingPartial_RunsWithoutBars_AndClosesTheWholeRemainder()
    {
        var sim = HoldingCall(Strategy(partial: true));
        sim.Tick(At(6), quotes: [Ce(160, 161)]);
        Assert.Single(sim.Of<PartialExitRequested>());
        sim.Tick(At(338));                                                                       // 15:08, no quote
        Assert.Equal(ExitReason.Terminal, Assert.Single(sim.Of<ExitRequested>()).Reason);
        sim.Tick(At(338, 5), quotes: [Ce(160, 161)]);
        Assert.Empty(sim.Of<PartialExitFilled>());
        Assert.Equal(650, Assert.Single(sim.Of<ExitFilled>()).Quantity);
        sim.Tick(At(340));
        Assert.Empty(sim.Of<LiquidationFailed>());
    }

    // ---- journal determinism ----

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReplayingOnlyTheStateChangingInputs_ReproducesTheLedgerAndPositions(bool avg5)
    {
        var strategy = Strategy(avg5: avg5, partial: true);
        var sim = New(strategy);
        var random = new Random(7);
        var skipped = 0;
        for (var bar = 1; bar <= 120; bar++)
        {
            sim.Bar(bar, dir: random.Next(4) switch { 0 => 10, 1 => -10, _ => 0 });
            for (var i = 1; i <= 8; i++)
            {
                var mid = 100 + (random.NextDouble() * 80);
                var quotes = random.Next(5) == 0 ? [] : new[] { Ce(mid, mid + 0.5, random.Next(10) == 0 ? 9_000 : 100), Pe(240 - mid, 240.5 - mid) };
                var safety = random.Next(200) == 0 ? new SafetyState(Flatten: "x") : random.Next(60) == 0 ? new SafetyState(EntryBlock: "y") : SafetyState.Healthy;
                if (!sim.Tick(At(bar, i * 5), safety, 14, [.. Average(140, bar), .. Average(140, bar, "PE26000")], quotes)) skipped++;
            }
        }

        Assert.True(skipped > 0 && sim.Of<EntryFilled>().Count > 0);
        var replay = ExecFixtures.Engine([strategy]);
        foreach (var input in sim.Journal) replay.Step(input);
        Assert.Equal(sim.Engine.Events.Select(e => e.ToString()), replay.Events.Select(e => e.ToString()));
        Assert.Equal(sim.Engine.Runtimes.Single().Positions(), replay.Runtimes.Single().Positions());
        Assert.Equal(sim.Status, replay.Statuses().Single());
        Assert.Equal(sim.Engine.Events.Count, sim.Engine.Events.Select(e => e.EventId).Distinct().Count());
    }
}
