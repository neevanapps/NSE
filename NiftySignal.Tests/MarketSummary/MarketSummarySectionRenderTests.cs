using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.MarketSummary;

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

    static MarketSummaryView View(int bars, Func<int, string, bool>? skip = null)
    {
        var listed = Enumerable.Range(0, 9).Select(i => 25_800d + i * 50).Select(s => new ListedStrike(s, $"CE{s:0}", $"PE{s:0}")).ToList();
        var basket = BasketResolver.Resolve(Expiry, 26_000, listed);
        var r = new Random(5);
        var all = new List<ContractBar>();
        for (var seq = 1; seq <= bars; seq++)
            foreach (var c in basket.Contracts)
            {
                if (skip?.Invoke(seq, c.Token!) == true) continue;
                all.Add(new ContractBar(seq, c.Token!, c.IsCall, 100, 101 + r.Next(5), 500, 20, 100, 90, 10, 10, 1000, 1010, 12, 12.5, 0.42, 0.45, 5, 5.5, 0.1, 0.2, 10, 10, 50));
            }

        var durations = Enumerable.Range(1, bars).ToDictionary(i => i, _ => 30d);
        var fut = Enumerable.Range(1, bars).ToDictionary(i => i, i => new FuturesBar(i, 30, 26_000, 26_001, 13_650, 100, 100, 100, 10, 5, 1000, 1010, 0.1, 0.1, 20, 20, 5, 50));
        return new MarketSummaryView(bars, basket, FuturesSummaryCalculator.Compute(fut, bars),
            OptionSummaryCalculator.ComputeAll(basket, new ContractHistory(all, durations), bars), null);
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
        foreach (var g in new[] { "summary-futures", "summary-atm-core", "summary-itm-core", "summary-otm-core", "summary-total-core", "summary-atm-context", "summary-itm-context", "summary-otm-context", "summary-total-context" })
            Assert.Equal(5, BodyRows(html, g));
        foreach (var w in new[] { "5 Bars", "10 Bars", "50 Bars", "100 Bars", "Day" }) Assert.Contains($"data-window=\"{w}\"", html);
        Assert.Contains("10/10 – FULL", html);
        Assert.Contains("ΔDelta ×100", html);
        Assert.Contains("ΔVega ×100", html);
    }

    [Fact]
    public async Task Render_DeltaIsScaledBy100ForDisplayOnly()
    {
        var html = await RenderAsync(View(20));
        // each contract: DeltaStart .42 -> DeltaEnd .45 over any window => change 0.03 => displayed 3.00
        Assert.Contains("3.00 (", html);
        Assert.DoesNotContain("0.03 (", html);
    }

    [Fact]
    public async Task Render_InsufficientHistoryShowsExplicitIndicator_NotZero()
    {
        var html = await RenderAsync(View(7));
        Assert.Contains("— – INSUFFICIENT DATA", html);
        Assert.Equal(5, BodyRows(html, "summary-total-core"));
        Assert.Contains("10/10 – FULL", html);       // the 5-bar window is complete
    }

    [Fact]
    public async Task Render_PartialCoverage_AndUnavailable()
    {
        var html = await RenderAsync(View(30, (seq, tok) => tok == "CE25900" && seq == 28));
        Assert.Contains("9/10 – PARTIAL (levels 10/10)", html);
        Assert.Contains("3/4 – PARTIAL (levels 4/4)", html);
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
        var css = File.ReadAllText(Path.Combine(NiftySignal.Tests.Trading.Data.TradingModelTests.RepoRoot(), "NiftySignal.Dashboard", "Components", "Dashboard", "MarketSummarySection.razor.css"));
        var full = await RenderAsync(View(120));
        var partial = await RenderAsync(View(30, (seq, tok) => tok == "CE25900" && seq == 28));
        string Page(string body) => "<!doctype html><meta charset=utf-8><style>:root{--surface:#12161c;--border:#2a3340;--text-dim:#8b97a8;--warn:#dbab09}body{margin:0;background:#0d1117;color:#e6edf3;font-family:Segoe UI,Arial,sans-serif;font-size:14px;padding:20px}" + css + "</style><main class=adaptive-capture>" + body + "</main>";
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "summary-full.html"), Page(full));
        File.WriteAllText(Path.Combine(dir, "summary-partial.html"), Page(partial));
    }
}
