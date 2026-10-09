using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Selection;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

public class ConfigCp2HashTests
{
    // Independently computed (Python hashlib over the hand-written canonical text), then pinned. See contract section A.12.
    private const string GoldenVariantsJson = """{"Candles":{"Clock":"AdaptiveFuturesBars","HistoryDepth":64,"MaxCloseAge":null,"MinObservationsPerCandle":1},"ConfigVersion":"cp2-test","SchemaVersion":2,"Selection":{"ExpiryPolicy":"NearestExpiryOnOrAfterTradeDate","MaxQuoteAge":"00:00:02","MaximumEntryPremium":150,"MinCalendarDaysToExpiry":0,"MinimumEntryPremium":100,"Ranking":["AskNearestBandMidpoint","SmallestSpread"]},"Session":{"EntryClose":"15:00:00.0000000","EntryOpen":"09:30:00.0000000","FinalClose":"15:10:00.0000000","LiquidationStart":null,"SessionEnd":"15:35:00.0000000","TimeZoneId":"Asia/Kolkata"},"Variants":{"A":{"AccountId":null,"EntryCondition":{"Kind":"Immediate"},"MaxAttemptsPerOpportunity":null,"OnCapacityBlocked":null,"Reentry":"NewEpisodeRequired","RequestedQuantity":650,"StrategyId":"S","VariantId":"A"},"A2":{"AccountId":null,"EntryCondition":{"Kind":"AskBelowCandleMA","N":10,"PriceSource":"AskClose"},"MaxAttemptsPerOpportunity":null,"OnCapacityBlocked":null,"Reentry":"NewEpisodeRequired","RequestedQuantity":650,"StrategyId":"S","VariantId":"A2"}}}""";

    private const string GoldenVariantsHash = "63cad4b98652cbe9e7501047933c210dcd7bfb51c60c9b57c1317209cf7ab118";
    private const string GoldenA2VariantHash = "a2016e0b27564c74f7e4dbe425fdfe6009674c2463f4cf2f6b40ac28d8b3f484";

    private static VariantSpec A() => new() { VariantId = "A", StrategyId = "S", EntryCondition = new ImmediateEntrySpec() };

    private static VariantSpec A2(int n = 10, CandlePriceSource source = CandlePriceSource.AskClose)
        => new() { VariantId = "A2", StrategyId = "S", EntryCondition = new AskBelowCandleMaSpec { N = n, PriceSource = source } };

    private static TradingConfig WithVariants(params VariantSpec[] variants)
        => new() { ConfigVersion = "cp2-test", Variants = variants.ToDictionary(v => v.VariantId) };

    [Fact]
    public void ConfigWithVariants_HasAPinnedCanonicalFormAndHash()
    {
        var config = WithVariants(A(), A2());

        Assert.Equal(GoldenVariantsJson, config.ToCanonicalJson());
        Assert.Equal(GoldenVariantsHash, config.ComputeHash().Value);
        Assert.Equal(GoldenA2VariantHash, config.ComputeVariantHash("A2").Value);
    }

    [Fact]
    public void VariantDeclarationOrder_DoesNotChangeTheHash()
    {
        Assert.Equal(WithVariants(A(), A2()).ComputeHash(), WithVariants(A2(), A()).ComputeHash());
    }

    [Fact]
    public void PremiumBand_IsPartOfTheHash()
    {
        var baseline = WithVariants(A()).ComputeHash();

        Assert.Contains("\"MinimumEntryPremium\":100", WithVariants(A()).ToCanonicalJson());
        Assert.Contains("\"MaximumEntryPremium\":150", WithVariants(A()).ToCanonicalJson());
        Assert.NotEqual(baseline, (WithVariants(A()) with { Selection = new ContractSelectionConfig { MinimumEntryPremium = 101m } }).ComputeHash());
        Assert.NotEqual(baseline, (WithVariants(A()) with { Selection = new ContractSelectionConfig { MaximumEntryPremium = 151m } }).ComputeHash());
        Assert.NotEqual(baseline, (WithVariants(A()) with { Selection = new ContractSelectionConfig { MaxQuoteAge = TimeSpan.FromSeconds(3) } }).ComputeHash());
        Assert.NotEqual(baseline, (WithVariants(A()) with { Selection = new ContractSelectionConfig { MinCalendarDaysToExpiry = 1 } }).ComputeHash());
        Assert.NotEqual(baseline, (WithVariants(A()) with { Selection = new ContractSelectionConfig { Ranking = new[] { SelectionRanking.SmallestSpread, SelectionRanking.AskNearestBandMidpoint } } }).ComputeHash());
    }

