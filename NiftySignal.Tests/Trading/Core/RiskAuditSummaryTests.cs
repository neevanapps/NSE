using System.Text.Json;
using System.Text.Json.Serialization;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Reporting;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Sizing;
using NiftySignal.Trading.Core.Trading;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

public class RiskProfileTests
{
    private static readonly RiskState Extreme = new(TradesToday: 999, DailyPnl: -1_000_000_000m, DailySlConfigured: true, DailySlBreached: true, CommittedPremium: 1_000_000_000m, NewPremium: 1_000_000_000m);

    [Fact]
    public void ResearchV1_IsTheDefault_AndEnforcesNothing()
    {
        var profile = RiskProfile.ResearchV1();

        Assert.Equal("ResearchV1", profile.Name);
        Assert.Null(profile.MaxTradesPerDay);
        Assert.Equal(DailySlEnforcement.None, profile.DailySlEnforcement);
        Assert.Null(profile.MaxPeakPremiumExposure);
        Assert.Empty(profile.Validate());
        Assert.Empty(profile.StartBlockers());
        Assert.Equal(profile, TradeKit.MakeConfig(variants: TradeKit.Immediate()).Risk);
        Assert.True(RiskPolicy.CheckEntry(profile, Extreme).Allowed);                        // whatever the state: no cap, no lockout, no exposure limit
        Assert.False(RiskPolicy.ShouldLiquidate(profile, Extreme));
    }

    [Fact]
    public void ResearchV1_RejectsEnforcementSettings_ThatBelongToTheRealOrderProfile()
    {
        Assert.NotEmpty((RiskProfile.ResearchV1() with { MaxTradesPerDay = 5 }).Validate());
        Assert.NotEmpty((RiskProfile.ResearchV1() with { DailySlEnforcement = DailySlEnforcement.BlockEntries }).Validate());
        Assert.NotEmpty((RiskProfile.ResearchV1() with { MaxPeakPremiumExposure = 100000m }).Validate());
        Assert.NotEmpty((RiskProfile.ResearchV1() with { Name = "Mystery" }).Validate());
        Assert.Empty((new RiskProfile { Name = RiskProfile.RealOrderName, MaxTradesPerDay = 5, DailySlEnforcement = DailySlEnforcement.BlockEntries, MaxPeakPremiumExposure = 100000m }).Validate());
        Assert.NotEmpty((new RiskProfile { Name = RiskProfile.RealOrderName, MaxTradesPerDay = 0 }).Validate());
    }

    [Fact]
    public void RealOrderProfile_IsUnstartable_NoEngineRunsWithIt()
    {
        var real = TradeKit.MakeConfig(variants: TradeKit.Immediate()) with { Risk = new RiskProfile { Name = RiskProfile.RealOrderName } };

        Assert.Empty(real.Risk.Validate());                                                  // configurable ...
        var error = Assert.Throws<ArgumentException>(() => new TradeKit(real));              // ... but never runnable
        Assert.Contains("not startable", error.Message);
    }

    [Fact]
    public void FutureEnforcementModes_AreConfigurableAndUnitTested_ThroughThePurePolicy()
    {
        var state = new RiskState(TradesToday: 5, DailyPnl: -500m, DailySlConfigured: true, DailySlBreached: false, CommittedPremium: 50000m, NewPremium: 30000m);
        RiskProfile Real(int? cap = null, DailySlEnforcement sl = DailySlEnforcement.None, decimal? exposure = null)
            => new() { Name = RiskProfile.RealOrderName, MaxTradesPerDay = cap, DailySlEnforcement = sl, MaxPeakPremiumExposure = exposure };

        Assert.Equal("RiskMaxTrades", RiskPolicy.CheckEntry(Real(cap: 5), state).ReasonCode);
        Assert.True(RiskPolicy.CheckEntry(Real(cap: 6), state).Allowed);
        Assert.Equal("RiskExposureLimit", RiskPolicy.CheckEntry(Real(exposure: 70000m), state).ReasonCode);
        Assert.True(RiskPolicy.CheckEntry(Real(exposure: 80000m), state).Allowed);

        var breached = state with { DailySlBreached = true };
        Assert.True(RiskPolicy.CheckEntry(Real(sl: DailySlEnforcement.None), breached).Allowed);
        Assert.Equal("RiskDailySlBreached", RiskPolicy.CheckEntry(Real(sl: DailySlEnforcement.BlockEntries), breached).ReasonCode);
        Assert.False(RiskPolicy.ShouldLiquidate(Real(sl: DailySlEnforcement.BlockEntries), breached));
        Assert.True(RiskPolicy.ShouldLiquidate(Real(sl: DailySlEnforcement.BlockEntriesAndLiquidate), breached));
        Assert.True(RiskPolicy.CheckEntry(Real(sl: DailySlEnforcement.BlockEntries), breached with { DailySlConfigured = false }).Allowed);
    }

