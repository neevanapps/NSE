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
const string baseConnection = "Host=localhost;Database=niftysignal;Username=postgres;Password=postgres";
var options = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(
    "Host=localhost;Database=niftysignal_adaptive_observer;Username=postgres;Password=postgres").Options;
await using var db = new AdaptiveObserverDbContext(options);
await db.Database.MigrateAsync();
await using (var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(baseConnection).Options))
    await source.Database.EnsureCreatedAsync();
await RelationalRestartParity.CheckAsync(options,baseConnection,args[1],evidence);
var at = DateTimeOffset.UtcNow;
var day = DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
var session = new AdaptiveSessionStateRow {
    TradeDate=day, ModelVersion=OpeningVolumeProjectionV1.ModelVersion, SourceBranch="isolated-ui-validation",
    SourceCommitSha=args[1], BuildUtc=at, FutureToken="VALIDATION", FutureSymbol="NIFTYFUT",
    FutureExpiry=day.AddDays(4), OpeningWindowStartUtc=at.AddMinutes(-15), OpeningWindowEndUtc=at,
    EstimatorName=nameof(OpeningVolumeProjectionV1), CreatedAtUtc=at, BaseBarVolume=3250,
    RollingWindowVolume=32500, OptionUniverseJson="[]" };
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
using var server=Process.Start(start) ?? throw new Exception("Dashboard did not start.");
var output=server.StandardOutput.ReadToEndAsync(); var errors=server.StandardError.ReadToEndAsync();
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
    await Expect(page.Locator(".adaptive-current-bar")).ToContainTextAsync("50.0%");
    await Expect(page.GetByLabel("Completed rows",new() { Exact=true })).ToBeEnabledAsync();
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
    await Expect(page.GetByLabel("Option side",new() { Exact=true })).ToHaveValueAsync("BOTH");
    await Expect(page.GetByLabel("Option measurement",new() { Exact=true })).ToHaveValueAsync("NOTIONAL");
    await Expect(page.GetByLabel("Residual variant",new() { Exact=true })).ToHaveValueAsync("BAND");
    await page.ScreenshotAsync(new() { Path=Path.Combine(evidence,"restarted-reader.png"),FullPage=true });
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
    await File.WriteAllTextAsync(Path.Combine(evidence,"dashboard.log"),(await output)+(await errors));
}
void AddRows(int seq) {
    var end=at.AddMinutes(seq-43);var begin=end.AddMinutes(-1);
    db.FutureBars.Add(new AdaptiveFutureBarRow { SessionId=session.Id,BarSeq=seq,StartAvailableAtUtc=begin,EndAvailableAtUtc=end,
        Open=23000,Close=23001,Volume=3250,DurationSeconds=60 });
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