    [Fact]
    public void ConditionParameters_AreEachPartOfTheHash_AndPartOfTheVariantHash()
    {
        var baseline = WithVariants(A2());

        Assert.NotEqual(baseline.ComputeHash(), WithVariants(A2(n: 11)).ComputeHash());
        Assert.NotEqual(baseline.ComputeHash(), WithVariants(A2(source: CandlePriceSource.MidClose)).ComputeHash());
        Assert.NotEqual(baseline.ComputeVariantHash("A2"), WithVariants(A2(n: 11)).ComputeVariantHash("A2"));
        Assert.NotEqual(baseline.ComputeHash(), (baseline with { Candles = new CandleConfig { MinObservationsPerCandle = 2 } }).ComputeHash());
        Assert.NotEqual(
            baseline.ComputeHash(),
            WithVariants(A2() with { RequestedQuantity = 130 }).ComputeHash());
        Assert.NotEqual(
            WithVariants(A()).ComputeVariantHash("A"),
            WithVariants(A() with { EntryCondition = new AskBelowCandleMaSpec { N = 1, PriceSource = CandlePriceSource.AskClose } }).ComputeVariantHash("A"));
    }

    [Fact]
    public void AnotherVariantsPresence_DoesNotChangeAVariantHash()
    {
        Assert.Equal(WithVariants(A2()).ComputeVariantHash("A2"), WithVariants(A(), A2()).ComputeVariantHash("A2"));
    }

    [Fact]
    public void Validation_CatchesCp2Mistakes()
    {
        Assert.Empty(WithVariants(A(), A2()).Validate());
        Assert.NotEmpty(WithVariants(A2(n: 0)).Validate());
        Assert.NotEmpty(WithVariants(A2(n: 65)).Validate());                          // beyond HistoryDepth 64
        Assert.NotEmpty((WithVariants(A()) with { Variants = new Dictionary<string, VariantSpec> { ["X"] = A() } }).Validate());   // key != VariantId
        Assert.NotEmpty(WithVariants(A() with { RequestedQuantity = 0 }).Validate());
        Assert.NotEmpty(WithVariants(A() with { MaxAttemptsPerOpportunity = 0 }).Validate());
        Assert.NotEmpty(WithVariants(A() with { StrategyId = " " }).Validate());
        Assert.NotEmpty((WithVariants(A()) with { Selection = new ContractSelectionConfig { MaximumEntryPremium = 50m } }).Validate());
    }

    [Fact]
    public void Spec_Defaults_AreTheConfirmedOnes()
    {
        var spec = A();

        Assert.Equal(650, spec.RequestedQuantity);
        Assert.Equal(CapacityBlockedPolicy.Reject, spec.ResolveCapacityPolicy());                   // Immediate
        Assert.Equal(CapacityBlockedPolicy.Wait, A2().ResolveCapacityPolicy());                       // waiting condition
        Assert.Equal(ReentryPolicy.NewEpisodeRequired, spec.Reentry);
        Assert.Null(spec.MaxAttemptsPerOpportunity);                                                  // attempts are not trades: unlimited
        Assert.Equal("A", spec.ResolveAccountId());
    }

    [Fact]
    public void ANandAPriceSource_HaveNoImplicitDefault()
    {
        // Both are 'required' members: a spec cannot be constructed without stating them (compile-time), and there is no default value to inherit.
        var required = typeof(AskBelowCandleMaSpec).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), inherit: false).Length > 0)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "N", "PriceSource" }, required);
    }
}
