using Microsoft.Extensions.Configuration;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Live;

namespace NiftySignal.Tests.Trading.Live;

/// <summary>Configuration of the three NPT1 strategies: off by default, refused when inconsistent, fixed 650 units, no exits other than signals and the terminal protocol, the frozen fee schedule.</summary>
public class Npt1ConfigTests
{
    private static PaperLiveOptions Enabled(params string[] ids)
        => new() { Enabled = true, Capital = 1_000_000m, BrokeragePerOrder = 0m, OptionTickSize = 0.05m, Strategies = ids.ToDictionary(i => i, _ => new StrategyToggleOptions { Enabled = true }) };

    [Fact]
    public void NoNpt1StrategyIsEnabledByDefault_AndEnablingPaperLiveAloneRegistersNone()
    {
        Assert.Empty(PaperLiveSetup.Npt1Strategies(new PaperLiveOptions()));
        Assert.Empty(PaperLiveSetup.Npt1Strategies(new PaperLiveOptions { Enabled = true, Capital = 1m }));
        Assert.False(new PaperLiveOptions { Strategies = new() { ["NPT1-MD5"] = new StrategyToggleOptions { Enabled = true } } }.AnyNpt1Enabled);      // a strategy switch is meaningless while paper-live itself is off
    }

    [Fact]
    public void ConfigurationBinding_ReadsTheStrategySwitches_FromTheNamedSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trading:PaperLive:Enabled"] = "true",
            ["Trading:PaperLive:Strategies:NPT1-MD5:Enabled"] = "true",
            ["Trading:PaperLive:Strategies:NPT1-MDX3:Enabled"] = "false",
            ["Trading:PaperLive:Strategies:NPT1-PE5:Enabled"] = "true",
        }).Build();

        var options = PaperLiveOptions.Bind(configuration);

        Assert.Equal(new[] { "NPT1-MD5", "NPT1-PE5" }, PaperLiveSetup.Npt1Strategies(options).Select(s => s.Family.StrategyId));
        Assert.True(options.AnyNpt1Enabled);
    }

    [Fact]
    public void EachEnabledStrategy_IsAFamilyWithOneMainVariant_Of650Units_AndNoStopTargetOrHold()
    {
        var strategies = PaperLiveSetup.Npt1Strategies(Enabled("NPT1-MD5", "NPT1-MDX3", "NPT1-PE5"));

        Assert.Equal(3, strategies.Count);
        foreach (var s in strategies)
        {
            var v = Assert.Single(s.Variants);
            Assert.Equal("MAIN", v.VariantId);
            Assert.Equal(s.Family.StrategyId, v.StrategyId);
            var policy = Assert.IsType<NextCompletedBarEntrySpec>(v.EntryCondition);
            Assert.Equal((100m, 150m, 125m, TimeSpan.FromSeconds(2)), (policy.AskMin, policy.AskMax, policy.AskTarget, policy.MaxQuoteAge));
            Assert.Equal(NextBarTerminalMode.LiveObservedBid, policy.TerminalMode);
            Assert.Equal("npt1-live1500-v1", policy.PolicyLabel);
            Assert.Equal(650, Assert.IsType<NiftySignal.Trading.Core.Sizing.FixedQuantitySpec>(v.Sizing).Units);
            Assert.Null(v.Exit.TradeSlPct);
            Assert.Null(v.Exit.Tp1Pct);
            Assert.Null(v.Exit.FinalTpPct);
            Assert.Null(v.Exit.MaxHold);
            Assert.Null(v.MaxAttemptsPerOpportunity);
        }
    }

    [Fact]
    public void TheNpt1Config_IsPaperOnly_UsesTheLiveSessionProfile_AndTheFrozenFeeSchedule()
    {
        var options = Enabled("NPT1-MD5", "NPT1-MDX3", "NPT1-PE5");
        var config = PaperLiveSetup.Npt1Config(options, PaperLiveSetup.Npt1Strategies(options));

        Assert.Empty(config.Validate());
        Assert.Equal(ExecutionAdapter.Simulated, config.Execution.Adapter);
        Assert.Equal(RiskProfile.PaperLiveV1Name, config.Risk.Name);
        Assert.Equal(1_000_000m, config.Risk.GlobalCapital);
        Assert.Equal(SessionConfig.Npt1Live1500(), config.Session);
        Assert.Equal(new TimeOnly(9, 30), config.Session.EntryOpen);
        Assert.Equal(new TimeOnly(15, 0), config.Session.EntryClose);
        Assert.Equal(new TimeOnly(15, 0), config.Session.LiquidationStart);

        var entry = Assert.Single(config.Costs.Schedules);
        Assert.Equal("npt1-frozen-fees-v1", entry.Version);
        Assert.Equal((0.0015m, 0.0003503m, 0.000001m, 0.00003m, 0.18m), (entry.SttSellRate, entry.ExchangeTransactionRate, entry.SebiTurnoverRate, entry.StampBuyRate, entry.GstRate));
        Assert.Equal(0m, Assert.IsType<FlatBrokerage>(entry.Brokerage).Amount);
    }

    [Fact]
    public void TheGlobalFeeSchedule_IsUnchanged_ItDiffersFromTheFrozenOneOnlyInTheExchangeRate()
    {
        var global = NseIndexOptionsCostSchedules.From20260401(new FlatBrokerage { Amount = 0m, Unit = BrokerageUnit.PerOrder });
        var frozen = NseIndexOptionsCostSchedules.Npt1FrozenV1(new FlatBrokerage { Amount = 0m, Unit = BrokerageUnit.PerOrder });

        Assert.Equal("nse-index-options-2026-04-01-provisional", global.Version);
        Assert.Equal(0.0003553m, global.ExchangeTransactionRate);                         // not silently changed
        Assert.Equal(0.0003503m, frozen.ExchangeTransactionRate);
        Assert.Equal((global.SttSellRate, global.SebiTurnoverRate, global.StampBuyRate, global.GstRate), (frozen.SttSellRate, frozen.SebiTurnoverRate, frozen.StampBuyRate, frozen.GstRate));
    }

    [Fact]
    public void InconsistentConfigurations_AreRefused_Visibly()
    {
        var withPlumbing = Enabled("NPT1-MD5");
        withPlumbing.PlumbingFamily = new PlumbingFamilyOptions { Enabled = true, Windows = new() { new PlumbingWindowOptions { FromBarSeq = 1, ToBarSeq = 1 } } };
        Assert.Contains(withPlumbing.Validate(), e => e.Contains("plumbing", StringComparison.OrdinalIgnoreCase));

        var brokerage = Enabled("NPT1-PE5");
        brokerage.BrokeragePerOrder = 20m;
        Assert.Contains(brokerage.Validate(), e => e.Contains("BrokeragePerOrder must be 0", StringComparison.Ordinal));

        var unknown = Enabled("NPT1-MD5");
        unknown.Strategies["NPT1-MD6"] = new StrategyToggleOptions { Enabled = true };
        Assert.Contains(unknown.Validate(), e => e.Contains("NPT1-MD6", StringComparison.Ordinal));

        Assert.Empty(Enabled("NPT1-MD5", "NPT1-MDX3", "NPT1-PE5").Validate());
    }

    [Fact]
    public void ABarClockedVariant_CannotShareAConfigurationWithOtherVariants_OrUseAnotherSession()
    {
        var options = Enabled("NPT1-MD5");
        var config = PaperLiveSetup.Npt1Config(options, PaperLiveSetup.Npt1Strategies(options));

        var wrongSession = config with { Session = SessionConfig.PaperLiveV1() };
        Assert.Contains(wrongSession.Validate(), e => e.Contains("NPT1 variants fix the session rules", StringComparison.Ordinal));

        var mixed = config with { Variants = config.Variants.Concat(new[] { KeyValuePair.Create("S/A", new NiftySignal.Trading.Core.Variants.VariantSpec { StrategyId = "S", VariantId = "A", EntryCondition = new ImmediateEntrySpec() }) }).ToDictionary(kv => kv.Key, kv => kv.Value) };
        Assert.Contains(mixed.Validate(), e => e.Contains("cannot share a configuration", StringComparison.Ordinal));

        Assert.Empty((config with { Session = SessionConfig.Npt1HistoricalParity1100() }).Validate());
    }
}
