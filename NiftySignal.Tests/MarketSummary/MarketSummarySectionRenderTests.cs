using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.MarketSummary;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.MarketSummary;

public class MarketSummarySectionRenderTests
{
    static readonly DateOnly Expiry = new(2026, 11, 3);

    static async Task<string> RenderAsync(MarketSummaryView? view)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            System.Net.WebUtility.HtmlDecode((await renderer.RenderComponentAsync<MarketSummarySection>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = view }))).ToHtmlString()));
    }

    static MarketSummaryView View(int bars, Func<int, string, bool>? skip = null, bool asymmetricDelta = false)
    {
        var listed = Enumerable.Range(0, 9).Select(i => 25_800d + i * 50).Select(s => new ListedStrike(s, $"CE{s:0}", $"PE{s:0}")).ToList();
        var basket = BasketResolver.Resolve(Expiry, 26_000, listed);
        var r = new Random(5);
        var all = new List<ContractBar>();
        for (var seq = 1; seq <= bars; seq++)
            foreach (var c in basket.Contracts)
            {
                if (skip?.Invoke(seq, c.Token!) == true) continue;
                all.Add(new ContractBar(seq, c.Token!, c.IsCall, 100, 101 + r.Next(5), 500, 20, 100, 90, 10, 10, 1000, 1010, 12, 12.5, 0.42, asymmetricDelta && !c.IsCall ? 0.43 : 0.45, 5, 5.5, 0.1, 0.2, 10, 10, 50));
            }

        var durations = Enumerable.Range(1, bars).ToDictionary(i => i, _ => 30d);
        var fut = Enumerable.Range(1, bars).ToDictionary(i => i, i => new FuturesBar(i, 30, 26_000, 26_001, 13_650, 100, 100, 100, 10, 5, 1000, 1010, 0.1, 0.1, 20, 20, 5, 50));
        return new MarketSummaryView(bars, basket, FuturesSummaryCalculator.Compute(fut, bars),
            OptionSummaryCalculator.ComputeAll(basket, new ContractHistory(all, durations), bars), null);
    }

    internal static MarketSummaryView ViewWithLowerUpper(int bars)
    {
        var listed = Enumerable.Range(0, 11).Select(i => 25_750d + i * 50).Select(x => new ListedStrike(x, $"CE{x:0}", $"PE{x:0}")).ToList();
        var basket = BasketResolver.Resolve(Expiry, 26_000, listed);
        var lowerB = BasketResolver.Resolve(Expiry, 26_000, listed, -1);
        var upperB = BasketResolver.Resolve(Expiry, 26_000, listed, 1);
        var r = new Random(5);
        var tokens = lowerB.Contracts.Concat(basket.Contracts).Concat(upperB.Contracts).Where(c => c.Token is not null).DistinctBy(c => c.Token).ToList();
        var all = new List<ContractBar>();
        for (var seq = 1; seq <= bars; seq++)
            foreach (var c in tokens)
                all.Add(new ContractBar(seq, c.Token!, c.IsCall, 100, 101 + r.Next(5), 500, 20, 100, 90, 10, 10, 1000, 1010, 12, 12.5, 0.42, 0.45, 5, 5.5, 0.1, 0.2, 10, 10, 50));
        var history = new ContractHistory(all, Enumerable.Range(1, bars).ToDictionary(i => i, _ => 30d));
        var fut = Enumerable.Range(1, bars).ToDictionary(i => i, i => new FuturesBar(i, 30, 26_000, 26_001, 13_650, 100, 100, 100, 10, 5, 1000, 1010, 0.1, 0.1, 20, 20, 5, 50));
        return new MarketSummaryView(bars, basket, FuturesSummaryCalculator.Compute(fut, bars), OptionSummaryCalculator.ComputeAll(basket, history, bars), null)
        {
            LowerBasket = lowerB, Lower = OptionSummaryCalculator.Compute(lowerB, GridKind.Total, history, bars),
            UpperBasket = upperB, Upper = OptionSummaryCalculator.Compute(upperB, GridKind.Total, history, bars),
        };
    }

    [Fact]
    public async Task Render_LowerAtmUpper_ThreeLabelledTotalGrids_EachWithFiveRows_AndTheirOwnStrikes()
    {
        var html = await RenderAsync(ViewWithLowerUpper(120));
        foreach (var g in new[] { "summary-total", "summary-total-itm", "summary-total-otm" }) Assert.Equal(5, BodyRows(html, g));
        Assert.Contains("TOTAL SUMMARY - ITM-SHIFTED (ATM-1 step)", html);
        Assert.Contains("TOTAL SUMMARY - ATM", html);
        Assert.Contains("TOTAL SUMMARY - OTM-SHIFTED (ATM+1 step)", html);
        Assert.DoesNotContain("LOWER", html);
        Assert.DoesNotContain("UPPER", html);
        Assert.Equal(new[] { "summary-futures", "summary-total-itm", "summary-total", "summary-total-otm" }, Regex.Matches(html, "data-grid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray());
        var lower = Regex.Match(html, "data-testid=\"summary-total-itm-title\".*?</div>", RegexOptions.Singleline).Value;
        Assert.Contains("CE 25850", lower); Assert.Contains("PE 25850", lower); Assert.DoesNotContain("26100", lower);
        var upper = Regex.Match(html, "data-testid=\"summary-total-otm-title\".*?</div>", RegexOptions.Singleline).Value;
        Assert.Contains("CE 26150", upper); Assert.Contains("PE 26150", upper); Assert.DoesNotContain("25900", upper);
    }

    [Fact]
    public async Task Render_ForAViewWithoutShiftedBaskets_ShowsOnlyTheAtmGrid_NeverAGuess()
    {
        Assert.Equal(["summary-futures", "summary-total"], Regex.Matches(await RenderAsync(View(120)), "data-grid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray());
    }

    [Fact]
    public async Task Render_ShowsTheCloseAtmAndItsSource()
    {
        var v = ViewWithLowerUpper(120) with { };
        var html = await RenderAsync(new MarketSummaryView(v.EndBarSeq, v.Basket, v.Futures, v.Grids, null, v.Instants) { Lower = v.Lower, LowerBasket = v.LowerBasket, Upper = v.Upper, UpperBasket = v.UpperBasket, CloseAtmStrike = 26_000, CloseAtmSource = "stored" });
        Assert.Contains("ATM at bar close", html);
        Assert.Contains("26000", Regex.Match(html, "data-testid=\"summary-close-atm-strike\".*?</span>", RegexOptions.Singleline).Value);
        Assert.Contains("recorded at the bar", html);
    }

    static int BodyRows(string html, string grid)
    {
        var table = Regex.Match(html, "<table[^>]*data-grid=\"" + Regex.Escape(grid) + "\".*?</table>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(table);
        return Regex.Matches(Regex.Match(table, "<tbody[^>]*>.*</tbody>", RegexOptions.Singleline).Value, "<tr[ >]").Count;
    }

    [Fact]
    public async Task Render_EveryGridHasExactlyFiveRows_AndWindowLabels()
    {
        var html = await RenderAsync(View(120));
        foreach (var g in new[] { "summary-futures", "summary-total" })
            Assert.Equal(5, BodyRows(html, g));
        // the ATM / ITM / OTM summaries are no longer presented (2026-11-02), and the old two-table TOTAL is gone
        foreach (var g in new[] { "summary-atm-core", "summary-itm-core", "summary-otm-core", "summary-total-core", "summary-atm-context", "summary-itm-context", "summary-otm-context", "summary-total-context" })
            Assert.DoesNotContain($"data-grid=\"{g}\"", html);
        foreach (var t in new[] { "ATM SUMMARY", "ITM SUMMARY", "OTM SUMMARY" }) Assert.DoesNotContain(t, html);
        Assert.Equal(2, Regex.Matches(html, "<table").Count);
        foreach (var w in new[] { "5 Bars", "10 Bars", "50 Bars", "100 Bars", "Day" }) Assert.Contains($"data-window=\"{w}\"", html);
        Assert.Contains("10/10 – FULL", html);
        Assert.Contains("IV Dir (Inv)", html);
        Assert.Contains("Delta Dir", html);
        Assert.DoesNotContain("Delta Dir (Inv)", html);
        Assert.Contains("Vega Dir (Inv)", html);
    }

    [Fact]
    public async Task Render_DeltaIsScaledBy100ForDisplayOnly()
    {
        var html = await RenderAsync(View(20, asymmetricDelta: true));
        // CE contracts: DeltaStart .42 -> DeltaEnd .45 (+0.03); PE contracts .42 -> .43 (+0.01); Delta Dir = CE - PE = 0.02 raw => displayed +2.00 (raw, not inverted)
        Assert.Contains(">2.00 (", html);
        Assert.DoesNotContain(">-2.00 (", html);
        Assert.DoesNotContain("0.02 (", html);
    }

    [Fact]
    public async Task Render_InsufficientHistoryShowsExplicitIndicator_NotZero()
    {
        var html = await RenderAsync(View(7));
        Assert.Contains("— – INSUFFICIENT DATA", html);
        Assert.Equal(5, BodyRows(html, "summary-total"));
        Assert.Contains("10/10 – FULL", html);       // the 5-bar window is complete
    }

    [Fact]
    public async Task Render_PartialCoverage_AndUnavailable()
    {
        var html = await RenderAsync(View(30, (seq, tok) => tok == "CE25900" && seq == 28));
        Assert.Contains("9/10 – PARTIAL (levels 10/10)", html);
        var none = await RenderAsync(MarketSummaryView.Unavailable(0, "no frozen option universe for this session"));
        Assert.Contains("Market summary unavailable: no frozen option universe for this session", none);
        Assert.DoesNotContain("<table", none);
        Assert.Contains("Market summary unavailable", await RenderAsync(null));
    }

    [Fact]
    public async Task Render_FuturesSummary_ShowsVixLevelChangeAndAge_StaleAndMissingAsDashes()
    {
        var t0 = new DateTimeOffset(2026, 10, 7, 3, 45, 0, TimeSpan.Zero);
        var windows = SummaryWindow.All.Select((w, i) => new VixWindowSummary(w,
            new VixPoint(14.0, t0, 3, VixStatus.Fresh),
            i == 1 ? new VixPoint(null, t0, 90, VixStatus.Stale) : new VixPoint(15.0, t0, 4, VixStatus.Fresh),
            i == 1 ? null : 1.0, i == 1 ? null : 1.0 / 14.0 * 100d)).ToList();
        var html = await RenderAsync(View(120) with { Vix = windows });
        Assert.Contains("VIX Δ%", html);
        Assert.Contains("15.00", html);
        Assert.Contains("+1.00", html);
        Assert.Contains("+7.14", html);
        Assert.Contains("4s", html);
        Assert.Contains("stale", html);                                     // the 10-bar window's end observation was 90 s old
        var none = await RenderAsync(View(120) with { Vix = null, VixUnavailableReason = "no India VIX instrument in the instrument master" });
        Assert.Contains("VIX unavailable: no India VIX instrument in the instrument master", none);
    }

    /// <summary>Optional: when NSE_RENDER_DUMP_DIR is set, writes the rendered section (with its stylesheet and neutral theme variables) so layout can be inspected in a real browser at 1920 px. Never asserts layout.</summary>
    [Fact]
    public async Task Render_OptionalDumpForBrowserInspection()
    {
        var dir = Environment.GetEnvironmentVariable("NSE_RENDER_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var css = File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.Dashboard", "Components", "Dashboard", "MarketSummarySection.razor.css"));
        var full = await RenderAsync(View(120));
        var partial = await RenderAsync(View(30, (seq, tok) => tok == "CE25900" && seq == 28));
        string Page(string body) => "<!doctype html><meta charset=utf-8><style>:root{--surface:#12161c;--border:#2a3340;--text-dim:#8b97a8;--warn:#dbab09}body{margin:0;background:#0d1117;color:#e6edf3;font-family:Segoe UI,Arial,sans-serif;font-size:14px;padding:20px}" + css + "</style><main class=adaptive-capture>" + body + "</main>";
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "summary-full.html"), Page(full));
        File.WriteAllText(Path.Combine(dir, "summary-partial.html"), Page(partial));
    }

    // ---- Greek Delta Direction is displayed RAW (CE - PE); IV / Vega stay inverted (owner decision 2026-11-24) ----

    static MarketSummaryView DeltaView(double? ceEnd, double? peEnd, int bars = 10)
    {
        var listed = Enumerable.Range(0, 11).Select(i => 25_750d + i * 50).Select(x => new ListedStrike(x, $"CE{x:0}", $"PE{x:0}")).ToList();
        var basket = BasketResolver.Resolve(Expiry, 26_000, listed);
        var lowerB = BasketResolver.Resolve(Expiry, 26_000, listed, -1);
        var upperB = BasketResolver.Resolve(Expiry, 26_000, listed, 1);
        var tokens = lowerB.Contracts.Concat(basket.Contracts).Concat(upperB.Contracts).Where(c => c.Token is not null).DistinctBy(c => c.Token).ToList();
        var all = new List<ContractBar>();
        for (var seq = 1; seq <= bars; seq++)
            foreach (var c in tokens)
                all.Add(new ContractBar(seq, c.Token!, c.IsCall, 100, 105, 500, 20, 100, 90, 10, 10, 1000, 1010,
                    12, c.IsCall ? 13 : 12.5, ceEnd is null ? null : 0.42, c.IsCall ? ceEnd : peEnd, 5, c.IsCall ? 5.5 : 5.2, 0.1, 0.2, 10, 10, 50));
        var history = new ContractHistory(all, Enumerable.Range(1, bars).ToDictionary(i => i, _ => 30d));
        var fut = Enumerable.Range(1, bars).ToDictionary(i => i, i => new FuturesBar(i, 30, 26_000, 26_001, 13_650, 100, 100, 100, 10, 5, 1000, 1010, 0.1, 0.1, 20, 20, 5, 50));
        return new MarketSummaryView(bars, basket, FuturesSummaryCalculator.Compute(fut, bars), OptionSummaryCalculator.ComputeAll(basket, history, bars), null)
        {
            LowerBasket = lowerB, Lower = OptionSummaryCalculator.Compute(lowerB, GridKind.Total, history, bars),
            UpperBasket = upperB, Upper = OptionSummaryCalculator.Compute(upperB, GridKind.Total, history, bars),
        };
    }

    static List<(string Class, string Sign, string Text)> TotalCells(string html, string grid, string window)
    {
        var table = Regex.Match(html, "<table[^>]*data-grid=\"" + Regex.Escape(grid) + "\".*?</table>", RegexOptions.Singleline).Value;
        var row = Regex.Match(table, "<tr data-window=\"" + Regex.Escape(window) + "\"[^>]*>.*?</tr>", RegexOptions.Singleline).Value;
        Assert.True(row.Length > 0, grid + " " + window);
        return Regex.Matches(row, "<td([^>]*)>(.*?)</td>", RegexOptions.Singleline).Select(m =>
            (Regex.Match(m.Groups[1].Value, "class=\"([^\"]*)\"").Groups[1].Value, Regex.Match(m.Groups[1].Value, "data-sign=\"([^\"]*)\"").Groups[1].Value, m.Groups[2].Value)).ToList();
    }

    // CE ΔDelta = ceEnd - 0.42, PE ΔDelta = peEnd - 0.42 (all contracts identical) -> raw Direction = CE - PE
    [Theory]
    [InlineData(0.45, 0.43, "2.00 (", "pos")]     // +0.03 - +0.01 = +0.02 -> displayed +2.00
    [InlineData(0.43, 0.45, "-2.00 (", "neg")]    // +0.01 - +0.03 = -0.02 -> displayed -2.00
    [InlineData(0.45, 0.45, "0.00 (", "zero")]    // zero stays zero: never "-0.00", never tinted
    [InlineData(null, null, "—", "none")]         // no Greek data: unavailable, never zero
    public async Task TotalSummary_DeltaDir_IsRaw_InAllThreeTotalGrids_AndIvVegaStayInverted(double? ceEnd, double? peEnd, string expectedPrefix, string sign)
    {
        var view = DeltaView(ceEnd, peEnd);
        var html = await RenderAsync(view);
        var calc = new[] { ("summary-total-itm", view.Lower!), ("summary-total", view.Grids.Single(g => g.Kind == GridKind.Total)), ("summary-total-otm", view.Upper!) };
        foreach (var (grid, summary) in calc)
            foreach (var w in new[] { "5 Bars", "10 Bars" })
            {
                var cells = TotalCells(html, grid, w);
                var row = summary.Rows.Single(r => r.Window.Name == w).Direction!;
                Assert.StartsWith(expectedPrefix, cells[10].Text);               // ×100 once, raw sign
                Assert.Equal(sign, cells[10].Sign);
                if (expectedPrefix != "—") Assert.DoesNotContain("-0.00", cells[10].Text);
                // IV and Vega: still displayed inverted (displayed = -raw), Vega also ×100 once
                Assert.StartsWith(RatioFormat.Cell(new RatioPart(DirectionDisplay.Invert(row.Iv).Raw, DirectionDisplay.Invert(row.Iv).Num, row.Iv.Den), 2), cells[9].Text);
                var vegaInv = DirectionDisplay.Invert(row.Vega);
                Assert.StartsWith(RatioFormat.Cell(new RatioPart(DisplayScale.Times100(vegaInv.Raw), vegaInv.Num, vegaInv.Den), 2), cells[11].Text);
            }
        Assert.Contains("Delta Dir", html); Assert.DoesNotContain("Delta Dir (Inv)", html);
    }

    [Fact]
    public void SourceGuard_NoDeltaDirectionInversion_ButIvVegaMicroDevStillInverted()
    {
        var dash = Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.Dashboard");
        var src = string.Join(Environment.NewLine, Directory.EnumerateFiles(dash, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".razor") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));
        Assert.DoesNotMatch(@"(Invert|InvertDirectionOnly)\(\s*[\w.]*\.Delta\s*\)", src);
        // the Options Core Direction grid (its own IV / Vega inversion) was removed by decision A8; the market-summary grids keep the inversion
        Assert.Contains("Invert(d.Iv)", src); Assert.Contains("Invert(d.Vega)", src);
    }
}
