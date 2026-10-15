using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserver.Commentary;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Tests.Dashboard;

/// <summary>Research-capture work: toasts, direction highlighting, fixed-strike Roll ΔPx, data quality, causal events / strategy state, caption and the screenshot schedule.</summary>
public class ResearchDashboardTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 14, 3, 45, 0, TimeSpan.Zero);          // 09:15 IST
    static readonly DateOnly Day = new(2026, 10, 14);
    static readonly DateOnly Expiry = new(2026, 10, 20);

    // =====================================================================================
    // Toasts
    // =====================================================================================

    static AdaptiveCommentaryEventRow Commentary(string identity, CommentaryLifecycle lifecycle = CommentaryLifecycle.Flipped, EventBias bias = EventBias.Short, int minutes = 10) => new()
    {
        Id = minutes, SessionId = 1, TradeDate = Day, BarSeq = minutes, OccurredAtUtc = T0.AddMinutes(minutes), EventType = CommentaryEventType.SellerExpansion, EventBias = bias,
        MarketRegime = MarketRegime.Bearish, Lifecycle = lifecycle, EvidenceAgreement = EvidenceAgreement.High, Severity = CommentarySeverity.High, PrimaryEvidenceJson = "[]",
        ConfirmationEvidenceJson = "[]", ContradictionEvidenceJson = "[]", DataQualityJson = "[]", RenderedCommentary = "x", CommentaryVersion = CommentaryEvaluator.Version, EventIdentity = identity,
    };

    static PaperClosedTrade Closed(string id, string net, string strategy = "NPT1-MDX3") =>
        new(strategy, "MAIN", id, "Long", "PE", "SYM", T0.AddMinutes(20), T0.AddMinutes(30), "OppositeSignal", "118.30", "131.20", 650, "8385.00", "0.00", net, "20.00", "-3.00", null, null, "Complete", 3, 5, "25200", "TKN");

    static PaperOpenPosition Open(string id, string strategy = "NPT1-MDX3") =>
        new(strategy, "MAIN", id, "Long", "PE", "SYM", "25200", T0.AddMinutes(20), "118.30", 650, 650, "NotTriggered", null, null, null, "118.00", "-1300", "5.00", "-3.00", null, null, "Complete", false, T0, "TKN");

    static PaperLiveView View(DateOnly date, IReadOnlyList<PaperOpenPosition> open, IReadOnlyList<PaperClosedTrade> closed) => new(
        new PaperLiveRunStatus("run", date, false, "owner", 1, 1, 1, 10, T0, T0, null, null, 350000m, Array.Empty<string>()), open, closed, [], [],
        new PaperRiskPanel(350000m, 0m, 0m, 0, 0m, "Healthy", [], []));

    [Fact]
    public void Toasts_TheFirstObservationOnlyEstablishesABaseline_NoHistoricalStorm()
    {
        var tracker = new ToastTracker();
        var history = Enumerable.Range(1, 30).Select(i => ToastFactory.FromCommentary(Commentary($"id{i}", minutes: i))).ToList();
        Assert.Empty(tracker.Observe("commentary", history));                       // opening the page / a browser refresh replays nothing
        Assert.Empty(tracker.Observe("commentary", history));
        Assert.Equal(30, tracker.SeenCount);

        var fresh = tracker.Observe("commentary", history.Append(ToastFactory.FromCommentary(Commentary("id31", minutes: 31))));
        Assert.Equal("commentary:id31", Assert.Single(fresh).Id);                       // a NEW event after the baseline: exactly one toast
        Assert.Empty(tracker.Observe("commentary", history.Append(ToastFactory.FromCommentary(Commentary("id31", minutes: 31)))));   // the same durable identity never toasts twice
    }

    [Fact]
    public void Toasts_EachSourceHasItsOwnBaseline_AndAnUnrelatedRefreshAddsNothing()
    {
        var tracker = new ToastTracker();
        Assert.Empty(tracker.Observe("commentary", []));
        var ledger = ToastFactory.FromLedger(View(Day, [Open("p1")], [Closed("p2", "5000.00")]), Day).ToList();
        Assert.Empty(tracker.Observe("trades", ledger));                            // trades baseline is separate from the commentary baseline
        Assert.Empty(tracker.Observe("trades", ledger));                            // an unrelated ledger refresh: nothing new
        Assert.Empty(tracker.Observe("commentary", []));

        var opened = tracker.Observe("trades", ToastFactory.FromLedger(View(Day, [Open("p1"), Open("p3")], [Closed("p2", "5000.00")]), Day));
        Assert.Equal("open:p3", Assert.Single(opened).Id);
        var closed = tracker.Observe("trades", ToastFactory.FromLedger(View(Day, [Open("p1")], [Closed("p2", "5000.00"), Closed("p3", "-1500.00")]), Day));
        Assert.Equal(["close:p3"], closed.Select(c => c.Id));                       // p3's open was already toasted; only its close is new
    }

    [Fact]
    public void Toasts_OnlyTodaysRunProducesTradeToasts_AndOnlyNpt1()
    {
        var yesterday = ToastFactory.FromLedger(View(Day.AddDays(-1), [Open("p1")], [Closed("p2", "10.00")]), Day);
        Assert.Empty(yesterday);
        var other = ToastFactory.FromLedger(View(Day, [Open("p1", "ADAPTIVE-X")], [Closed("p2", "10.00", "ADAPTIVE-X")]), Day);
        Assert.Empty(other);
        Assert.Empty(ToastFactory.FromLedger(null, Day));
    }

    [Fact]
    public void Toasts_TheTrackerIsBounded()
    {
        var tracker = new ToastTracker(capacity: 20);
        tracker.Observe("commentary", []);
        for (var i = 0; i < 100; i++) tracker.Observe("commentary", [ToastFactory.FromCommentary(Commentary($"e{i}", minutes: i % 59 + 1))]);
        Assert.Equal(20, tracker.SeenCount);
    }

    [Fact]
    public void Toasts_ContentUsesThePersistedTerminology_AndTheDurableLedger()
    {
        var c = ToastFactory.FromCommentary(Commentary("x", CommentaryLifecycle.Flipped, EventBias.Short));
        Assert.Equal("COMMENTARY", c.Title);
        Assert.Contains("FLIPPED → SHORT", c.Lines);
        Assert.Contains("Regime BEARISH", c.Lines);
        Assert.Contains("Agreement HIGH", c.Lines);
        Assert.Contains("09:25:00", c.Lines);                                        // IST time of the persisted event
        Assert.DoesNotContain(c.Lines, l => l.Contains('%') || l.Contains("probab", StringComparison.OrdinalIgnoreCase));

        var open = ToastFactory.FromOpen(Open("p1"));
        Assert.Equal("TRADE OPENED", open.Title);
        Assert.Equal(["MDX3", "PE 25200", "₹118.30 x650"], open.Lines);
        var close = ToastFactory.FromClosed(Closed("p1", "8385.00"));
        Assert.Equal("TRADE CLOSED", close.Title);
        Assert.Equal(["MDX3", "PE 25200", "Exit ₹131.20", "Net +₹8,385", "OppositeSignal"], close.Lines);
        Assert.Equal("profit", close.Tone);
        Assert.Equal("loss", ToastFactory.FromClosed(Closed("p2", "-250.00")).Tone);
        Assert.Contains("Net -₹250", ToastFactory.FromClosed(Closed("p2", "-250.00")).Lines);
    }

    [Fact]
    public void Toasts_CannotReachAnyTradingOrNotificationWritePath()
    {
        var injected = typeof(ToastHost).GetProperties(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null).Select(p => p.PropertyType).ToHashSet();
        Assert.Equal(
            new HashSet<Type> { typeof(AdaptiveObserverDataService), typeof(AdaptiveCommentaryDataService), typeof(PaperLiveDataService), typeof(Npt1LiveQuotes) }, injected);   // read-only sources only
        var written = typeof(ToastHost).Assembly.GetType("NiftySignal.Dashboard.Components.Dashboard.ToastHost")!;
        Assert.DoesNotContain(written.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly), m => m.Name.Contains("Send", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Order", StringComparison.OrdinalIgnoreCase));
    }

    // =====================================================================================
    // Direction highlighting: sign only
    // =====================================================================================

    [Theory]
    [InlineData(5.0, "val-pos", "pos")]
    [InlineData(0.0001, "val-pos", "pos")]
    [InlineData(-5.0, "val-neg", "neg")]
    [InlineData(0.0, "", "zero")]
    public void DirectionSign_PositiveNegativeZero_NoMagnitudeThreshold(double raw, string cssClass, string sign)
    {
        var p = RatioPart.Of(raw, raw, 100);
        Assert.Equal(cssClass, RatioFormat.SignClass(p));
        Assert.Equal(sign, RatioFormat.SignName(p));
    }

    [Fact]
    public void DirectionSign_UnavailableIsNeutral_AndNaNIsNeverColoured()
    {
        Assert.Equal("", RatioFormat.SignClass(RatioPart.None));
        Assert.Equal("none", RatioFormat.SignName(RatioPart.None));
        Assert.Equal("none", RatioFormat.SignName(RatioPart.Of(double.NaN, 1, 1)));
        Assert.Equal("none", RatioFormat.SignName(RatioPart.Of(double.PositiveInfinity, 1, 1)));
    }

    [Fact]
    public void DirectionSign_TheOiDirectionFollowsItsOwnPutMinusCallSign()
    {
        AdaptiveOptionStrikeBarRow Side(OptionType side, long delta) => new()
        {
            SessionId = 1, BarSeq = 1, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, EndAvailableAtUtc = T0, Expiry = Expiry, Strike = 25000, Side = side,
            Token = side.ToString(), TradingSymbol = "X", OiDelta = delta, OiStart = 1000,
        };

        var putBuild = OptionDirectionRatios.Core(Side(OptionType.Call, 100), Side(OptionType.Put, 900)).Oi.Direction;     // PE - CE = +800
        Assert.Equal(800, putBuild.Raw);
        Assert.Equal("val-pos", RatioFormat.SignClass(putBuild));
        var callBuild = OptionDirectionRatios.Core(Side(OptionType.Call, 900), Side(OptionType.Put, 100)).Oi.Direction;    // PE - CE = -800
        Assert.Equal(-800, callBuild.Raw);
        Assert.Equal("val-neg", RatioFormat.SignClass(callBuild));
        Assert.Equal("zero", RatioFormat.SignName(OptionDirectionRatios.Core(Side(OptionType.Call, 500), Side(OptionType.Put, 500)).Oi.Direction));
    }

    [Fact]
    public void DirectionSign_NoStylesheetRuleKeysOnMagnitude()
    {
        var css = File.ReadAllText(Path.Combine(NiftySignal.Tests.Trading.Data.TradingModelTests.RepoRoot(), "NiftySignal.Dashboard", "Components", "Dashboard", "AdaptiveObserverPanel.razor.css"));
        var rules = System.Text.RegularExpressions.Regex.Matches(css, @"adaptive-direction[^{]*\{[^}]*\}").Select(m => m.Value).ToList();
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.DoesNotContain("strong", r, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rules, r => r.Contains("orange", StringComparison.OrdinalIgnoreCase) || r.Contains("yellow", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["val-pos", "val-neg"], rules.Where(r => r.Contains("rgba")).Select(r => r.Contains("val-pos") ? "val-pos" : "val-neg"));
    }

    // =====================================================================================
    // Fixed-strike Roll ΔPx
    // =====================================================================================

    static AdaptiveOptionStrikeBarRow Row(int bar, OptionType side, double start, double end, string? token = null, double strike = 25000) => new()
    {
        SessionId = 1, BarSeq = bar, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, EndAvailableAtUtc = T0.AddMinutes(bar), Expiry = Expiry, Strike = strike, Side = side,
        Token = token ?? (side == OptionType.Call ? "CE" : "PE"), TradingSymbol = "X", PriceStart = start, PriceEnd = end, PriceChange = end - start,
    };

    static (FixedStrikeCatalog, FixedStrikeContract) Catalog()
    {
        var catalog = new FixedStrikeCatalog(Expiry, [new FixedStrikeContract(25000, "CE", "CE25000", "PE", "PE25000"), new FixedStrikeContract(25050, "CE2", "CE25050", "PE2", "PE25050")]);
        return (catalog, catalog.Strikes[0]);
    }

    static IReadOnlyList<(int, DateTimeOffset)> Bars(params int[] seqs) => seqs.Select(s => (s, T0.AddMinutes(s))).ToList();

    [Fact]
    public void RollPrice_UsesOnlyTheExactContractsOverTheWindow_AndDirectionIsCeMinusPe()
    {
        var (catalog, contract) = Catalog();
        var stored = new List<AdaptiveOptionStrikeBarRow>();
        for (var b = 1; b <= 5; b++)
        {
            stored.Add(Row(b, OptionType.Call, 100 + b, 101 + b));                 // CE: 101..106 across bars 1..5 (start 100+b, end 101+b)
            stored.Add(Row(b, OptionType.Put, 80 - b, 79 - b));                    // PE: falling
        }

        // decoys of OTHER strikes / tokens in the same bars must never enter the window
        stored.Add(Row(3, OptionType.Call, 9999, 9999, token: "CE2", strike: 25050));
        stored.Add(Row(4, OptionType.Put, 1, 1, token: "OTHER"));
        var h = FixedStrikeProjection.Assemble(catalog, contract, Bars(4, 5), stored, rollWindowBars: 3);
        var bar5 = h.Rows.Single(r => r.BarSeq == 5);
        Assert.Equal(new RollPrice(106 - 103, 103), bar5.CeRoll);                  // window bars 3..5: PriceStart(bar 3) = 103, PriceEnd(bar 5) = 106
        Assert.Equal(new RollPrice(74 - 77, 77), bar5.PeRoll);

        var core = OptionDirectionRatios.Core(bar5);
        Assert.Equal(3d, core.RollPrice.Ce.Raw);
        Assert.Equal(-3d, core.RollPrice.Pe.Raw);
        Assert.Equal(6d, core.RollPrice.Direction.Raw);                            // CE - PE
        Assert.Equal(3d / 103d * 100d, core.RollPrice.Ce.Ratio!.Value, 9);
        Assert.Equal(-3d / 77d * 100d, core.RollPrice.Pe.Ratio!.Value, 9);
        Assert.Equal(6d / (103d + 77d) * 100d, core.RollPrice.Direction.Ratio!.Value, 9);        // combined rolling-start denominator, not CE% - PE%
    }

    [Fact]
    public void RollPrice_AMissingBarOrWindowIsUnavailable_NeverBridgedOrSubstituted()
    {
        var (catalog, contract) = Catalog();
        var stored = new List<AdaptiveOptionStrikeBarRow>();
        for (var b = 1; b <= 5; b++)
        {
            stored.Add(Row(b, OptionType.Call, 100, 101));
            if (b != 4) stored.Add(Row(b, OptionType.Put, 80, 79));                // the PE bar 4 is missing from the window
        }

        var h = FixedStrikeProjection.Assemble(catalog, contract, Bars(2, 5), stored, rollWindowBars: 3);
        Assert.NotNull(h.Rows.Single(r => r.BarSeq == 5).CeRoll);
        Assert.Null(h.Rows.Single(r => r.BarSeq == 5).PeRoll);                      // incomplete PE window: unavailable
        Assert.Null(h.Rows.Single(r => r.BarSeq == 2).CeRoll);                      // fewer than N bars exist so far: unavailable
        var core = OptionDirectionRatios.Core(h.Rows.Single(r => r.BarSeq == 5));
        Assert.Null(core.RollPrice.Pe.Raw);
        Assert.Null(core.RollPrice.Direction.Raw);                                  // no direction without both sides
        Assert.Equal("—", RatioFormat.Cell(core.RollPrice.Pe));
        Assert.Null(FixedStrikeProjection.Assemble(catalog, contract, Bars(5), stored, rollWindowBars: 0).Rows[0].CeRoll);     // no window configured
    }

    [Fact]
    public void RollPrice_NonPositiveStartPriceHasNoRatio_AndAvgAggregatesNumeratorsAndDenominators()
    {
        var zeroStart = OptionDirectionRatios.Core(new FixedStrikeRow(5, T0, null, null, new RollPrice(2, 0), new RollPrice(1, 10)));
        Assert.Null(zeroStart.RollPrice.Ce.Ratio);
        Assert.Equal(2d, zeroStart.RollPrice.Ce.Raw);
        Assert.Null(zeroStart.RollPrice.Direction.Ratio);                           // the combined start is only valid when both starts are positive

        var rows = new[]
        {
            OptionDirectionRatios.Core(new FixedStrikeRow(5, T0, null, null, new RollPrice(3, 100), new RollPrice(-2, 50))),
            OptionDirectionRatios.Core(new FixedStrikeRow(6, T0, null, null, new RollPrice(1, 300), new RollPrice(4, 50))),
            OptionDirectionRatios.Core(new FixedStrikeRow(7, T0, null, null, null, null)),
        };
        var avg = OptionDirectionRatios.Average(rows.Select(r => r.RollPrice));
        Assert.Equal(2d, avg.Ce.Raw);                                               // arithmetic mean of the available raw values
        Assert.Equal(4d / 400d * 100d, avg.Ce.Ratio!.Value, 9);                     // SUM(roll delta) / SUM(rolling start), not mean of 3% and 0.33%
        Assert.Equal(2d / 100d * 100d, avg.Pe.Ratio!.Value, 9);
        Assert.Equal(((3d - -2d) + (1d - 4d)) / (150d + 350d) * 100d, avg.Direction.Ratio!.Value, 9);
        Assert.Equal(1d, avg.Direction.Raw);                                        // mean of (5, -3) directions... = 1
    }

    // =====================================================================================
    // Data quality, events and strategy state
    // =====================================================================================

    static AdaptiveSessionStateRow SessionRow(string model = AdaptiveFixedClockV1.ModelVersion, double? strong = 0.2) => new()
    {
        Id = 1, TradeDate = Day, ModelVersion = model, SourceBranch = "b", SourceCommitSha = "abcdef1234567890", BuildUtc = T0, FutureToken = "F", FutureSymbol = "F", FutureExpiry = Expiry,
        OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0.AddMinutes(15), EstimatorName = "e", CreatedAtUtc = T0, StrongThreshold = strong, WeeklyOptionExpiry = Expiry, BaseBarVolume = 13650, RollingWindowBars = 10,
    };

    static AdaptiveObserverSnapshot Snapshot(AdaptiveSessionStateRow session, IReadOnlySet<int>? invalidFutures = null, IReadOnlyDictionary<int, AdaptiveBasisSupplementalRow>? basis = null) => new(
        session, new AdaptiveObserverRuntimeRow { SessionId = 1, RuntimeStatus = AdaptiveRuntimeStatus.Live, LastHeartbeatUtc = T0 }, [], [], [], [], 10,
        InvalidFuturesSupplementalBars: invalidFutures, BasisSupplemental: basis);

    [Fact]
    public void DataQuality_IsOkOnlyWhenNothingKnownIsMissing_AndNamesEachFact()
    {
        var ok = ResearchDataQuality.Evaluate(Snapshot(SessionRow()), null, false, false);
        Assert.Empty(ok);
        Assert.Equal("Data: OK", ResearchDataQuality.Text(ok));

        var invalid = ResearchDataQuality.Evaluate(Snapshot(SessionRow(), invalidFutures: new HashSet<int> { 3 }), null, false, false);
        Assert.Equal(["1 projection(s) failed verification"], invalid);
        Assert.NotEqual("Data: OK", ResearchDataQuality.Text(invalid));              // never "OK" with a known-invalid projection in range

        var basis = new Dictionary<int, AdaptiveBasisSupplementalRow> { [4] = new() { SessionId = 1, BarSeq = 4, MetricsVersion = "v", Status = "StaleEnd" }, [5] = new() { SessionId = 1, BarSeq = 5, MetricsVersion = "v", Status = FuturesBasisBar.StatusUsable } };
        Assert.Equal(["Basis stale/unavailable in 1 bar(s)"], ResearchDataQuality.Evaluate(Snapshot(SessionRow(), basis: basis), null, false, false));

        var all = ResearchDataQuality.Evaluate(Snapshot(SessionRow(strong: null)), null, quoteUnavailable: true, commentaryDegraded: true);
        Assert.Equal(["pinned quote unavailable", "commentary degraded", "Strong/Weak uncalibrated"], all);
        Assert.Equal("Data: pinned quote unavailable | commentary degraded | Strong/Weak uncalibrated", ResearchDataQuality.Text(all));
        Assert.Empty(ResearchDataQuality.Evaluate(Snapshot(SessionRow("adaptive-v1", strong: null)), null, false, false));     // uncalibrated is a fixed-clock fact only
    }

    [Fact]
    public void DataQuality_CountsMissingFixedStrikeRows()
    {
        var (catalog, contract) = Catalog();
        var history = FixedStrikeProjection.Assemble(catalog, contract, Bars(1, 2), [Row(1, OptionType.Call, 1, 2), Row(1, OptionType.Put, 1, 2), Row(2, OptionType.Call, 1, 2)]);
        Assert.Equal(["1 unavailable fixed-strike option row(s)"], ResearchDataQuality.Evaluate(Snapshot(SessionRow()), history, false, false));
    }

    [Fact]
    public void Events_NeverIncludeAnythingAfterTheBoundary_AndFollowTheStatedPolicy()
    {
        var all = new[]
        {
            new ResearchEvent("a", T0.AddMinutes(5), "Commentary", "A"), new ResearchEvent("b", T0.AddMinutes(10), "Trade", "B"), new ResearchEvent("c", T0.AddMinutes(20), "Trade", "C"),
            new ResearchEvent("d", T0.AddMinutes(30), "Commentary", "D"), new ResearchEvent("e", T0.AddMinutes(31), "Commentary", "E-future"),
        };
        var boundary = T0.AddMinutes(30);
        var (latest, latestPolicy) = ResearchEvents.Select(all, boundary, null);
        Assert.Equal(["c", "d"], latest.Select(e => e.Id).TakeLast(2));
        Assert.Equal(["b", "c", "d"], latest.Select(e => e.Id));                    // the latest three at or before the boundary, oldest first
        Assert.DoesNotContain(latest, e => e.Id == "e");
        Assert.Contains("latest 3 events at or before this boundary", latestPolicy);

        var (since, sincePolicy) = ResearchEvents.Select(all, boundary, T0.AddMinutes(10));
        Assert.Equal(["c", "d"], since.Select(e => e.Id));                          // strictly after the previous capture, up to the boundary (inclusive)
        Assert.Contains("since the previous scheduled capture", sincePolicy);
        Assert.Empty(ResearchEvents.Select(all, T0.AddMinutes(1), null).Events);     // before any event
        Assert.Equal(6, ResearchEvents.Select(Enumerable.Range(1, 20).Select(i => new ResearchEvent($"x{i}", T0.AddMinutes(i), "Trade", "t")), boundary, T0).Events.Count);
    }

    [Fact]
    public void StrategyState_IsReconstructedAsOfTheBoundary_NeverFromCurrentState_AndUnavailableWhenNotEstablishable()
    {
        // MD5: PE opened at 20 and closed at 30; MDX3: CE open since 25 (still open now); PE5: nothing.
        var md5 = Closed("p1", "100.00", "NPT1-MD5");                                          // entry +20, exit +30
        var mdx3 = Open("p2", "NPT1-MDX3") with { OptionType = "CE", EntryAt = T0.AddMinutes(25) };
        var view = View(Day, [mdx3], [md5]);
        Assert.Equal("MD5: FLAT | MDX3: FLAT | PE5: FLAT", ResearchEvents.StrategyStateAsOf(view, Day, T0.AddMinutes(10)));
        Assert.Equal("MD5: PE | MDX3: FLAT | PE5: FLAT", ResearchEvents.StrategyStateAsOf(view, Day, T0.AddMinutes(22)));
        Assert.Equal("MD5: PE | MDX3: CE | PE5: FLAT", ResearchEvents.StrategyStateAsOf(view, Day, T0.AddMinutes(27)));
        Assert.Equal("MD5: FLAT | MDX3: CE | PE5: FLAT", ResearchEvents.StrategyStateAsOf(view, Day, T0.AddMinutes(35)));         // MD5 had exited; MDX3 is still open
        Assert.Equal("strategy state unavailable", ResearchEvents.StrategyStateAsOf(null, Day, T0));
        Assert.Equal("strategy state unavailable", ResearchEvents.StrategyStateAsOf(View(Day.AddDays(-1), [], []), Day, T0));        // another date's run is never substituted
    }

    [Fact]
    public void LedgerEvents_AreOpenAndCloseOfNpt1Only_WithDurableIdentity()
    {
        var events = ResearchEvents.FromLedger(View(Day, [Open("p1")], [Closed("p2", "250.00")])).OrderBy(e => e.AtUtc).ToList();
        Assert.Equal(["o:p1", "o:p2", "x:p2"], events.Select(e => e.Id));
        Assert.Contains("MDX3 OPEN PE 25200 @ 118.30", events[0].Text);
        Assert.Contains("CLOSE PE 25200 | Net +₹250 | OppositeSignal", events[2].Text);
        Assert.Empty(ResearchEvents.FromLedger(View(Day, [Open("p1", "OTHER")], [])));
    }

    // =====================================================================================
    // Caption
    // =====================================================================================

    static ResearchModel Model(bool auto = true) => new(Day, 37, T0.AddMinutes(87), AdaptiveFixedClockV1.ModelVersion, "Fixed Volume 13,650 | start 09:15", Expiry, 25200, 25200, auto,
        "Data: OK", [], "MD5: PE | MDX3: FLAT | PE5: CE", [], "latest 3 events at or before this boundary", "adaptive-fixed13650-v1 | session source abcdef12 | Dashboard 1234abcd", true);

    [Fact]
    public void Caption_DescribesTheResearchViewCompactlyAndWithoutSecrets()
    {
        var job = new AdaptiveScreenshotJobRow { Id = 9, SessionId = 1, TradeDate = Day, TargetBarSeq = 37, Kind = AdaptiveScreenshotKind.FiveBars, TriggeredAtUtc = T0 };
        var text = ResearchCaption.Build(job, Model(), "deadbeefcafe");
        Assert.Equal("NIFTY Research | 14-Oct-2026 | FiveBars\nBar 37 @ 10:42:00 IST\nFixed Volume 13,650 | start 09:15\nExpiry 20-Oct | Strike 25200 | Auto Follow ON\ncompleted rows only\nData: OK\nDashboard SHA deadbeefcafe", text);
        Assert.DoesNotContain("NOTIONAL", text);
        Assert.DoesNotContain("ATM±2", text);
        Assert.True(text.Length < 400);
        Assert.Contains("Auto Follow OFF", ResearchCaption.Build(job, Model(auto: false), "x"));
        Assert.Contains("research context unavailable", ResearchCaption.Build(job, null, "x"));
    }

    // =====================================================================================
    // Screenshot schedule
    // =====================================================================================

    static AdaptiveObserverDbContext Observer() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    static async Task<AdaptiveSessionStateRow> Seed(AdaptiveObserverDbContext db, string model, int completed, DateTimeOffset createdAt, bool historicalSeed = false)
    {
        var s = SessionRow(model);
        s.Id = 0; s.CreatedAtUtc = createdAt; s.IsHistoricalSeed = historicalSeed; s.OpeningWindowEndUtc = T0.AddMinutes(15);
        db.Sessions.Add(s); await db.SaveChangesAsync();
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = s.Id, RuntimeStatus = AdaptiveRuntimeStatus.Live, LastHeartbeatUtc = T0, LastCompletedBarSeq = completed });
        for (var b = 1; b <= completed; b++)
        {
            db.FutureBars.Add(new AdaptiveFutureBarRow { SessionId = s.Id, BarSeq = b, StartAvailableAtUtc = T0.AddMinutes(b - 1), EndAvailableAtUtc = T0.AddMinutes(b) });
        }

        await db.SaveChangesAsync();
        return s;
    }

    static AdaptiveCommentaryEventRow CommentaryAt(long session, int id, DateTimeOffset at, CommentaryLifecycle lifecycle) => new()
    {
        Id = 0, SessionId = session, TradeDate = Day, BarSeq = 1, OccurredAtUtc = at, EventType = CommentaryEventType.BuyerExpansion, EventBias = EventBias.Long, MarketRegime = MarketRegime.Bullish, Lifecycle = lifecycle,
        EvidenceAgreement = EvidenceAgreement.High, Severity = CommentarySeverity.Info, PrimaryEvidenceJson = "[]", ConfirmationEvidenceJson = "[]", ContradictionEvidenceJson = "[]", DataQualityJson = "[]",
        RenderedCommentary = "x", CommentaryVersion = CommentaryEvaluator.Version, EventIdentity = $"{CommentaryEvaluator.Version}:{session}:{id}",
    };

    [Fact]
    public async Task Schedule_FixedClock_FirstCompletedBarThenEveryFifthAndNeverIncomplete()
    {
        await using var db = Observer();
        var s = await Seed(db, AdaptiveFixedClockV1.ModelVersion, completed: 12, createdAt: T0.AddSeconds(101));
        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, T0.AddHours(1), default);
        var jobs = await db.ScreenshotJobs.OrderBy(j => j.TargetBarSeq).ToListAsync();
        Assert.Equal([(1, AdaptiveScreenshotKind.Initialization), (6, AdaptiveScreenshotKind.FiveBars), (11, AdaptiveScreenshotKind.FiveBars)], jobs.Select(j => (j.TargetBarSeq, j.Kind)));
        Assert.All(jobs, j => Assert.Equal(s.Id, j.SessionId));

        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, T0.AddHours(1), default);              // idempotent
        Assert.Equal(3, await db.ScreenshotJobs.CountAsync());
        var runtime = await db.Runtime.SingleAsync(); runtime.LastCompletedBarSeq = 16;
        db.FutureBars.AddRange(Enumerable.Range(13, 4).Select(b => new AdaptiveFutureBarRow { SessionId = s.Id, BarSeq = b, StartAvailableAtUtc = T0.AddMinutes(b - 1), EndAvailableAtUtc = T0.AddMinutes(b) }));
        await db.SaveChangesAsync();
        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, T0.AddHours(1), default);
        Assert.Equal([1, 6, 11, 16], (await db.ScreenshotJobs.OrderBy(j => j.TargetBarSeq).ToListAsync()).Select(j => j.TargetBarSeq));
    }

    [Fact]
    public async Task Schedule_FixedClock_WaitsForTheFirstCompletedBar_AndSkipsASessionThatIsNotLive()
    {
        await using var db = Observer();
        var s = await Seed(db, AdaptiveFixedClockV1.ModelVersion, completed: 0, createdAt: T0.AddSeconds(102));
        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, T0.AddHours(1), default);
        Assert.Empty(await db.ScreenshotJobs.ToListAsync());                                                  // no completed bar yet: nothing is captured, no incomplete bar
        var runtime = await db.Runtime.SingleAsync(); runtime.RuntimeStatus = AdaptiveRuntimeStatus.Rebuilding; runtime.LastCompletedBarSeq = 7;
        await db.SaveChangesAsync();
        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, T0.AddHours(1), default);
        Assert.Empty(await db.ScreenshotJobs.ToListAsync());                                                  // recovery in progress: not yet
        Assert.Equal(s.Id, (await db.Sessions.SingleAsync()).Id);
    }

    [Fact]
    public async Task Schedule_EventCaptures_AreDeduplicatedPerBoundary_AndNeverDuplicateAScheduledOne()
    {
        await using var db = Observer();
        var s = await Seed(db, AdaptiveFixedClockV1.ModelVersion, completed: 11, createdAt: T0.AddSeconds(103));
        // commentary: a Confirmed and a Flipped event inside bar 8 (same boundary), a mere Strengthening (ignored), and one inside scheduled bar 6
        db.CommentaryEvents.AddRange(
            CommentaryAt(s.Id, 1, T0.AddMinutes(7).AddSeconds(10), CommentaryLifecycle.Confirmed),
            CommentaryAt(s.Id, 2, T0.AddMinutes(7).AddSeconds(40), CommentaryLifecycle.Flipped),
            CommentaryAt(s.Id, 3, T0.AddMinutes(8).AddSeconds(5), CommentaryLifecycle.Strengthening),
            CommentaryAt(s.Id, 4, T0.AddMinutes(5).AddSeconds(30), CommentaryLifecycle.Confirmed));
        await db.SaveChangesAsync();
        IReadOnlyList<ScreenshotEventCandidate> ledger = [new("o:p1", T0.AddMinutes(8).AddSeconds(20), "Trade"), new("x:p1", T0.AddMinutes(9).AddSeconds(1), "Trade")];

        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, T0.AddHours(1), default, (_, _) => Task.FromResult(ledger));
        var jobs = await db.ScreenshotJobs.OrderBy(j => j.TargetBarSeq).ToListAsync();
        Assert.Equal(
            new[] { (1, AdaptiveScreenshotKind.Initialization), (6, AdaptiveScreenshotKind.FiveBars), (8, AdaptiveScreenshotKind.EventTriggered), (9, AdaptiveScreenshotKind.EventTriggered),
                (10, AdaptiveScreenshotKind.EventTriggered), (11, AdaptiveScreenshotKind.FiveBars) },
            jobs.Select(j => (j.TargetBarSeq, j.Kind)).ToArray());
        Assert.Single(jobs, j => j.TargetBarSeq == 8);                                                        // two commentary events on one boundary: ONE screenshot
        Assert.Single(jobs, j => j.TargetBarSeq == 6);                                                        // the event inside a scheduled bar adds no second capture
        Assert.DoesNotContain(jobs, j => j.Kind == AdaptiveScreenshotKind.EventTriggered && j.TargetBarSeq == 6);

        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, T0.AddHours(1), default, (_, _) => Task.FromResult(ledger));          // idempotent
        Assert.Equal(jobs.Count, await db.ScreenshotJobs.CountAsync());
        Assert.All(jobs, j => Assert.True(j.TargetBarSeq <= 11));                                             // completed bars only
    }

    [Fact]
    public async Task Schedule_AdaptiveV1Sessions_KeepTheirOriginalScheduleExactly()
    {
        await using var db = Observer();
        var s = await Seed(db, "adaptive-v1", completed: 12, createdAt: T0.AddSeconds(104));
        db.CommentaryEvents.Add(CommentaryAt(s.Id, 1, T0.AddMinutes(7), CommentaryLifecycle.Confirmed));
        await db.SaveChangesAsync();
        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, T0.AddHours(1), default, (_, _) => Task.FromResult<IReadOnlyList<ScreenshotEventCandidate>>([new("o:p", T0.AddMinutes(8), "Trade")]));
        var jobs = await db.ScreenshotJobs.OrderBy(j => j.TargetBarSeq).ToListAsync();
        Assert.DoesNotContain(jobs, j => j.Kind == AdaptiveScreenshotKind.EventTriggered);                    // no event shots for an old session
        Assert.Equal(AdaptiveScreenshotKind.Initialization, jobs[0].Kind);
        Assert.All(jobs.Skip(1), j => Assert.Equal(AdaptiveScreenshotKind.FiveBars, j.Kind));
        Assert.All(jobs.Skip(1), j => Assert.Equal(0, (j.TargetBarSeq - jobs[0].TargetBarSeq) % 5));
    }

    [Fact]
    public async Task Schedule_DeliveryUncertainIsNeverResent()
    {
        await using var db = Observer();
        db.ScreenshotJobs.Add(new AdaptiveScreenshotJobRow { SessionId = null, TradeDate = Day, TargetBarSeq = 3, Kind = AdaptiveScreenshotKind.EventTriggered, Status = AdaptiveScreenshotStatus.Sending, TriggeredAtUtc = T0, CreatedAtUtc = T0, NextAttemptUtc = T0 });
        await db.SaveChangesAsync();
        await AdaptiveScreenshotSchedule.RecoverInterruptedAsync(db, default);
        var job = await db.ScreenshotJobs.SingleAsync();
        Assert.Equal(AdaptiveScreenshotStatus.DeliveryUncertain, job.Status);
        Assert.Contains("Interrupted upload", job.LastError);
    }

    // =====================================================================================
    // Renderer contract
    // =====================================================================================

    [Fact]
    public void Renderer_RequiresNamedPrimaryGrids_NotAnExactTableCount()
    {
        Assert.Equal(["futures-compact", "options-core-direction", "options-context", "residual-compact"], AdaptiveScreenshotRenderer.RequiredGrids.Select(g => g.Grid));
        Assert.Equal(["research-header", "research-data-quality", "recent-events"], AdaptiveScreenshotRenderer.RequiredBlocks);
        Assert.False(AdaptiveScreenshotRenderer.RequiredGrids.Single(g => g.Grid == "residual-compact").StartsAtTarget);      // legitimately empty before the 09:30 anchor
        var source = File.ReadAllText(Path.Combine(NiftySignal.Tests.Trading.Data.TradingModelTests.RepoRoot(), "NiftySignal.Dashboard", "Services", "AdaptiveScreenshotRenderer.cs"));
        Assert.DoesNotContain("ToHaveCountAsync(3)", source);                                                                  // the obsolete "exactly three tables" assumption is gone
        Assert.Contains("did not render; image not uploaded", source);                                                         // a missing primary grid fails safely
        Assert.Contains("must not be sticky", source);
    }
}
