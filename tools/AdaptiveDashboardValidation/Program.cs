using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain;
using NiftySignal.Host;
using NiftySignal.Persistence;
using static Microsoft.Playwright.Assertions;

// Isolated synthetic UI fixtures on a throwaway PostgreSQL cluster. These validate rendering, transport and read paths; not research calculations, market behaviour or production latency.
// Usage: AdaptiveDashboardValidation <repository root> <commit sha>. Part 1: the retained diagnostic grids, live quote, screenshots and restart. Part 2: market summary, price averages, paper grids, health.
if (BuildIdentity.Current().CommitSha != args[1]
    || BuildIdentity.Current(typeof(AdaptiveSessionCoordinator).Assembly).CommitSha != args[1])
    throw new Exception("Validation and Host assembly SHA metadata differ from the checked-out revision.");
var root = Path.GetFullPath(args[0]);
var evidence = Path.Combine(root, "artifacts", "adaptive-dashboard-validation");
Directory.CreateDirectory(evidence);
var dashboardDirectory = Path.Combine(root, "NiftySignal.Dashboard", "bin", "Release", "net10.0");
// A local settings file beside the Dashboard would override the isolated settings below (and could hold real credentials and recipients).
if (File.Exists(Path.Combine(dashboardDirectory, "appsettings.Local.json")))
    throw new Exception("appsettings.Local.json exists beside the Dashboard build: refusing to start it. Validate a clean build.");
// Part 2 uses a session dated today (09:30-09:35 IST) and the execution monitor's weekday rule, so it can only run on an IST weekday after 09:40; otherwise it is reported as NOT RUN.
var istNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5));
var currentDashboard = istNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && istNow.TimeOfDay >= new TimeSpan(9, 40, 0);
// The Dashboard's screenshot renderer needs Playwright's own Chromium. A developer without it can set ADAPTIVE_VALIDATION_BROWSER_CHANNEL (e.g. msedge) to check the pages with an installed browser; every
// capture check is then reported as NOT RUN and the run is not a complete validation. CI must leave it unset.
var channel = Environment.GetEnvironmentVariable("ADAPTIVE_VALIDATION_BROWSER_CHANNEL");
var capture = string.IsNullOrWhiteSpace(channel);
// CI uses the default port. ADAPTIVE_VALIDATION_PGPORT lets a developer point the run at a throwaway local cluster
// (the harness writes synthetic rows) instead of their working database.
var pgPort = Environment.GetEnvironmentVariable("ADAPTIVE_VALIDATION_PGPORT");
var portPart = string.IsNullOrWhiteSpace(pgPort) ? "" : $";Port={pgPort}";
var baseConnection = "Host=localhost;Database=niftysignal;Username=postgres;Password=postgres" + portPart;
var options = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(
    "Host=localhost;Database=niftysignal_adaptive_observer;Username=postgres;Password=postgres" + portPart).Options;
await using var db = new AdaptiveObserverDbContext(options);
await db.Database.MigrateAsync();
await using (var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(baseConnection).Options))
    await source.Database.EnsureCreatedAsync();
await RelationalRestartParity.CheckAsync(options,baseConnection,args[1],evidence);
await LateStartRelationalValidation.CheckAsync(options,baseConnection,args[1],evidence);
await using var telegramStub = new TelegramScreenshotStub();
var at = DateTimeOffset.UtcNow.AddMinutes(-5);      // bars 44..48, added later, then end no later than the start of the run
var day = DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
var session = new AdaptiveSessionStateRow {
    TradeDate=day, ModelVersion=OpeningVolumeProjectionV1.ModelVersion, SourceBranch="isolated-ui-validation",
    SourceCommitSha=args[1], BuildUtc=at, FutureToken="VALIDATION", FutureSymbol="NIFTYFUT",
    FutureExpiry=day.AddDays(4), OpeningWindowStartUtc=at.AddMinutes(-45), OpeningWindowEndUtc=at.AddMinutes(-30),
    EstimatorName=nameof(OpeningVolumeProjectionV1), CreatedAtUtc=at, BaseBarVolume=3250, OpeningVolume=30000,
    RollingWindowVolume=32500, OptionUniverseJson=JsonSerializer.Serialize(CaptureFixture.Universe(day.AddDays(4))) };
db.Sessions.Add(session); await db.SaveChangesAsync();
var runtime = new AdaptiveObserverRuntimeRow { SessionId=session.Id, LastHeartbeatUtc=DateTimeOffset.UtcNow,
    RuntimeStatus=AdaptiveRuntimeStatus.Live, LastCompletedBarSeq=20, CurrentPartialBarVolume=1625,
    CurrentPartialBarStartedAtUtc=at };
db.Runtime.Add(runtime);
for (var seq=1;seq<=20;seq++) AddRows(seq);
await db.SaveChangesAsync();
for (var seq=1;seq<=20;seq++) await CaptureFixture.PersistAsync(db,session.Id,seq,BarEnd(seq),day.AddDays(4));
const string url="http://127.0.0.1:5089";
const string Grids="table.adaptive-table:not([data-testid='market-summary'] table):not([data-grid='option-price-averages'])";     // options ATM±2 (diagnostics) and residual
const string password="isolated-ci-validation-password";
var start=new ProcessStartInfo("dotnet") {
    WorkingDirectory=dashboardDirectory, RedirectStandardOutput=true, RedirectStandardError=true };