    [Fact]
    public void TheProfile_IsPartOfTheConfigurationHash()
    {
        var baseline = TradeKit.MakeConfig(variants: TradeKit.Immediate());

        Assert.Contains("\"Risk\":{\"DailySlEnforcement\":\"None\",\"Families\":{},\"Global\":null,\"GlobalCapital\":null,\"MaxPeakPremiumExposure\":null,\"MaxTradesPerDay\":null,\"Name\":\"ResearchV1\",\"Version\":\"1\"}", baseline.ToCanonicalJson());
        Assert.NotEqual(baseline.ComputeHash(), (baseline with { Risk = RiskProfile.ResearchV1() with { Version = "2" } }).ComputeHash());
        Assert.NotEqual(baseline.ComputeVariantHash(Vk.S("A")), (baseline with { Risk = RiskProfile.ResearchV1() with { Version = "2" } }).ComputeVariantHash(Vk.S("A")));
    }

    [Fact]
    public void PerTradeSl_IsIndependentOfTheDailySl()
    {
        // Per-trade SL only: the stop fires, no daily-SL observation exists.
        var tradeOnly = TradeKit.Opened(TradeKit.Immediate(exit: new ExitSpec { TradeSlPct = 0.10m }));
        tradeOnly.Feed(tradeOnly.Q(tradeOnly.At(2), 100m, 101m));
        Assert.Equal(ExitReason.TradeSl, Assert.Single(tradeOnly.Out<ExitOrderCreated>("A")).Reason);
        Assert.Empty(tradeOnly.Out<DailySlBreached>("A"));

        // Daily SL only: the breach is recorded, nothing is exited or blocked.
        var dailyOnly = TradeKit.Opened(TradeKit.Immediate(dailySl: 1m));
        dailyOnly.Feed(dailyOnly.Q(dailyOnly.At(2), 100m, 101m));
        dailyOnly.Loop.AdvanceTo(dailyOnly.At(10));
        Assert.Single(dailyOnly.Out<DailySlBreached>("A"));
        Assert.Empty(dailyOnly.Out<ExitOrderCreated>("A"));
        Assert.Equal(PositionState.Open, dailyOnly.Position().State);

        // Both: each acts by its own rule, and they are reported as different things.
        var both = TradeKit.Opened(TradeKit.Immediate(exit: new ExitSpec { TradeSlPct = 0.10m }, dailySl: 1m));
        both.Feed(both.Q(both.At(2), 100m, 101m));
        Assert.Single(both.Out<DailySlBreached>("A"));
        Assert.Equal(ExitReason.TradeSl, Assert.Single(both.Out<ExitOrderCreated>("A")).Reason);
    }
}

