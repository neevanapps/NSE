using System.Text.Json.Nodes;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Metrics;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Trading.Core.Costs;

namespace NiftySignal.Tests.PaperEngineV2;

sealed class FixedLots(int? lot) : ILotSizeSource
{
    public int? LotSize(DateOnly tradeDate, string token) => lot;
}

sealed class ListSink : IPaperEventSink
{
    public List<PaperEvent> Events { get; } = [];

    public void Append(PaperEvent e) => Events.Add(e);
}

sealed class ThrowingSink : IPaperEventSink
{
    public void Append(PaperEvent e) => throw new InvalidOperationException("sink down");
}

static class ExecFixtures
{
    public const string Fees = "npt1-frozen-fees-v1";
    public static readonly FrameDisposition Allowed = new(true, IntegrityFlags.None, 0, 10, true, false, false);
    public static readonly FrameDisposition Blocked = new(false, IntegrityFlags.SequenceGap, 0, 0, false, false, false);

    public static CostScheduleEntry Schedule() => NseIndexOptionsCostSchedules.Npt1FrozenV1(new FlatBrokerage { Unit = BrokerageUnit.PerFill, Amount = 0m });

    public static CostScheduleEntry OldSchedule() => Schedule() with { Version = "old-v", EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = new DateOnly(2026, 3, 31) };

    public static ExecutionOptions Options(int? lot = 65, TimeOnly? retryUntil = null) =>
        new(retryUntil ?? new TimeOnly(15, 35, 0), [Schedule(), OldSchedule()], new FixedLots(lot));

    public static CompiledStrategy Strat(string id = "S01", Action<JsonObject>? edit = null)
    {
        var json = ConfigTests.Mutate(o =>
        {
            o["strategyId"] = id;
            o["execution"]!["fillQuote"] = "AsOfEligibleBarEnd";
            o["fees"]!["schedule"] = Fees;
            o["exit"]!["conditions"] = JsonNode.Parse("""{ "op": "OR", "items": [ { "metric": "fut.strict:pct", "window": "5", "cmp": "<", "value": -50 } ] }""");
            edit?.Invoke(o);
        });
        var r = StrategyConfigParser.Parse(json);
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        return CompiledStrategy.Compile(r.Config!);
    }

    public sealed record Q(string Token, double Bid, double Ask, int AgeMs = 300, long Qty = 650);

    public static readonly Q[] Default = [new("CE26000", 125, 126), new("CE25950", 129, 130), new("PE26000", 125, 126), new("PE25950", 129, 130)];

    /// <summary>A frame for bar <paramref name="seq"/> (one minute per bar, 09:30 IST + seq). <paramref name="dir"/> drives the entry conditions (&gt; 5 buys a call, &lt; -5 a put), <paramref name="fut"/> &lt; -50 triggers the discretionary exit.</summary>
    public static MarketStateFrame Fr(int seq, double dir = 0, double fut = 1, IEnumerable<Q>? quotes = null, bool verified = true, DateTimeOffset? end = null, int ordinal = 0, long? tick = null, long session = 1, long qtick = 0)
    {
        var e = end ?? FrameFixtures.End(seq);
        var metrics = new List<MetricValue>
        {
            new(FrameFixtures.H("opt.atm.strict.dir:pct"), SummaryWindowId.W10, dir),
            new(FrameFixtures.H("fut.strict:pct"), SummaryWindowId.W5, fut),
            new(FrameFixtures.H("vix.fresh"), SummaryWindowId.W5, 1),
        };
        var qs = (quotes ?? Default).Select(q => new QuoteSnapshot(q.Token, q.Bid, q.Ask, q.Qty, q.Qty, e.AddMilliseconds(-q.AgeMs), (qtick == 0 ? 5_000 + seq : qtick))).ToList();
        return MarketStateFrame.Create(FrameFixtures.Identity(session, seq, e, tick, ordinal, verified), FrameFixtures.BasketId(), metrics, qs, [], new VixAvailability(e.AddSeconds(-2), 2, "Fresh"));
    }

    public static void Run(PaperEngine engine, IEnumerable<MarketStateFrame> frames, FrameDisposition? disposition = null)
    {
        MarketStateFrame? prev = null;
        foreach (var f in frames)
        {
            engine.ProcessAsync(f, prev, disposition ?? Allowed, default).AsTask().GetAwaiter().GetResult();
            prev = f;
        }
    }

    public static void Process(PaperEngine engine, MarketStateFrame frame, FrameDisposition disposition) => engine.ProcessAsync(frame, null, disposition, default).AsTask().GetAwaiter().GetResult();