start.ArgumentList.Add(Path.Combine(dashboardDirectory,"NiftySignal.Dashboard.dll"));
start.ArgumentList.Add("--urls");start.ArgumentList.Add(url);
start.Environment["ASPNETCORE_ENVIRONMENT"]="Development";
start.Environment["ASPNETCORE_URLS"]=url;
start.Environment["ConnectionStrings__NiftySignalDb"]=baseConnection;
start.Environment["DashboardAuth__Username"]="validation-user";
start.Environment["DashboardAuth__PasswordSha256"]=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
start.Environment["AdaptiveScreenshots__Enabled"]=capture ? "true" : "false";
start.Environment["AdaptiveScreenshots__BaseUrl"]=url;
start.Environment["AdaptiveScreenshots__OutputDirectory"]=Path.Combine(evidence,"screenshots");
start.Environment["Telegram__BotToken"]="123456:ci-no-real-token";
start.Environment["Telegram__ChatId"]="ci-no-real-chat";
start.Environment["AdaptiveScreenshotValidationApiUrl"]="http://127.0.0.1:5091/";
// No real recipient: the health monitors' text alerts have no loopback emulator, so they are off.
start.Environment["ObserverHealth__Telegram"]="false";
start.Environment["ExecutionHealth__Telegram"]="false";
start.Environment["ExecutionHealth__Required"]="true";
start.Environment["ExecutionHealth__MonitorFromIst"]="00:00";
start.Environment["ExecutionHealth__MonitorUntilIst"]="23:59";
start.Environment["ExecutionHealth__StaleHeartbeatSeconds"]="8";
start.Environment["ExecutionHealth__PollSeconds"]="1";
start.Environment["ConnectionStrings__TradingPaperLive"]=baseConnection;
start.Environment["AgentFeed__JsonlEnabled"]="false";
using var server=Process.Start(start) ?? throw new Exception("Dashboard did not start.");
var output=server.StandardOutput.ReadToEndAsync(); var errors=server.StandardError.ReadToEndAsync();
var previousDashboardLog="";
try {
    using var http=new HttpClient();
    var ready=false;
    for(var attempt=0;attempt<100;attempt++) {
        try { if((await http.GetAsync(url+"/login")).IsSuccessStatusCode) { ready=true;break; } } catch(HttpRequestException) { }
        if(server.HasExited)throw new Exception("Dashboard exited before listening.");
        await Task.Delay(100);
    }
    if(!ready)throw new Exception("Dashboard startup timed out.");
    using var playwright=await Playwright.CreateAsync();
    await using var browser=await playwright.Chromium.LaunchAsync(new() { Headless=true, Channel=capture ? null : channel });
    await using var context=await browser.NewContextAsync(new() { ViewportSize=new() { Width=1600,Height=1100 } });
    await context.Tracing.StartAsync(new() { Screenshots=true,Snapshots=true,Sources=true });
    var page=await context.NewPageAsync();
    page.SetDefaultTimeout(15000);
    try {
    await page.GotoAsync(url+"/");
    await page.GetByLabel("Username",new() { Exact=true }).FillAsync("validation-user");
    await page.GetByLabel("Password",new() { Exact=true }).FillAsync(password);
    await page.GetByRole(AriaRole.Button,new() { Name="Sign in",Exact=true }).ClickAsync();
    await Expect(page.Locator(Grids)).ToHaveCountAsync(2);
    await Expect(page.Locator(".adaptive-dashboard-build")).ToHaveAttributeAsync("data-commit-sha",args[1]);
    await Expect(page.Locator(".adaptive-dashboard-build")).ToHaveAttributeAsync("data-source-branch",BuildIdentity.Current().SourceBranch);
    await Expect(page.Locator(".adaptive-dashboard-build")).ToContainTextAsync(args[1]);
    await Expect(page.GetByTestId("incomplete-bar-qty")).ToContainTextAsync("1,625");
    await Expect(page.GetByLabel("Completed rows",new() { Exact=true })).ToBeEnabledAsync();
    // The retained diagnostic grids (collapsed by default) keep their columns and values. The futures grid was removed from the Dashboard.
    await Expect(page.Locator(".adaptive-table[data-grid='options-compact'] thead th")).ToHaveTextAsync(new[] { "Seq","End IST","Center","Roll?",
        "CE ΔPx","PE ΔPx","CE Roll ΔPx","PE Roll ΔPx","CE Strict Δ","PE Strict Δ","CE Enriched Δ","PE Enriched Δ","CE Roll Strict","PE Roll Strict",
        "CE OI Δ","PE OI Δ","CE Position","PE Position","CE ΔIV","PE ΔIV","IV Skew","Vol PCR","Roll Vol PCR" });
    await Expect(page.Locator(".adaptive-table[data-grid='residual-compact'] thead th")).ToHaveTextAsync(new[] { "Seq","End IST","Future Δ09:30",
        "CE Res %","CE Res Δ%","PE Res %","PE Res Δ%","Directional Res %","Adjacent Directional Res Δ","Straddle Res %","Direction","Relation","Quote Age" });
    await Expect(page.Locator(".adaptive-table[data-grid='options-compact'] tbody tr").First.Locator("td").Nth(2)).ToHaveTextAsync("23000");
    // Options sidecar (adaptive_options_supplemental_bars) values shown in the compact options grid; residual deltas are adjacent-bar and need no table.
    var optionsFirst=page.Locator(".adaptive-table[data-grid='options-compact'] tbody tr").First.Locator("td");
    await Expect(optionsFirst.Nth(14)).ToHaveTextAsync("+100"); await Expect(optionsFirst.Nth(15)).ToHaveTextAsync("-100");
    await Expect(optionsFirst.Nth(16)).ToHaveTextAsync("CallLongBuild"); await Expect(optionsFirst.Nth(17)).ToHaveTextAsync("PutShortCover");
    await Expect(optionsFirst.Nth(18)).ToHaveTextAsync("+1.50"); await Expect(optionsFirst.Nth(19)).ToHaveTextAsync("-0.50");
    await Expect(optionsFirst.Nth(20)).ToHaveTextAsync("+0.75"); await Expect(optionsFirst.Nth(21)).ToHaveTextAsync("0.90"); await Expect(optionsFirst.Nth(22)).ToHaveTextAsync("1.20");
    var residualFirst=page.Locator(".adaptive-table[data-grid='residual-compact'] tbody tr").First.Locator("td");
    await Expect(residualFirst.Nth(4)).ToHaveTextAsync("0.00%");   // CE Res Δ%: bar 19 is valid, so the adjacent delta exists
    await Expect(residualFirst.Nth(8)).ToHaveTextAsync("0.00%");   // Adjacent Directional Res Δ
    await Expect(residualFirst.Nth(9)).ToHaveTextAsync("—");       // Straddle Res %: this fixture has no frozen anchor baselines
    // Deployment-order safety: a Dashboard that starts before the Host migrated has no sidecar table; the panel must still render, with the sidecar values unavailable.
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE adaptive_basis_supplemental_bars RENAME TO adaptive_basis_supplemental_bars_hidden");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE adaptive_futures_supplemental_bars RENAME TO adaptive_futures_supplemental_bars_hidden");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE adaptive_options_supplemental_bars RENAME TO adaptive_options_supplemental_bars_hidden");
    try
    {
        await page.ReloadAsync();
        await Expect(page.Locator(Grids)).ToHaveCountAsync(2);
        await Expect(page.Locator(".adaptive-table[data-grid='options-compact'] tbody tr").First.Locator("td").Nth(14)).ToHaveTextAsync("—");
        await Expect(page.Locator(".adaptive-alert-error")).ToHaveCountAsync(0);
    }
    finally
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE adaptive_basis_supplemental_bars_hidden RENAME TO adaptive_basis_supplemental_bars");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE adaptive_futures_supplemental_bars_hidden RENAME TO adaptive_futures_supplemental_bars");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE adaptive_options_supplemental_bars_hidden RENAME TO adaptive_options_supplemental_bars");
    }
    await page.ReloadAsync();
    await Expect(page.Locator(".adaptive-table[data-grid='options-compact'] tbody tr").First.Locator("td").Nth(14)).ToHaveTextAsync("+100");
    await Expect(page.GetByLabel("Completed rows",new() { Exact=true })).ToBeEnabledAsync();
    Console.WriteLine("SIDECAR BROWSER PASS: option values rendered from the versioned sidecar; missing sidecar table degrades to unavailable without an error banner.");
    // Reproduce the lost-selector race: a second selection while the first one's read is held back by a table lock.
    await using(var blocker=new AdaptiveObserverDbContext(options))
    await using(var transaction=await blocker.Database.BeginTransactionAsync())
    {
        await blocker.Database.ExecuteSqlRawAsync("LOCK TABLE adaptive_future_bars IN ACCESS EXCLUSIVE MODE");
        await page.GetByLabel("Completed rows",new() { Exact=true }).SelectOptionAsync("15");
        await Task.Delay(1500);   // the first selection's read is now waiting on the table lock
        await page.GetByLabel("Completed rows",new() { Exact=true }).SelectOptionAsync("5");
        await Expect(page.Locator(".adaptive-observer")).ToHaveAttributeAsync("data-requested-rows","5");
        await transaction.CommitAsync();
    }
    foreach(var table in await page.Locator(Grids).AllAsync())
        await Expect(table.Locator("tbody tr:not(.adaptive-avg-row)")).ToHaveCountAsync(5);
    await page.Locator("details.adaptive-diagnostics > summary").ClickAsync();
    await page.GetByLabel("Diagnostic view",new() { Exact=true }).CheckAsync();
    await Expect(page.Locator(Grids+"[data-grid]")).ToHaveCountAsync(0); // diagnostic tables replace the compact ones
    await Expect(page.Locator(Grids)).ToHaveCountAsync(2);
    await Expect(page.Locator(Grids).Nth(1).Locator("thead")).ToContainTextAsync("Legacy Bridged Directional Δ");
    foreach(var count in new[] { 5,10,15 }) {
        await page.GetByLabel("Completed rows",new() { Exact=true }).SelectOptionAsync(count.ToString());
        foreach(var side in new[] { "BOTH","CE","PE" }) {
            await page.GetByLabel("Option side",new() { Exact=true }).SelectOptionAsync(side);
            foreach(var measure in new[] { "NOTIONAL","CONTRACT" }) {
                await page.GetByLabel("Option measurement",new() { Exact=true }).SelectOptionAsync(measure);
                foreach(var variant in new[] { "BAND","ATM" }) {
                    await page.GetByLabel("Residual variant",new() { Exact=true }).SelectOptionAsync(variant);
                    foreach(var table in await page.Locator(Grids).AllAsync()) {
                        await Expect(table.Locator("tbody tr:not(.adaptive-avg-row)")).ToHaveCountAsync(count);
                        var actual=await table.Locator("tbody tr:not(.adaptive-avg-row) td:first-child").AllTextContentsAsync();
                        var expected=Enumerable.Range(21-count,count).Reverse().Select(x=>x.ToString()).ToArray();
                        if(!actual.SequenceEqual(expected))throw new Exception("Grid sequences are not synchronized.");
                    }
                    var optionFirst=page.Locator(Grids).Nth(0).Locator("tbody tr").First.Locator("td");
                    var ce=measure=="NOTIONAL" ? "+0.400" : "+0.200";
                    var pe=measure=="NOTIONAL" ? "-0.500" : "-0.300";
                    await Expect(optionFirst.Nth(7)).ToHaveTextAsync(side=="PE"?pe:ce);
                    if(side=="BOTH") {
                        await Expect(optionFirst.Nth(8)).ToHaveTextAsync(pe);
                        await Expect(optionFirst.Nth(9)).ToHaveTextAsync(measure=="NOTIONAL"?"+0.900":"+0.500");
                    }
                    await Expect(page.Locator(Grids).Nth(1).Locator("tbody tr").First.Locator("td").Nth(4))
                        .ToHaveTextAsync(variant=="ATM"?"+1.00":"+5.00");
                    await Expect(page.Locator(Grids).Nth(0).Locator("tbody tr:has(td:first-child:text-is('18'))"))
                        .ToContainTextAsync("Unavailable");
                    if(variant=="BAND")
                        await Expect(page.Locator(Grids).Nth(1).Locator("tbody tr:has(td:first-child:text-is('17'))"))
                            .ToContainTextAsync("Unavailable");
                }
            }
        }
    }
    await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"selectors.png"),FullPage=true });
    await using var push=new HubConnectionBuilder().WithUrl(url+"/hubs/market-data").Build();
    await push.StartAsync();
    await LiveQuoteBrowserValidation.CheckAsync(page, push, baseConnection, evidence);
    // Pinned Live Quote fixtures. Target bar 43 ends at `at`; ticks after `at` (including the live quote ticks above) must never appear in it.
    await using(var pinnedDb=new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(baseConnection).Options))
    {
        pinnedDb.Ticks.AddRange(
            PinTick("QUOTE-SPOT",24900m,at.AddMinutes(-40)), PinTick("QUOTE-SPOT",24987.50m,at.AddSeconds(-10)),
            PinTick("VALIDATION",23000m,at.AddMinutes(-40)), PinTick("VALIDATION",23044m,at.AddSeconds(-15)),
            PinTick("PIN-CE",100m,at.AddMinutes(-40)), PinTick("PIN-CE",150.25m,at.AddSeconds(-20),true), PinTick("PIN-CE",999m,at.AddSeconds(3),true),
            PinTick("PIN-PE",120m,at.AddMinutes(-40)), PinTick("PIN-PE",140.75m,at.AddSeconds(-25),true));
        await pinnedDb.SaveChangesAsync();
    }
    var samples=new List<double>();
    for(var seq=21;seq<=43;seq++) {
        AddRows(seq); await db.SaveChangesAsync();
        // The bar's sidecars are stored before it is announced complete, so no capture of it can start without them.
        await CaptureFixture.PersistAsync(db,session.Id,seq,BarEnd(seq),day.AddDays(4));
        runtime.LastCompletedBarSeq=seq;runtime.LastHeartbeatUtc=DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(); // Starts only after the database commit returns.
        var timer=Stopwatch.StartNew();
        await push.InvokeAsync("PushAdaptiveStateChanged",session.Id,seq);
        foreach(var table in await page.Locator(Grids).AllAsync())
            await Expect(table.Locator("tbody tr td:first-child").First).ToHaveTextAsync(seq.ToString(),new() { Timeout=5000 });
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        if(seq>=24)samples.Add(timer.Elapsed.TotalMilliseconds); // Three warm-up commits.
    }
    await page.ReloadAsync();
    await Expect(page.Locator(Grids).First.Locator("tbody tr td:first-child").First).ToHaveTextAsync("43");
    await Expect(page.GetByLabel("Completed rows",new() { Exact=true })).ToHaveValueAsync("10");
    await Expect(page.Locator(".adaptive-table[data-grid$='compact']")).ToHaveCountAsync(2); // reload returns to the compact default
    await page.Locator("details.adaptive-diagnostics > summary").ClickAsync();
    await page.GetByLabel("Diagnostic view",new() { Exact=true }).CheckAsync();
    await Expect(page.GetByLabel("Option side",new() { Exact=true })).ToHaveValueAsync("BOTH");
    await Expect(page.GetByLabel("Option measurement",new() { Exact=true })).ToHaveValueAsync("NOTIONAL");
    await Expect(page.GetByLabel("Residual variant",new() { Exact=true })).ToHaveValueAsync("BAND");
    await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"restarted-reader.png"),FullPage=true });
    // Durable worker, its own cookie, real production Chromium capture and multipart upload.
    var expectedTargets = new[] { 13,18,23,28,33,38,43 };
    var screenshotJobs = new List<AdaptiveScreenshotJobRow>();
    if (capture) {
    for(var attempt=0;attempt<240;attempt++) {
        screenshotJobs=await db.ScreenshotJobs.AsNoTracking().Where(x=>x.SessionId==session.Id).OrderBy(x=>x.TargetBarSeq).ToListAsync();
        if(screenshotJobs.Count==expectedTargets.Length && screenshotJobs.All(x=>x.Status==AdaptiveScreenshotStatus.Sent))break;
        await Task.Delay(500);
    }
    if(!screenshotJobs.Select(x=>x.TargetBarSeq).SequenceEqual(expectedTargets) || screenshotJobs.Any(x=>x.Status!=AdaptiveScreenshotStatus.Sent))
        throw new Exception("Screenshot outbox did not deliver pinned initial/every-five-bar snapshots: " +
            JsonSerializer.Serialize(screenshotJobs.Select(x=>new { x.TargetBarSeq,x.Status,x.LastError,x.Attempts })));
    if(telegramStub.Uploads.Count!=7 || telegramStub.Uploads.Any(x=>x.Width<1920))throw new Exception("Screenshot upload count/PNG dimensions invalid.");
    // Every bar, the newest one (43, with no later bar in the database) included, was delivered on its first attempt, once, under its own identity.
    if(screenshotJobs.Any(x=>x.Attempts!=1))throw new Exception("A capture needed a retry: "+JsonSerializer.Serialize(screenshotJobs.Select(x=>new { x.TargetBarSeq,x.Attempts })));
    if(await db.FutureBars.AsNoTracking().AnyAsync(x=>x.SessionId==session.Id && x.BarSeq>43))throw new Exception("Bar 43 is meant to be the newest bar.");
    foreach(var target in expectedTargets)
        if(telegramStub.Uploads.Count(x=>x.Caption.Contains($"Bar {target} @"))!=1)throw new Exception($"Expected exactly one upload captioned for bar {target}.");
    Console.WriteLine("SCREENSHOT DELIVERY PASS: bars 13, 18, 23, 28, 33, 38 and 43 (newest) each delivered once on the first attempt to the loopback emulator.");
    }
    // Readiness is based on the committed boundary, not future rows already in the fixture.
    runtime.LastCompletedBarSeq=9; await db.SaveChangesAsync();
    await page.GotoAsync(url+"/");
    await Expect(page.Locator("[data-readiness='warming']")).ToContainTextAsync("9/10");
    await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"warmup-nine-bars.png"),FullPage=true });
    runtime.LastCompletedBarSeq=43; await db.SaveChangesAsync();
    await page.ReloadAsync();
    await Expect(page.Locator("[data-readiness='ready']")).ToBeVisibleAsync();
    var lastFixtureBar=db.FutureBars.Local.Single(x=>x.SessionId==session.Id && x.BarSeq==43);
    // Completed bars are immutable in production, so readiness only changes when the completed sequence moves (the steady-state header refresh reads
    // just the runtime row). Model "the latest completed bar is invalid" as the sequence advancing onto an invalid bar.
    var newestSeq=page.Locator("table[data-grid='options-compact'] tbody tr").First.Locator("td").First;
    async Task MoveTo(int seq) { runtime.LastCompletedBarSeq=seq; await db.SaveChangesAsync(); await Expect(newestSeq).ToHaveTextAsync(seq.ToString()); }
    lastFixtureBar.TradeUpdates=0; await MoveTo(42);
    await Expect(page.Locator("[data-readiness='ready']")).ToBeVisibleAsync();
    await MoveTo(43);
    await Expect(page.Locator("[data-readiness='warming']")).ToContainTextAsync("0/10");
    lastFixtureBar.TradeUpdates=1; await MoveTo(42);
    await MoveTo(43);
    await Expect(page.Locator("[data-readiness='ready']")).ToBeVisibleAsync();
    Console.WriteLine("READINESS BROWSER PASS: nine bars provisional, ten-plus valid bars ready, invalid latest bar revokes readiness.");
    if (capture) {
    // Restart the real worker against the same PostgreSQL outbox; acknowledged images must not resend.
    await push.StopAsync();
    server.Kill(entireProcessTree:true);
    await server.WaitForExitAsync();
    previousDashboardLog=(await output)+(await errors);
    if(!server.Start())throw new Exception("Dashboard restart failed.");
    output=server.StandardOutput.ReadToEndAsync(); errors=server.StandardError.ReadToEndAsync();
    ready=false;
    for(var attempt=0;attempt<100;attempt++) {
        try { if((await http.GetAsync(url+"/login")).IsSuccessStatusCode) { ready=true;break; } } catch(HttpRequestException) { }
        if(server.HasExited)throw new Exception("Dashboard exited during restart.");
        await Task.Delay(100);
    }
    if(!ready)throw new Exception("Dashboard restart timed out.");
    await Task.Delay(3000);
    if(telegramStub.Uploads.Count!=7 || await db.ScreenshotJobs.AsNoTracking().CountAsync(x=>x.SessionId==session.Id)!=7)
        throw new Exception("Dashboard worker restart duplicated acknowledged screenshots.");
    await page.GotoAsync(url+"/adaptive-screenshots");
    await Expect(page.GetByRole(AriaRole.Button,new() { Name="Send pre-live test",Exact=true })).ToBeEnabledAsync();
    await page.GetByRole(AriaRole.Button,new() { Name="Send pre-live test",Exact=true }).ClickAsync();
    await Expect(page.Locator("p[role='status']")).ToContainTextAsync("Pre-live test queued");
    for(var attempt=0;attempt<60;attempt++) {
        if(await db.ScreenshotJobs.AsNoTracking().AnyAsync(x=>x.Kind==AdaptiveScreenshotKind.PreLiveTest && x.Status==AdaptiveScreenshotStatus.Sent))break;
        await Task.Delay(500);
    }
    if(telegramStub.Uploads.Count!=8)throw new Exception("Pre-live test did not deliver its labeled PNG: " +
        JsonSerializer.Serialize(await db.ScreenshotJobs.AsNoTracking().Where(x=>x.Kind==AdaptiveScreenshotKind.PreLiveTest)
            .Select(x=>new { x.Id,x.Status,x.LastError,x.Attempts }).ToListAsync()));
    await File.WriteAllTextAsync(Path.Combine(evidence,"telegram-screenshots.json"),JsonSerializer.Serialize(new { targets=expectedTargets,uploads=telegramStub.Uploads.Count,
        jobIds=screenshotJobs.Select(x=>x.Id),messageIds=screenshotJobs.Select(x=>x.TelegramMessageId),environment="loopback Telegram emulator, not real account" }));
    Console.WriteLine("Telegram screenshot PASS: seven pinned initial/five-bar snapshots, full-width PNG multipart delivery, real worker restart without resends, pre-live UI test; no real Telegram call.");
    var job43=screenshotJobs.Single(x=>x.TargetBarSeq==43);
    await page.GotoAsync(url+$"/adaptive-screenshot/{job43.Id}");
    var pinnedPanel=page.Locator(".quote-panel[data-capture-ready='true']");
    await Expect(pinnedPanel).ToBeVisibleAsync();
    await Expect(pinnedPanel).ToContainTextAsync("24987.50 (+87.50)");
    await Expect(pinnedPanel).ToContainTextAsync("23044.00");
    await Expect(pinnedPanel).ToContainTextAsync("150.25 (+50.25) · 149.75 / 150.75");
    await Expect(pinnedPanel).ToContainTextAsync("140.75 (+20.75) · 140.25 / 141.25");
    await Expect(pinnedPanel.GetByLabel("Nifty call strike",new() { Exact=true })).ToHaveTextAsync("23000");
    await Expect(pinnedPanel.GetByLabel("Nifty put strike",new() { Exact=true })).ToHaveTextAsync("23000");
    await Expect(pinnedPanel.Locator("select")).ToHaveCountAsync(0);
    await Expect(pinnedPanel).Not.ToContainTextAsync("27000.00"); // the later live spot push
    await Expect(pinnedPanel).Not.ToContainTextAsync("999.00");   // the CE tick after the boundary
    await Expect(pinnedPanel).Not.ToContainTextAsync("Gamma Flip");
    await Expect(page.Locator(Grids)).ToHaveCountAsync(1);       // the residual grid: the ATM±2 diagnostics are not part of a capture
    await Expect(page.GetByTestId("market-summary").Locator("table[data-grid]")).ToHaveCountAsync(4);
    await Expect(page.GetByTestId("paper-trading")).ToHaveCountAsync(0);
    await Expect(page.Locator("table[data-grid='option-price-averages']")).ToHaveCountAsync(0);
    await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"pinned-quote-capture-page.png"),FullPage=true });
    Console.WriteLine("PINNED LIVE QUOTE BROWSER PASS: capture page shows boundary-pinned spot/future/center CE+PE with strike labels; later ticks and live-only controls absent.");
    // Each delivered job's page, with every later bar already stored: it still shows exactly its own completed bar.
    foreach(var job in screenshotJobs) {
        await CaptureFixture.CheckCapturePageAsync(page,url,job.Id,job.TargetBarSeq);
        if(job.TargetBarSeq is 13 or 43)await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,$"capture-page-bar-{job.TargetBarSeq}.png"),FullPage=true });
    }
    var preLive=await db.ScreenshotJobs.AsNoTracking().SingleAsync(x=>x.Kind==AdaptiveScreenshotKind.PreLiveTest);
    if(preLive.Status!=AdaptiveScreenshotStatus.Sent || preLive.Attempts!=1)throw new Exception($"Pre-live test: {preLive.Status}, attempts {preLive.Attempts}.");
    if(preLive.SessionId==session.Id)await CaptureFixture.CheckCapturePageAsync(page,url,preLive.Id,preLive.TargetBarSeq);
    else await page.GotoAsync(url+$"/adaptive-screenshot/{preLive.Id}");
    await Expect(page.Locator(".adaptive-capture > h2")).ToContainTextAsync("PRE-LIVE DELIVERY TEST");
    if(preLive.SessionId is null) {
        await Expect(page.Locator(".adaptive-capture h1")).ToContainTextAsync("no market bar");
        await Expect(page.GetByTestId("capture-subtitle")).ToHaveTextAsync($"Delivery check only · no adaptive session is pinned, so no market grids are shown · job {preLive.Id}");
        await Expect(page.Locator(".adaptive-capture table")).ToHaveCountAsync(0);
        await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"capture-page-pre-live.png"),FullPage=true });
    }
    Console.WriteLine($"CAPTURE PAGE PASS: seven job pages show their own completed bar, exactly the five grids, the corrected subtitle and the expected ITM / ATM / OTM values; pre-live page labelled as a delivery check with {(preLive.SessionId is null ? "no session, no market bar and no grid" : $"bar {preLive.TargetBarSeq}")}.");

    // Option Price Averages (live only): exact Avg5 / Avg10 of the 23000 contracts at bar 43, and the gaps of a fresh ask.
    await using(var quoteDb=new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(baseConnection).Options))
    {
        var now=DateTimeOffset.UtcNow;
        quoteDb.Ticks.AddRange(PinTick("PIN-CE",270m,now,true), PinTick("PIN-PE",355m,now,true));
        await quoteDb.SaveChangesAsync();
    }
    await page.GotoAsync(url+"/");
    var averageRows=page.Locator("table[data-grid='option-price-averages'] tbody tr");
    await Expect(averageRows).ToHaveCountAsync(2);
    foreach(var (side,cells) in new[] {
        ("CE", new[] { "PIN-CE","270.00","269.50","270.50",null,"264.00","+6.50","+2.46%","254.00","+16.50","+6.50%" }),
        ("PE", new[] { "PIN-PE","355.00","354.50","355.50",null,"359.00","-3.50","-0.97%","361.50","-6.00","-1.66%" }) })
        for(var i=0;i<cells.Length;i++)
            if(cells[i] is { } value) await Expect(page.Locator($"tr[data-side='{side}'] td").Nth(i+1)).ToHaveTextAsync(value);
    await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"option-price-averages.png"),FullPage=true });
    Console.WriteLine("OPTION PRICE AVERAGES BROWSER PASS: exact Avg5 / Avg10 and ask gaps for CE and PE at bar 43.");

    // A summary that is temporarily unavailable: bar 48 is complete but its close ATM is not stored yet (the Observer retries that write).
    for(var seq=44;seq<=48;seq++) {
        AddRows(seq); await db.SaveChangesAsync();
        await CaptureFixture.PersistAsync(db,session.Id,seq,BarEnd(seq),day.AddDays(4),closeAtm:seq!=48);
    }
    if(BarEnd(48)>DateTimeOffset.UtcNow)throw new Exception("The fixture stored a completed bar that ends in the future.");
    var uploadsBefore=telegramStub.Uploads.Count;
    runtime.LastCompletedBarSeq=48;runtime.LastHeartbeatUtc=DateTimeOffset.UtcNow; await db.SaveChangesAsync();
    async Task<AdaptiveScreenshotJobRow?> Job48() => await db.ScreenshotJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.SessionId==session.Id && x.TargetBarSeq==48);
    AdaptiveScreenshotJobRow? retry=null;
    for(var attempt=0;attempt<120 && retry?.LastError is null;attempt++) { await Task.Delay(500); retry=await Job48(); }
    if(retry is null || retry.Status!=AdaptiveScreenshotStatus.Pending || retry.Attempts!=1 || retry.LastError?.Contains("summary-futures")!=true || telegramStub.Uploads.Count!=uploadsBefore)
        throw new Exception("An unavailable summary was not refused and queued: "+JsonSerializer.Serialize(retry is null ? null : new { retry.Status,retry.Attempts,retry.LastError,uploads=telegramStub.Uploads.Count }));
    await CaptureFixture.PersistCloseAtmAsync(db,session.Id,48,BarEnd(48),day.AddDays(4));
    for(var attempt=0;attempt<180 && retry!.Status!=AdaptiveScreenshotStatus.Sent;attempt++) { await Task.Delay(500); retry=await Job48(); }
    if(retry!.Status!=AdaptiveScreenshotStatus.Sent || retry.Attempts!=2)throw new Exception($"The queued capture was not delivered by its retry: {retry.Status}, attempts {retry.Attempts}, {retry.LastError}");
    await Task.Delay(5000);
    retry=await Job48();
    if(retry!.Attempts!=2 || telegramStub.Uploads.Count!=uploadsBefore+1 || telegramStub.Uploads.Count(x=>x.Caption.Contains("Bar 48 @"))!=1)
        throw new Exception($"Duplicate or missing delivery after the retry: attempts {retry.Attempts}, uploads {telegramStub.Uploads.Count}.");
    await CaptureFixture.CheckCapturePageAsync(page,url,retry.Id,48);
    Console.WriteLine("CAPTURE RETRY PASS: bar 48 refused without a close ATM (no upload), delivered on the second attempt once the row existed, exactly once.");
    } else { await push.StopAsync(); Console.WriteLine("NOT RUN: screenshot delivery, worker restart without resends, pre-live test and pinned capture page (no Playwright Chromium)."); }
    await page.GotoAsync(url+"/legacy");
    await Expect(page.Locator(".dashboard-shell")).ToBeVisibleAsync();

    var sorted=samples.Order().ToArray(); var p95=sorted[(int)Math.Ceiling(sorted.Length*.95)-1];
    var report=new { sourceSha=args[1], environment="isolated PostgreSQL and headless browser, synthetic data; not VM measurement",
        selectorCombinations=36,selectorRaceWithHeldRead="table lock held for a fixed delay; the blocked read is not separately proven",assemblySourceShaVerified=true,paintFramesAwaited=true, latencySamples=samples, meanMs=samples.Average(),p95Ms=p95,maxMs=samples.Max(),
        allSamplesBelowOneSecond=samples.All(x=>x<1000), readerRestart=capture, screenshotCaptureRun=capture, currentDashboardChecksRun=currentDashboard, browser=capture ? "playwright-chromium" : channel };
    await File.WriteAllTextAsync(Path.Combine(evidence,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }));
    Console.WriteLine($"Dashboard PASS{(capture ? "" : " (PARTIAL: capture checks not run)")}: 36 selector combinations, commit-to-grids p95={p95:F1}ms max={samples.Max():F1}ms.");
    if(samples.Any(x=>x>=1000))throw new Exception("Commit-to-visible dashboard latency exceeded one second.");
    if (currentDashboard) await CurrentDashboardValidation.CheckAsync(page, options, baseConnection, root, evidence, url, args[1], telegramStub, capture);
    else Console.WriteLine("NOT RUN: market summary, price averages, paper grids, health banners and recovery information (needs an IST weekday after 09:40).");
    } finally {
        try {
            await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"last-state.png"),FullPage=true });
            await context.Tracing.StopAsync(new() { Path=Path.Combine(evidence,"browser-trace.zip") });
        } catch(PlaywrightException ex) { Console.WriteLine($"Browser evidence capture failed: {ex.Message}"); }
    }
} finally {
    if(!server.HasExited)server.Kill(entireProcessTree:true);
    await server.WaitForExitAsync();
    await File.WriteAllTextAsync(Path.Combine(evidence,"dashboard.log"),previousDashboardLog+(await output)+(await errors));
}
NiftySignal.Domain.Entities.Tick PinTick(string token,decimal price,DateTimeOffset when,bool depth=false) => new() {
    Token=token,Exchange=Exchange.Nfo,ExchangeTimestamp=when,ReceivedAt=when,LastPrice=price,
    Depth=depth ? new NiftySignal.Domain.ValueObjects.MarketDepth(price-.5m,65,0,0,0,0,0,0,0,0,price+.5m,65,0,0,0,0,0,0,0,0) : null };