public class AuditTests
{
    private static TradeKit GoldenKit()
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate(exit: new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m, FinalTpPct = 0.20m })));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Loop.AdvanceTo(kit.At(1.5));
        kit.Feed(kit.Q(kit.At(120), 138m, 139m));
        kit.Feed(kit.Q(kit.At(180), 151m, 152m));
        kit.Loop.AdvanceTo(kit.At(200));
        return kit;
    }

    [Fact]
    public void EveryOutput_AppearsOnceInTheAuditTrail_WithIdsConfigHashAndReasonCodes()
    {
        var kit = GoldenKit();
        var trail = kit.Engine.AuditTrailEntries();

        Assert.Equal(kit.Engine.Outputs.Count, trail.Count);
        Assert.Equal(Enumerable.Range(0, trail.Count).Select(i => (long)i), trail.Select(e => e.Seq));
        Assert.All(trail, e =>
        {
            Assert.Equal("run-unspecified", e.RunId);
            Assert.Equal(kit.Engine.Variants.ConfigHash.Value, e.ConfigHash);
            Assert.Equal("S/A", e.AccountId);
            Assert.Equal("S", e.StrategyId);
            Assert.Equal("A", e.VariantId);
            Assert.Equal(kit.Engine.Variants.GetVariant(Vk.S("A")).VariantHash, e.VariantHash);
        });
        Assert.DoesNotContain(trail, e => e.Reason == ReasonCode.Other);
        Assert.All(trail.Where(e => e.Category == AuditCategory.Fill), e =>
        {
            Assert.NotNull(e.OrderId);
            Assert.NotNull(e.PositionId);
        });
        Assert.All(trail.Where(e => e.Category == AuditCategory.Opportunity), e => Assert.NotNull(e.OpportunityId));
        Assert.Equal(3, trail.Count(e => e.Category == AuditCategory.Fill));
        Assert.Equal(kit.Out<OpportunityTransition>().Count(), trail.Count(e => e.Category == AuditCategory.Opportunity && e.Event.Contains("->", StringComparison.Ordinal)));
        Assert.Contains(trail, e => e.Category == AuditCategory.EntryIntent && e.Reason == ReasonCode.IntentIssued);
        Assert.Contains(trail, e => e.Category == AuditCategory.Exit && e.Reason == ReasonCode.Tp1);
        Assert.Contains(trail, e => e.Category == AuditCategory.Exit && e.Reason == ReasonCode.FinalTarget);
        Assert.Contains(trail, e => e.Category == AuditCategory.Position && e.Reason == ReasonCode.PositionClosed);
    }

    [Fact]
    public void RunContext_ComesFromTheEngine()
    {
        var config = TradeKit.MakeConfig(variants: TradeKit.Immediate());
        var engine = new TradingEngine(config, Clock.Day, Kit.StandardUniverse().ToList(), new Dictionary<string, IBaseStrategy> { ["S"] = ScriptedStrategy.Valid("S", 1, null) }, "run-42", "LivePaper");
        var loop = new EngineLoop(engine, engine.Variants.Schedule.DayStartUtc);
        engine.Start(loop);
        loop.ProcessGroup(new EngineEvent[] { Ev.Opt(TradeKit.T0, 1, "CE23900", 124m, 125m), Ev.Bar(TradeKit.T0, 1) });

        var trail = engine.AuditTrailEntries();

        Assert.NotEmpty(trail);
        Assert.All(trail, e =>
        {
            Assert.Equal("run-42", e.RunId);
            Assert.Equal("LivePaper", e.RunMode);
        });
    }

    [Fact]
    public void RepetitiveRejections_AreBounded_ButTheirCountsAndEveryStateChangeArePreserved()
    {
        var variant = new VariantSpec { VariantId = "A", StrategyId = "S", EntryCondition = new NiftySignal.Trading.Core.Entry.AskBelowCandleMaSpec { N = 1, PriceSource = NiftySignal.Trading.Core.Candles.CandlePriceSource.AskClose } };
        var kit = new TradeKit(TradeKit.MakeConfig(variants: variant));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));                              // candle 1 closes at 125: only an ask below 125 qualifies
        for (var i = 1; i <= 50; i++)
        {
            kit.Feed(kit.Q(kit.At(i * 0.1), 129m, 130m));                                      // fifty identical rejections
        }

        kit.Loop.Finish(kit.Engine.Variants.Schedule.SessionEndUtc);
        var trail = kit.Engine.AuditTrailEntries();

        var evaluationRows = trail.Where(e => e.Category == AuditCategory.Evaluation && e.Reason == ReasonCode.ConditionNotSatisfied).ToList();
        Assert.Single(evaluationRows);                                                         // first occurrence only
        var summary = Assert.Single(trail, e => e.Category == AuditCategory.Summary && e.Reason == ReasonCode.ConditionNotSatisfied);
        Assert.Contains("x50", summary.Detail, StringComparison.Ordinal);                      // the count is not lost
        Assert.Equal(50, kit.Engine.DailySummaries(kit.At(10)).Single().RejectionReasons["NotSatisfied:AskNotBelowAverage"]);
        Assert.Equal(kit.Out<OpportunityTransition>().Count(), trail.Count(e => e.Category == AuditCategory.Opportunity && e.Event.Contains("->", StringComparison.Ordinal)));
    }

    [Fact]
    public void IntegrityKillQuarantineAndDailySl_AreAudited()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate(dailySl: 1m));
        kit.Feed(Ctl.Degraded(kit.At(2), 1, FeedFaultReason.DataGapOpen));
        kit.Feed(kit.Q(kit.At(3), 100m, 101m));
        kit.Feed(Ctl.Kill(kit.At(4), 1));
        kit.Engine.Admission.Admit(kit.Loop, new EngineEvent[] { kit.Q(kit.At(1), 100m, 101m) });

        var trail = kit.Engine.AuditTrailEntries();

        Assert.Contains(trail, e => e.Category == AuditCategory.Integrity && e.Event == "Healthy->Degraded");
        Assert.Contains(trail, e => e.Category == AuditCategory.DailySl && e.Reason == ReasonCode.DailySlBreached && e.Event.Contains("observation only", StringComparison.Ordinal));
        Assert.Contains(trail, e => e.Category == AuditCategory.KillSwitch && e.Event == "Kill:Killed");
        Assert.Contains(trail, e => e.Category == AuditCategory.Quarantine && e.Event == "Late");
    }
}

