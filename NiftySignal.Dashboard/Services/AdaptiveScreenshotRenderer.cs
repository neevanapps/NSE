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

public sealed class AdaptiveScreenshotRenderer(IOptions<AdaptiveScreenshotOptions> settings,
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions, IOptions<DashboardAuthOptions> auth) : IAdaptiveScreenshotRenderer
{
    public async Task CaptureAsync(AdaptiveScreenshotJobRow job, string path, CancellationToken ct)
    {
        var origin = settings.Value.LocalUri(); // Never send a privileged cookie to a configured external URL.
        if (!auth.Value.IsConfigured) throw new InvalidOperationException("Dashboard authentication is not configured.");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Timeout = 30000 });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1920, Height = 1080 } });
        var scheme = CookieAuthenticationDefaults.AuthenticationScheme;
        var cookie = cookieOptions.Get(scheme);
        var now = DateTimeOffset.UtcNow;
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "adaptive-screenshot-reader")], scheme)),
            new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddMinutes(2), AllowRefresh = false }, scheme);
        await context.AddCookiesAsync([new Cookie { Name = cookie.Cookie.Name!, Value = cookie.TicketDataFormat.Protect(ticket),
            Url = origin.ToString(), HttpOnly = true, Secure = origin.Scheme == "https", SameSite = SameSiteAttribute.Lax }]);
        await context.RouteAsync("**/*", route => Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var requested)
            && requested.IsLoopback && requested.Scheme == origin.Scheme && requested.Authority == origin.Authority
            ? route.ContinueAsync() : route.AbortAsync());
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30000);
        ct.ThrowIfCancellationRequested();
        await page.GotoAsync(new Uri(origin, $"adaptive-screenshot/{job.Id}").ToString());
        await page.Locator(".adaptive-observer[data-capture-ready='true']").WaitForAsync();
        await Microsoft.Playwright.Assertions.Expect(page.Locator(".adaptive-capture")).ToHaveAttributeAsync("data-job-id", job.Id.ToString());
        if (job.SessionId is not null)
        {
            await Microsoft.Playwright.Assertions.Expect(page.Locator(".adaptive-table")).ToHaveCountAsync(3);
            foreach (var table in await page.Locator(".adaptive-table").AllAsync())
                if (job.TargetBarSeq > 0)
                    await Microsoft.Playwright.Assertions.Expect(table.Locator("tbody tr td:first-child").First).ToHaveTextAsync(job.TargetBarSeq.ToString());
        }
        // Wide tables scroll in the human UI. Expand capture to expose every column, not just the viewport.
        var width = await page.EvaluateAsync<int>("() => Math.ceil(Math.max(1920,...Array.from(document.querySelectorAll('table')).map(t=>t.scrollWidth+100)))");
        if (width > 10000) throw new InvalidOperationException("Capture table exceeds safe image width.");
        await page.SetViewportSizeAsync(width, 1080);
        await page.EvaluateAsync("() => new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
        if (!await page.EvaluateAsync<bool>("() => Array.from(document.querySelectorAll('.adaptive-table')).every(t=>t.scrollWidth<=t.parentElement.clientWidth+1)"))
            throw new InvalidOperationException("Capture would hide grid columns; image not uploaded.");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await page.ScreenshotAsync(new() { Path = path, FullPage = true });
    }
}
