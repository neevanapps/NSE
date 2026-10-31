using System.Text.Json.Nodes;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Metrics;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Phase 6a: strict schema, frozen-contract validation, identity/hash, the 30-strategy set rule.</summary>
public class ConfigTests
{
    internal const string Valid = """
    {
      "schema": "pev2-strategy/1", "strategyId": "S01", "version": 1, "enabled": true, "sourceModel": "fixed13650-summary-v1",
      "entry": {
        "buyCall": { "op": "AND", "items": [
          { "metric": "opt.atm.strict.dir:pct", "window": "10", "cmp": ">", "value": 5 },
          { "metric": "fut.strict:pct", "window": "5", "cmp": ">", "value": 0 } ] },
        "buyPut": { "op": "AND", "items": [
          { "metric": "opt.atm.strict.dir:pct", "window": "10", "cmp": "<", "value": -5 } ] } },
      "exit": { "conditions": { "op": "OR", "items": [ { "cross": "Down", "metric": "opt.atm.strict.dir:pct", "window": "10", "level": 0 } ] }, "onOppositeEntrySignal": "Exit" },
      "filters": { "entryFrom": "09:30:00", "entryBefore": "15:00:00", "requireFreshVix": false },
      "instrument": { "basket": "SignalTimeAtmPm2", "premium": { "min": 100, "max": 150 }, "rank": "PremiumProximityThenCentreDistanceThenStrikeThenToken" },
      "execution": { "fillQuote": "FirstAfterDecision", "entryPrice": "Ask", "exitPrice": "Bid", "slippage": { "entry": 0.5, "exit": 0.5 }, "latencyMs": 0, "maxQuoteAgeMs": 2000,
                     "entryOrderLifetimeMs": 60000, "maxProcessingLagMs": 5000, "fillMode": "AllOrNone" },
      "quantity": { "lots": 10 }, "capital": { "virtualCapital": 350000 }, "position": { "maxConcurrent": 1 },
      "risk": { "maxDailyLoss": 20000, "maxDailyTrades": 5, "onBreach": { "blockEntries": true, "liquidation": "None" } },
      "fees": { "schedule": "NSE-OPT-V1" }
    }
    """;

