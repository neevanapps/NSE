using System.Text.Json.Nodes;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Metrics;
using NiftySignal.PaperEngineV2.Execution;
using static NiftySignal.Tests.PaperEngineV2.FrameFixtures;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>The 09:20 IST early-entry versions (version 2) of the three TOTAL strategies and the verified-bar warm-up that makes an early entry safe. The deployed version-1 files stay byte-identical.</summary>
public class EarlyEntryTests
{
    static readonly string[] Ids = ["TOTAL_ACTIVITY_5_10", "TOTAL_VOTE_4_OF_6", "TOTAL_VOTE_5_OF_6"];

    // identities of the files deployed for the 2026-11-04 session (they appear in the ledger: they must never move)
    static readonly Dictionary<string, string> V1Hashes = new()
    {
        ["TOTAL_ACTIVITY_5_10"] = "d158ef122ca7fd0251801a9ea32d3aa5e178717032f0a738f116090fda8d801a",
        ["TOTAL_VOTE_4_OF_6"] = "4f64b92148d8a0305ff9996c99288f6fa079162823ab6251fd928b414849dc19",
        ["TOTAL_VOTE_5_OF_6"] = "cf687b2d12df9b10267a94b1fcf3807f7b970cb46894e4ad05d418fe895769d3",
    };

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "NiftySignal.slnx"))) d = d.Parent;
        return Path.Combine(d!.FullName, "paper-engine-v2-strategies");
    }

    static string V1Json(string id) => File.ReadAllText(Path.Combine(Root(), id + ".json"));

    static string V2Json(string id) => File.ReadAllText(Path.Combine(Root(), "next-0920", id + ".json"));

    static StrategyConfig Parse(string json)
    {
        var r = StrategyConfigParser.Parse(json);
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        return r.Config!;
    }

    [Theory]
    [InlineData("TOTAL_ACTIVITY_5_10")] [InlineData("TOTAL_VOTE_4_OF_6")] [InlineData("TOTAL_VOTE_5_OF_6")]
    public void DeployedVersionOneFiles_KeepTheirExactIdentity(string id)
    {
        var c = Parse(V1Json(id));
        Assert.Equal(1, c.Identity.Version);
        Assert.Equal(V1Hashes[id], c.Identity.Hash);
        Assert.Equal(new TimeOnly(9, 30), c.Filters.EntryFrom);
        Assert.Null(c.Filters.MinCompletedBars);
        Assert.DoesNotContain("minCompletedBars", c.CanonicalJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TOTAL_ACTIVITY_5_10")] [InlineData("TOTAL_VOTE_4_OF_6")] [InlineData("TOTAL_VOTE_5_OF_6")]
    public void VersionTwo_ChangesOnlyTheEntryStart_AndAddsTheWarmup(string id)
    {
        var v1 = Parse(V1Json(id)); var v2 = Parse(V2Json(id));
        Assert.Equal(id, v2.Identity.StrategyId);
        Assert.Equal(2, v2.Identity.Version);
        Assert.NotEqual(v1.Identity.Hash, v2.Identity.Hash);
        Assert.Equal(new TimeOnly(9, 20), v2.Filters.EntryFrom);
        Assert.Equal(new TimeOnly(15, 0), v2.Filters.EntryBefore);
        Assert.Equal(10, v2.Filters.MinCompletedBars);
        // everything else is identical: the signal predicates, metrics, windows, thresholds, limits, execution policy, fees and exit mechanism
        var a = JsonNode.Parse(V1Json(id))!.AsObject(); var b = JsonNode.Parse(V2Json(id))!.AsObject();
        a.Remove("version"); b.Remove("version"); a.Remove("filters"); b.Remove("filters");
        Assert.Equal(a.ToJsonString(), b.ToJsonString());
        var f1 = JsonNode.Parse(V1Json(id))!["filters"]!.AsObject(); var f2 = JsonNode.Parse(V2Json(id))!["filters"]!.AsObject();
        Assert.Equal(f1["entryBefore"]!.ToJsonString(), f2["entryBefore"]!.ToJsonString());
        Assert.Equal(f1["requireFreshVix"]!.ToJsonString(), f2["requireFreshVix"]!.ToJsonString());
        Assert.Equal(5, v2.Risk.MaxDailyTrades); Assert.Equal(1_500d, v2.Risk.MaxDailyLoss); Assert.Equal(2, v2.Lots); Assert.Equal(50_000d, v2.VirtualCapital); Assert.Equal(1, v2.MaxConcurrent);
        Assert.Equal(new PremiumRange(100, 150), v2.Premium);
        Assert.Equal(StrategyConfigParser.ExitMechanismOppositeSignalOrSessionEnd, v2.ExitMechanism);
        Assert.Equal(v1.Execution, v2.Execution);
    }

    [Fact]
    public void TheThreeVersionTwoFiles_FormOneAcceptedSet_AndCannotRunBesideVersionOne()
    {
        var v2 = StrategySet.Load(Ids.Select(i => (i, V2Json(i))).ToList());
        Assert.True(v2.SetAccepted); Assert.Empty(v2.Rejected); Assert.Equal(3, v2.Active.Count);
        var both = StrategySet.Load(Ids.Select(i => (i + ".v1", V1Json(i))).Concat(Ids.Select(i => (i + ".v2", V2Json(i)))).ToList());
        Assert.False(both.SetAccepted);                                                                  // same strategyId twice: the swap at a session boundary replaces the files, it never mixes them
    }

    [Theory]
    [InlineData("09:19:59", 10, false)]      // never before 09:20
    [InlineData("09:20:00", 10, true)]
    [InlineData("09:20:00", 12, true)]
    [InlineData("09:20:00", 9, false)]       // fewer than ten completed bars: refused
    [InlineData("09:20:00", null, false)]    // an early start without the warm-up: refused
    [InlineData("09:29:59", null, false)]
    [InlineData("09:30:00", null, true)]     // the original contract
    public void EarlyStart_IsValidOnlyWithAtLeastTenWarmupBars(string from, int? bars, bool valid)
    {
        var o = JsonNode.Parse(V1Json("TOTAL_VOTE_4_OF_6"))!.AsObject();
        o["filters"]!["entryFrom"] = from;
        if (bars is { } n) o["filters"]!["minCompletedBars"] = n;
        Assert.Equal(valid, StrategyConfigParser.Parse(o.ToJsonString()).IsValid);
    }

    // ---- engine: signals before ten verified bars never enter; the clock alone never opens an entry ----

    static readonly string[] Six = ["opt.total.price.dir:raw", "opt.total.enriched.dir:raw", "opt.total.oi.dir:raw", "opt.total.microdev_inv.dir:raw", "opt.total.ofi.dir:raw", "opt.total.activity.dir:raw"];

    static DateTimeOffset Ist(int h, int m, int s = 0) => new DateTimeOffset(Day.ToDateTime(new TimeOnly(h, m, s)), TimeSpan.FromHours(5.5)).ToUniversalTime();

    static MarketStateFrame Fr(int seq, DateTimeOffset end, double sign = 1, bool coverage = true, bool verified = true, IEnumerable<ExecFixtures.Q>? quotes = null)
    {
        var m = new List<MetricValue>();
        foreach (var id in Six) m.Add(new MetricValue(H(id), SummaryWindowId.W5, 2 * sign));
        m.Add(new MetricValue(H("opt.total.activity.dir:raw"), SummaryWindowId.W10, 2 * sign));
        if (coverage) { m.Add(new MetricValue(H("opt.total.coverage_full"), SummaryWindowId.W5, 1)); m.Add(new MetricValue(H("opt.total.coverage_full"), SummaryWindowId.W10, 1)); }
        m.Add(new MetricValue(H("vix.fresh"), SummaryWindowId.W5, 1));
        QuoteSnapshot Snap(ExecFixtures.Q q) => new(q.Token, q.Bid, q.Ask, q.Qty, q.Qty, end.AddMilliseconds(-q.AgeMs), 5_000 + seq);
        return MarketStateFrame.Create(Identity(1, seq, end, verified: verified), BasketId(), m, (quotes ?? ExecFixtures.Default).Select(Snap).ToList(), [], new VixAvailability(end.AddSeconds(-2), 2, "Fresh"));
    }

    static PaperEngine Engine(string id, bool v2 = true) => ExecFixtures.Engine([CompiledStrategy.Compile(Parse(v2 ? V2Json(id) : V1Json(id)))]);

    static List<T> Of<T>(PaperEngine e) where T : PaperEvent => ExecFixtures.Of<T>(e);

    [Fact]
    public void NoEntryBefore0920_EvenWithTenBars()
    {
        var e = Engine("TOTAL_VOTE_4_OF_6");
        ExecFixtures.Run(e, Enumerable.Range(1, 11).Select(i => Fr(i, Ist(9, 8, 0).AddSeconds(i * 50))).ToList());      // eleven bars, the last one at 09:17:30
        Assert.Empty(Of<EntryOrderPlaced>(e));
        Assert.Contains(Of<EntryRejected>(e), r => r.Reason == RejectionReason.OutsideEntryWindow);
    }

    [Fact]
    public void At0920_WithFewerThanTenVerifiedBars_TheSignalIsRefused_NotTheClock()
    {
        var e = Engine("TOTAL_VOTE_4_OF_6");
        ExecFixtures.Run(e, Enumerable.Range(1, 9).Select(i => Fr(i, Ist(9, 21, 0).AddSeconds(i * 20))).ToList());     // nine bars, all after 09:20, all signalling
        Assert.Empty(Of<EntryOrderPlaced>(e));
        var rej = Of<EntryRejected>(e);
        Assert.NotEmpty(rej);
        Assert.All(rej, r => Assert.Equal(RejectionReason.WarmupIncomplete, r.Reason));
    }

    [Fact]
    public void TenthVerifiedBar_AllowsTheEntry_AndTheFillNeedsAStrictlyLaterQuote()
    {
        var e = Engine("TOTAL_VOTE_4_OF_6");
        var frames = Enumerable.Range(1, 10).Select(i => Fr(i, Ist(9, 20, 30).AddSeconds(i * 20))).ToList();
        ExecFixtures.Run(e, frames);
        var placed = Assert.Single(Of<EntryOrderPlaced>(e));
        Assert.Empty(Of<EntryFilled>(e));                                                                   // the decision frame itself never fills
        ExecFixtures.Run(e, [Fr(11, Ist(9, 20, 30).AddSeconds(11 * 20), quotes: [new("CE26000", 125.5, 127, AgeMs: 100)])]);
        var fill = Assert.Single(Of<EntryFilled>(e));
        Assert.True(fill.QuoteAvailableAt > placed.Decision.AvailableAt);
    }

    [Fact]
    public void UnverifiedBars_DoNotCountTowardsTheWarmup()
    {
        var e = Engine("TOTAL_VOTE_4_OF_6");
        var frames = Enumerable.Range(1, 12).Select(i => Fr(i, Ist(9, 22, 0).AddSeconds(i * 15), verified: i > 4)).ToList();      // bars 1-4 unverified: only eight verified bars by bar 12
        ExecFixtures.Run(e, frames);
        Assert.Empty(Of<EntryOrderPlaced>(e));
        ExecFixtures.Run(e, [Fr(13, Ist(9, 22, 0).AddSeconds(13 * 15)), Fr(14, Ist(9, 22, 0).AddSeconds(14 * 15))]);
        Assert.NotEmpty(Of<EntryOrderPlaced>(e));                                                           // the tenth verified bar
    }

    [Fact]
    public void MissingOrPartialCoverage_StillSuppressesTheSignal_AfterTheWarmup()
    {
        var e = Engine("TOTAL_VOTE_5_OF_6");
        ExecFixtures.Run(e, Enumerable.Range(1, 14).Select(i => Fr(i, Ist(9, 25, 0).AddSeconds(i * 20), coverage: false)).ToList());
        Assert.Empty(Of<EntryOrderPlaced>(e));
        Assert.DoesNotContain(Of<EntryRejected>(e), r => r.Reason == RejectionReason.WarmupIncomplete);     // the signal itself was Unknown/False, so no entry was even attempted
    }

    [Fact]
    public void VersionOne_IsUnchanged_NoWarmupAndNoEntryBefore0930()
    {
        var e = Engine("TOTAL_VOTE_4_OF_6", v2: false);
        ExecFixtures.Run(e, Enumerable.Range(1, 5).Select(i => Fr(i, Ist(9, 25, 0).AddSeconds(i * 20))).ToList());
        Assert.Empty(Of<EntryOrderPlaced>(e));
        Assert.All(Of<EntryRejected>(e), r => Assert.Equal(RejectionReason.OutsideEntryWindow, r.Reason));
        ExecFixtures.Run(e, [Fr(6, Ist(9, 30, 5)), Fr(7, Ist(9, 30, 25))]);
        Assert.NotEmpty(Of<EntryOrderPlaced>(e));                                                           // at 09:30 with only a handful of bars, exactly as deployed
    }

    [Fact]
    public void Warmup_IsRebuiltIdentically_ByAJournalReplay_AndNoEntryAtOrAfter1500()
    {
        var frames = Enumerable.Range(1, 12).Select(i => Fr(i, Ist(9, 21, 0).AddSeconds(i * 20))).ToList();
        var a = Engine("TOTAL_VOTE_4_OF_6"); ExecFixtures.Run(a, frames);
        var b = Engine("TOTAL_VOTE_4_OF_6"); ExecFixtures.Run(b, frames.Select(f => FrameSerializer.Read(f.PayloadJson)).ToList());
        Assert.Equal(a.Events.Select(x => x.ToString()), b.Events.Select(x => x.ToString()));
        var late = Engine("TOTAL_VOTE_4_OF_6");
        ExecFixtures.Run(late, Enumerable.Range(1, 12).Select(i => Fr(i, Ist(15, 0, 0).AddSeconds(i * 20))).ToList());
        Assert.Empty(Of<EntryOrderPlaced>(late));
    }
}
