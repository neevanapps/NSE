using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.MarketSummary;

namespace NiftySignal.Tests.MarketSummary;

/// <summary>
/// Futures grid directional tint. Direction conventions verified against the producers (not the column names):
/// Price = Close - Open; Strict / Enriched = buy minus sell volume; TOB = (BidQty - AskQty) / total (bid-heavy positive);
/// MicroDev = MicroPrice - Mid (positive when the bid side is heavier); OFI = Cont-Kukanov-Stoikov contribution (bid growth positive).
/// So a positive value is bullish for every tinted metric. OI change, volume, activity, VIX, gross OFI and durations are NOT directional and never tinted.
/// </summary>
public class FuturesDirectionColorTests
{
    static readonly DateOnly Expiry = new(2026, 11, 3);

    static async Task<string> RenderAsync(MarketSummaryView view)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            System.Net.WebUtility.HtmlDecode((await renderer.RenderComponentAsync<MarketSummarySection>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = view }))).ToHtmlString()));
    }

    static MarketSummaryView View(int bars, Func<int, FuturesBar> bar)
    {
        var listed = Enumerable.Range(0, 9).Select(i => 25_800d + i * 50).Select(s => new ListedStrike(s, $"CE{s:0}", $"PE{s:0}")).ToList();
        var basket = BasketResolver.Resolve(Expiry, 26_000, listed);
        var fut = Enumerable.Range(1, bars).ToDictionary(i => i, bar);
        return new MarketSummaryView(bars, basket, FuturesSummaryCalculator.Compute(fut, bars), [], null);
    }

    /// <summary>One futures bar whose directional fields all carry <paramref name="sign"/> (+1 / -1 / 0); OI change is always +10 so it must stay untinted.</summary>
    static FuturesBar Signed(int i, int sign, bool micro = true) =>
        new(i, 30, 26_000, 26_000 + sign, 13_650, 100 * sign, 100, 100, 10, 5 * sign, 1000, 1010,
            micro ? 0.1 * sign : null, micro ? 0.1 * sign : null, micro ? 20 : null, micro ? 20 : null, micro ? 5 * sign : null, micro ? 50 : null);

    /// <summary>The td list (inner attributes) of one futures-summary window row: [window, coverage, price, volume, strict, enriched, microdev, ofi, oi, activity, vix...].</summary>
    static List<(string Class, string Sign, string Text)> Cells(string html, string window)
    {
        var table = Regex.Match(html, "<table[^>]*data-grid=\"summary-futures\".*?</table>", RegexOptions.Singleline).Value;
        var row = Regex.Match(table, "<tr data-window=\"" + Regex.Escape(window) + "\"[^>]*>.*?</tr>", RegexOptions.Singleline).Value;
        Assert.True(row.Length > 0, html.Length > 3000 ? html[..3000] : html);
        return Regex.Matches(row, "<td([^>]*)>(.*?)</td>", RegexOptions.Singleline).Select(m =>
            (Regex.Match(m.Groups[1].Value, "class=\"([^\"]*)\"").Groups[1].Value, Regex.Match(m.Groups[1].Value, "data-sign=\"([^\"]*)\"").Groups[1].Value, m.Groups[2].Value)).ToList();
    }

    static readonly int[] Directional = [2, 4, 5, 6, 7];          // Price, Strict, Enriched, MicroDev (displayed INVERTED), OFI
    const int MicroDevColumn = 6;
    static readonly int[] NeverTinted = [0, 1, 3, 8, 9, 10, 11, 12, 13];

    [Theory]
    [InlineData(1, "adaptive-signed val-pos", "pos")]
    [InlineData(-1, "adaptive-signed val-neg", "neg")]
    [InlineData(0, "", "zero")]
    public async Task FuturesSummary_AllFiveWindows_TintTheDirectionalColumnsBySignOnly(int sign, string cssClass, string signName)
    {
        var html = await RenderAsync(View(120, i => Signed(i, sign)));
        foreach (var w in new[] { "5 Bars", "10 Bars", "50 Bars", "100 Bars", "Day" })
        {
            var cells = Cells(html, w);
            foreach (var c in Directional)
            {
                // MicroDev is shown inverted (2026-11-02): a positive raw value is displayed negative (red) and vice versa; every other tinted column keeps its own sign
                var (expectedClass, expectedSign) = c == MicroDevColumn ? sign switch { 1 => ("adaptive-signed val-neg", "neg"), -1 => ("adaptive-signed val-pos", "pos"), _ => (cssClass, signName) } : (cssClass, signName);
                Assert.Equal(expectedClass, cells[c].Class); Assert.Equal(expectedSign, cells[c].Sign);
            }
            foreach (var c in NeverTinted) Assert.DoesNotContain("val-", cells[c].Class);
        }
    }

    [Fact]
    public async Task FuturesSummary_OiChangeIsNeverTinted_EvenWhenPositiveOrNegative()
    {
        // OI +10 per bar (positive) in the bullish case, negative OI change in the bearish one: neither is directional
        foreach (var (oiClose, _) in new[] { (1010L, "pos"), (990L, "neg") })
        {
            var html = await RenderAsync(View(120, i => Signed(i, 1) with { OiClose = oiClose }));
            Assert.All(new[] { "5 Bars", "Day" }, w => { var c = Cells(html, w); Assert.DoesNotContain("val-", c[8].Class); Assert.Equal("", c[8].Sign); });
        }
    }

    [Fact]
    public async Task FuturesSummary_UnavailableMicrostructure_IsNeutral_NotZeroNotColoured()
    {
        var html = await RenderAsync(View(120, i => Signed(i, 1, micro: false)));
        var c = Cells(html, "50 Bars");
        Assert.Equal("adaptive-signed val-pos", c[2].Class);     // price still available
        foreach (var i in new[] { 6, 7 }) { Assert.Equal("none", c[i].Sign); Assert.Equal("", c[i].Class); Assert.Contains("—", c[i].Text); }
    }

    [Fact]
    public async Task FuturesSummary_InsufficientHistory_ShowsDashesAndNoTint()
    {
        var html = await RenderAsync(View(7, i => Signed(i, 1)));
        var c = Cells(html, "100 Bars");
        Assert.Contains("INSUFFICIENT", c[1].Text);
        foreach (var i in Directional) { Assert.Equal("—", c[i].Text); Assert.Equal("", c[i].Class); }
    }

    [Fact]
    public async Task FuturesSummary_NoFutureBarLeaksIntoTheSelectedBoundary()
    {
        // bars 1..60 bullish, then 61..120 bearish; a view pinned at bar 60 must show every window bullish, the full view must differ for the short windows
        var all = Enumerable.Range(1, 120).ToDictionary(i => i, i => Signed(i, i <= 60 ? 1 : -1));
        var listed = Enumerable.Range(0, 9).Select(i => 25_800d + i * 50).Select(s => new ListedStrike(s, $"CE{s:0}", $"PE{s:0}")).ToList();
        var basket = BasketResolver.Resolve(Expiry, 26_000, listed);
        var upTo60 = all.Where(kv => kv.Key <= 60).ToDictionary(kv => kv.Key, kv => kv.Value);
        var pinned = await RenderAsync(new MarketSummaryView(60, basket, FuturesSummaryCalculator.Compute(upTo60, 60), [], null));
        var late = await RenderAsync(new MarketSummaryView(120, basket, FuturesSummaryCalculator.Compute(all, 120), [], null));
        foreach (var w in new[] { "5 Bars", "10 Bars", "50 Bars" }) Assert.Equal("adaptive-signed val-pos", Cells(pinned, w)[2].Class);
        foreach (var w in new[] { "5 Bars", "10 Bars", "50 Bars" }) Assert.Equal("adaptive-signed val-neg", Cells(late, w)[2].Class);
    }

    [Theory]
    [InlineData(1.5, "adaptive-signed val-pos", "pos")]
    [InlineData(-1.5, "adaptive-signed val-neg", "neg")]
    [InlineData(0.0, "", "zero")]
    [InlineData(double.NaN, "", "none")]
    [InlineData(double.PositiveInfinity, "", "none")]
    public void Helper_Double_SignOnly(double v, string cssClass, string name)
    {
        Assert.Equal(cssClass, RatioFormat.SignedClass(v));
        Assert.Equal(name, RatioFormat.SignName(v));
    }

    [Fact]
    public void Helper_Null_RatioNone_AndAverages()
    {
        Assert.Equal("", RatioFormat.SignedClass((double?)null));
        Assert.Equal("none", RatioFormat.SignName((double?)null));
        Assert.Equal("", RatioFormat.SignedClass(RatioPart.None));
        Assert.Equal("adaptive-signed val-pos", RatioFormat.SignedClass(7L));
        Assert.Equal("adaptive-signed val-neg", RatioFormat.SignedClass(-7L));
        Assert.Equal("none", RatioFormat.SignName((long?)null));
        // AVG row values: the pooled average keeps the sign of the pooled raw value, ratio = sum(num)/sum(den)
        var avgNeg = RatioAverage.Aggregate([RatioPart.Of(-30, -30, 100), RatioPart.Of(10, 10, 100)]);
        Assert.Equal("adaptive-signed val-neg", RatioFormat.SignedClass(avgNeg));
        var avgPos = RatioAverage.Aggregate([RatioPart.Of(30, 30, 100), RatioPart.Of(-10, -10, 100)]);
        Assert.Equal("adaptive-signed val-pos", RatioFormat.SignedClass(avgPos));
        Assert.Equal("", RatioFormat.SignedClass(RatioAverage.Aggregate([])));
    }

    [Fact]
    public void Stylesheets_ReuseTheOptionsDirectionColours_AndDoNotKeyOnMagnitude()
    {
        var dir = Path.Combine(NiftySignal.Tests.Trading.Data.TradingModelTests.RepoRoot(), "NiftySignal.Dashboard", "Components", "Dashboard");
        var options = File.ReadAllText(Path.Combine(dir, "AdaptiveObserverPanel.razor.css"));
        var pos = Regex.Match(options, @"adaptive-direction\.val-pos\s*\{[^}]*(rgba\([^)]*\))").Groups[1].Value;
        var neg = Regex.Match(options, @"adaptive-direction\.val-neg\s*\{[^}]*(rgba\([^)]*\))").Groups[1].Value;
        Assert.NotEmpty(pos); Assert.NotEmpty(neg);
        foreach (var file in new[] { "AdaptiveObserverPanel.razor.css", "MarketSummarySection.razor.css" })
        {
            var css = File.ReadAllText(Path.Combine(dir, file));
            Assert.Contains(pos, Regex.Match(css, @"adaptive-signed\.val-pos\s*\{[^}]*\}").Value);
            Assert.Contains(neg, Regex.Match(css, @"adaptive-signed\.val-neg\s*\{[^}]*\}").Value);
        }
    }

    [Fact]
    public void DetailedFuturesGrid_TintsOnlyTheDirectionalColumns()
    {
        // source-level guard: the panel's futures grids tint price, strict, enriched, MicroDev, OFI, rolling and return-bps cells and nothing else
        var src = File.ReadAllText(Path.Combine(NiftySignal.Tests.Trading.Data.TradingModelTests.RepoRoot(), "NiftySignal.Dashboard", "Components", "Dashboard", "AdaptiveObserverPanel.razor"));
        var tinted = Regex.Matches(src, @"class=""@Sc\(([^)]*)\)""").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(tinted);
        Assert.All(tinted, t => Assert.Matches(@"PriceDisplacement|StrictDelta|EnrichedDelta|ReturnBps|Residual|\.(Strict|Enriched|MicroDev|Ofi|BarPx|RollPx)$|^(frMicro|avgMicro|davgMicro)$|^dr\.", t));
        Assert.DoesNotContain(tinted, t => Regex.IsMatch(t, "Oi|Basis|Volume|Efficiency|Duration|Urgency|Gross|Abs|DeltaChange", RegexOptions.IgnoreCase));
    }
}
