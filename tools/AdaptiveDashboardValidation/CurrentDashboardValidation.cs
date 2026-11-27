using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Persistence;
using static Microsoft.Playwright.Assertions;

/// <summary>Market summary, option price averages, the two paper grids, health banners, Observer recovery information and the capture page of a fixed-clock session.</summary>
static class CurrentDashboardValidation
{
    public static async Task CheckAsync(IPage page, DbContextOptions<AdaptiveObserverDbContext> options, string connection, string root, string evidence, string url, string sha, TelegramScreenshotStub telegram, bool capture)
    {
        var checks = new List<string>();
        void Passed(string text) { checks.Add(text); Console.WriteLine("PASS: " + text); }
        var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).Date);
        var market = await SyntheticSession.CreateAsync(options, connection, sha, day);
        await using var paper = await PaperFixture.CreateAsync(root, connection, day);
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.SetDefaultTimeout(20000);
        await page.GotoAsync(url + "/");

        var summary = page.GetByTestId("market-summary");
        await Expect(page.GetByTestId("market-summary-basket")).ToHaveAttributeAsync("data-end-bar", market.LastBar.ToString(), new() { Timeout = 30000 });
        await Expect(summary.Locator("table[data-grid]")).ToHaveCountAsync(4);
        await Expect(summary.Locator("table[data-grid='summary-futures']")).ToHaveCountAsync(1);
        foreach (var testId in new[] { "summary-total-title", "summary-total-itm-title", "summary-total-otm-title" }) await Expect(page.GetByTestId(testId)).ToBeVisibleAsync();
        foreach (var grid in await summary.Locator("table[data-grid]").AllAsync())
        {
            Check(await grid.Locator("tbody tr").CountAsync() >= 2, "a market-summary grid has no window rows");
            Check(Regex.IsMatch(await grid.Locator("tbody").InnerTextAsync(), @"[+-]\d"), "a market-summary grid shows no signed value");
        }
        Passed($"four market-summary grids with values, ending at bar {market.LastBar}");

        var averages = page.Locator("table[data-grid='option-price-averages']");
        await Expect(averages.Locator("tbody tr")).ToHaveCountAsync(2);
        var call = averages.Locator("tr[data-side='CE'] td");
        await Expect(call.Nth(6)).ToHaveTextAsync(new Regex(@"^\d+\.\d+"));
        await Expect(call.Nth(9)).ToHaveTextAsync(new Regex(@"^\d+\.\d+"));
        await Expect(call.Nth(5)).ToContainTextAsync("stale");
        await market.AddFreshOptionQuotesAsync();
        await Expect(call.Nth(5)).Not.ToContainTextAsync("stale", new() { Timeout = 15000 });
        Passed("option price averages: Avg5/Avg10 for the exact contract, stale quote marked, fresh quote picked up");

        var section = page.GetByTestId("paper-trading");
        await Expect(section.Locator("table")).ToHaveCountAsync(2);
        foreach (var testId in new[] { "paper-current", "paper-closed" })
        {
            await Expect(page.GetByTestId(testId).Locator("tbody tr")).ToHaveCountAsync(4);
            foreach (var id in PaperFixture.Strategies) await Expect(page.GetByTestId(testId).Locator($"tr[data-strategy='{id}']")).ToHaveCountAsync(1);
        }
        ILocator Row(string grid, int strategy, string cell) => page.GetByTestId(grid).Locator($"tr[data-strategy='{PaperFixture.Strategies[strategy]}']").GetByTestId(cell);
        foreach (var bid in new[] { 110.0, 118.0, 104.0 })
        {
            paper.Bid = bid;
            var unrealized = paper.Unrealized(bid);
            await Expect(Row("paper-current", 0, "paper-bid")).ToHaveTextAsync(bid.ToString("0.00", CultureInfo.InvariantCulture), new() { Timeout = 10000 });
            await Expect(Row("paper-current", 0, "paper-unrealized")).ToHaveTextAsync(Money(unrealized));
            await Expect(Row("paper-current", 0, "paper-partial")).ToHaveTextAsync(Money(PaperFixture.PartialNet));
            await Expect(Row("paper-current", 0, "paper-total")).ToHaveTextAsync(Money(PaperFixture.PartialNet + unrealized));
        }
        await Expect(Row("paper-current", 0, "paper-remaining")).ToHaveTextAsync("65");
        Passed("live bid, partial realized, unrealized and total follow the quote without a new bar");
        await Expect(Row("paper-current", 1, "paper-row-status")).ToHaveTextAsync("Waiting for Avg5");
        await Expect(Row("paper-current", 1, "paper-unrealized")).ToHaveTextAsync("—");
        await Expect(Row("paper-current", 2, "paper-quote")).ToContainTextAsync("STALE");
        await Expect(Row("paper-current", 2, "paper-unrealized")).ToHaveTextAsync("unavailable");
        await Expect(Row("paper-current", 3, "paper-row-status")).ToHaveTextAsync("Flat");
        await Expect(Row("paper-closed", 3, "paper-closed-count")).ToHaveTextAsync("2");
        await Expect(Row("paper-closed", 3, "paper-closed-net")).ToHaveTextAsync(Money(PaperFixture.ClosedNet));
        await Expect(Row("paper-closed", 0, "paper-closed-count")).ToHaveTextAsync("0");     // the open position's partial is not counted again as closed
        Passed("pending Avg5, stale quote without a profit figure, closed accounting without double counting");
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "dashboard.png"), FullPage = true });

        paper.Condition = "synthetic condition: market data is stale";
        await Expect(page.Locator("[data-execution-condition]")).ToContainTextAsync("synthetic condition", new() { Timeout = 10000 });
        paper.Silent = true;
        await Expect(page.Locator("[data-execution-condition='Silent']")).ToContainTextAsync("NO protection", new() { Timeout = 30000 });
        await Expect(page.GetByTestId("paper-notice")).ToContainTextAsync("EXECUTION SERVICE SILENT", new() { Timeout = 40000 });
        await Expect(Row("paper-current", 0, "paper-row-status")).ToContainTextAsync("Last reported");
        await Expect(Row("paper-current", 0, "paper-unrealized")).ToHaveTextAsync("unavailable");
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "execution-silent.png"), FullPage = true });
        paper.Silent = false; paper.Condition = null;
        await Expect(page.Locator("[data-execution-condition]")).ToHaveCountAsync(0, new() { Timeout = 20000 });
        await Expect(page.GetByTestId("paper-notice")).ToHaveCountAsync(0, new() { Timeout = 20000 });
        Passed("execution health banner: reported condition, silent service naming unprotected positions, recovery");

        await market.SetRebuildingAsync();
        await page.ReloadAsync();
        var recovery = page.GetByTestId("runtime-recovery");
        await Expect(recovery).ToContainTextAsync("rebuilding since");
        await Expect(recovery).ToContainTextAsync("replaying");
        await Expect(recovery).ToContainTextAsync("42 bars replayed (60 persisted before restart)");
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "observer-recovering.png"), FullPage = true });
        await market.SetRecoveryFailedAsync();
        await page.ReloadAsync();
        await Expect(recovery).ToContainTextAsync("RECOVERY FAILED (consecutive failures: 3)");
        await market.SetLiveAsync();
        await page.ReloadAsync();
        await Expect(recovery).Not.ToContainTextAsync("FAILED");
        Passed("Observer recovery progress, failure and return to live are shown");

        if (capture)
        {
            await using var db = new AdaptiveObserverDbContext(options);
            AdaptiveScreenshotJobRow? job = null;
            for (var attempt = 0; attempt < 240 && job is null; attempt++)
            {
                job = await db.ScreenshotJobs.AsNoTracking().Where(x => x.SessionId == market.SessionId && x.Status == AdaptiveScreenshotStatus.Sent && x.TargetBarSeq >= 11).OrderByDescending(x => x.Id).FirstOrDefaultAsync();
                if (job is null) await Task.Delay(500);
            }
            Check(job is not null, "no screenshot of the fixed-clock session was delivered: " + JsonSerializer.Serialize(await db.ScreenshotJobs.AsNoTracking()
                .Where(x => x.SessionId == market.SessionId).Select(x => new { x.TargetBarSeq, x.Status, x.LastError, x.Attempts }).ToListAsync()));
            Check(telegram.Uploads.All(x => x.Width >= 1920), "screenshot upload dimensions are invalid");
            await page.GotoAsync(url + $"/adaptive-screenshot/{job!.Id}");
            await Expect(page.Locator(".quote-panel[data-capture-ready='true']")).ToBeVisibleAsync(new() { Timeout = 30000 });
            await Expect(page.GetByTestId("market-summary").Locator("table[data-grid]")).ToHaveCountAsync(4);
            await Expect(page.GetByTestId("market-summary-basket")).ToHaveAttributeAsync("data-end-bar", job.TargetBarSeq.ToString());
            await Expect(page.Locator(".adaptive-capture h1")).ToContainTextAsync($"completed bar {job.TargetBarSeq}");
            await Expect(page.GetByTestId("capture-subtitle")).ToContainTextAsync("TOTAL summaries (ITM-shifted, ATM, OTM-shifted) and CE / PE theoretical residual");
            var grids = await page.EvaluateAsync<string[]>("() => Array.from(document.querySelectorAll('table')).map(t => t.getAttribute('data-grid') || '(unnamed)')");
            Check(grids.SequenceEqual(new[] { "summary-futures", "summary-total-itm", "summary-total", "summary-total-otm", "residual-compact" }), "fixed-clock capture tables: " + string.Join(", ", grids));
            foreach (var grid in await page.GetByTestId("market-summary").Locator("table[data-grid]").AllAsync())
                Check(Regex.IsMatch(await grid.Locator("tbody").InnerTextAsync(), @"[+-]?\d+\.\d+ \("), "a fixed-clock capture grid shows no value");
            await Expect(page.GetByTestId("paper-trading")).ToHaveCountAsync(0);
            await Expect(page.Locator("table[data-grid='option-price-averages']")).ToHaveCountAsync(0);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "capture-page.png"), FullPage = true });
            Passed($"fixed-clock capture delivered to the loopback emulator (bar {job.TargetBarSeq}); its page shows that bar, exactly the five grids with values and no live-only table");
        }
        else Console.WriteLine("NOT RUN: fixed-clock screenshot capture and capture page.");
        Check(pageErrors.Count == 0, "browser errors: " + string.Join("; ", pageErrors));
        await File.WriteAllTextAsync(Path.Combine(evidence, "current-dashboard-report.json"), JsonSerializer.Serialize(new { sourceSha = sha, environment = "isolated PostgreSQL, synthetic data, headless browser", screenshotCaptureRun = capture, checks },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    static void Check(bool condition, string problem) { if (!condition) throw new Exception(problem); }
    static string Money(decimal v) => (v > 0 ? "+" : "") + v.ToString("N2", CultureInfo.InvariantCulture);
}
