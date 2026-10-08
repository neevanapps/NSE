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

// Isolated synthetic UI fixtures. These validate rendering/transport, not research calculations.
if (BuildIdentity.Current().CommitSha != args[1]
    || BuildIdentity.Current(typeof(AdaptiveSessionCoordinator).Assembly).CommitSha != args[1])
    throw new Exception("Validation and Host assembly SHA metadata differ from the checked-out revision.");
var root = Path.GetFullPath(args[0]);
var evidence = Path.Combine(root, "artifacts", "adaptive-dashboard-validation");
Directory.CreateDirectory(evidence);
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
var at = DateTimeOffset.UtcNow;
var day = DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
var session = new AdaptiveSessionStateRow {
    TradeDate=day, ModelVersion=OpeningVolumeProjectionV1.ModelVersion, SourceBranch="isolated-ui-validation",
    SourceCommitSha=args[1], BuildUtc=at, FutureToken="VALIDATION", FutureSymbol="NIFTYFUT",
    FutureExpiry=day.AddDays(4), OpeningWindowStartUtc=at.AddMinutes(-45), OpeningWindowEndUtc=at.AddMinutes(-30),
    EstimatorName=nameof(OpeningVolumeProjectionV1), CreatedAtUtc=at, BaseBarVolume=3250, OpeningVolume=30000,
    RollingWindowVolume=32500, OptionUniverseJson=JsonSerializer.Serialize(new[] {
        new ObserverOptionInstrument("PIN-CE","PIN-CE",OptionType.Call,23000,day.AddDays(4),65),
        new ObserverOptionInstrument("PIN-PE","PIN-PE",OptionType.Put,23000,day.AddDays(4),65) }) };
db.Sessions.Add(session); await db.SaveChangesAsync();
var runtime = new AdaptiveObserverRuntimeRow { SessionId=session.Id, LastHeartbeatUtc=at,
    RuntimeStatus=AdaptiveRuntimeStatus.Live, LastCompletedBarSeq=20, CurrentPartialBarVolume=1625,
    CurrentPartialBarStartedAtUtc=at };
db.Runtime.Add(runtime);
for (var seq=1;seq<=20;seq++) AddRows(seq);
await db.SaveChangesAsync();
const string url="http://127.0.0.1:5089";
const string password="isolated-ci-validation-password";
var start=new ProcessStartInfo("dotnet") {
    WorkingDirectory=root, RedirectStandardOutput=true, RedirectStandardError=true };
