using NiftySignal.AdaptiveObserverData;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.Dashboard.Components;
using NiftySignal.Dashboard.Hubs;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Configuration;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;
using Npgsql;
using NiftySignal.Notifications;

Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", Path.Combine(AppContext.BaseDirectory, ".playwright-browsers"));
if (args.Contains("--install-screenshot-browser"))
{
    // ExecutablePath is version-specific: retain a matching install, install again on upgrade.
    using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
    if (File.Exists(playwright.Chromium.ExecutablePath))
    {
        Console.WriteLine("Screenshot Chromium already installed for this Playwright version.");
        return;
    }
    Environment.ExitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
    return;
}

// Windows Services start with their working directory at C:\Windows\System32, not the
// exe's own folder -- any future relative path (log files, etc.) would silently land there
// without this. Cheap insurance; see NiftySignal.Host/Program.cs for where this actually
// bit us (Serilog's file sink).
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddWindowsService(options => options.ServiceName = "NiftySignalDashboard");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Host -> Dashboard live push (see Hubs/MarketDataHub.cs). Browsers never connect to this
// hub directly -- only Host does, as a SignalR client.
builder.Services.AddSignalR();

// Factory, not AddDbContext: Blazor Server runs sibling components' OnInitializedAsync
// concurrently within one circuit, and they'd otherwise share one scoped DbContext
// instance -- two components touching it at once throws (DbContext isn't thread-safe).
// Each component creates its own short-lived context per operation instead.
builder.Services.AddDbContextFactory<NiftySignalDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("NiftySignalDb")));

// Read-only view of the live PAPER trading ledger (the "trading" schema). Registered only when its connection string exists; the connection itself is opened
// read-only, so this process cannot change the ledger even by mistake. There is no write path and no broker/order code behind this registration.
{
    var paperLiveConnection = builder.Configuration.GetConnectionString("TradingPaperLive");
    if (!string.IsNullOrWhiteSpace(paperLiveConnection))
    {
        var readOnly = new NpgsqlConnectionStringBuilder(paperLiveConnection) { Options = "-c default_transaction_read_only=on" }.ConnectionString;
        builder.Services.AddDbContextFactory<NiftySignal.Trading.Data.TradingDbContext>(options =>
            options.UseNpgsql(readOnly, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", NiftySignal.Trading.Data.TradingDbContext.SchemaName)));
    }
}

// Read-only view of the NPT1 M1 producer's own schema (npt1m1) on the same paper-live database: stream health only; absent or unreadable it simply shows nothing.
{
    var paperLiveConnection = builder.Configuration.GetConnectionString("TradingPaperLive");
    if (!string.IsNullOrWhiteSpace(paperLiveConnection))
    {
        var readOnly = new NpgsqlConnectionStringBuilder(paperLiveConnection) { Options = "-c default_transaction_read_only=on" }.ConnectionString;
        builder.Services.AddDbContextFactory<NiftySignal.Npt1M1.Data.Npt1M1DbContext>(options =>
            options.UseNpgsql(readOnly, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", NiftySignal.Npt1M1.Data.Npt1M1DbContext.Schema)));
    }
}

builder.Services.AddSingleton<Npt1LiveQuotes>();
builder.Services.AddSingleton(sp => new PaperLiveDataService(sp.GetService<IDbContextFactory<NiftySignal.Trading.Data.TradingDbContext>>(), null, sp.GetService<IDbContextFactory<NiftySignal.Npt1M1.Data.Npt1M1DbContext>>()));

// Phase F of docs/LIVE_PARITY_PLAN.md: same separate-database override NiftySignal.Host's own
// VolumeBarDbContext registration already uses (see NiftySignal.Host/Program.cs) -- same
// server/credentials as NiftySignalDb, different Database= override
// (VolumeBarPopulator.VolumeBarDatabaseName). Read-only from this Dashboard's side: the
// /live-options-score page never writes to it. Factory, not AddDbContext, for the same
// concurrent-sibling-component reason NiftySignalDbContext above already documents.
builder.Services.AddDbContextFactory<VolumeBarDbContext>(options =>
{
    var baseConnectionString = builder.Configuration.GetConnectionString("NiftySignalDb")
        ?? throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set.");
    var volumeBarConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
    {
        Database = VolumeBarPopulator.VolumeBarDatabaseName,
    }.ConnectionString;
    options.UseNpgsql(volumeBarConnectionString);
});

builder.Services.AddDbContextFactory<AdaptiveObserverDbContext>(options =>
{
    var baseConnectionString = builder.Configuration.GetConnectionString("NiftySignalDb")
        ?? throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set.");
    var adaptiveConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
    {
        Database = AdaptiveObserverDbContext.DatabaseName,
    }.ConnectionString;
    options.UseNpgsql(adaptiveConnectionString);
});

builder.Services.Configure<FlatTradeOptions>(builder.Configuration.GetSection(FlatTradeOptions.SectionName));
builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection(PricingOptions.SectionName));
builder.Services.AddHttpClient<FlatTradeAuthClient>();

builder.Services.AddSingleton<LiveDataService>();
builder.Services.AddSingleton<AdaptiveObserverDataService>();
builder.Services.AddSingleton<AdaptiveCommentaryDataService>();
builder.Services.AddSingleton<AdaptiveQuoteAsOfService>();
builder.Services.Configure<AdaptiveScreenshotOptions>(builder.Configuration.GetSection(AdaptiveScreenshotOptions.SectionName));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.AddSingleton<IAdaptiveScreenshotRenderer, AdaptiveScreenshotRenderer>();
builder.Services.AddSingleton<ITelegramDocumentSender>(sp =>
{
    // No HTTP logging handler: request URLs contain the bot token. Optional loopback emulator is Development-only.
    Uri? emulator = null;
    var testUrl = builder.Configuration["AdaptiveScreenshotValidationApiUrl"];
    if (builder.Environment.IsDevelopment() && !string.IsNullOrEmpty(testUrl))
    {
        emulator = new Uri(testUrl);
        if (!emulator.IsLoopback || emulator.Scheme != "http") throw new InvalidOperationException("Validation API must be loopback HTTP.");
    }
    return new TelegramDocumentSender(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) },
        sp.GetRequiredService<IOptions<TelegramOptions>>(), emulator);
});
builder.Services.AddSingleton<AdaptiveScreenshotProcessor>();
// Commentary Telegram outbox (08-Oct plan section 61). Explicit opt-in: AdaptiveCommentaryTelegram:Enabled (default false) AND Dashboard Telegram settings.
builder.Services.Configure<AdaptiveCommentaryTelegramOptions>(builder.Configuration.GetSection(AdaptiveCommentaryTelegramOptions.SectionName));
builder.Services.AddSingleton<ITelegramTextSender>(sp =>
{
    // Same rules as the document sender: no HTTP logging handler (URLs contain the bot token); loopback emulator is Development-only.
    Uri? emulator = null;
    var testUrl = builder.Configuration["AdaptiveScreenshotValidationApiUrl"];
    if (builder.Environment.IsDevelopment() && !string.IsNullOrEmpty(testUrl))
    {
        emulator = new Uri(testUrl);
        if (!emulator.IsLoopback || emulator.Scheme != "http") throw new InvalidOperationException("Validation API must be loopback HTTP.");
    }
    return new TelegramTextSender(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) },
        sp.GetRequiredService<IOptions<TelegramOptions>>(), emulator);
});
builder.Services.AddSingleton<AdaptiveCommentaryNotificationProcessor>();
builder.Services.AddHostedService<AdaptiveCommentaryNotificationWorker>();
builder.Services.AddHostedService<AdaptiveScreenshotWorker>();

