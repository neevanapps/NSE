using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.Tests.Trading.Core;
using NiftySignal.Tests.Trading.Recovery;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Live;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Live;

/// <summary>
/// The live risk limits come from <c>Trading:PaperLive:Risk</c> configuration ONLY, through the same path the Host uses (<see cref="PaperLiveOptions.Bind"/> then
/// <see cref="PaperLiveSetup.Config"/> inside the coordinator), and are then enforced by the engine on real inputs. The strategy family here is a TEST/PLUMBING script with three
/// variants; nothing about its code knows any limit.
/// </summary>
public class PaperLiveRiskConfigTests
{
    private static IConfiguration Config(IDictionary<string, string?> values, bool plumbing = false)
    {
        var all = new Dictionary<string, string?>
        {
            ["Trading:PaperLive:Enabled"] = "true",
            ["Trading:PaperLive:Capital"] = "10000000",
            ["Trading:PaperLive:BrokeragePerOrder"] = "20",
            ["Trading:PaperLive:OptionTickSize"] = "0.05",
        };
        foreach (var (k, v) in values)
        {
            all["Trading:PaperLive:" + k] = v;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(all).Build();
    }

    /// <summary>Family X, variants X1, X2, X3: each enters on the bars of its window (X1 and X2 on bars 1 and 3, X3 on bar 1); every variant stops out at -10%.</summary>
    private static IReadOnlyList<PaperLiveStrategy> Strategies(long x1Second = 3, long x2Second = 3)
    {
        PlumbingVariantScript Script(params (long From, long To)[] w) => new(w.Select(x => new PlumbingWindow(x.From, x.To, StrategyDirection.Long)).ToList());
        var family = new PlumbingFamily("X", new Dictionary<string, PlumbingVariantScript>
        {
            ["X1"] = Script((1, 1), (x1Second, x1Second)),
            ["X2"] = Script((1, 1), (x2Second, x2Second)),
            ["X3"] = Script((1, 1)),
        });
        VariantSpec V(string id) => new() { StrategyId = "X", VariantId = id, EntryCondition = new ImmediateEntrySpec(), Exit = new ExitSpec { TradeSlPct = 0.10m } };
        return new[] { new PaperLiveStrategy(family, new[] { V("X1"), V("X2"), V("X3") }) };
    }

    private sealed class Sim : IDisposable
    {
        private readonly PaperLiveCoordinator _c;
        private long _id;
        private int _bar;

        public Sim(IConfiguration configuration, IReadOnlyList<PaperLiveStrategy> strategies)
        {
            Options = PaperLiveOptions.Bind(configuration);
            _c = new PaperLiveCoordinator(Options, strategies, () => new InMemoryRunStore(), NullLogger.Instance, "host");
            _c.StartSession(new LiveSessionInfo("session-1", Clock.Day, LiveHarness.Universe(), "FUT", "test-readiness/1"));
        }

        public PaperLiveOptions Options { get; }

        public PaperLiveCoordinator C => _c;

        public IReadOnlyList<EntryRejected> Rejections => _c.Run!.Engine.Outputs.OfType<EntryRejected>().ToList();

        public int Entries(string variant) => _c.Run!.Engine.Outputs.OfType<PositionOpened>().Count(p => p.VariantId == variant);

        public void Bar(int h, int m, decimal bid = 124m)
        {
            var at = Clock.Ist(h, m);
            _c.OnGroup(at, Quotes(bid), new[] { new LiveBar(++_bar, at, new BarInfo(at.AddSeconds(-30), at, 1000, 50, true, 5, 3, true, true, "test-readiness/1"), true) });
            _c.OnHeartbeat(at.AddSeconds(3));
        }

        public void Move(int h, int m, int s, decimal bid)
        {
            var at = Clock.Ist(h, m, s);
            _c.OnGroup(at, Quotes(bid), Array.Empty<LiveBar>());
            _c.OnHeartbeat(at.AddSeconds(3));
        }

        private IReadOnlyList<LiveQuote> Quotes(decimal bid) => new[]
        {
            new LiveQuote("CE23900", ++_id, false, bid, bid + 1, bid, 100000, 100000),
            new LiveQuote("PE23900", ++_id, false, bid, bid + 1, bid, 100000, 100000),
        };

        public void Dispose() => _c.Dispose();
    }

    /// <summary>Bar 1 enters; the market drops and stops the entered variants out (a realized loss of about 16 000 each); bar 3 asks again.</summary>
    private static Sim Play(IDictionary<string, string?> risk, IReadOnlyList<PaperLiveStrategy>? strategies = null)
    {
        var sim = new Sim(Config(risk), strategies ?? Strategies());
        Assert.Equal(PaperLiveState.Live, sim.C.Status.State);
        sim.Bar(10, 0);
        sim.Move(10, 0, 5, 100m);
        sim.Bar(10, 1, 100m);
        sim.Move(10, 1, 30, 124m);
        sim.Bar(10, 2, 124m);
        return sim;
    }

    private static void AssertRejected(Sim sim, string variant, string reason, long barSeq)
        => Assert.Contains(sim.Rejections, r => r.Variant.VariantId == variant && (barSeq == 0 || r.BarSeq == barSeq) && r.Reasons.Contains(reason, StringComparison.Ordinal));

    [Fact]
    public void WithNoLimitsConfigured_NothingIsBlocked_NoLimitIsInventedByTheSource()
    {
        using var sim = Play(new Dictionary<string, string?>());

        Assert.Empty(sim.Rejections);
        Assert.Equal(2, sim.Entries("X1"));
        Assert.Equal(2, sim.Entries("X2"));
        Assert.Equal(1, sim.Entries("X3"));
    }

    [Fact]
    public void GlobalMaxTradesPerDay_IsEnforced()
    {
        using var sim = Play(new Dictionary<string, string?> { ["Risk:Global:MaxTradesPerDay"] = "2" });

        Assert.Equal(2, sim.Entries("X1") + sim.Entries("X2") + sim.Entries("X3"));
        AssertRejected(sim, "X3", "GlobalTradeLimit", 0);
    }

    [Fact]
    public void GlobalDailyLoss_IsEnforced_AfterARealizedLoss()
    {
        using var sim = Play(new Dictionary<string, string?> { ["Risk:Global:MaxDailyLoss"] = "1000" });

        Assert.Equal(1, sim.Entries("X1"));                                              // the second X1 entry (bar 3) was blocked
        AssertRejected(sim, "X1", "GlobalDailyLoss", 3);
        AssertRejected(sim, "X2", "GlobalDailyLoss", 3);
    }

    [Fact]
    public void FamilyMaxTradesPerDay_IsEnforced()
    {
        using var sim = Play(new Dictionary<string, string?> { ["Risk:Families:X:MaxTradesPerDay"] = "1" });

        Assert.Equal(1, sim.Entries("X1") + sim.Entries("X2") + sim.Entries("X3"));
        AssertRejected(sim, "X3", "FamilyTradeLimit", 0);
    }

    [Fact]
    public void FamilyDailyLoss_IsEnforced()
    {
        using var sim = Play(new Dictionary<string, string?> { ["Risk:Families:X:MaxDailyLoss"] = "1000" });

        AssertRejected(sim, "X2", "FamilyDailyLoss", 3);
        Assert.Equal(1, sim.Entries("X2"));                                              // only the bar-1 entry; the bar-3 entry was blocked
    }

    [Fact]
    public void FamilyConsecutiveLosses_AreEnforced()
    {
        using var sim = Play(new Dictionary<string, string?> { ["Risk:Families:X:MaxConsecutiveLosses"] = "1" });

        AssertRejected(sim, "X2", "FamilyConsecutiveLoss", 3);
        Assert.Equal(1, sim.Entries("X2"));                                              // only the bar-1 entry; the bar-3 entry was blocked
    }

    [Fact]
    public void VariantMaxTradesPerDay_IsEnforced_OnlyForThatVariant()
    {
        using var sim = Play(new Dictionary<string, string?> { ["Risk:Variants:X/X1:MaxTradesPerDay"] = "1" });

        Assert.Equal(1, sim.Entries("X1"));
        AssertRejected(sim, "X1", "VariantTradeLimit", 3);
        Assert.Equal(2, sim.Entries("X2"));                                              // another variant of the same family is unaffected
    }

    [Fact]
    public void VariantDailyLoss_IsEnforced_OnlyForThatVariant()
    {
        using var sim = Play(new Dictionary<string, string?> { ["Risk:Variants:X/X1:MaxDailyLoss"] = "1000" });

        AssertRejected(sim, "X1", "VariantDailyLoss", 3);
        Assert.Equal(2, sim.Entries("X2"));
    }

    [Fact]
    public void VariantConsecutiveLosses_AreEnforced_OnlyForThatVariant()
    {
        using var sim = Play(new Dictionary<string, string?> { ["Risk:Variants:X/X1:MaxConsecutiveLosses"] = "1" });

        AssertRejected(sim, "X1", "VariantConsecutiveLoss", 3);
        Assert.Equal(2, sim.Entries("X2"));
    }

    [Fact]
    public void TheGlobalCapitalPool_IsEnforced_FromTheConfiguredCapital()
    {
        using var sim = new Sim(Config(new Dictionary<string, string?> { ["Capital"] = "100000" }), Strategies());      // one 650 x 125 entry = 81 250
        sim.Bar(10, 0);

        Assert.Equal(1, sim.Entries("X1") + sim.Entries("X2") + sim.Entries("X3"));
        AssertRejected(sim, "X3", "GlobalCapital", 0);
    }

    [Fact]
    public void TheConfiguredLimits_ReachTheEngineConfiguration_AtTheirExactScope()
    {
        var options = PaperLiveOptions.Bind(Config(new Dictionary<string, string?>
        {
            ["Risk:Global:MaxTradesPerDay"] = "7",
            ["Risk:Global:MaxDailyLoss"] = "9000",
            ["Risk:Families:X:MaxTradesPerDay"] = "5",
            ["Risk:Families:X:MaxDailyLoss"] = "4000",
            ["Risk:Families:X:MaxConsecutiveLosses"] = "3",
            ["Risk:Variants:X/X1:MaxTradesPerDay"] = "2",
            ["Risk:Variants:X/X2:MaxConsecutiveLosses"] = "2",
        }));

        var config = PaperLiveSetup.Config(options, Strategies());

        Assert.Empty(config.Validate());
        Assert.Equal(10_000_000m, config.Risk.GlobalCapital);
        Assert.Equal((7, 9000m, null), (config.Risk.Global!.MaxTradesPerDay, config.Risk.Global.MaxDailyLoss, config.Risk.Global.MaxConsecutiveLosses));
        Assert.Equal((5, 4000m, 3), (config.Risk.Families["X"].MaxTradesPerDay, config.Risk.Families["X"].MaxDailyLoss, config.Risk.Families["X"].MaxConsecutiveLosses));
        Assert.Equal(2, config.Variants["X/X1"].Limits!.MaxTradesPerDay);
        Assert.Equal(2, config.Variants["X/X2"].Limits!.MaxConsecutiveLosses);
        Assert.Null(config.Variants["X/X3"].Limits);
    }

    // ------------------------------------------------------------------ validation: fail closed, visibly

    [Theory]
    [InlineData("Risk:Global:MaxTradesPerDay", "0")]
    [InlineData("Risk:Global:MaxTradesPerDay", "-3")]
    [InlineData("Risk:Global:MaxDailyLoss", "0")]
    [InlineData("Risk:Global:MaxDailyLoss", "-5")]
    [InlineData("Risk:Families:X:MaxTradesPerDay", "0")]
    [InlineData("Risk:Families:X:MaxDailyLoss", "-1")]
    [InlineData("Risk:Families:X:MaxConsecutiveLosses", "0")]
    [InlineData("Risk:Variants:X/X1:MaxTradesPerDay", "-1")]
    [InlineData("Risk:Variants:X/X1:MaxDailyLoss", "0")]
    [InlineData("Risk:Variants:X/X1:MaxConsecutiveLosses", "0")]
    [InlineData("Risk:Global:MaxConsecutiveLosses", "3")]                              // not enforced for the account: refused, not ignored
    [InlineData("Risk:Global:MaxTradesPerDy", "3")]                                    // a misspelt key
    [InlineData("Risk:Families:X:MaxDailyLosss", "3")]
    [InlineData("Risk:Families:Bad Id:MaxTradesPerDay", "3")]                          // malformed StrategyId
    [InlineData("Risk:Variants:X:MaxTradesPerDay", "3")]                               // not StrategyId/VariantId
    [InlineData("Risk:Variants:X/X1/Z:MaxTradesPerDay", "3")]
    [InlineData("Risk:Variants:X/X9:MaxTradesPerDay", "3")]                            // the variant is not registered
    [InlineData("Risk:Families:Y:MaxTradesPerDay", "3")]                               // the family is not registered
    [InlineData("Risk:Variants:Y/Y1:MaxTradesPerDay", "3")]
    [InlineData("Risk:Global:MaxTradesPerDay", "abc")]                                 // unreadable
    [InlineData("Risk:Unknown:MaxTradesPerDay", "3")]
    public void AnInvalidRiskConfiguration_HaltsVisibly_AndNothingTrades(string key, string value)
    {
        using var sim = new Sim(Config(new Dictionary<string, string?> { [key] = value }), Strategies());
        sim.Bar(10, 0);

        Assert.Equal(PaperLiveState.Halted, sim.C.Status.State);
        Assert.Contains("configuration invalid", sim.C.Status.Detail, StringComparison.Ordinal);
        Assert.Null(sim.C.Run);
    }

    [Fact]
    public void DuplicateKeysThatDifferOnlyInCase_AreRefused()
    {
        var options = PaperLiveOptions.Bind(new ConfigurationBuilder().Build());
        options.Enabled = true;
        options.Risk.Families["X"] = new ScopeLimitOptions { MaxTradesPerDay = 1 };
        options.Risk.Families["x"] = new ScopeLimitOptions { MaxTradesPerDay = 2 };

        Assert.Contains(options.ValidateRisk(Strategies()), e => e.Contains("duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoStrategyRegistered_ValidLimitsAreAccepted_AndNothingIsInvented()
    {
        var options = PaperLiveOptions.Bind(Config(new Dictionary<string, string?> { ["Risk:Families:X:MaxTradesPerDay"] = "3", ["Risk:Variants:X/X1:MaxDailyLoss"] = "500" }));
        using var c = new PaperLiveCoordinator(options, PaperLiveSetup.Strategies(options), () => throw new InvalidOperationException("no store"), NullLogger.Instance, "host");

        c.StartSession(new LiveSessionInfo("s", Clock.Day, LiveHarness.Universe(), "FUT", "r"));

        Assert.Equal(PaperLiveState.NoStrategies, c.Status.State);                         // enabled + no strategy = no trades, no run, no journal
        Assert.Null(c.Run);
    }

    [Fact]
    public void WithNoStrategyRegistered_AMalformedLimitIsStillRefused()
    {
        var options = PaperLiveOptions.Bind(Config(new Dictionary<string, string?> { ["Risk:Global:MaxDailyLoss"] = "-1" }));
        using var c = new PaperLiveCoordinator(options, PaperLiveSetup.Strategies(options), () => throw new InvalidOperationException("no store"), NullLogger.Instance, "host");

        Assert.Equal(PaperLiveState.Halted, c.Status.State);
    }

    [Fact]
    public void TheCoreRefusesAGlobalConsecutiveLossLimit_ItDoesNotEnforce()
    {
        var profile = RiskProfile.PaperLiveV1(1_000m, new ScopeLimits { MaxConsecutiveLosses = 2 });

        Assert.Contains(profile.Validate(), e => e.Contains("Global.MaxConsecutiveLosses is not enforced", StringComparison.Ordinal));
    }

    [Fact]
    public void ADisabledInstance_IgnoresEveryRiskKey()
    {
        var options = PaperLiveOptions.Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Trading:PaperLive:Risk:Global:MaxTradesPerDay"] = "0" }).Build());
        using var c = new PaperLiveCoordinator(options, PaperLiveSetup.Strategies(options), () => throw new InvalidOperationException("no store"), NullLogger.Instance, "host");

        Assert.Equal(PaperLiveState.Disabled, c.Status.State);
    }
}