    internal static string Mutate(Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(Valid)!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    static IReadOnlyList<ConfigError> ErrorsOf(string json) => StrategyConfigParser.Parse(json).Errors;

    static void AssertRejected(string json, string code, string pathPart)
    {
        var r = StrategyConfigParser.Parse(json);
        Assert.False(r.IsValid);
        Assert.Null(r.Config);
        Assert.Contains(r.Errors, e => e.Code == code && e.Path.Contains(pathPart, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidConfig_Parses_WithExpectedContract()
    {
        var r = StrategyConfigParser.Parse(Valid);
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        var c = r.Config!;
        Assert.Equal(("S01", 1), (c.Identity.StrategyId, c.Identity.Version));
        Assert.Equal(64, c.Identity.Hash.Length);
        Assert.NotNull(c.BuyCall); Assert.NotNull(c.BuyPut); Assert.NotNull(c.ExitConditions);
        Assert.Equal(125d, c.Premium.Target);                                         // frozen tie-break target: midpoint of Rs 100-150
        Assert.Equal(FillMode.AllOrNone, c.Execution.FillMode);
        Assert.Equal(OnOppositeEntrySignal.Exit, c.OnOppositeEntrySignal);
        Assert.False(c.Exits.Any);                                                    // stops / targets / trailing / max hold are disabled by default
        Assert.False(c.Filters.RequireFreshVix);
    }

    [Fact]
    public void Identity_IsStableAcrossFormattingAndKeyOrder_AndChangesWithAnyRealChange()
    {
        var h = StrategyConfigParser.Parse(Valid).Config!.Identity.Hash;
        // whitespace, key order and numeric spelling do not change the hash
        var reordered = JsonNode.Parse(Valid)!.AsObject();
        var sl = reordered["execution"]!["slippage"]!.AsObject();
        sl["entry"] = JsonNode.Parse("0.50"); sl["exit"] = JsonNode.Parse("0.5000");
        var json = reordered.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(h, StrategyConfigParser.Parse(json).Config!.Identity.Hash);
        var swapped = Mutate(o => { var fees = o["fees"]; o.Remove("fees"); o["fees"] = fees; });
        Assert.Equal(h, StrategyConfigParser.Parse(swapped).Config!.Identity.Hash);

        foreach (var change in new Action<JsonObject>[]
        {
            o => o["version"] = 2, o => o["quantity"]!["lots"] = 11, o => o["execution"]!["slippage"]!["entry"] = 0.6, o => o["fees"]!["schedule"] = "OTHER",
            o => o["entry"]!["buyCall"]!["items"]!.AsArray()[0]!["value"] = 6, o => o["strategyId"] = "S02",
        })
            Assert.NotEqual(h, StrategyConfigParser.Parse(Mutate(change)).Config!.Identity.Hash);
    }

    [Fact]
    public void Strict_UnknownDuplicateAndMistypedProperties_AreRejected()
    {
        AssertRejected(Mutate(o => o["surprise"] = 1), "Unknown", "surprise");
        AssertRejected(Mutate(o => o["execution"]!["extra"] = true), "Unknown", "execution/extra");
        AssertRejected(Valid.Replace("\"version\": 1,", "\"version\": 1, \"version\": 2,"), "Duplicate", "version");
        AssertRejected(Mutate(o => o["version"] = "1"), "Type", "version");
        AssertRejected(Mutate(o => o["enabled"] = 1), "Type", "enabled");
        AssertRejected(Mutate(o => o["quantity"]!["lots"] = 1.5), "Type", "lots");
        AssertRejected("{ not json", "Json", "$");
        AssertRejected(Mutate(o => o["schema"] = "pev2-strategy/2"), "Value", "schema");
        AssertRejected(Mutate(o => o["strategyId"] = "1bad id"), "Value", "strategyId");
        AssertRejected(Mutate(o => o["sourceModel"] = "npt1-m1-v2"), "Value", "sourceModel");           // the clocks are never mixed
        AssertRejected("[]", "Type", "$");
    }

    [Theory]
    [InlineData("entry")] [InlineData("exit")] [InlineData("filters")] [InlineData("instrument")] [InlineData("execution")] [InlineData("quantity")] [InlineData("capital")] [InlineData("position")]
    [InlineData("risk")] [InlineData("fees")] [InlineData("strategyId")] [InlineData("version")] [InlineData("enabled")] [InlineData("sourceModel")] [InlineData("schema")]
    public void MandatorySections_MissingMeansTheStrategyCannotActivate(string section) =>
        AssertRejected(Mutate(o => o.Remove(section)), "Required", section);

    [Theory]
    [InlineData("execution", "fillQuote")] [InlineData("execution", "slippage")] [InlineData("execution", "latencyMs")] [InlineData("execution", "maxQuoteAgeMs")]
    [InlineData("execution", "entryOrderLifetimeMs")] [InlineData("execution", "maxProcessingLagMs")] [InlineData("execution", "fillMode")] [InlineData("execution", "entryPrice")] [InlineData("execution", "exitPrice")]
    [InlineData("risk", "maxDailyLoss")] [InlineData("risk", "maxDailyTrades")] [InlineData("risk", "onBreach")]
    [InlineData("instrument", "premium")] [InlineData("instrument", "basket")] [InlineData("instrument", "rank")]
    [InlineData("filters", "entryFrom")] [InlineData("filters", "entryBefore")]
    public void MandatoryPolicyFields_MissingMeansTheStrategyCannotActivate(string section, string field) =>
        AssertRejected(Mutate(o => o[section]!.AsObject().Remove(field)), "Required", $"{section}/{field}");

    [Fact]
    public void MandatoryRisk_AndCapital_HaveNoEngineDefaults_AndInvalidValuesAreRejected()
    {
        AssertRejected(Mutate(o => o["capital"]!["virtualCapital"] = 0), "Value", "virtualCapital");
        AssertRejected(Mutate(o => o["capital"]!["virtualCapital"] = -5), "Value", "virtualCapital");
        AssertRejected(Mutate(o => o["risk"]!["maxDailyLoss"] = 0), "Value", "maxDailyLoss");
        AssertRejected(Mutate(o => o["risk"]!["maxDailyTrades"] = 0), "Value", "maxDailyTrades");
        AssertRejected(Mutate(o => o["position"]!["maxConcurrent"] = 0), "Value", "maxConcurrent");
        AssertRejected(Mutate(o => o["quantity"]!["lots"] = 0), "Value", "lots");
        AssertRejected(Mutate(o => o["risk"]!["onBreach"]!["blockEntries"] = false), "Value", "blockEntries");
        AssertRejected(Mutate(o => o["risk"]!["onBreach"]!["liquidation"] = "Sometimes"), "Value", "liquidation");
        AssertRejected(Mutate(o => o["fees"]!["schedule"] = ""), "Value", "schedule");
        AssertRejected(Mutate(o => o["instrument"]!["premium"]!["max"] = 100), "Value", "premium/max");
        AssertRejected(Mutate(o => o["instrument"]!["premium"]!["min"] = 0), "Value", "premium/min");
        AssertRejected(Mutate(o => o["execution"]!["slippage"]!["entry"] = -0.1), "Value", "slippage/entry");
        AssertRejected(Mutate(o => o["execution"]!["maxQuoteAgeMs"] = 0), "Value", "maxQuoteAgeMs");
        AssertRejected(Mutate(o => o["execution"]!["entryOrderLifetimeMs"] = 0), "Value", "entryOrderLifetimeMs");
        AssertRejected(Mutate(o => o["execution"]!["maxSpread"] = new JsonObject { ["fraction"] = 2 }), "Value", "maxSpread/fraction");
        Assert.True(StrategyConfigParser.Parse(Mutate(o => o["execution"]!["maxSpread"] = new JsonObject { ["absolute"] = 2.0, ["fraction"] = 0.03 })).IsValid);
    }

    [Fact]
    public void BuyOnly_SellShortWriteAndReversalVocabularyIsUnrepresentable()
    {
        foreach (var key in new[] { "sell", "short", "write", "writeCall", "writePut", "shortCall", "sellPut", "long", "side", "direction" })
        {
            AssertRejected(Mutate(o => o[key] = "x"), "ForbiddenSell", key);
            AssertRejected(Mutate(o => o["entry"]!.AsObject()[key] = new JsonObject()), "ForbiddenSell", $"entry/{key}");
        }

        AssertRejected(Mutate(o => o["exit"]!["onOppositeEntrySignal"] = "ExitAndReverse"), "Value", "onOppositeEntrySignal");     // frozen: opposite signal = Exit only
        AssertRejected(Mutate(o => o["exit"]!["onOppositeEntrySignal"] = "Reverse"), "Value", "onOppositeEntrySignal");
        AssertRejected(Mutate(o => o["execution"]!["entryPrice"] = "Bid"), "Value", "entryPrice");                                  // entries buy at the ask
        AssertRejected(Mutate(o => o["execution"]!["exitPrice"] = "Ask"), "Value", "exitPrice");
        AssertRejected(Mutate(o => o["execution"]!["entryPrice"] = "Mid"), "Value", "entryPrice");                                  // no midpoint / LTP fills
        AssertRejected(Mutate(o => o["entry"] = new JsonObject()), "Required", "entry");                                           // at least one buy group
    }

    [Fact]
    public void LiveAndBrokerPermission_CanNeverBeExpressedInAStrategyFile()
    {
        foreach (var key in new[] { "live", "liveEnabled", "liveAuthorized", "authorization", "authorized", "broker", "router", "route", "submitOrders", "realOrders", "production" })
        {
            AssertRejected(Mutate(o => o[key] = true), "ForbiddenLive", key);
            AssertRejected(Mutate(o => o["execution"]!.AsObject()[key] = true), "ForbiddenLive", $"execution/{key}");
        }
    }

    [Fact]
    public void FrozenExecutionDefaults_AreEnforced()
    {
        AssertRejected(Mutate(o => o["execution"]!["fillMode"] = "Partial"), "Value", "fillMode");                                 // all-or-none until size units are verified
        AssertRejected(Mutate(o => o["filters"]!["entryFrom"] = "09:29:59"), "Value", "entryFrom");                                // entries begin 09:30
        AssertRejected(Mutate(o => o["filters"]!["entryBefore"] = "15:00:01"), "Value", "entryBefore");                             // and cease 15:00
        AssertRejected(Mutate(o => o["filters"]!["entryBefore"] = "09:30:00"), "Value", "entryBefore");
        AssertRejected(Mutate(o => o["filters"]!["entryFrom"] = "9:30"), "Value", "entryFrom");
        AssertRejected(Mutate(o => o["instrument"]!["basket"] = "WholeChain"), "Value", "basket");
        AssertRejected(Mutate(o => o["instrument"]!["rank"] = "Cheapest"), "Value", "rank");
        AssertRejected(Mutate(o => o["execution"]!["fillQuote"] = "AtLastPrice"), "Value", "fillQuote");
        Assert.True(StrategyConfigParser.Parse(Mutate(o => o["filters"]!["entryFrom"] = "09:45:00")).IsValid);                    // a later start is fine
        Assert.True(StrategyConfigParser.Parse(Mutate(o => o["filters"]!["requireFreshVix"] = true)).Config!.Filters.RequireFreshVix);
        Assert.False(StrategyConfigParser.Parse(Mutate(o => ((JsonObject)o["filters"]!).Remove("requireFreshVix"))).Config!.Filters.RequireFreshVix);   // optional, OFF by default
    }

    [Fact]
    public void OptionalExits_AreDisabledByDefault_AndValidatedWhenPresent()
    {
        var ok = StrategyConfigParser.Parse(Mutate(o => o["exits"] = JsonNode.Parse("""{ "stopLoss": { "type": "Percent", "value": 20 }, "target": { "type": "Points", "value": 30 }, "trailing": { "type": "Percent", "value": 10 }, "maxHoldBars": 12 }""")));
        Assert.True(ok.IsValid, string.Join("; ", ok.Errors));
        Assert.True(ok.Config!.Exits.Any);
        Assert.Equal((DistanceKind.Percent, 20d), (ok.Config.Exits.StopLoss!.Kind, ok.Config.Exits.StopLoss.Value));
        Assert.Equal(12, ok.Config.Exits.MaxHoldBars);
        AssertRejected(Mutate(o => o["exits"] = JsonNode.Parse("""{ "stopLoss": { "type": "Ticks", "value": 5 } }""")), "Value", "stopLoss/type");
        AssertRejected(Mutate(o => o["exits"] = JsonNode.Parse("""{ "stopLoss": { "type": "Points", "value": 0 } }""")), "Value", "stopLoss/value");
        AssertRejected(Mutate(o => o["exits"] = JsonNode.Parse("""{ "maxHoldBars": 0 }""")), "Value", "maxHoldBars");
        AssertRejected(Mutate(o => o["exits"] = JsonNode.Parse("""{ "tickStop": { "type": "Points", "value": 1 } }""")), "Unknown", "tickStop");   // no tick-based execution path
    }

    [Fact]
    public void AStrategyNeedsAnExitMechanism_ExitConditionsOrOneOfTheOptionalExits()
    {
        AssertRejected(Mutate(o => ((JsonObject)o["exit"]!).Remove("conditions")), "NoExitMechanism", "exit");
        Assert.True(StrategyConfigParser.Parse(Mutate(o => { ((JsonObject)o["exit"]!).Remove("conditions"); o["exits"] = JsonNode.Parse("""{ "maxHoldBars": 6 }"""); })).IsValid);
    }

    [Fact]
    public void ErrorsAreReportedTogether_NotOneAtATime()
    {
        var errors = ErrorsOf(Mutate(o => { o["capital"]!["virtualCapital"] = 0; o["risk"]!["maxDailyTrades"] = 0; o["bogus"] = 1; o["execution"]!["fillMode"] = "Partial"; }));
        Assert.True(errors.Count >= 4);
        Assert.Contains(errors, e => e.Path.EndsWith("virtualCapital", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Path.EndsWith("maxDailyTrades", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Code == "Unknown");
        Assert.Contains(errors, e => e.Path.EndsWith("fillMode", StringComparison.Ordinal));
    }

    // ---- the set ----

    static string Doc(string id, bool enabled = true, int version = 1) => Mutate(o => { o["strategyId"] = id; o["enabled"] = enabled; o["version"] = version; });

    [Fact]
    public void Set_ThirtyEnabledAreAccepted_ThirtyOneRejectTheWholeSet()
    {
        var thirty = Enumerable.Range(1, 30).Select(i => ($"f{i}", Doc($"S{i:00}"))).ToList();
        var ok = StrategySet.Load(thirty);
        Assert.True(ok.SetAccepted);
        Assert.Equal(30, ok.Active.Count);
        Assert.Equal(ok.Active.Select(a => a.Identity.StrategyId).OrderBy(x => x, StringComparer.Ordinal), ok.Active.Select(a => a.Identity.StrategyId));     // deterministic ascending order

        var thirtyOne = thirty.Append(("f31", Doc("S31"))).ToList();
        var bad = StrategySet.Load(thirtyOne);
        Assert.False(bad.SetAccepted);
        Assert.Empty(bad.Active);                                                  // no partial activation
        Assert.Contains(bad.SetErrors, e => e.Code == "TooManyEnabled");

        var withDisabled = thirty.Append(("f31", Doc("S31", enabled: false))).ToList();      // disabled strategies do not count
        Assert.Equal(30, StrategySet.Load(withDisabled).Active.Count);
    }

    [Fact]
    public void Set_InvalidFilesCannotActivate_ButDoNotBlockTheOthers_AndDuplicatesRejectTheSet()
    {
        var set = StrategySet.Load([("good", Doc("A")), ("bad", Mutate(o => o["capital"]!["virtualCapital"] = 0))]);
        Assert.True(set.SetAccepted);
        Assert.Single(set.Active);
        Assert.Single(set.Rejected);
        Assert.Equal("bad", set.Rejected[0].Source);

        var dup = StrategySet.Load([("a1", Doc("A")), ("a2", Doc("A", version: 2))]);
        Assert.False(dup.SetAccepted);
        Assert.Contains(dup.SetErrors, e => e.Code == "DuplicateStrategyId");
        Assert.Empty(dup.Active);
    }

    [Fact]
    public void Set_AKnownVersionWithADifferentHashIsRefused_ANewVersionIsFine()
    {
        var first = StrategyConfigParser.Parse(Doc("A")).Config!.Identity;
        var known = new Dictionary<(string, int), string> { [("A", 1)] = first.Hash };
        Assert.True(StrategySet.Load([("f", Doc("A"))], known).SetAccepted);                                              // same hash: unchanged
        var changed = Mutate(o => { o["strategyId"] = "A"; o["quantity"]!["lots"] = 12; });                               // same version, different content
        var r = StrategySet.Load([("f", changed)], known);
        Assert.False(r.SetAccepted);
        Assert.Contains(r.SetErrors, e => e.Code == "VersionHashConflict");
        var next = Mutate(o => { o["strategyId"] = "A"; o["version"] = 2; o["quantity"]!["lots"] = 12; });                // a change needs a new version
        Assert.True(StrategySet.Load([("f", next)], known).SetAccepted);
    }
}