public class DailySummaryTests
{
    private static TradeKit GoldenKit(SizingSpec? sizing = null)
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate(sizing: sizing, exit: new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m, FinalTpPct = 0.20m })));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Loop.AdvanceTo(kit.At(1.5));
        kit.Feed(kit.Q(kit.At(120), 138m, 139m));
        kit.Feed(kit.Q(kit.At(180), 151m, 152m));
        kit.Loop.AdvanceTo(kit.At(200));
        return kit;
    }

    [Fact]
    public void GoldenDay_ClosedTradeAccounting_MatchesTheIndependentLedger_WithProvisionalLabels()
    {
        var kit = GoldenKit();

        var s = Assert.Single(kit.Engine.DailySummaries(kit.At(200)));

        Assert.Equal((1, 1, 1), (s.OpportunitiesCreated, s.EntryAttempts, s.TradesOpened));
        Assert.Equal(1, s.ClosedTrades);
        Assert.Equal(1, s.OpportunityEnds["Consumed:Filled"]);
        Assert.Equal(1, s.EntryOrderOutcomes["Filled"]);
        Assert.Equal((650L, 650L, 0), (s.RequestedQuantity, s.FilledEntryQuantity, s.PartialEntries));
        Assert.Equal(1, s.Tp1Exits);                                                           // partial exits are not new trades
        Assert.Equal(1, s.ExitFillsByReason["Tp1"]);
        Assert.Equal(1, s.ExitFillsByReason["FinalTarget"]);
        Assert.Equal((0, 0, 0), (s.BelowBandFills, s.LateClosures, s.UnresolvedPositions));

        Assert.Equal(12675.00m, s.ClosedGrossPnl);
        Assert.Equal(60.00m, s.ClosedTradeCosts.Brokerage);
        Assert.Equal(140.89m, s.ClosedTradeCosts.Stt);
        Assert.Equal(62.25m, s.ClosedTradeCosts.ExchangeCharge);
        Assert.Equal(0.17m, s.ClosedTradeCosts.SebiFee);
        Assert.Equal(2.44m, s.ClosedTradeCosts.StampDuty);
        Assert.Equal(22.04m, s.ClosedTradeCosts.Gst);
        Assert.Equal(287.79m, s.ClosedTradeCosts.Total);
        Assert.Equal(12387.21m, s.ClosedNetPnl);
        Assert.Equal(12471.86m, s.ClosedNetPnlExcludingProvisionalComponents);                  // 12,675.00 - (STT 140.89 + exchange 62.25)
        Assert.Equal(new[] { "Brokerage", "SebiFee", "StampDuty", "Gst" }, s.ProvisionalComponents);
        Assert.Contains("PROVISIONAL", s.CostSourceStatus);
        Assert.Equal(287.79m, s.AllFillCosts.Total);
        Assert.Equal("ResearchV1/1", s.RiskProfile);
        Assert.Equal("independent-virtual-book:S/A", s.Book);
        Assert.Empty(s.OpenValuations);
        Assert.Equal(12387.21m, s.DailyPnlAsOf);
        Assert.Equal(81250m, s.PeakPremiumExposure);                                           // committed premium: 650 x 125, recorded, not limited
    }

    [Fact]
    public void BidBasedExcursion_HasUnitsCoverageFlagsAndAFirstExitSnapshot()
    {
        var kit = GoldenKit();

        var x = Assert.Single(kit.Engine.DailySummaries(kit.At(200)).Single().Excursions);

        Assert.Contains("premium rupees per unit", x.Unit);
        Assert.Equal(125m, x.AvgEntryPrice);
        Assert.Equal(26m, x.Mfe);                                                              // highest eligible bid 151 - 125
        Assert.Equal(-1m, x.Mae);                                                              // lowest eligible bid 124 - 125 (the entry quote's bid)
        Assert.Equal(20.8m, x.MfePct);
        Assert.Equal(-0.8m, x.MaePct);
        Assert.Equal(3, x.Observations);
        Assert.Equal(2, x.Gaps);                                                               // 119 s and 60 s between observations, both beyond the 2 s quote age
        Assert.Equal(ExcursionCoverage.HasGaps, x.Coverage);
        Assert.Equal(13m, x.MfeAtFirstExit);                                                   // at the TP1 fill the highest bid so far was 138
        Assert.Equal(-1m, x.MaeAtFirstExit);
        Assert.Equal(PositionState.Closed, x.State);
    }

    [Fact]
    public void OpenPosition_IsKeptSeparateFromClosedResults_AndUnavailableValuationIsFlagged()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate());

        var fresh = kit.Engine.DailySummaries(kit.At(1.5)).Single();
        Assert.Equal(0, fresh.ClosedTrades);
        Assert.Equal(0m, fresh.ClosedGrossPnl);
        Assert.Equal(ValuationStatus.Marked, Assert.Single(fresh.OpenValuations).Status);
        Assert.Equal(-650m, fresh.UnrealisedGrossMarked);                                      // (bid 124 - 125) x 650
        Assert.Equal(0, fresh.UnavailableValuations);

        var stale = kit.Engine.DailySummaries(kit.At(100)).Single();
        Assert.Equal(ValuationStatus.Stale, Assert.Single(stale.OpenValuations).Status);
        Assert.Equal(1, stale.UnavailableValuations);
        Assert.Equal(0m, stale.UnrealisedGrossMarked);                                         // nothing is valued from an old quote
        Assert.True(stale.DailyPnlConservativeMark);                                           // the daily-SL observation marks the unmarkable position at zero and says so

        kit.Loop.Finish(kit.Engine.Variants.Schedule.SessionEndUtc);
        var ended = kit.Engine.DailySummaries(Clock.Ist(15, 36)).Single();
        Assert.Equal(1, ended.UnresolvedPositions);
        Assert.Equal(0, ended.ClosedTrades);
    }

    [Fact]
    public void PartialEntriesAndPartialExits_AreNotNewTrades()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate(exit: new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m }), askQty: 300);   // 4 of 10 lots fill
        kit.Feed(kit.Q(kit.At(2), 138m, 139m, bidQty: 65));                                    // TP1 target 130, one lot displayed
        kit.Feed(kit.Q(kit.At(3.7), 138m, 139m, bidQty: 130));                                 // new displayed state
        kit.Loop.AdvanceTo(kit.At(4.5));

        var s = kit.Engine.DailySummaries(kit.At(4.5)).Single();

        Assert.Equal((650L, 260L, 1), (s.RequestedQuantity, s.FilledEntryQuantity, s.PartialEntries));
        Assert.Equal(1, s.TradesOpened);
        Assert.Equal(0, s.ClosedTrades);
        Assert.Equal(1, s.Tp1Exits);                                                           // one TP1 order ...
        Assert.Equal(2, s.ExitFillsByReason["Tp1"]);                                           // ... filled in two parts
    }

    [Fact]
    public void RejectionReasons_AggregateBlockingAndSizingOutcomes()
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate(sizing: new PremiumBudgetSpec { Rupees = 8000 })));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        for (var i = 1; i <= 4; i++)
        {
            kit.Feed(kit.Q(kit.At(i), 125m, 126m));
        }

        kit.Loop.Finish(kit.Engine.Variants.Schedule.SessionEndUtc);
        var s = kit.Engine.DailySummaries(Clock.Ist(15, 36)).Single();

        Assert.Equal(5, s.RejectionReasons["Blocked:InsufficientBudget"]);
        Assert.Equal(0, s.EntryAttempts);
    }

    [Fact]
    public void DailySl_AndPeakExposure_AreObservedNotEnforced()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate(dailySl: 10m));
        kit.Feed(kit.Q(kit.At(2), 100m, 101m));

        var s = kit.Engine.DailySummaries(kit.At(2)).Single();

        Assert.Equal(10m, s.DailySlConfigured);
        Assert.True(s.DailySlBreachObserved);
        Assert.True(s.WorstDailyPnl < -10m);
        Assert.Equal(PositionState.Open, kit.Position().State);                               // nothing was liquidated
        Assert.Equal(81250m, s.PeakPremiumExposure);
    }

    [Fact]
    public void WritesRepresentativeOutput_WhenRequested()
    {
        var directory = Environment.GetEnvironmentVariable("NSE_CP4_SAMPLE_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var kit = GoldenKit();
        kit.Feed(Ctl.Degraded(kit.At(210), 1, FeedFaultReason.NoMessageWatchdog));
        kit.Feed(Ctl.Healthy(kit.At(220), 2));
        kit.Feed(kit.Q(kit.At(230), 124m, 125m));
        var options = new JsonSerializerOptions { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };
        Directory.CreateDirectory(directory);
        File.WriteAllLines(
            Path.Combine(directory, "audit.jsonl"),
            kit.Engine.AuditTrailEntries().Select(e => JsonSerializer.Serialize(e, options)));
        File.WriteAllText(
            Path.Combine(directory, "summary.json"),
            JsonSerializer.Serialize(kit.Engine.DailySummaries(kit.At(230)), new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
    }
}