    public static PaperEngine Engine(IEnumerable<CompiledStrategy> s, ExecutionOptions? o = null, IPaperEventSink? sink = null) => new(s, o ?? Options(), sink);

    public static List<T> Of<T>(PaperEngine e) where T : PaperEvent => e.Events.OfType<T>().ToList();
}

/// <summary>Phase 6c: the simulated paper execution - causality, selection, fills, exits, capital, risk, isolation, determinism. Pure and in memory.</summary>
public class ExecutionTests
{
    static readonly ExecFixtures.Q[] Calls = [new("CE26000", 125, 126)];

    // ---- entry, causality and fills ----

    [Fact]
    public void Signal_PlacesOrder_AndFillsOnLaterQuote_AtAskPlusSlippage_WithModelFees()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(4), ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)])]);
        var placed = Assert.Single(ExecFixtures.Of<EntryOrderPlaced>(e));
        Assert.Equal(("CE26000", 650, 126m, 5), (placed.Token, placed.Quantity, placed.SelectedAsk, placed.BarSeq));
        var fill = Assert.Single(ExecFixtures.Of<EntryFilled>(e));
        Assert.Equal(127.5m, fill.FillPrice);                                           // ask 127 + slippage 0.5
        Assert.Equal(650, fill.Quantity);
        Assert.Equal(650, fill.DisplayedQuantity);                                      // recorded only
        var expected = new CostModel(new CostsConfig { Schedules = [ExecFixtures.Schedule()] }, FrameFixtures.Day).Compute(TradeSide.Buy, 127.5m * 650).Total;
        Assert.Equal(expected, fill.Fees);
        Assert.True(fill.QuoteAvailableAt > placed.Decision.AvailableAt);
        Assert.Equal(placed.IntentId, fill.IntentId);
        Assert.Equal(6, fill.BarSeq);
    }

    [Fact]
    public void SameFrame_NeverFills_AndSiblingOfSameInstant_NeverFills()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        var d = ExecFixtures.Fr(5, dir: 10);
        var sibling = ExecFixtures.Fr(6, quotes: [new("CE26000", 130, 131, AgeMs: 0)], end: d.Identity.BarEndAvailableAt, tick: d.Identity.LastSourceTickId, ordinal: 1, qtick: 999);      // same market update, newest quote available AT the decision instant
        ExecFixtures.Run(e, [d, sibling]);
        Assert.Single(ExecFixtures.Of<EntryOrderPlaced>(e));
        Assert.Empty(ExecFixtures.Of<EntryFilled>(e));
        ExecFixtures.Run(e, [ExecFixtures.Fr(7, quotes: [new("CE26000", 125.5, 127)], end: d.Identity.BarEndAvailableAt.AddSeconds(30))]);
        Assert.Single(ExecFixtures.Of<EntryFilled>(e));
    }

    [Fact]
    public void Latency_DelaysTheFill_ToAQuoteBeyondDecisionPlusLatency()
    {
        var s = ExecFixtures.Strat(edit: o => o["execution"]!["latencyMs"] = 500);
        var e = ExecFixtures.Engine([s]);
        // fill frame ends 60 s after the decision; a quote 59.8 s old is available 200 ms after the decision: inside the latency
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125, 127, AgeMs: 59_800)])]);
        Assert.Empty(ExecFixtures.Of<EntryFilled>(e));
    }

    [Fact]
    public void StaleOrMissingQuote_NeverFills_AndTheOrderExpiresWithTheTypedReason()
    {
        var stale = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(stale, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125, 127, AgeMs: 2500)]), ExecFixtures.Fr(7, quotes: [new("CE26000", 125, 127, AgeMs: 2500)])]);
        Assert.Empty(ExecFixtures.Of<EntryFilled>(stale));
        Assert.Equal(RejectionReason.StaleQuote, Assert.Single(ExecFixtures.Of<EntryRejected>(stale)).Reason);

        var missing = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(missing, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: []), ExecFixtures.Fr(7, quotes: [])]);
        Assert.Equal(RejectionReason.MissingQuote, Assert.Single(ExecFixtures.Of<EntryRejected>(missing)).Reason);

        var late = ExecFixtures.Engine([ExecFixtures.Strat(edit: o => o["execution"]!["entryOrderLifetimeMs"] = 1000)]);
        ExecFixtures.Run(late, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125, 127)])]);          // the only newer quote is 59.7 s after the decision, beyond the 1 s lifetime
        Assert.Empty(ExecFixtures.Of<EntryFilled>(late));
        Assert.Equal(RejectionReason.ExpiredOpportunity, Assert.Single(ExecFixtures.Of<EntryRejected>(late)).Reason);
    }

    [Fact]
    public void EntriesBlocked_RejectsTheSignal_AndCancelsAPendingOrder()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Process(e, ExecFixtures.Fr(5, dir: 10), ExecFixtures.Blocked);
        Assert.Empty(ExecFixtures.Of<EntryOrderPlaced>(e));
        Assert.Equal(RejectionReason.EntriesBlocked, Assert.Single(ExecFixtures.Of<EntryRejected>(e)).Reason);

        var p = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(p, [ExecFixtures.Fr(5, dir: 10)]);
        ExecFixtures.Process(p, ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)]), ExecFixtures.Blocked);
        Assert.Empty(ExecFixtures.Of<EntryFilled>(p));                                  // blocked before the fill: fail closed, never filled later
        Assert.Contains(ExecFixtures.Of<EntryRejected>(p), r => r.Reason == RejectionReason.EntriesBlocked && r.IntentId is not null);
        ExecFixtures.Run(p, [ExecFixtures.Fr(7, quotes: [new("CE26000", 125.5, 127)])]);
        Assert.Empty(ExecFixtures.Of<EntryFilled>(p));
    }

    [Fact]
    public void UnverifiedFrame_NeverPlacesAnEntry()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10, verified: false)]);
        Assert.Empty(e.Events);
    }

    [Fact]
    public void EntryOutsideWindow_IsRejected()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat(edit: o => o["filters"]!["entryFrom"] = "10:00:00")]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10)]);                              // 09:35 IST
        Assert.Equal(RejectionReason.OutsideEntryWindow, Assert.Single(ExecFixtures.Of<EntryRejected>(e)).Reason);
    }

    // ---- instrument selection ----

    [Theory]
    [InlineData("CE26000:135,CE25950:118,CE26050:132", "CE25950")]       // 25950 and 26050 tie on proximity (7) and centre distance (50): the lower strike wins
    [InlineData("CE26100:118,CE25950:132,CE26000:140", "CE25950")]       // 26100 and 25950 tie on proximity: the nearer to the centre wins
    [InlineData("CE26000:150,CE25900:101,CE26100:149", "CE25900")]       // 150 is in range (inclusive bound) but farther (25); 101 and 149 tie (24) on proximity and centre distance: the lower strike wins
    [InlineData("CE26050:127,CE26000:123", "CE26000")]                    // equal proximity (2): the centre strike beats a farther one
    public void Selection_RanksByPremiumProximity_ThenCentreDistance_ThenStrike(string book, string expected)
    {
        var quotes = book.Split(',').Select(x => x.Split(':')).Select(p => new ExecFixtures.Q(p[0], double.Parse(p[1]) - 1, double.Parse(p[1]))).ToArray();
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10, quotes: quotes)]);
        Assert.Equal(expected, Assert.Single(ExecFixtures.Of<EntryOrderPlaced>(e)).Token);
    }

    [Fact]
    public void Selection_FailureReasons_AreTyped()
    {
        ExecFixtures.Q[] none = [];
        Assert.Equal(RejectionReason.MissingQuote, Reject(none));
        Assert.Equal(RejectionReason.StaleQuote, Reject([new("CE26000", 125, 126, AgeMs: 5000)]));
        Assert.Equal(RejectionReason.NoQualifyingContract, Reject([new("CE26000", 160, 161)]));
        Assert.Equal(RejectionReason.NoQualifyingContract, Reject([new("CE26000", 90, 99.5)]));
        Assert.Equal(RejectionReason.SpreadTooWide, Reject([new("CE26000", 110, 126)], o => o["execution"]!["maxSpread"] = new JsonObject { ["absolute"] = 2.0 }));
        Assert.Equal(RejectionReason.SpreadTooWide, Reject([new("CE26000", 100, 126)], o => o["execution"]!["maxSpread"] = new JsonObject { ["fraction"] = 0.05 }));

        static RejectionReason Reject(ExecFixtures.Q[] q, Action<JsonObject>? edit = null)
        {
            var e = ExecFixtures.Engine([ExecFixtures.Strat(edit: edit)]);
            ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10, quotes: q)]);
            return Assert.Single(ExecFixtures.Of<EntryRejected>(e)).Reason;
        }
    }

    [Fact]
    public void PutSignal_BuysAPut_NeverSellsACall()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: -10)]);
        var placed = Assert.Single(ExecFixtures.Of<EntryOrderPlaced>(e));
        Assert.Equal((OptionRight.Put, "PE26000"), (placed.Right, placed.Token));
    }

    [Fact]
    public void ConflictingSignals_BuyNothing()
    {
        var s = ExecFixtures.Strat(edit: o => o["entry"]!["buyPut"] = JsonNode.Parse("""{ "op": "AND", "items": [ { "metric": "opt.atm.strict.dir:pct", "window": "10", "cmp": ">", "value": 5 } ] }"""));
        var e = ExecFixtures.Engine([s]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10)]);
        Assert.Equal(RejectionReason.ConflictingSignals, Assert.Single(ExecFixtures.Of<EntryRejected>(e)).Reason);
        Assert.Empty(ExecFixtures.Of<EntryOrderPlaced>(e));
    }

    // ---- capital, lots, risk ----

    [Fact]
    public void LotSizeAndCapital_RejectOrders()
    {
        var noLot = ExecFixtures.Engine([ExecFixtures.Strat()], ExecFixtures.Options(lot: null));
        ExecFixtures.Run(noLot, [ExecFixtures.Fr(5, dir: 10)]);
        Assert.Equal(RejectionReason.LotSizeViolation, Assert.Single(ExecFixtures.Of<EntryRejected>(noLot)).Reason);

        var poor = ExecFixtures.Engine([ExecFixtures.Strat(edit: o => o["capital"]!["virtualCapital"] = 50_000)]);
        ExecFixtures.Run(poor, [ExecFixtures.Fr(5, dir: 10)]);                           // 650 x ~126.5 = ~82k > 50k
        Assert.Equal(RejectionReason.InsufficientCapital, Assert.Single(ExecFixtures.Of<EntryRejected>(poor)).Reason);
    }

    [Fact]
    public void Capital_IsCommittedAtEntry_AndReleasedWithProceedsAtExit()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        var rt = e.Runtimes.Single();
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)])]);
        var fill = ExecFixtures.Of<EntryFilled>(e).Single();
        Assert.Equal(350_000m - (127.5m * 650) - fill.Fees, rt.Cash);
        ExecFixtures.Run(e, [ExecFixtures.Fr(7, fut: -60, quotes: [new("CE26000", 130, 131)]), ExecFixtures.Fr(8, quotes: [new("CE26000", 130, 131)])]);
        var x = ExecFixtures.Of<ExitFilled>(e).Single();
        Assert.Equal(350_000m - (127.5m * 650) - fill.Fees + (x.FillPrice * 650) - x.Fees, rt.Cash);
    }

    [Fact]
    public void MaxConcurrentAndDailyTrades_AreEnforced()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)]), ExecFixtures.Fr(7, dir: 10, quotes: [new("CE26000", 125.5, 127)])]);
        Assert.Contains(ExecFixtures.Of<EntryRejected>(e), r => r.Reason == RejectionReason.PositionLimit);

        var one = ExecFixtures.Engine([ExecFixtures.Strat(edit: o => { o["risk"]!["maxDailyTrades"] = 1; o["position"]!["maxConcurrent"] = 3; })]);
        ExecFixtures.Run(one, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)]), ExecFixtures.Fr(7, dir: 10, quotes: [new("CE26000", 125.5, 127)])]);
        Assert.Contains(ExecFixtures.Of<EntryRejected>(one), r => r.Reason == RejectionReason.RiskBreach && r.Detail.Contains("daily trades", StringComparison.Ordinal));
        Assert.Single(ExecFixtures.Of<EntryFilled>(one));
    }

    // ---- exits ----

    [Fact]
    public void DiscretionaryExit_FillsAtBidMinusSlippage_WithPnlAndFees_AndNeedsNoEntryGate()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)]), ExecFixtures.Fr(7, fut: -60, quotes: [new("CE26000", 140, 141)])]);
        var req = Assert.Single(ExecFixtures.Of<ExitRequested>(e));
        Assert.Equal(ExitReason.Discretionary, req.Reason);
        Assert.Empty(ExecFixtures.Of<ExitFilled>(e));                                    // not on the decision frame
        ExecFixtures.Process(e, ExecFixtures.Fr(8, quotes: [new("CE26000", 141, 142)]), ExecFixtures.Blocked);          // entries blocked: exits still fill
        var x = Assert.Single(ExecFixtures.Of<ExitFilled>(e));
        Assert.Equal(140.5m, x.FillPrice);
        var model = new CostModel(new CostsConfig { Schedules = [ExecFixtures.Schedule()] }, FrameFixtures.Day);
        var entryFees = model.Compute(TradeSide.Buy, 127.5m * 650).Total;
        var exitFees = model.Compute(TradeSide.Sell, 140.5m * 650).Total;
        Assert.Equal(exitFees, x.Fees);
        Assert.Equal((140.5m - 127.5m) * 650, x.GrossPnl);
        Assert.Equal(x.GrossPnl - entryFees - exitFees, x.NetPnl);
        Assert.Equal(PositionState.Closed, e.Runtimes.Single().Positions().Single().State);
    }

    [Fact]
    public void OppositeSignal_ExitsOnly_NoReversal()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127), new("PE26000", 125, 126)]), ExecFixtures.Fr(7, dir: -10, quotes: [new("CE26000", 130, 131), new("PE26000", 125, 126)])]);
        Assert.Equal(ExitReason.OppositeSignal, Assert.Single(ExecFixtures.Of<ExitRequested>(e)).Reason);
        Assert.Single(ExecFixtures.Of<EntryOrderPlaced>(e));                              // no put order was placed on the opposite-signal bar
    }

    [Theory]
    [InlineData("stop", ExitReason.StopLoss)]
    [InlineData("target", ExitReason.Target)]
    [InlineData("trail", ExitReason.Trailing)]
    [InlineData("hold", ExitReason.MaxHold)]
    public void OptionalExits_RequestOnTheBarThatBreachesThem_AndFillLater(string kind, ExitReason expected)
    {
        var s = ExecFixtures.Strat(edit: o =>
        {
            o["exit"]!["conditions"] = JsonNode.Parse("""{ "op": "OR", "items": [ { "metric": "fut.strict:pct", "window": "5", "cmp": "<", "value": -500 } ] }""");
            o["exits"] = kind switch
            {
                "stop" => JsonNode.Parse("""{ "stopLoss": { "type": "Percent", "value": 10 } }"""),
                "target" => JsonNode.Parse("""{ "target": { "type": "Points", "value": 20 } }"""),
                "trail" => JsonNode.Parse("""{ "trailing": { "type": "Points", "value": 10 } }"""),
                _ => JsonNode.Parse("""{ "maxHoldBars": 3 }"""),
            };
        });
        var e = ExecFixtures.Engine([s]);
        var flat = new ExecFixtures.Q("CE26000", 127, 128);
        List<MarketStateFrame> frames = [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 126.5, 127)])];      // entry fill at 127.5
        switch (kind)
        {
            case "stop": frames.Add(ExecFixtures.Fr(7, quotes: [new("CE26000", 114, 115)])); break;                               // 114 <= 127.5 - 12.75
            case "target": frames.Add(ExecFixtures.Fr(7, quotes: [new("CE26000", 147.5, 148)])); break;                           // 147.5 >= 127.5 + 20
            case "trail": frames.Add(ExecFixtures.Fr(7, quotes: [new("CE26000", 140, 141)])); frames.Add(ExecFixtures.Fr(8, quotes: [new("CE26000", 129.9, 131)])); break;    // peak 140 - 129.9 >= 10
            default: frames.Add(ExecFixtures.Fr(7, quotes: [flat])); frames.Add(ExecFixtures.Fr(8, quotes: [flat])); frames.Add(ExecFixtures.Fr(9, quotes: [flat])); break;      // 3 bars held
        }

        ExecFixtures.Run(e, frames);
        var req = Assert.Single(ExecFixtures.Of<ExitRequested>(e));
        Assert.Equal(expected, req.Reason);
        Assert.Empty(ExecFixtures.Of<ExitFilled>(e));
        ExecFixtures.Run(e, [ExecFixtures.Fr(20, quotes: [new("CE26000", 120, 121)])]);
        var x = Assert.Single(ExecFixtures.Of<ExitFilled>(e));
        Assert.Equal(119.5m, x.FillPrice);                                                // the fill is the later quote's bid - slippage, never the threshold price
    }

    [Fact]
    public void OptionalExit_NeedsAUsableMark()
    {
        var s = ExecFixtures.Strat(edit: o => o["exits"] = JsonNode.Parse("""{ "stopLoss": { "type": "Percent", "value": 10 } }"""));
        var e = ExecFixtures.Engine([s]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 126.5, 127)]), ExecFixtures.Fr(7, quotes: [new("CE26000", 50, 51, AgeMs: 9000)]), ExecFixtures.Fr(8, quotes: [])]);
        Assert.Empty(ExecFixtures.Of<ExitRequested>(e));                                 // stale / missing marks never trigger a stop
        Assert.Equal(2, e.Runtimes.Single().Positions().Single().MarkGaps);
    }

    [Fact]
    public void MfeAndMae_TrackTheBidMarks()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 126.5, 127)]), ExecFixtures.Fr(7, quotes: [new("CE26000", 137.5, 139)]), ExecFixtures.Fr(8, quotes: [new("CE26000", 120.5, 122)])]);
        var p = e.Runtimes.Single().Positions().Single();
        Assert.Equal(10m * 650, p.Mfe);
        Assert.Equal(-7m * 650, p.Mae);
    }

    // ---- terminal handling ----

    [Fact]
    public void Terminal_ExitsAt1500_RetriesUntilCutoff_ThenReportsUnresolved()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 126.5, 127)])]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(330, quotes: [])]);                           // 15:00:00 IST
        Assert.Equal(ExitReason.Terminal, Assert.Single(ExecFixtures.Of<ExitRequested>(e)).Reason);
        ExecFixtures.Run(e, [ExecFixtures.Fr(340, quotes: [new("CE26000", 100, 101, AgeMs: 5000)])]);      // only a stale quote
        Assert.Empty(ExecFixtures.Of<PositionUnresolved>(e));
        ExecFixtures.Run(e, [ExecFixtures.Fr(366, quotes: [])]);                           // 15:36 IST > the 15:35 cut-off
        Assert.Single(ExecFixtures.Of<PositionUnresolved>(e));
        Assert.Equal(PositionState.Unresolved, e.Runtimes.Single().Positions().Single().State);
        Assert.Empty(ExecFixtures.Of<ExitFilled>(e));
    }

    [Fact]
    public void Terminal_ExitFills_WhenAQuoteArrivesBeforeTheCutoff()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 126.5, 127)]), ExecFixtures.Fr(330, quotes: []), ExecFixtures.Fr(331, quotes: [new("CE26000", 120, 121)])]);
        Assert.Equal(ExitReason.Terminal, Assert.Single(ExecFixtures.Of<ExitFilled>(e)).Reason);
    }

    [Fact]
    public void PendingEntry_IsCancelledAtTheTerminalTime_NeverFilledAfter1500()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat(edit: o => o["execution"]!["entryOrderLifetimeMs"] = 600_000)]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(329, dir: 10, quotes: Calls), ExecFixtures.Fr(330, quotes: [new("CE26000", 125.5, 127)])]);
        Assert.Empty(ExecFixtures.Of<EntryFilled>(e));
        Assert.Contains(ExecFixtures.Of<EntryRejected>(e), r => r.Reason == RejectionReason.OutsideEntryWindow);
    }

    [Fact]
    public void NewTradingDay_ClosesTheBooks_OpenPositionsBecomeUnresolved()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 126.5, 127)])]);
        var nextDay = MarketStateFrame.Create(FrameFixtures.Identity(2, 1) with { TradeDate = FrameFixtures.Day.AddDays(1), BarStartAvailableAt = FrameFixtures.End(1).AddDays(1).AddSeconds(-60), BarEndAvailableAt = FrameFixtures.End(1).AddDays(1) },
            FrameFixtures.BasketId(), [], [], [], new VixAvailability(null, null, "Missing"));
        ExecFixtures.Process(e, nextDay, ExecFixtures.Allowed);
        Assert.Equal(PositionState.Unresolved, e.Runtimes.Single().Positions().Single().State);
    }

    // ---- risk breach ----

    [Fact]
    public void DailyLossBreach_BlocksEntries_AndLiquidatesWhenConfigured()
    {
        var s = ExecFixtures.Strat(edit: o =>
        {
            o["risk"]!["maxDailyLoss"] = 1000; o["risk"]!["onBreach"] = JsonNode.Parse("""{ "blockEntries": true, "liquidation": "ExitOpenPositions" }""");
            o["position"]!["maxConcurrent"] = 2;
            o["exits"] = JsonNode.Parse("""{ "stopLoss": { "type": "Points", "value": 5 } }""");
        });
        var e = ExecFixtures.Engine([s]);
        ExecFixtures.Run(e,
        [
            ExecFixtures.Fr(5, dir: 10, quotes: [new("CE26000", 125, 126)]),
            ExecFixtures.Fr(6, quotes: [new("CE26000", 126.5, 127)]),                                                       // A fills on CE26000
            ExecFixtures.Fr(7, dir: 10, quotes: [new("CE26000", 126.5, 160), new("CE25950", 129, 130)]),                  // B decided on CE25950 (CE26000 out of range)
            ExecFixtures.Fr(8, quotes: [new("CE26000", 126.5, 127), new("CE25950", 129, 130)]),                           // B fills at 130.5
            ExecFixtures.Fr(9, quotes: [new("CE26000", 100, 101), new("CE25950", 129, 130)]),                             // A breaches its stop
            ExecFixtures.Fr(10, quotes: [new("CE26000", 100, 101), new("CE25950", 129, 130)]),                            // A exits at 99.5: a big loss
            ExecFixtures.Fr(11, quotes: [new("CE26000", 100, 101), new("CE25950", 129.5, 131)]),                          // breach seen; B liquidated
            ExecFixtures.Fr(12, quotes: [new("CE26000", 100, 101), new("CE25950", 129.5, 131)]),
        ]);
        Assert.Single(ExecFixtures.Of<RiskBreached>(e));
        Assert.Contains(ExecFixtures.Of<ExitRequested>(e), r => r.Reason == ExitReason.RiskLiquidation);
        Assert.Equal(2, ExecFixtures.Of<ExitFilled>(e).Count);
        ExecFixtures.Run(e, [ExecFixtures.Fr(13, dir: 10, quotes: Calls)]);
        Assert.Contains(ExecFixtures.Of<EntryRejected>(e), r => r.Reason == RejectionReason.RiskBreach);
    }

    // ---- activation, isolation, determinism ----

    [Fact]
    public void Activation_FailsClosed_ForUnsupportedFillRuleAndMissingSchedule()
    {
        var firstAfter = ExecFixtures.Strat("S01", o => o["execution"]!["fillQuote"] = "FirstAfterDecision");
        var noSchedule = ExecFixtures.Strat("S02", o => o["fees"]!["schedule"] = "no-such-schedule");
        var ok = ExecFixtures.Strat("S03");
        var e = ExecFixtures.Engine([firstAfter, noSchedule, ok]);
        Assert.Equal([("S01", ActivationFailureKind.UnsupportedFillQuoteRule), ("S02", ActivationFailureKind.MissingCostSchedule)], e.ActivationFailures.Select(f => (f.StrategyId, f.Kind)).ToArray());
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10)]);
        Assert.Equal(["S03"], e.Events.Select(x => x.StrategyId).Distinct().ToArray());
    }

    [Fact]
    public void DisabledStrategies_NeverRun()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat(edit: o => o["enabled"] = false)]);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10)]);
        Assert.Empty(e.Runtimes);
        Assert.Empty(e.Events);
    }

    [Fact]
    public void OptionsRequireExplicitValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionOptions(new TimeOnly(15, 0, 0), [ExecFixtures.Schedule()], new FixedLots(65)));
        Assert.Throws<ArgumentException>(() => new ExecutionOptions(new TimeOnly(15, 30, 0), [], new FixedLots(65)));
    }

    [Fact]
    public void FaultInOneStrategy_IsContained_OthersAndTheSinkSurvive()
    {
        var bad = ExecFixtures.Strat("S01", o => o["fees"]!["schedule"] = "old-v");        // schedule exists but is not effective on the trade date: the strategy faults
        var good = ExecFixtures.Strat("S02");
        var sink = new ListSink();
        var e = ExecFixtures.Engine([bad, good], sink: sink);
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)])]);
        Assert.Single(ExecFixtures.Of<StrategyFaulted>(e));
        Assert.Equal("S01", ExecFixtures.Of<StrategyFaulted>(e).Single().StrategyId);
        Assert.Single(ExecFixtures.Of<EntryFilled>(e));
        Assert.Equal("S02", ExecFixtures.Of<EntryFilled>(e).Single().StrategyId);
        Assert.Equal(e.Events, sink.Events);

        var broken = ExecFixtures.Engine([ExecFixtures.Strat()], sink: new ThrowingSink());
        ExecFixtures.Run(broken, [ExecFixtures.Fr(5, dir: 10)]);
        Assert.Single(ExecFixtures.Of<EntryOrderPlaced>(broken));                      // a failing sink never reaches the strategies
        Assert.Equal(1, broken.SinkFailures);
    }

    [Fact]
    public void SameFrameTwice_IsIdempotent()
    {
        var e = ExecFixtures.Engine([ExecFixtures.Strat()]);
        var f = ExecFixtures.Fr(5, dir: 10);
        ExecFixtures.Run(e, [f, f, f]);
        Assert.Single(e.Events);
    }

    internal static List<MarketStateFrame> Day()
    {
        var frames = new List<MarketStateFrame>();
        for (var i = 1; i <= 60; i++)
        {
            var dir = i % 9 == 5 ? 10 : i % 9 == 8 ? -10 : 0;
            frames.Add(ExecFixtures.Fr(i, dir: dir, fut: i % 13 == 0 ? -60 : 1, quotes: [new("CE26000", 120 + i % 7, 123 + i % 7), new("CE25950", 125 + i % 5, 128 + i % 5), new("PE26000", 119 + i % 6, 122 + i % 6), new("PE25950", 124, 127)]));
        }

        return frames;
    }

    [Fact]
    public void ThirtyStrategies_AreIsolated_AndOrderIndependent()
    {
        CompiledStrategy[] All() => Enumerable.Range(1, 30).Select(i => ExecFixtures.Strat($"S{i:00}", o =>
        {
            o["instrument"]!["premium"] = new JsonObject { ["min"] = 100 + (i % 5), ["max"] = 150 };
            o["quantity"]!["lots"] = 1 + i % 4;
            o["position"]!["maxConcurrent"] = 1 + i % 3;
        })).ToArray();

        var all = All();
        var together = ExecFixtures.Engine(all);
        ExecFixtures.Run(together, Day());
        Assert.NotEmpty(together.Events);

        var shuffled = ExecFixtures.Engine(all.OrderByDescending(s => s.Config.Identity.StrategyId, StringComparer.Ordinal));
        ExecFixtures.Run(shuffled, Day());
        Assert.Equal(together.Events, shuffled.Events);                                  // registration order never matters

        foreach (var s in all.Where((_, i) => i % 7 == 0))
        {
            var alone = ExecFixtures.Engine([s]);
            ExecFixtures.Run(alone, Day());
            Assert.Equal(alone.Events, together.Events.Where(x => x.StrategyId == s.Config.Identity.StrategyId).ToList());     // no strategy influences another
        }

        Assert.Equal(together.Events.Count, together.Events.Select(x => x.EventId).Distinct().Count());                          // event ids are unique and deterministic
    }

    [Fact]
    public async Task LiveRunAndJournalReplay_ProduceIdenticalLedgers()
    {
        var journal = new InMemoryFrameJournal();
        var live = ExecFixtures.Engine([ExecFixtures.Strat("S01"), ExecFixtures.Strat("S02", o => o["position"]!["maxConcurrent"] = 2)]);
        var clock = new ManualClock(FrameFixtures.End(1));
        var consumer = new FrameConsumer(null, new HandoffOptions(16, TimeSpan.FromHours(1), 1), journal, live, clock);
        foreach (var f in Day()) { clock.Now = f.Identity.BarEndAvailableAt.AddMilliseconds(50); await consumer.HandleAsync(f, default); }
        Assert.NotEmpty(live.Events);

        var replayed = ExecFixtures.Engine([ExecFixtures.Strat("S01"), ExecFixtures.Strat("S02", o => o["position"]!["maxConcurrent"] = 2)]);
        await FrameReplayer.ReplayAsync(journal, 1, replayed, default);
        Assert.Equal(live.Events, replayed.Events);
    }

    [Fact]
    public void Costs_AreNeverAssumedZero()
    {
        var zeroed = ExecFixtures.Engine([ExecFixtures.Strat()]);
        ExecFixtures.Run(zeroed, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)])]);
        Assert.True(ExecFixtures.Of<EntryFilled>(zeroed).Single().Fees > 0m);
    }

    // ---- release blockers ----

    [Fact]
    public void ExecutionAssembly_HasNoBrokerLiveOrNetworkReferences_AndNoSellToOpenVocabulary()
    {
        var asm = typeof(PaperEngine).Assembly;
        var refs = asm.GetReferencedAssemblies().Select(a => a.Name!).Where(n => !n.StartsWith("System", StringComparison.Ordinal) && n != "mscorlib" && n != "netstandard").OrderBy(n => n).ToArray();
        Assert.Equal(["NiftySignal.PaperEngineV2.Core", "NiftySignal.Trading.Core"], refs);
        var banned = new[] { "Broker", "Live", "Sell", "Short", "Write", "Http", "Telegram", "Flattrade" };
        foreach (var t in asm.GetTypes())
        {
            Assert.DoesNotContain(banned, b => t.Name.Contains(b, StringComparison.OrdinalIgnoreCase));
            foreach (var m in t.GetMembers().Where(m => m.DeclaringType == t))
                Assert.DoesNotContain(banned, b => m.Name.Contains(b, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(["Call", "Put"], Enum.GetNames<OptionRight>());
    }
}
