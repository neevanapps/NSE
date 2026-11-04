using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.MarketSummary;
using NiftySignal.Tests.Trading.Data;
using SummaryMetric = NiftySignal.MarketSummary.Metric;

namespace NiftySignal.Tests.MarketSummary;

/// <summary>
/// 2026-11-02 Dashboard simplification: the retained sections and their order, the single compact TOTAL SUMMARY, the display-only MicroDev inversion (exactly once, raw values untouched), sign-only colouring, the
/// full-width OPTION CONTEXT, the incomplete-bar view of the sticky quote and the screenshot renderer's grid contract. Presentation only: nothing here touches a metric calculation.
/// </summary>
public class DashboardSimplificationTests
{
    static readonly DateOnly Expiry = new(2026, 11, 3);

    static async Task<string> RenderAsync(MarketSummaryView? view)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            System.Net.WebUtility.HtmlDecode((await renderer.RenderComponentAsync<MarketSummarySection>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = view }))).ToHtmlString()));
    }

    /// <summary>CE and PE bars chosen so every Direction (CE − PE, OI = PE − CE) has a known sign. <paramref name="mode"/>: +1 CE-strong, -1 PE-strong (every sign flips), 0 CE and PE identical (every Direction is zero).</summary>
    static MarketSummaryView View(int bars, int mode, bool micro = true, Func<int, string, bool>? skip = null)
    {
        var listed = Enumerable.Range(0, 9).Select(i => 25_800d + i * 50).Select(s => new ListedStrike(s, $"CE{s:0}", $"PE{s:0}")).ToList();
        var basket = BasketResolver.Resolve(Expiry, 26_000, listed);
        var all = new List<ContractBar>();
        for (var seq = 1; seq <= bars; seq++)
            foreach (var c in basket.Contracts)
            {
                if (skip?.Invoke(seq, c.Token!) == true) continue;
                var strong = mode == 0 || (c.IsCall == (mode > 0));      // the "strong" side; with mode 0 both sides carry the strong values
                var k = strong ? 1 : -1;
                all.Add(new ContractBar(seq, c.Token!, c.IsCall, 100, strong ? 110 : 95, strong ? 500 : 100, 50 * k, 100, 100, 0, 30 * k, 1000, strong ? 1010 : 990,
                    20, strong ? 21 : 19.5, 0.42, strong ? 0.45 : 0.43, 5, strong ? 5.5 : 5.2,
                    micro ? 0.4 * k : null, micro ? 0.2 * k : null, micro ? 40 : null, 20 * k, 100));
            }

        var durations = Enumerable.Range(1, bars).ToDictionary(i => i, _ => 30d);
        var fut = Enumerable.Range(1, bars).ToDictionary(i => i, i => new FuturesBar(i, 30, 26_000, 26_001, 13_650, 100, 100, 100, 10, 5, 1000, 1010, 0.1, 0.1, 20, 20, 5, 50));
        return new MarketSummaryView(bars, basket, FuturesSummaryCalculator.Compute(fut, bars), OptionSummaryCalculator.ComputeAll(basket, new ContractHistory(all, durations), bars), null);
    }

    static string Table(string html, string grid) => Regex.Match(html, "<table[^>]*data-grid=\"" + Regex.Escape(grid) + "\".*?</table>", RegexOptions.Singleline).Value;

    static List<string> Headers(string html, string grid) =>
        Regex.Matches(Regex.Match(Table(html, grid), "<thead[^>]*>.*?</thead>", RegexOptions.Singleline).Value, @"<th(?:\s[^>]*)?>(.*?)</th>", RegexOptions.Singleline).Select(m => m.Groups[1].Value.Trim()).ToList();

    static List<(string Class, string Sign, string Text)> Cells(string html, string grid, string window)
    {
        var row = Regex.Match(Table(html, grid), "<tr data-window=\"" + Regex.Escape(window) + "\"[^>]*>.*?</tr>", RegexOptions.Singleline).Value;
        Assert.True(row.Length > 0, "row " + window);
        return Regex.Matches(row, "<td([^>]*)>(.*?)</td>", RegexOptions.Singleline).Select(m =>
            (Regex.Match(m.Groups[1].Value, "class=\"([^\"]*)\"").Groups[1].Value, Regex.Match(m.Groups[1].Value, "data-sign=\"([^\"]*)\"").Groups[1].Value, m.Groups[2].Value)).ToList();
    }

    static readonly string[] TotalHeaders =
        ["Window", "Coverage", "Price Dir", "Roll Dir", "Strict Dir", "Enriched Dir", "OI Dir", "MicroDev Dir (Inv)", "OFI Dir", "IV Dir (Inv)", "Delta Dir (Inv)", "Vega Dir (Inv)", "Activity Dir"];

    // ---- 1 / 2 / 4: retained sections, order, the exact TOTAL columns and rows ----

    [Fact]
    public async Task MarketSummary_RendersOnlyFuturesAndTotal_InThatOrder_WithoutAtmItmOtmOrTheOldTwoTableTotal()
    {
        var html = await RenderAsync(View(120, 1));
        var grids = Regex.Matches(html, "<table[^>]*data-grid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["summary-futures", "summary-total"], grids);
        Assert.True(html.IndexOf("FUTURES SUMMARY", StringComparison.Ordinal) < html.IndexOf("TOTAL SUMMARY", StringComparison.Ordinal));
        foreach (var gone in new[] { "ATM SUMMARY", "ITM SUMMARY", "OTM SUMMARY", "summary-total-core", "summary-total-context" }) Assert.DoesNotContain(gone, html);
        Assert.Equal(1, Regex.Matches(html, "TOTAL SUMMARY").Count);      // no duplicated section
    }

    [Fact]
    public async Task TotalSummary_HasExactlyTheThirteenColumns_AndFiveWindowRows()
    {
        var html = await RenderAsync(View(120, 1));
        Assert.Equal(TotalHeaders, Headers(html, "summary-total"));
        var rows = Regex.Matches(Table(html, "summary-total"), "<tr data-window=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["5 Bars", "10 Bars", "50 Bars", "100 Bars", "Day"], rows);
        foreach (var w in rows) Assert.Equal(13, Cells(html, "summary-total", w).Count);
    }

    // ---- 3: the TOTAL cells are the calculator's Direction values, scaled for display only ----

    [Fact]
    public async Task TotalSummary_CellsAreTheTenContractDirectionValues_AndCoverageIsPreserved()
    {
        var view = View(120, 1, skip: (seq, tok) => tok == "CE25900" && seq == 118);
        var html = await RenderAsync(view);
        var total = view.Grids.Single(g => g.Kind == GridKind.Total);
        Assert.Equal(10, total.Expected);
        foreach (var r in total.Rows)
        {
            var cells = Cells(html, "summary-total", r.Window.Name);
            Assert.Equal(r.Coverage.Text, Regex.Replace(cells[1].Text, " · .*$", ""));
            var d = r.Direction!;
            if (r.Window.Bars is 5 or 10)
                Assert.StartsWith(r.Coverage.Status == CoverageStatus.Full ? "10/10 – FULL" : "9/10 – PARTIAL (levels 10/10)", cells[1].Text);
            Assert.Equal(RatioFormat.Cell(new RatioPart(d.Price.Raw, d.Price.Num, d.Price.Den), 2), cells[2].Text);
            Assert.Equal(RatioFormat.Cell(new RatioPart(d.Oi.Raw, d.Oi.Num, d.Oi.Den), 0), cells[6].Text);                                  // OI keeps the PE − CE convention of the contract
            // IV / Delta / Vega Dir are displayed INVERTED (numerator and raw negated together, denominator untouched, zero stays zero); Delta and Vega are scaled ×100 exactly once
            static double? Neg(double? x) => x is { } v && double.IsFinite(v) ? (v == 0d ? 0d : -v) : x;
            Assert.Equal(RatioFormat.Cell(new RatioPart(Neg(d.Iv.Raw), Neg(d.Iv.Num), d.Iv.Den), 2), cells[9].Text);
            Assert.Equal(RatioFormat.Cell(new RatioPart(DisplayScale.Times100(Neg(d.Delta.Raw)), Neg(d.Delta.Num), d.Delta.Den), 2), cells[10].Text);   // ×100 once
            Assert.Equal(RatioFormat.Cell(new RatioPart(DisplayScale.Times100(Neg(d.Vega.Raw)), Neg(d.Vega.Num), d.Vega.Den), 2), cells[11].Text);
        }

        Assert.Contains("9/10 – PARTIAL", Cells(html, "summary-total", "5 Bars")[1].Text);
    }

    // ---- 5 / 6: MicroDev inversion, once, never doubled; Delta / Vega ×100 once ----

    [Fact]
    public void MicroDevInversion_PositiveNegativeZeroNullAndUnavailable()
    {
        Assert.Equal(-0.25, MicroDevDisplay.Invert(0.25));
        Assert.Equal(0.25, MicroDevDisplay.Invert(-0.25));
        Assert.Equal(0d, MicroDevDisplay.Invert(0d));
        Assert.False(double.IsNegative(MicroDevDisplay.Invert(0d)!.Value));                    // zero never becomes "-0.00"
        Assert.False(double.IsNegative(MicroDevDisplay.Invert(-0d)!.Value));
        Assert.Null(MicroDevDisplay.Invert((double?)null));
        Assert.True(double.IsNaN(MicroDevDisplay.Invert(double.NaN)!.Value));                  // non-finite is passed through (the formatter shows a dash), never flipped into a finite value
        Assert.Equal(RatioPart.None, MicroDevDisplay.Invert(RatioPart.None));
        Assert.Equal(SummaryMetric.None, MicroDevDisplay.Invert(SummaryMetric.None));

        var p = new RatioPart(0.3, 12d, 40d, 40d);
        var inv = MicroDevDisplay.Invert(p);
        Assert.Equal(-0.3, inv.Raw); Assert.Equal(-12d, inv.Num); Assert.Equal(40d, inv.Den); Assert.Equal(40d, inv.RawWeight);   // the denominator and the weight are untouched
        Assert.Equal(-p.Ratio, inv.Ratio);                                                      // the ratio flips sign with the raw value
        Assert.Equal(p, MicroDevDisplay.Invert(inv));                                           // it is a pure sign flip: applying it twice is the identity (so a double application would be visible)
        var side = new SideDirection(new(0.3, 12, 40), new(-0.1, -4, 40), new(0.4, 8, 40));
        var flipped = MicroDevDisplay.Invert(side);
        Assert.Equal(-0.3, flipped.Ce.Raw); Assert.Equal(0.1, flipped.Pe.Raw); Assert.Equal(-0.4, flipped.Direction.Raw);
    }

    [Theory]
    [InlineData(1, "adaptive-signed val-neg", "neg")]
    [InlineData(-1, "adaptive-signed val-pos", "pos")]
    [InlineData(0, "", "zero")]
    public async Task TotalSummary_MicroDevDir_IsShownInverted_ExactlyOnce(int mode, string cssClass, string sign)
    {
        var view = View(120, mode);
        var html = await RenderAsync(view);
        var raw = view.Grids.Single(g => g.Kind == GridKind.Total).Rows[0].Direction!.MicroDev;     // the untouched calculator value (CE − PE of the time-weighted MicroDev)
        var cell = Cells(html, "summary-total", "5 Bars")[7];
        Assert.Equal(cssClass, cell.Class); Assert.Equal(sign, cell.Sign);
        Assert.StartsWith(RatioFormat.Cell(new RatioPart(MicroDevDisplay.Invert(raw.Raw), MicroDevDisplay.Invert(raw.Num), raw.Den), 2), cell.Text);   // once; a second inversion would restore the raw sign
        if (mode != 0) Assert.NotEqual(RatioFormat.Cell(new RatioPart(raw.Raw, raw.Num, raw.Den), 2), cell.Text);
    }

    [Fact]
    public async Task FuturesSummary_MicroDev_IsInverted_AndLabelled_AndTheLegendIsShown()
    {
        var html = await RenderAsync(View(120, 1));
        Assert.Contains("MicroDev (Inv)", Headers(html, "summary-futures"));
        Assert.Contains("displayed = −raw MicroDev", html);
        // the futures bar carries MicroDev +0.1: displayed -0.10, red
        var micro = Cells(html, "summary-futures", "5 Bars")[6];
        Assert.StartsWith("-0.10", micro.Text);
        Assert.Equal("neg", micro.Sign);
    }

    [Fact]
    public async Task TotalSummary_UnavailableMicroDev_IsADash_NeverZeroAndNeverTinted()
    {
        var html = await RenderAsync(View(120, 1, micro: false));
        var cell = Cells(html, "summary-total", "50 Bars")[7];
        Assert.Equal("—", cell.Text); Assert.Equal("none", cell.Sign); Assert.Equal("", cell.Class);
    }

    [Fact]
    public async Task TotalSummary_DeltaAndVegaAreScaledOnce_NotTwice()
    {
        var html = await RenderAsync(View(30, 1));
        // CE delta change +0.03, PE +0.01 -> raw Direction 0.02 -> displayed inverted -2.00 (and not -200.00 / +2.00); vega CE +0.5 - PE +0.2 = 0.3 -> displayed -30.00
        var c = Cells(html, "summary-total", "5 Bars");
        Assert.StartsWith("-2.00 (", c[10].Text);
        Assert.StartsWith("-30.00 (", c[11].Text);
    }

    // ---- 7: the raw metrics are untouched by rendering ----

    [Fact]
    public async Task Rendering_DoesNotChangeTheViewOrTheRawMetrics()
    {
        var view = View(120, 1);
        var before = view.Grids.SelectMany(g => g.Rows).Select(r => (r.Direction, r.Ce, r.Pe)).ToList();
        var futuresBefore = view.Futures.Select(f => f.MicroDev).ToList();
        var first = await RenderAsync(view);
        var second = await RenderAsync(view);
        Assert.Equal(first, second);                                                           // deterministic: rendering has no memory (no accumulating inversion)
        Assert.Equal(before, view.Grids.SelectMany(g => g.Rows).Select(r => (r.Direction, r.Ce, r.Pe)).ToList());
        Assert.Equal(futuresBefore, view.Futures.Select(f => f.MicroDev).ToList());
    }

    [Fact]
    public void Inversion_LivesOnlyInTheDashboard_NeverInHostTradingOrV2Code()
    {
        var root = TradingModelTests.RepoRoot();
        foreach (var dir in new[] { "NiftySignal.Host", "NiftySignal.MarketSummary", "NiftySignal.PaperEngineV2.Core", "NiftySignal.PaperEngineV2.Execution", "NiftySignal.PaperEngineV2.Frames",
                     "NiftySignal.PaperEngineV2.Runtime", "NiftySignal.Trading.Core", "NiftySignal.Trading.Signals", "NiftySignal.Trading.Live", "NiftySignal.Npt1M1", "NiftySignal.AdaptiveObserver" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
                Assert.DoesNotContain("MicroDevDisplay", File.ReadAllText(file));
    }

    [Fact]
    public void DirectionDisplay_IsAPureSignFlip_ZeroAndMissingStayNeutral_DenominatorUntouched()
    {
        var p = new SummaryMetric(0.02, 8d, 40d);
        var inv = DirectionDisplay.Invert(p);
        Assert.Equal(-0.02, inv.Raw); Assert.Equal(-8d, inv.Num); Assert.Equal(40d, inv.Den);        // numerator and raw flip together; the denominator is untouched
        Assert.Equal(p, DirectionDisplay.Invert(inv));                                              // applying it twice is the identity, so a double inversion would be visible in the tests above
        Assert.Equal(SummaryMetric.None, DirectionDisplay.Invert(SummaryMetric.None));
        var zero = DirectionDisplay.Invert(new SummaryMetric(0d, 0d, 40d));
        Assert.False(double.IsNegative(zero.Raw!.Value)); Assert.False(double.IsNegative(zero.Num!.Value));   // never "-0.00"

        var side = new SideDirection(new(0.3, 12, 40), new(-0.1, -4, 40), new(0.4, 8, 40));
        var shown = DirectionDisplay.InvertDirectionOnly(side);
        Assert.Equal(side.Ce, shown.Ce); Assert.Equal(side.Pe, shown.Pe);                           // the CE and PE source values are NEVER inverted
        Assert.Equal(-0.4, shown.Direction.Raw); Assert.Equal(-8d, shown.Direction.Num); Assert.Equal(40d, shown.Direction.Den);
        Assert.Equal(["IV Dir (Inv)", "Delta Dir (Inv)", "Vega Dir (Inv)"], [DirectionDisplay.IvLabel, DirectionDisplay.DeltaLabel, DirectionDisplay.VegaLabel]);
    }

    [Fact]
    public void DirectionDisplay_LivesOnlyInTheDashboard_NeverInHostTradingOrV2Code()
    {
        var root = TradingModelTests.RepoRoot();
        foreach (var dir in new[] { "NiftySignal.Host", "NiftySignal.MarketSummary", "NiftySignal.PaperEngineV2.Core", "NiftySignal.PaperEngineV2.Execution", "NiftySignal.PaperEngineV2.Frames",
                     "NiftySignal.PaperEngineV2.Runtime", "NiftySignal.Trading.Core", "NiftySignal.Trading.Signals", "NiftySignal.Trading.Live", "NiftySignal.Npt1M1", "NiftySignal.AdaptiveObserver" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
                Assert.DoesNotContain("DirectionDisplay", File.ReadAllText(file));
    }

    // ---- 8: colour classes for the TOTAL Direction columns ----

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task TotalSummary_AllElevenDirectionColumns_AreTintedBySign(int mode)
    {
        var html = await RenderAsync(View(120, mode));
        // CE-strong (mode 1): Price, Roll, Strict, Enriched, OFI, Activity are CE − PE > 0; OI = PE − CE < 0; MicroDev, IV, Delta and Vega are displayed inverted (< 0), coloured from the DISPLAYED sign
        var positive = mode > 0 ? new[] { 2, 3, 4, 5, 8, 12 } : new[] { 6, 7, 9, 10, 11 };
        var negative = mode > 0 ? new[] { 6, 7, 9, 10, 11 } : new[] { 2, 3, 4, 5, 8, 12 };
        foreach (var w in new[] { "5 Bars", "10 Bars", "50 Bars", "100 Bars", "Day" })
        {
            var cells = Cells(html, "summary-total", w);
            foreach (var i in positive) { Assert.Equal("adaptive-signed val-pos", cells[i].Class); Assert.Equal("pos", cells[i].Sign); }
            foreach (var i in negative) { Assert.Equal("adaptive-signed val-neg", cells[i].Class); Assert.Equal("neg", cells[i].Sign); }
        }
    }

    [Fact]
    public async Task TotalSummary_ZeroDirections_AreNeutral_AndInsufficientRowsAreDashes()
    {
        var zero = await RenderAsync(View(120, 0));
        var cells = Cells(zero, "summary-total", "50 Bars");
        foreach (var i in Enumerable.Range(2, 11)) { Assert.Equal("", cells[i].Class); Assert.Equal("zero", cells[i].Sign); }
        var shortHistory = await RenderAsync(View(7, 1));
        var c = Cells(shortHistory, "summary-total", "100 Bars");
        Assert.Contains("INSUFFICIENT", c[1].Text);
        foreach (var i in Enumerable.Range(2, 11)) { Assert.Equal("—", c[i].Text); Assert.Equal("", c[i].Class); }
    }

    // ---- 11 / 12 / 14: the incomplete-bar view ----

    static readonly DateTimeOffset T0 = new(2026, 11, 2, 4, 0, 0, TimeSpan.Zero);

    static BarProgress Progress(long qty, int lastBar, DateTimeOffset heartbeat, string status = "Live", DateTimeOffset? started = null, DateTimeOffset? tick = null, long target = 13_650) =>
        new(qty, target, qty * 100d / target, lastBar, status, heartbeat, null, started, tick);

    [Fact]
    public void IncompleteBar_ProgressesWithinABar_AndResetsWhenTheNextBarStarts()
    {
        var started = T0.AddSeconds(-120);
        var a = IncompleteBarView.From(Progress(11_505, 150, T0, started: started, tick: T0), T0.AddSeconds(1));
        Assert.Equal(IncompleteBarState.Live, a.State); Assert.Equal(151, a.BarSeq); Assert.Equal(11_505L, a.Quantity);
        Assert.Equal("11,505 / 13,650 · 84.3%", a.QuantityText);
        Assert.Equal("2:01", IncompleteBarView.Duration(a.Elapsed));
        var b = IncompleteBarView.From(Progress(13_600, 150, T0.AddSeconds(5), started: started), T0.AddSeconds(5));
        Assert.True(b.Percent > a.Percent);
        // bar 151 completes: the runtime row now names bar 151 as completed and the partial restarts small with a new start time
        var c = IncompleteBarView.From(Progress(40, 151, T0.AddSeconds(8), started: T0.AddSeconds(7)), T0.AddSeconds(8));
        Assert.Equal(152, c.BarSeq); Assert.Equal(40L, c.Quantity);
        Assert.True(c.Percent < 1d);
        Assert.Equal("0:01", IncompleteBarView.Duration(c.Elapsed));
        Assert.Equal(IncompleteBarState.Live, c.State);
        Assert.Equal(100d, IncompleteBarView.From(Progress(13_650, 150, T0), T0).Percent);            // exactly the target is a full bar, never an overflow
    }

    [Fact]
    public void IncompleteBar_MissingStaleClosedAndNotLive_AreNeverShownAsCurrentLiveProgress()
    {
        Assert.Equal(IncompleteBarState.Unavailable, IncompleteBarView.From(null, T0).State);
        var none = IncompleteBarView.From(BarProgress.Unavailable("no adaptive session today"), T0);
        Assert.Equal(IncompleteBarState.Unavailable, none.State); Assert.Null(none.Quantity); Assert.Equal("no adaptive session today", none.Reason);
        Assert.False(none.ShowsProgress);
        var bad = IncompleteBarView.From(BarProgress.From(new AdaptiveSessionStateRow
        {
            Id = 1, TradeDate = DateOnly.FromDateTime(T0.DateTime), ModelVersion = "adaptive-fixed13650-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0, FutureToken = "F", FutureSymbol = "F", FutureExpiry = DateOnly.FromDateTime(T0.DateTime).AddDays(20),
            OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0, EstimatorName = "x", CreatedAtUtc = T0, BaseBarVolume = 13_650,
        }, new AdaptiveObserverRuntimeRow { SessionId = 1, RuntimeStatus = AdaptiveRuntimeStatus.Live, LastHeartbeatUtc = T0, CurrentPartialBarVolume = 20_000, LastCompletedBarSeq = 3 }), T0);
        Assert.Equal(IncompleteBarState.Unavailable, bad.State);                                    // an overflow is reported, not clamped and not drawn
        Assert.Null(bad.Quantity);

        var started = T0.AddSeconds(-60);
        var stale = IncompleteBarView.From(Progress(5_000, 150, T0, started: started), T0.AddSeconds(45));
        Assert.Equal(IncompleteBarState.Stale, stale.State);
        Assert.Equal("1:00", IncompleteBarView.Duration(stale.Elapsed));                            // frozen at the last heartbeat: a stopped Host does not show a growing elapsed time
        Assert.Contains("45s", stale.Reason);
        Assert.Equal(T0, stale.UpdatedAtUtc);                                                       // the SOURCE time stays the heartbeat, not the Dashboard refresh time
        Assert.Equal(IncompleteBarState.Live, IncompleteBarView.From(Progress(5_000, 150, T0, started: started), T0.AddSeconds(10)).State);   // exactly at the limit is still fresh
        Assert.Equal(IncompleteBarState.Closed, IncompleteBarView.From(Progress(0, 600, T0, "Closed"), T0).State);
        var rebuilding = IncompleteBarView.From(Progress(5_000, 150, T0, "Rebuilding", started), T0);
        Assert.Equal(IncompleteBarState.NotLive, rebuilding.State);
        Assert.Contains("Rebuilding", rebuilding.Reason);
        Assert.Equal(TimeSpan.Zero, IncompleteBarView.From(Progress(1, 1, T0.AddSeconds(3)), T0).HeartbeatAge);   // a Host clock slightly ahead is not a negative age
        Assert.Null(IncompleteBarView.From(Progress(1, 1, T0), T0).Elapsed);                        // no start time in the row: elapsed is unavailable, not guessed
    }

    [Fact]
    public void IncompleteBar_NamesTheFieldsTheDataContractCannotSupply()
    {
        Assert.Equal(["Open", "High", "Low", "Last price", "Trade-update count"], IncompleteBarView.UnavailableFields);
        var src = File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.Dashboard", "Services", "IncompleteBarView.cs"));
        Assert.DoesNotContain("Live.FutureLtp", src);                                               // nothing is borrowed from unrelated quote snapshots
    }

    // ---- 13: sticky behaviour and layout contract (source level; the real layout is verified in a browser) ----

    [Fact]
    public void Stylesheets_KeepTheQuoteSticky_AndMakeOptionContextFullWidth()
    {
        var dir = Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.Dashboard", "Components", "Dashboard");
        var quote = File.ReadAllText(Path.Combine(dir, "LiveQuotePanel.razor.css"));
        Assert.Matches(@"\.quote-panel-sticky\s*\{[^}]*position:\s*sticky;[^}]*top:\s*0;", quote);
        Assert.Contains(".quote-progress", quote);
        var panel = File.ReadAllText(Path.Combine(dir, "AdaptiveObserverPanel.razor.css"));
        var ctx = Regex.Match(panel, @"\.adaptive-table\[data-grid=""options-context""\]\s*\{[^}]*\}").Value;
        Assert.Contains("width: 100%", ctx); Assert.Contains("min-width: 100%", ctx);
        Assert.DoesNotContain("width: auto", ctx);
    }

    // ---- 15: the screenshot renderer's grid contract ----

    [Fact]
    public void Renderer_RequiresTheSixNamedGridsInOrder_AndRefusesTheRemovedOnes()
    {
        string[] ok = ["summary-futures", "summary-total", "futures-compact", "options-core-direction", "options-context", "residual-compact"];
        Assert.True(AdaptiveScreenshotRenderer.IsExpectedGridSequence(ok));
        Assert.True(AdaptiveScreenshotRenderer.IsExpectedGridSequence([.. ok, "unrelated-extra-grid"]));        // an unrelated table never breaks a capture
        Assert.False(AdaptiveScreenshotRenderer.IsExpectedGridSequence(ok.Take(5).ToArray()));                  // a missing grid
        Assert.False(AdaptiveScreenshotRenderer.IsExpectedGridSequence([.. ok, "residual-compact"]));           // a duplicated section
        Assert.False(AdaptiveScreenshotRenderer.IsExpectedGridSequence([ok[1], ok[0], .. ok.Skip(2)]));         // wrong order
        foreach (var gone in AdaptiveScreenshotRenderer.ForbiddenGrids)
            Assert.False(AdaptiveScreenshotRenderer.IsExpectedGridSequence([gone, .. ok]));
        Assert.Equal(6, AdaptiveScreenshotRenderer.RequiredGrids.Count);
        var src = File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.Dashboard", "Services", "AdaptiveScreenshotRenderer.cs"));
        Assert.Contains("Removed capture grid", src);
        Assert.Contains("not in the expected order", src);
    }
}
