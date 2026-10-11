using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Ids;
using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Tests.Trading.Core;

public class DeterministicIdTests
{
    private static readonly ConfigHash Config = new("abc123");
    private static readonly DateOnly Date = new(2026, 9, 22);

    // Golden ids: changing the scheme re-identifies every persisted entity. Update only with a new Scheme tag (tid2: the identity is (StrategyId, VariantId); nothing was persisted under tid1).
    private const string GoldenDecision = "dec_fa802b9d65701442244b73c747bfd18f";
    private const string GoldenOrder = "ord_0a689a24511f0241408d59383fd80d0f";
    private const string GoldenOpportunity = "opp_d31c7da6aec3d121b6633952e3ef340a";
    private const string GoldenPosition = "pos_e75d48c35d61d7fc4742e3f3238650b2";

    [Fact]
    public void Ids_AreStableGoldenValues()
    {
        var decision = EngineIds.Decision(Config, new VariantKey("S1", "A2-ma10"), Date, "Bar", 17, 0);

        Assert.Equal(GoldenDecision, decision);
        Assert.Equal(GoldenOrder, EngineIds.Order(decision, "entry", 1));
        Assert.Equal(GoldenOpportunity, EngineIds.Opportunity(new VariantKey("S1", "A2-ma10"), new ConfigHash("v1"), Date, "episode-17"));
        Assert.Equal(GoldenPosition, EngineIds.Position(Config, new VariantKey("S1", "A2-ma10"), Date, decision));
    }

    [Fact]
    public void SameInputs_SameId_AcrossCulturesAndRepeats()
    {
        var baseline = EngineIds.Decision(Config, new VariantKey("S", "A"), Date, "Bar", 5, 0);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(baseline, EngineIds.Decision(Config, new VariantKey("S", "A"), Date, "Bar", 5, 0));
        }

        foreach (var culture in new[] { "tr-TR", "ar-SA", "th-TH", "de-DE" })
        {
            Assert.Equal(baseline, Culture.Under(culture, () => EngineIds.Decision(Config, new VariantKey("S", "A"), Date, "Bar", 5, 0)));
        }
    }

    [Fact]
    public void EveryComponent_ChangesTheId()
    {
        var ids = new[]
        {
            EngineIds.Decision(Config, new VariantKey("S", "A"), Date, "Bar", 5, 0),
            EngineIds.Decision(new ConfigHash("other"), new VariantKey("S", "A"), Date, "Bar", 5, 0),
            EngineIds.Decision(Config, new VariantKey("S", "B"), Date, "Bar", 5, 0),
            EngineIds.Decision(Config, new VariantKey("T", "A"), Date, "Bar", 5, 0),
            EngineIds.Decision(Config, new VariantKey("S", "A"), Date.AddDays(1), "Bar", 5, 0),
            EngineIds.Decision(Config, new VariantKey("S", "A"), Date, "Timer", 5, 0),
            EngineIds.Decision(Config, new VariantKey("S", "A"), Date, "Bar", 6, 0),
            EngineIds.Decision(Config, new VariantKey("S", "A"), Date, "Bar", 5, 1),
        };

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void VariantsNeverShareAnIdNamespace()
    {
        Assert.NotEqual(
            EngineIds.Opportunity(new VariantKey("S", "A"), new ConfigHash("v"), Date, "e1"),
            EngineIds.Opportunity(new VariantKey("S", "A2"), new ConfigHash("v"), Date, "e1"));
        Assert.NotEqual(
            EngineIds.Position(Config, new VariantKey("S", "A"), Date, "dec_x"),
            EngineIds.Position(Config, new VariantKey("S", "A2"), Date, "dec_x"));
    }

    [Fact]
    public void TheSameVariantIdUnderTwoStrategies_NeverSharesAnIdNamespace()
    {
        var xa = new VariantKey("X", "A");
        var ya = new VariantKey("Y", "A");
        var hash = new ConfigHash("v");

        Assert.NotEqual(EngineIds.Decision(Config, xa, Date, "Bar", 5, 0), EngineIds.Decision(Config, ya, Date, "Bar", 5, 0));
        Assert.NotEqual(EngineIds.Opportunity(xa, hash, Date, "e1"), EngineIds.Opportunity(ya, hash, Date, "e1"));
        Assert.NotEqual(EngineIds.Position(Config, xa, Date, "dec_x"), EngineIds.Position(Config, ya, Date, "dec_x"));
        // the split point between the two parts must not collide either: "X"+"A.B" versus "X.A"+"B"
        Assert.NotEqual(
            EngineIds.Opportunity(new VariantKey("X", "A.B"), hash, Date, "e1"),
            EngineIds.Opportunity(new VariantKey("X.A", "B"), hash, Date, "e1"));
    }

    [Fact]
    public void ConcatenationAndTypeAmbiguity_DoesNotCollide()
    {
        Assert.NotEqual(DeterministicId.Create("x", "k", "ab", "c"), DeterministicId.Create("x", "k", "a", "bc"));
        Assert.NotEqual(DeterministicId.Create("x", "k", 1), DeterministicId.Create("x", "k", "1"));
        Assert.NotEqual(DeterministicId.Create("x", "k", "a", null), DeterministicId.Create("x", "k", "a", "null"));
        Assert.NotEqual(DeterministicId.Create("x", "k1", "a"), DeterministicId.Create("x", "k2", "a"));
    }

    [Fact]
    public void IdFormat_IsPrefixPlus32LowerHex()
    {
        var id = EngineIds.Order("dec_x", "entry", 1);

        Assert.Matches("^ord_[0-9a-f]{32}$", id);
    }
}
