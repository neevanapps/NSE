using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.Dashboard.Components;
using NiftySignal.Dashboard.Hubs;
using NiftySignal.Dashboard.Services;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Persistence;

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

builder.Services.Configure<FlatTradeOptions>(builder.Configuration.GetSection(FlatTradeOptions.SectionName));
builder.Services.AddHttpClient<FlatTradeAuthClient>();

builder.Services.AddSingleton<LiveDataService>();

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
    });
builder.Services.AddAuthorization();
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