start.ArgumentList.Add(Path.Combine(root,"NiftySignal.Dashboard/bin/Release/net10.0/NiftySignal.Dashboard.dll"));
start.ArgumentList.Add("--urls");start.ArgumentList.Add(url);
start.Environment["ASPNETCORE_ENVIRONMENT"]="Development";
start.Environment["ASPNETCORE_URLS"]=url;
start.Environment["ConnectionStrings__NiftySignalDb"]=baseConnection;
start.Environment["DashboardAuth__Username"]="validation-user";
start.Environment["DashboardAuth__PasswordSha256"]=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
start.Environment["AdaptiveScreenshots__Enabled"]="true";
start.Environment["AdaptiveScreenshots__BaseUrl"]=url;
start.Environment["AdaptiveScreenshots__OutputDirectory"]=Path.Combine(evidence,"screenshots");
start.Environment["Telegram__BotToken"]="123456:ci-no-real-token";
start.Environment["Telegram__ChatId"]="ci-no-real-chat";
start.Environment["AdaptiveScreenshotValidationApiUrl"]="http://127.0.0.1:5091/";
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
    await using var browser=await playwright.Chromium.LaunchAsync(new() { Headless=true });
    await using var context=await browser.NewContextAsync(new() { ViewportSize=new() { Width=1600,Height=1100 } });
    await context.Tracing.StartAsync(new() { Screenshots=true,Snapshots=true,Sources=true });
    var page=await context.NewPageAsync();
    page.SetDefaultTimeout(15000);
    try {
    await page.GotoAsync(url+"/");
    await page.GetByLabel("Username",new() { Exact=true }).FillAsync("validation-user");
    await page.GetByLabel("Password",new() { Exact=true }).FillAsync(password);
    await page.GetByRole(AriaRole.Button,new() { Name="Sign in",Exact=true }).ClickAsync();
    await Expect(page.Locator(".adaptive-table")).ToHaveCountAsync(3);
    await Expect(page.Locator(".adaptive-dashboard-build")).ToHaveAttributeAsync("data-commit-sha",args[1]);
    await Expect(page.Locator(".adaptive-dashboard-build")).ToHaveAttributeAsync("data-source-branch",BuildIdentity.Current().SourceBranch);
    await Expect(page.Locator(".adaptive-dashboard-build")).ToContainTextAsync(args[1]);
    await Expect(page.Locator(".adaptive-current-bar")).ToContainTextAsync("50.0%");
    await Expect(page.GetByLabel("Completed rows",new() { Exact=true })).ToBeEnabledAsync();
    // Slice 1: compact grids are the default; only columns whose slice has landed are shown.
    await Expect(page.Locator(".adaptive-table[data-grid='futures-compact'] thead th")).ToHaveTextAsync(new[] { "Seq","End IST","Dur s","Urgency",
        "Bar ΔPx","Roll ΔPx","Strict Δ","Enriched Δ","Roll Strict","Roll Enriched","|Strict| Δ","MicroDev","OFI","Roll OI Δ","Roll Efficiency","Evolution","State" });
    await Expect(page.Locator(".adaptive-table[data-grid='options-compact'] thead th")).ToHaveTextAsync(new[] { "Seq","End IST","Center","Roll?",
        "CE ΔPx","PE ΔPx","CE Roll ΔPx","PE Roll ΔPx","CE Strict Δ","PE Strict Δ","CE Enriched Δ","PE Enriched Δ","CE Roll Strict","PE Roll Strict",
        "CE OI Δ","PE OI Δ","CE Position","PE Position","CE ΔIV","PE ΔIV","IV Skew","Vol PCR","Roll Vol PCR" });
    await Expect(page.Locator(".adaptive-table[data-grid='residual-compact'] thead th")).ToHaveTextAsync(new[] { "Seq","End IST","Future Δ09:30",
        "CE Res %","CE Res Δ%","PE Res %","PE Res Δ%","Directional Res %","Adjacent Directional Res Δ","Straddle Res %","Direction","Relation","Quote Age" });
    // Fixture bars: 3250 volume in 60 s => Urgency 54.2 (derived on read, not persisted).
    await Expect(page.Locator(".adaptive-table[data-grid='futures-compact'] tbody tr").First.Locator("td").Nth(3)).ToHaveTextAsync("54.2");
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
    // Sidecar metrics come from adaptive_futures_supplemental_bars (bar 20: MicroDev +0.125, OFI -420).
    await Expect(page.Locator(".adaptive-table[data-grid='futures-compact'] tbody tr").First.Locator("td").Nth(11)).ToHaveTextAsync("+0.125");
    await Expect(page.Locator(".adaptive-table[data-grid='futures-compact'] tbody tr").First.Locator("td").Nth(12)).ToHaveTextAsync("-420");
    // Deployment-order safety: a Dashboard that starts before the Host migrated has no sidecar table; the panel must still render, with MicroDev/OFI unavailable.
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE adaptive_futures_supplemental_bars RENAME TO adaptive_futures_supplemental_bars_hidden");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE adaptive_options_supplemental_bars RENAME TO adaptive_options_supplemental_bars_hidden");
    try
    {
        await page.ReloadAsync();
        await Expect(page.Locator(".adaptive-table[data-grid='futures-compact'] tbody tr").First.Locator("td").Nth(11)).ToHaveTextAsync("—");
        await Expect(page.Locator(".adaptive-table[data-grid='futures-compact'] tbody tr").First.Locator("td").Nth(12)).ToHaveTextAsync("—");
        await Expect(page.Locator(".adaptive-table")).ToHaveCountAsync(3);
        await Expect(page.Locator(".adaptive-table[data-grid='options-compact'] tbody tr").First.Locator("td").Nth(14)).ToHaveTextAsync("—");
        await Expect(page.Locator(".adaptive-alert-error")).ToHaveCountAsync(0);
    }
    finally
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE adaptive_futures_supplemental_bars_hidden RENAME TO adaptive_futures_supplemental_bars");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE adaptive_options_supplemental_bars_hidden RENAME TO adaptive_options_supplemental_bars");
    }
    await page.ReloadAsync();
    await Expect(page.Locator(".adaptive-table[data-grid='futures-compact'] tbody tr").First.Locator("td").Nth(11)).ToHaveTextAsync("+0.125");
    await Expect(page.GetByLabel("Completed rows",new() { Exact=true })).ToBeEnabledAsync();
    Console.WriteLine("SIDECAR BROWSER PASS: MicroDev/OFI rendered from the versioned sidecar; missing sidecar table degrades to unavailable without an error banner.");
    // Reproduce the lost-selector race with a real in-flight PostgreSQL read.
    await using(var blocker=new AdaptiveObserverDbContext(options))
    await using(var transaction=await blocker.Database.BeginTransactionAsync())
    {
        await blocker.Database.ExecuteSqlRawAsync("LOCK TABLE adaptive_future_bars IN ACCESS EXCLUSIVE MODE");
        await page.GetByLabel("Completed rows",new() { Exact=true }).SelectOptionAsync("15");
        var blocked=false;
        for(var attempt=0;attempt<30;attempt++)
        {
            var count=await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE '%adaptive_future_bars%'").SingleAsync();
            if(count>0) { blocked=true;break; }
            await Task.Delay(100);
        }
        if(!blocked)throw new Exception("Selector race fixture did not create an in-flight blocked database read.");
        await page.GetByLabel("Completed rows",new() { Exact=true }).SelectOptionAsync("5");
        await Expect(page.Locator(".adaptive-observer")).ToHaveAttributeAsync("data-requested-rows","5");
        await transaction.CommitAsync();
    }
    foreach(var table in await page.Locator(".adaptive-table").AllAsync())
        await Expect(table.Locator("tbody tr")).ToHaveCountAsync(5);
    await page.GetByLabel("Diagnostic view",new() { Exact=true }).CheckAsync();
    await Expect(page.Locator(".adaptive-table[data-grid]")).ToHaveCountAsync(0); // diagnostic tables replace the compact ones
    await Expect(page.Locator(".adaptive-table")).ToHaveCountAsync(3);
    await Expect(page.Locator(".adaptive-table").First.Locator("thead")).ToContainTextAsync("Roll OI Δ");
    await Expect(page.Locator(".adaptive-table").Nth(2).Locator("thead")).ToContainTextAsync("Legacy Bridged Directional Δ");
    foreach(var count in new[] { 5,10,15 }) {
        await page.GetByLabel("Completed rows",new() { Exact=true }).SelectOptionAsync(count.ToString());
        foreach(var side in new[] { "BOTH","CE","PE" }) {
            await page.GetByLabel("Option side",new() { Exact=true }).SelectOptionAsync(side);
            foreach(var measure in new[] { "NOTIONAL","CONTRACT" }) {
                await page.GetByLabel("Option measurement",new() { Exact=true }).SelectOptionAsync(measure);
                foreach(var variant in new[] { "BAND","ATM" }) {
                    await page.GetByLabel("Residual variant",new() { Exact=true }).SelectOptionAsync(variant);
                    foreach(var table in await page.Locator(".adaptive-table").AllAsync()) {
                        await Expect(table.Locator("tbody tr")).ToHaveCountAsync(count);
                        var actual=await table.Locator("tbody tr td:first-child").AllTextContentsAsync();
                        var expected=Enumerable.Range(21-count,count).Reverse().Select(x=>x.ToString()).ToArray();
                        if(!actual.SequenceEqual(expected))throw new Exception("Grid sequences are not synchronized.");
                    }
                    var optionFirst=page.Locator(".adaptive-table").Nth(1).Locator("tbody tr").First.Locator("td");
                    var ce=measure=="NOTIONAL" ? "+0.400" : "+0.200";
                    var pe=measure=="NOTIONAL" ? "-0.500" : "-0.300";
                    await Expect(optionFirst.Nth(7)).ToHaveTextAsync(side=="PE"?pe:ce);
                    if(side=="BOTH") {
                        await Expect(optionFirst.Nth(8)).ToHaveTextAsync(pe);
                        await Expect(optionFirst.Nth(9)).ToHaveTextAsync(measure=="NOTIONAL"?"+0.900":"+0.500");
                    }
                    await Expect(page.Locator(".adaptive-table").Nth(2).Locator("tbody tr").First.Locator("td").Nth(4))
                        .ToHaveTextAsync(variant=="ATM"?"+1.00":"+5.00");
                    await Expect(page.Locator(".adaptive-table").Nth(1).Locator("tbody tr:has(td:first-child:text-is('18'))"))
                        .ToContainTextAsync("Unavailable");
                    if(variant=="BAND")
                        await Expect(page.Locator(".adaptive-table").Nth(2).Locator("tbody tr:has(td:first-child:text-is('17'))"))
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
        AddRows(seq); runtime.LastCompletedBarSeq=seq;runtime.LastHeartbeatUtc=DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(); // Starts only after the database commit returns.
        var timer=Stopwatch.StartNew();
        await push.InvokeAsync("PushAdaptiveStateChanged",session.Id,seq);
        foreach(var table in await page.Locator(".adaptive-table").AllAsync())
            await Expect(table.Locator("tbody tr td:first-child").First).ToHaveTextAsync(seq.ToString(),new() { Timeout=5000 });
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
        if(seq>=24)samples.Add(timer.Elapsed.TotalMilliseconds); // Three warm-up commits.
    }
    await page.ReloadAsync();
    await Expect(page.Locator(".adaptive-table").First.Locator("tbody tr td:first-child").First).ToHaveTextAsync("43");
    await Expect(page.GetByLabel("Completed rows",new() { Exact=true })).ToHaveValueAsync("10");
    await Expect(page.Locator(".adaptive-table[data-grid]")).ToHaveCountAsync(3); // reload returns to the compact default
    await page.GetByLabel("Diagnostic view",new() { Exact=true }).CheckAsync();
    await Expect(page.GetByLabel("Option side",new() { Exact=true })).ToHaveValueAsync("BOTH");
    await Expect(page.GetByLabel("Option measurement",new() { Exact=true })).ToHaveValueAsync("NOTIONAL");
    await Expect(page.GetByLabel("Residual variant",new() { Exact=true })).ToHaveValueAsync("BAND");
    await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"restarted-reader.png"),FullPage=true });
    // Durable worker, its own cookie, real production Chromium capture and multipart upload.
    var expectedTargets = new[] { 13,18,23,28,33,38,43 };
    var screenshotJobs = new List<AdaptiveScreenshotJobRow>();
    for(var attempt=0;attempt<100;attempt++) {
        screenshotJobs=await db.ScreenshotJobs.AsNoTracking().Where(x=>x.SessionId==session.Id).OrderBy(x=>x.TargetBarSeq).ToListAsync();
        if(screenshotJobs.Count==expectedTargets.Length && screenshotJobs.All(x=>x.Status==AdaptiveScreenshotStatus.Sent))break;
        await Task.Delay(500);
    }
    if(!screenshotJobs.Select(x=>x.TargetBarSeq).SequenceEqual(expectedTargets) || screenshotJobs.Any(x=>x.Status!=AdaptiveScreenshotStatus.Sent))
        throw new Exception("Screenshot outbox did not deliver pinned initial/every-five-bar snapshots: " +
            JsonSerializer.Serialize(screenshotJobs.Select(x=>new { x.TargetBarSeq,x.Status,x.LastError,x.Attempts })));
    if(telegramStub.Uploads.Count!=7 || telegramStub.Uploads.Any(x=>x.Width<1920))throw new Exception("Screenshot upload count/PNG dimensions invalid.");
    // Readiness is based on the committed boundary, not future rows already in the fixture.
    runtime.LastCompletedBarSeq=9; await db.SaveChangesAsync();
    await page.GotoAsync(url+"/");
    await Expect(page.Locator("[data-readiness='warming']")).ToContainTextAsync("9/10");
    await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"warmup-nine-bars.png"),FullPage=true });
    runtime.LastCompletedBarSeq=43; await db.SaveChangesAsync();
    await page.ReloadAsync();
    await Expect(page.Locator("[data-readiness='ready']")).ToBeVisibleAsync();
    var lastFixtureBar=db.FutureBars.Local.Single(x=>x.SessionId==session.Id && x.BarSeq==43);
    lastFixtureBar.TradeUpdates=0; await db.SaveChangesAsync();
    await Expect(page.Locator("[data-readiness='warming']")).ToContainTextAsync("0/10");
    lastFixtureBar.TradeUpdates=1; await db.SaveChangesAsync();
    await Expect(page.Locator("[data-readiness='ready']")).ToBeVisibleAsync();
    Console.WriteLine("READINESS BROWSER PASS: nine bars provisional, ten-plus valid bars ready, invalid latest bar revokes readiness.");
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
    await Expect(page.Locator(".adaptive-table")).ToHaveCountAsync(3);
    await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"pinned-quote-capture-page.png"),FullPage=true });
    Console.WriteLine("PINNED LIVE QUOTE BROWSER PASS: capture page shows boundary-pinned spot/future/center CE+PE with strike labels; later ticks and live-only controls absent.");
    await page.GotoAsync(url+"/legacy");
    await Expect(page.Locator(".dashboard-shell")).ToBeVisibleAsync();

    var sorted=samples.Order().ToArray(); var p95=sorted[(int)Math.Ceiling(sorted.Length*.95)-1];
    var report=new { sourceSha=args[1], environment="isolated GitHub Actions PostgreSQL and Chromium; not VM measurement",
        selectorCombinations=36,blockedReaderSelectorRace=true,assemblySourceShaVerified=true,paintFramesAwaited=true, latencySamples=samples, meanMs=samples.Average(),p95Ms=p95,maxMs=samples.Max(),
        allSamplesBelowOneSecond=samples.All(x=>x<1000), readerRestart=true };
    await File.WriteAllTextAsync(Path.Combine(evidence,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }));
    Console.WriteLine($"Dashboard PASS: 36 selector combinations, reader restart, commit-to-three-grids p95={p95:F1}ms max={samples.Max():F1}ms.");
    if(samples.Any(x=>x>=1000))throw new Exception("Commit-to-visible dashboard latency exceeded one second.");
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
void AddRows(int seq) {
    var end=at.AddMinutes(seq-43);var begin=end.AddMinutes(-1);
    db.FutureBars.Add(new AdaptiveFutureBarRow { SessionId=session.Id,BarSeq=seq,StartAvailableAtUtc=begin,EndAvailableAtUtc=end,
        Open=23000,High=23001,Low=23000,Close=23001,Volume=3250,TradeUpdates=1,DurationSeconds=60 });
    db.OptionsSupplemental.Add(new AdaptiveOptionsSupplementalRow { SessionId=session.Id,BarSeq=seq,MetricsVersion=OptionsSupplementalBar.MetricsVersion,
        CenterStrike=23000,CeOiDelta=100,PeOiDelta=-100,CePosition="CallLongBuild",PePosition="PutShortCover",CeDeltaIv=1.5,PeDeltaIv=-.5,
        IvSkewEnd=.75,VolPcr=.9,RollVolPcr=1.2,DayCeVolume=1000,DayPeVolume=900,UniverseTokenCount=2,TokensObserved=2 });
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
