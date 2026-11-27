using System.Text.Json.Nodes;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Decisions A3 / A4 / A7: the strategy set the execution service loads.</summary>
public class ExecutionServiceStrategyTests
{
    static readonly string[] Enabled = ["TOTAL_ACTIVITY_5_10", "TOTAL_ACTIVITY_5_10_AVG5", "TOTAL_VOTE_5_OF_6", "TOTAL_VOTE_5_OF_6_AVG5"];

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        // the repository folder, not the copy of the shipped execution-service files in the test output
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "NiftySignal.slnx"))) d = d.Parent;
        return Path.Combine(d!.FullName, "paper-engine-v2-strategies");
    }

    static string Json(string id, string set = "execution-service") => File.ReadAllText(Path.Combine(Root(), set, id + ".json"));

    static StrategyConfig Load(string id)
    {
        var r = StrategyConfigParser.Parse(Json(id));
        Assert.True(r.IsValid, id + ": " + string.Join("; ", r.Errors));
        return r.Config!;
    }

    static StrategyConfig[] All() => Directory.GetFiles(Path.Combine(Root(), "execution-service"), "*.json").Select(f => Load(Path.GetFileNameWithoutExtension(f))).ToArray();

    [Fact]
    public void ExactlyFourStrategiesAreEnabled_AndTheFourOfSixVoteIsPreservedButDisabled()
    {
        var all = All();
        Assert.Equal(Enabled, all.Where(c => c.Enabled).Select(c => c.Identity.StrategyId).Order(StringComparer.Ordinal));
        Assert.False(all.Single(c => c.Identity.StrategyId == "TOTAL_VOTE_4_OF_6").Enabled);
        var engine = new PaperEngine(all.Select(CompiledStrategy.Compile), ExecFixtures.Options());
        Assert.Empty(engine.ActivationFailures);
        Assert.Equal(Enabled, engine.Runtimes.Select(r => r.StrategyId));
    }

    [Fact]
    public void ImmediateAndAvg5Variants_DifferOnlyInIdentityAndEntryTiming()
    {
        foreach (var id in new[] { "TOTAL_ACTIVITY_5_10", "TOTAL_VOTE_5_OF_6" })
        {
            var a = JsonNode.Parse(Json(id))!.AsObject();
            var b = JsonNode.Parse(Json(id + "_AVG5"))!.AsObject();
            Assert.Equal(EntryTiming.Immediate, Load(id).Timing);
            Assert.Equal(EntryTiming.Avg5Pullback, Load(id + "_AVG5").Timing);
            foreach (var o in new[] { a, b }) { o.Remove("strategyId"); o.Remove("version"); o.Remove("entryTiming"); }
            Assert.Equal(a.ToJsonString(), b.ToJsonString());
        }
    }

    [Fact]
    public void EveryEnabledStrategy_KeepsTheSignalDefinition_AndCarriesTheApprovedRules()
    {
        foreach (var id in Enabled)
        {
            var c = Load(id);
            var source = JsonNode.Parse(Json(id.Replace("_AVG5", "", StringComparison.Ordinal), "next-0920"))!;
            Assert.Equal(source["entry"]!.ToJsonString(), JsonNode.Parse(Json(id))!["entry"]!.ToJsonString());
            Assert.Equal(20, c.Filters.MinCompletedBars);
            Assert.Equal(OnOppositeEntrySignal.ExitThenEnterOpposite, c.OnOppositeEntrySignal);
            Assert.Equal((2, 50_000d, 1_500d, 5, 1), (c.Lots, c.VirtualCapital, c.Risk.MaxDailyLoss, c.Risk.MaxDailyTrades, c.MaxConcurrent));
            Assert.Equal((100d, 150d), (c.Premium.Min, c.Premium.Max));
            Assert.Equal((0.02, 2000, 60_000), (c.Execution.MaxSpreadFraction!.Value, c.Execution.MaxQuoteAgeMs, c.Execution.EntryOrderLifetimeMs));
            var p = c.PartialProfit!;
            Assert.Equal((50d, 16d), (p.ExitQuantityPercent, p.VixThreshold));
            var matrix = p.Targets.SelectMany(t => t.Dte.Select(d => (d, t.BelowVixPercent, t.AtOrAboveVixPercent))).OrderByDescending(x => x.d).ToArray();
            Assert.Equal([(6, 20d, 25d), (5, 20d, 25d), (4, 25d, 30d), (3, 25d, 30d), (2, 25d, 30d), (1, 30d, 40d), (0, 40d, 50d)], matrix);
        }
    }
}
