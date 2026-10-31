using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>
/// Validation aid, skipped unless <c>NSE_V2_DASHBOARD_DB</c> (a connection string to a disposable pev2 database, for example one kept by the host replay) and <c>NSE_V2_DASHBOARD_ASOF</c> (UTC ISO instant) are set.
/// Renders the real read-only Home V2 section from that database as of the given instant into a standalone HTML file (Dashboard stylesheet inlined) for a browser check. Read-only: no write is issued.
/// </summary>
public class DashboardExportTests
{
    [Fact]
    public async Task ExportHomeV2Section_FromADisposableDatabase()
    {
        var cs = Environment.GetEnvironmentVariable("NSE_V2_DASHBOARD_DB");
        var asOf = Environment.GetEnvironmentVariable("NSE_V2_DASHBOARD_ASOF");
        if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(asOf)) return;

        using var svc = new PaperEngineV2DataService(cs);
        var view = await svc.LoadAsync(DateTimeOffset.Parse(asOf, System.Globalization.CultureInfo.InvariantCulture), default);
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<PaperEngineV2Section>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = view }))).ToHtmlString());

        var root = TradingModelTests.RepoRoot();
        var css = File.ReadAllText(Path.Combine(root, "NiftySignal.Dashboard", "wwwroot", "app.css"));
        var scoped = Path.Combine(root, "NiftySignal.Dashboard", "obj", "Debug", "net10.0", "scopedcss", "bundle", "NiftySignal.Dashboard.styles.css");
        if (File.Exists(scoped)) css += Environment.NewLine + File.ReadAllText(scoped);
        var page = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>V2 Home export</title><style>{css}</style></head><body style=\"padding:12px\">{html}</body></html>";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "paper-v2-home-export.html"), page);
        Assert.Contains("data-testid=\"v2-shadow\"", html);
    }
}
