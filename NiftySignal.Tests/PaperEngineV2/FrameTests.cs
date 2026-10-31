using System.Reflection;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Metrics;
using static NiftySignal.Tests.PaperEngineV2.FrameFixtures;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Phase 6b: the immutable MarketStateFrame - identity, validation, canonical payload / hash, immutability and causality helpers.</summary>
public class FrameTests
{
    static void AssertInvalid(Action create, string fragment)
    {
        var ex = Assert.Throws<FrameValidationException>(create);
        Assert.Contains(fragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    static MarketStateFrame Make(Func<FrameIdentity, FrameIdentity>? id = null, IEnumerable<MetricValue>? metrics = null, IEnumerable<QuoteSnapshot>? quotes = null, VixAvailability? vix = null, BasketIdentity? basket = null)
    {
        var identity = (id ?? (x => x))(Identity(1, 5));
        return MarketStateFrame.Create(identity, basket ?? BasketId(), metrics ?? [new MetricValue(H("fut.strict:pct"), SummaryWindowId.W5, 2.5)], quotes ?? [], [], vix ?? new VixAvailability(identity.BarEndAvailableAt.AddSeconds(-1), 1, "Fresh"));
    }

    [Fact]
    public void Frame_HasAStableIdentityDecisionKeyAndCanonicalHash()
    {
        var a = Light(1, 5); var b = Light(1, 5);
        Assert.Equal(a.Hash, b.Hash); Assert.Equal(a.PayloadJson, b.PayloadJson); Assert.Equal(64, a.Hash.Length);
        Assert.Equal(MetricRegistry.SourceModel, a.Identity.SourceModel);
        Assert.Equal((End(5), 1_005L), (a.Identity.DecisionKey.AvailableAt, a.Identity.DecisionKey.SourceTickId));
        Assert.True(a.Identity.DecisionKey.CompareTo(Light(1, 6).Identity.DecisionKey) < 0);
        Assert.True(new DecisionKey(End(5), 1).CompareTo(new DecisionKey(End(5), 2)) < 0);              // same instant: ordered by source tick id
    }

    [Fact]
    public void Payload_RoundTripsExactly_AndEveryRealChangeChangesTheHash()
    {
        var f = Light(1, 5, strict: 3.14159);
        var back = FrameSerializer.Read(f.PayloadJson);
        Assert.Equal(f.PayloadJson, back.PayloadJson); Assert.Equal(f.Hash, back.Hash);
        Assert.Equal(f.Metrics().ToList(), back.Metrics().ToList());
        Assert.Equal(f.Quotes, back.Quotes); Assert.Equal(f.Identity, back.Identity);
        Assert.Equal(f.Basket.Slots, back.Basket.Slots);
        var changes = new[]
        {
            Light(1, 5, strict: 3.14160), Light(1, 6, strict: 3.14159), Light(2, 5, strict: 3.14159), Light(1, 5, strict: 3.14159, verified: false), Light(1, 5, strict: 3.14159, duration: 59),
            Light(1, 5, strict: 3.14159, tick: 7), Light(1, 5, strict: 3.14159, omitStrict: true), Light(1, 5, strict: 3.14159, end: End(5).AddTicks(1)), Light(1, 5, strict: 3.14159, variant: "x"),
        };
        Assert.All(changes, c => Assert.NotEqual(f.Hash, c.Hash));
        Assert.Equal(changes.Length, changes.Select(c => c.Hash).Distinct().Count());
    }

    [Fact]
    public void Read_RejectsMalformedOrInconsistentPayloads()
    {
        var good = Light(1, 5).PayloadJson;
        AssertInvalid(() => FrameSerializer.Read("{ not json"), "malformed");
        AssertInvalid(() => FrameSerializer.Read("{}"), "malformed");
        AssertInvalid(() => FrameSerializer.Read(good.Replace("fixed13650-summary-v1", "npt1-m1-v2")), "source model");
        AssertInvalid(() => FrameSerializer.Read(good.Replace("\"verified\":true", "\"verified\":false")), "unverified frame must state why");
    }

    [Fact]
    public void Create_RejectsAForeignClockSchemaAndInconsistentIdentity()
    {
        AssertInvalid(() => Make(i => i with { SourceModel = "npt1-m1-v2" }), "source model");                         // the clocks are never mixed
        AssertInvalid(() => Make(i => i with { Schema = "pev2-frame/9" }), "schema");
        AssertInvalid(() => Make(i => i with { BarSeq = 0 }), "BarSeq");
        AssertInvalid(() => Make(i => i with { SameInstantOrdinal = -1 }), "Ordinal");
        AssertInvalid(() => Make(i => i with { BarStartAvailableAt = i.BarEndAvailableAt.AddSeconds(1) }), "before it starts");
        AssertInvalid(() => Make(i => i with { DurationSeconds = -1 }), "DurationSeconds");
        AssertInvalid(() => Make(i => i with { DurationSeconds = double.NaN }), "DurationSeconds");
        AssertInvalid(() => Make(i => i with { Verified = false, UnverifiedReason = null }), "must state why");
        AssertInvalid(() => Make(i => i with { Verified = true, UnverifiedReason = "x" }), "no unverified reason");
    }

    [Fact]
    public void Create_RejectsInvalidMetricValues_UnavailableMeansOmittedNeverNaN()
    {
        AssertInvalid(() => Make(metrics: [new MetricValue(H("fut.strict:pct"), SummaryWindowId.W5, double.NaN)]), "not finite");
        AssertInvalid(() => Make(metrics: [new MetricValue(H("fut.strict:pct"), SummaryWindowId.W5, double.PositiveInfinity)]), "not finite");
        AssertInvalid(() => Make(metrics: [new MetricValue(MetricRegistry.Count, SummaryWindowId.W5, 1)]), "unknown metric handle");
        AssertInvalid(() => Make(metrics: [new MetricValue(-1, SummaryWindowId.W5, 1)]), "unknown metric handle");
        AssertInvalid(() => Make(metrics: [new MetricValue(0, (SummaryWindowId)9, 1)]), "unknown window");
        AssertInvalid(() => Make(metrics: [new MetricValue(0, SummaryWindowId.W5, 1), new MetricValue(0, SummaryWindowId.W5, 2)]), "twice");
        var f = Make(metrics: [new MetricValue(H("fut.strict:pct"), SummaryWindowId.W5, 2.5)]);
        Assert.True(f.TryRead(H("fut.strict:pct"), SummaryWindowId.W5, out var v)); Assert.Equal(2.5, v);
        Assert.False(f.TryRead(H("fut.strict:pct"), SummaryWindowId.W10, out _));        // another window is not a substitute
        Assert.False(f.TryRead(H("fut.enriched:pct"), SummaryWindowId.W5, out _));
        Assert.False(f.TryRead(-1, SummaryWindowId.W5, out _)); Assert.False(f.TryRead(int.MaxValue / 8, SummaryWindowId.W5, out _));
        Assert.Equal(1, f.PresentMetricCount);
    }

    [Fact]
    public void Create_RefusesLookAhead_QuotesAndVixAvailableAfterTheBarEnd()
    {
        var end = End(5);
        QuoteSnapshot Q(DateTimeOffset at) => new("CE26000", 120, 121, 650, 650, at, 1);
        Assert.NotNull(Make(quotes: [Q(end)]));                                                                           // exactly at the boundary is available
        AssertInvalid(() => Make(quotes: [Q(end.AddTicks(1))]), "look-ahead");
        AssertInvalid(() => Make(vix: new VixAvailability(end.AddMilliseconds(1), 0, "Fresh")), "look-ahead");
        AssertInvalid(() => Make(quotes: [new QuoteSnapshot("PE99999", 120, 121, 1, 1, end, 1)]), "not in the basket");        // no substitution
        AssertInvalid(() => Make(quotes: [new QuoteSnapshot("CE26000", 0, 121, 1, 1, end, 1)]), "valid two-sided");
        AssertInvalid(() => Make(quotes: [new QuoteSnapshot("CE26000", 122, 121, 1, 1, end, 1)]), "valid two-sided");        // crossed
        AssertInvalid(() => Make(quotes: [Q(end), Q(end)]), "two quotes");
    }

    [Fact]
    public void Frame_IsImmutable_OwnsCopies_AndExposesNoMutableState()
    {
        var metrics = new List<MetricValue> { new(H("fut.strict:pct"), SummaryWindowId.W5, 2.5) };
        var quotes = new List<QuoteSnapshot> { new("CE26000", 120, 121, 650, 650, End(5), 1) };
        var slots = BasketId().Slots.ToList();
        var basket = new BasketIdentity(Expiry, 26_000, 50, slots, ["note"]);
        var f = MarketStateFrame.Create(Identity(1, 5), basket, metrics, quotes, [], new VixAvailability(null, null, "Unavailable"));
        var hash = f.Hash; var payload = f.PayloadJson;
        metrics.Add(new MetricValue(H("fut.enriched:pct"), SummaryWindowId.W5, 9)); metrics[0] = new MetricValue(H("fut.strict:pct"), SummaryWindowId.W5, -99);
        quotes.Clear(); slots.Clear();
        Assert.Equal(hash, f.Hash); Assert.Equal(payload, f.PayloadJson);
        Assert.True(f.TryRead(H("fut.strict:pct"), SummaryWindowId.W5, out var v)); Assert.Equal(2.5, v);
        Assert.Single(f.Quotes); Assert.Equal(10, f.Basket.Slots.Count);

        Assert.False(f.Quotes is Array); Assert.False(f.QuoteIssues is Array); Assert.False(f.Basket.Slots is Array); Assert.False(f.Basket.Problems is Array);   // cannot be cast back to a mutable array
        Assert.Throws<NotSupportedException>(() => ((IList<QuoteSnapshot>)f.Quotes).Add(f.Quotes[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<BasketSlot>)f.Basket.Slots).Clear());
        Assert.NotSame(slots, f.Basket.Slots);

        // no public setter or public mutable field anywhere on the frame, its identity or its parts
        foreach (var t in new[] { typeof(MarketStateFrame), typeof(FrameIdentity), typeof(BasketIdentity), typeof(BasketSlot), typeof(QuoteSnapshot), typeof(QuoteIssue), typeof(VixAvailability) })
        {
            Assert.All(t.GetProperties(BindingFlags.Public | BindingFlags.Instance), p => Assert.True(p.SetMethod is null || p.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit"), $"{t.Name}.{p.Name} has a public setter"));
            Assert.All(t.GetFields(BindingFlags.Public | BindingFlags.Instance), fld => Assert.True(fld.IsInitOnly, $"{t.Name}.{fld.Name} is a mutable field"));
        }

        Assert.Equal(["TryRead"], typeof(NiftySignal.PaperEngineV2.Core.Conditions.IMarketStateFrame).GetMethods().Select(m => m.Name));
    }

    [Fact]
    public void ZeroDurationSiblings_AreNeverEligibleFillBarsForEachOther()
    {
        var t = End(5);
        var first = Light(1, 5, end: t, tick: 77, ordinal: 0);
        var sibling = Light(1, 6, end: t, tick: 77, ordinal: 1, duration: 0);
        var sibling2 = Light(1, 7, end: t, tick: 77, ordinal: 2, duration: 0);
        var later = Light(1, 8, end: t.AddSeconds(30), tick: 90);
        Assert.True(sibling.Identity.IsZeroDuration); Assert.True(sibling2.Identity.IsZeroDuration); Assert.False(first.Identity.IsZeroDuration);
        Assert.False(sibling.IsEligibleFillBarFor(first));         // same availability instant: the same market event
        Assert.False(sibling2.IsEligibleFillBarFor(first));
        Assert.False(first.IsEligibleFillBarFor(sibling));
        Assert.True(later.IsEligibleFillBarFor(first));            // a genuinely later bar
        Assert.True(later.IsEligibleFillBarFor(sibling2));
        Assert.False(first.IsEligibleFillBarFor(later));           // never an earlier bar
        Assert.False(first.IsEligibleFillBarFor(first));
    }

    [Fact]
    public void DescribedPeriodAndAvailability_AreDistinct()
    {
        var f = Light(1, 5, duration: 90);
        Assert.Equal(End(5).AddSeconds(-90), f.Identity.BarStartAvailableAt);               // what the bar DESCRIBES starts earlier ...
        Assert.Equal(End(5), f.Identity.DecisionKey.AvailableAt);                           // ... but it only BECAME available at its end
        Assert.All(f.Quotes, q => Assert.True(q.AvailableAt <= f.Identity.DecisionKey.AvailableAt));
    }
}