// Scripted flow (historical shape from the removed commentary validation): bar 10 SellerExpansion (New), 21 ShortCovering (Flipped), 22 SellerAbsorption (bias change), 23 Resolved.
(int Flow,long OiChange,double BarMove,double RollMove) FlowPattern(int seq) => seq switch {
    < 10 => (-1,50,-2,-5), <= 20 => (-1,50,-2,-5), 21 => (1,-5,2,5), 22 => (-1,50,.5,-5), _ => (0,0,0,0) };
DateTimeOffset BarEnd(int seq) => at.AddMinutes(seq-43);       // bar 43 ends at the session's reference instant
void AddRows(int seq) {
    var end=BarEnd(seq);var begin=end.AddMinutes(-1);
    var (flow,oiChange,barMove,rollMove)=FlowPattern(seq);
    db.FutureBars.Add(new AdaptiveFutureBarRow { SessionId=session.Id,BarSeq=seq,StartAvailableAtUtc=begin,EndAvailableAtUtc=end,
        Open=23000,High=23001,Low=23000,Close=23001,Volume=3250,TradeUpdates=1,DurationSeconds=60,
        BarPriceDisplacement=barMove,StrictDelta=flow*100,EnrichedDelta=flow*110 });
    if(seq>=10)
        db.RollingStates.Add(new AdaptiveRollingStateRow { SessionId=session.Id,StartBarSeq=seq-9,EndBarSeq=seq,StartAvailableAtUtc=begin.AddMinutes(-9),
            EndAvailableAtUtc=end,PriceDisplacement=rollMove,StrictDelta=flow*1000,EnrichedDelta=flow*1100,OiChange=oiChange,Efficiency=.5,
            RollingStrictAbsDeltaChange=0,State=AdaptiveStateKind.Normal });
    db.OptionsSupplemental.Add(new AdaptiveOptionsSupplementalRow { SessionId=session.Id,BarSeq=seq,MetricsVersion=OptionsSupplementalBar.MetricsVersion,
        CenterStrike=23000,CeOiDelta=100,PeOiDelta=-100,CePosition="CallLongBuild",PePosition="PutShortCover",CeDeltaIv=1.5,PeDeltaIv=-.5,
        IvSkewEnd=.75,VolPcr=.9,RollVolPcr=1.2,DayCeVolume=1000,DayPeVolume=900,UniverseTokenCount=2,TokensObserved=2 });
    db.BasisSupplemental.Add(new AdaptiveBasisSupplementalRow { SessionId=session.Id,BarSeq=seq,MetricsVersion=FuturesBasisBar.MetricsVersion,
        DeltaBasis=seq==20 ? 1.25 : null,BasisStart=seq==20 ? 100 : null,BasisEnd=seq==20 ? 101.25 : null,Status=seq==20 ? "Usable" : "StaleEnd" });
    db.FuturesSupplemental.Add(new AdaptiveFuturesSupplementalRow { SessionId=session.Id,BarSeq=seq,MetricsVersion=FuturesMicrostructureBar.MetricsVersion,
        MicroDevTimeWeighted=.125,Ofi=-400L-seq,ValidBookSeconds=60 });
    foreach(var side in new[] { OptionType.Call,OptionType.Put })
        db.OptionBandBars.Add(new AdaptiveOptionBandBarRow { SessionId=session.Id,BarSeq=seq,Side=side,
            StartAvailableAtUtc=begin,EndAvailableAtUtc=end,BandStrikes="22900,22950,23000,23050,23100",
            BandAvailable=seq!=18,UnavailableReason="fixture option quotes missing",CenterStrike=23000,DurationSeconds=60,
            ContractStrictDeltaRatioTotal=side==OptionType.Call ? .2 : -.3,
            NotionalStrictDeltaRatioTotal=side==OptionType.Call ? .4 : -.5 });
    foreach(var variant in new[] { ResidualVariant.Atm,ResidualVariant.AtmPlusMinus2 })
        db.OptionResidualBars.Add(new AdaptiveOptionResidualBarRow { SessionId=session.Id,BarSeq=seq,Variant=variant,
            EndAvailableAtUtc=end,Relationship="NEUTRAL",IsAvailable=variant!=ResidualVariant.AtmPlusMinus2 || seq!=17,
            UnavailableReason="fixture diagnostic quote missing",CenterStrike=23000,
            CEActualChange=variant==ResidualVariant.Atm?1:5 });
}