// Single-user cookie auth (2026-09-05) -- see DashboardAuthOptions for why this is deliberately
// minimal. Sliding expiration so a phone left on the dashboard doesn't get logged out mid-session.
builder.Services.Configure<DashboardAuthOptions>(builder.Configuration.GetSection(DashboardAuthOptions.SectionName));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/auth/logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    })
    .AddCookie(AdaptiveScreenshotRenderer.AuthenticationScheme, options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = ".NiftySignal.AdaptiveCapture";
        options.Cookie.Path = "/adaptive-screenshot";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(2);
        options.SlidingExpiration = false;
    });
builder.Services.AddAuthorization(options => options.AddPolicy("AdaptiveScreenshotRead", policy =>
    policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, AdaptiveScreenshotRenderer.AuthenticationScheme)
        .RequireAuthenticatedUser()));
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Deliberately NOT behind RequireAuthorization: Host connects to this hub as an
// unauthenticated SignalR client to push ticks, so gating it would silently kill live
// ingestion into the dashboard. The hub is only reachable on the VM's own network anyway --
// no public firewall rule exposes this port.
app.MapHub<MarketDataHub>("/hubs/market-data");

// Static form POST rather than an interactive component: a Blazor circuit can't write an auth
// cookie (response headers are already flushed by the time component code runs).
app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery, IOptions<DashboardAuthOptions> auth) =>
{
    // Validated explicitly -- the antiforgery middleware only auto-validates endpoints that
    // opt in via [FromForm] binding metadata, which reading the form directly doesn't do.
    await antiforgery.ValidateRequestAsync(context);

    var form = await context.Request.ReadFormAsync();
    if (!auth.Value.Matches(form["username"], form["password"]))
    {
        return Results.Redirect("/login?failed=true");
    }

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, auth.Value.Username)],
        CookieAuthenticationDefaults.AuthenticationScheme);

    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Redirect("/");
});

app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

try
{
    app.Run();
}
catch (OperationCanceledException)
{
    // WindowsServiceLifetime.StopAsync throws this when the Windows Service stop request
    // arrives while the host is still mid-shutdown -- benign (the service does stop cleanly
    // immediately after), but otherwise escapes as an unhandled exception and gets logged as
    // an Application Error / WER crash in Event Viewer on every single stop.
}
