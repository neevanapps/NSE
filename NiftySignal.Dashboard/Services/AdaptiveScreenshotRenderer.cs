using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Dashboard.Services;

public interface IAdaptiveScreenshotRenderer
{
    Task CaptureAsync(AdaptiveScreenshotJobRow job, string path, CancellationToken ct);
}

/// <summary>One primary grid every research capture must contain. <see cref="StartsAtTarget"/>: its first body row is the target completed bar (the residual grid is legitimately empty before the 09:30 anchor).</summary>
public sealed record RequiredCaptureGrid(string Grid, bool StartsAtTarget);

public sealed class AdaptiveScreenshotRenderer(IOptions<AdaptiveScreenshotOptions> settings,
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions, IOptions<DashboardAuthOptions> auth) : IAdaptiveScreenshotRenderer
{
    public const string AuthenticationScheme = "AdaptiveCapture";

    /// <summary>The stable identities of the primary research grids (data-grid attributes in AdaptiveObserverPanel).</summary>
    public static readonly IReadOnlyList<RequiredCaptureGrid> RequiredGrids =
    [
        new("futures-compact", true), new("options-core-direction", true), new("options-context", true), new("residual-compact", false),
    ];

    /// <summary>Other blocks a research capture must contain (data-testid): the identity header, its data-quality line and the recent-events strip.</summary>
    public static readonly IReadOnlyList<string> RequiredBlocks = ["research-header", "research-data-quality", "recent-events"];

    public async Task CaptureAsync(AdaptiveScreenshotJobRow job, string path, CancellationToken ct)
    {
        var origin = settings.Value.LocalUri(); // Never send a privileged cookie to a configured external URL.
        if (!auth.Value.IsConfigured) throw new InvalidOperationException("Dashboard authentication is not configured.");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Timeout = 30000 });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1920, Height = 1080 } });
        var scheme = AuthenticationScheme;
        var cookie = cookieOptions.Get(scheme);
        var now = DateTimeOffset.UtcNow;
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "adaptive-screenshot-reader")], scheme)),
            new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddMinutes(2), AllowRefresh = false }, scheme);
        await context.AddCookiesAsync([new Cookie { Name = cookie.Cookie.Name!, Value = cookie.TicketDataFormat.Protect(ticket),
            Domain = origin.Host, Path = "/adaptive-screenshot", HttpOnly = true, Secure = origin.Scheme == "https", SameSite = SameSiteAttribute.Strict }]);
        await context.RouteAsync("**/*", route => Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var requested)
            && requested.IsLoopback && requested.Scheme == origin.Scheme && requested.Authority == origin.Authority
            ? route.ContinueAsync() : route.AbortAsync());
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30000);
        ct.ThrowIfCancellationRequested();
        await page.GotoAsync(origin.ToString());
        if (await page.Locator(".adaptive-observer").CountAsync() != 0)
            throw new InvalidOperationException("Capture-only authentication unexpectedly grants normal Dashboard access.");
        var response = await page.GotoAsync(new Uri(origin, $"adaptive-screenshot/{job.Id}").ToString());
        if (response is null || response.Status >= 400)
            throw new InvalidOperationException($"Capture page returned HTTP {response?.Status}; check Dashboard server logs.");
        await page.Locator(".adaptive-observer[data-capture-ready='true']").WaitForAsync();
        // The pinned Live Quote panel must be present in the image (08-Oct plan section 18).
        await page.Locator(".quote-panel[data-capture-ready='true']").WaitForAsync();
        await Microsoft.Playwright.Assertions.Expect(page.Locator(".quote-panel")).ToHaveCountAsync(1);
        await Microsoft.Playwright.Assertions.Expect(page.Locator(".adaptive-capture")).ToHaveAttributeAsync("data-job-id", job.Id.ToString());
        // The pinned quote renders once, normally, at the top of the document: never sticky in a capture.
        if (await page.Locator(".quote-panel").EvaluateAsync<string>("e => getComputedStyle(e).position") == "sticky")
            throw new InvalidOperationException("Capture Live Quote must not be sticky; image not uploaded.");
        if (job.SessionId is not null)
        {
            // Required PRIMARY content is verified by stable identities, not by counting tables (Diagnostics and any future table must not break a capture).
            // A capture that is missing any of them is refused rather than sent partially.
            foreach (var required in RequiredGrids)
                if (await page.Locator($".adaptive-table[data-grid='{required.Grid}']").CountAsync() != 1)
                    throw new InvalidOperationException($"Required capture grid '{required.Grid}' did not render; image not uploaded.");
            foreach (var testId in RequiredBlocks)
                if (await page.Locator($"[data-testid='{testId}']").CountAsync() < 1)
                    throw new InvalidOperationException($"Required capture block '{testId}' did not render; image not uploaded.");
            if (job.TargetBarSeq > 0)
                foreach (var required in RequiredGrids.Where(x => x.StartsAtTarget))
                    await Microsoft.Playwright.Assertions.Expect(page.Locator($".adaptive-table[data-grid='{required.Grid}'] tbody tr td:first-child").First).ToHaveTextAsync(job.TargetBarSeq.ToString());
        }
        // Wide tables scroll in the human UI. Expand capture to expose every column, not just the viewport.
        const string PrimaryTables = ".adaptive-capture .adaptive-table";
        for (var pass = 0; pass < 3; pass++)
        {
            var overflow = await page.EvaluateAsync<int>("() => Math.ceil(Math.max(0,...Array.from(document.querySelectorAll('" + PrimaryTables + "')).map(t=>t.scrollWidth-t.parentElement.clientWidth)))");
            if (overflow <= 1) break;
            var width = page.ViewportSize!.Width + overflow + 32;
            if (width > 10000) throw new InvalidOperationException("Capture table exceeds safe image width.");
            await page.SetViewportSizeAsync(width, 1080);
            await page.EvaluateAsync("() => new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
        }
        if (!await page.EvaluateAsync<bool>("() => Array.from(document.querySelectorAll('" + PrimaryTables + "')).every(t=>t.scrollWidth<=t.parentElement.clientWidth+1)"))
            throw new InvalidOperationException("Capture would hide grid columns; image not uploaded.");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await page.ScreenshotAsync(new() { Path = path, FullPage = true });
    }
}
