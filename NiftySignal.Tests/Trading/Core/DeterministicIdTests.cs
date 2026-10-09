using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Ids;

namespace NiftySignal.Tests.Trading.Core;

public class DeterministicIdTests
{
    private static readonly ConfigHash Config = new("abc123");
    private static readonly DateOnly Date = new(2026, 9, 22);

    // Golden ids: changing the scheme re-identifies every persisted entity. Update only with a new Scheme tag.
    private const string GoldenDecision = "dec_e74ccb421c9350f836e3385d3f597d6a";
    private const string GoldenOrder = "ord_8597464e5cf6baf263d6e9399bda8cd6";
    private const string GoldenOpportunity = "opp_e978c8e0b05ab966dbd81d6ae384ab45";
    private const string GoldenPosition = "pos_8ba3a94873adbbc0a32842261a2c1918";

    [Fact]
    public void Ids_AreStableGoldenValues()
    {
        var decision = EngineIds.Decision(Config, "A2-ma10", "S1", Date, "Bar", 17, 0);

        Assert.Equal(GoldenDecision, decision);
        Assert.Equal(GoldenOrder, EngineIds.Order(decision, "entry", 1));
        Assert.Equal(GoldenOpportunity, EngineIds.Opportunity("A2-ma10", new ConfigHash("v1"), Date, "episode-17"));
        Assert.Equal(GoldenPosition, EngineIds.Position(Config, "A2-ma10", Date, decision));
    }

    [Fact]
    public void SameInputs_SameId_AcrossCulturesAndRepeats()
    {
        var baseline = EngineIds.Decision(Config, "A", "S", Date, "Bar", 5, 0);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(baseline, EngineIds.Decision(Config, "A", "S", Date, "Bar", 5, 0));
        }

        foreach (var culture in new[] { "tr-TR", "ar-SA", "th-TH", "de-DE" })
        {
            Assert.Equal(baseline, Culture.Under(culture, () => EngineIds.Decision(Config, "A", "S", Date, "Bar", 5, 0)));
        }
    }

    [Fact]
    public void EveryComponent_ChangesTheId()
    {
        var ids = new[]
        {
            EngineIds.Decision(Config, "A", "S", Date, "Bar", 5, 0),
            EngineIds.Decision(new ConfigHash("other"), "A", "S", Date, "Bar", 5, 0),
            EngineIds.Decision(Config, "B", "S", Date, "Bar", 5, 0),
            EngineIds.Decision(Config, "A", "T", Date, "Bar", 5, 0),
            EngineIds.Decision(Config, "A", "S", Date.AddDays(1), "Bar", 5, 0),
            EngineIds.Decision(Config, "A", "S", Date, "Timer", 5, 0),
            EngineIds.Decision(Config, "A", "S", Date, "Bar", 6, 0),
            EngineIds.Decision(Config, "A", "S", Date, "Bar", 5, 1),
        };

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void VariantsNeverShareAnIdNamespace()
    {
        Assert.NotEqual(
            EngineIds.Opportunity("A", new ConfigHash("v"), Date, "e1"),
            EngineIds.Opportunity("A2", new ConfigHash("v"), Date, "e1"));
        Assert.NotEqual(
            EngineIds.Position(Config, "A", Date, "dec_x"),
            EngineIds.Position(Config, "A2", Date, "dec_x"));
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
