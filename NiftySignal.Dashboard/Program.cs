using Microsoft.EntityFrameworkCore;
using NiftySignal.Dashboard.Components;
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

// Factory, not AddDbContext: Blazor Server runs sibling components' OnInitializedAsync
// concurrently within one circuit, and they'd otherwise share one scoped DbContext
// instance -- two components touching it at once throws (DbContext isn't thread-safe).
// Each component creates its own short-lived context per operation instead.
builder.Services.AddDbContextFactory<NiftySignalDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("NiftySignalDb")));

builder.Services.Configure<FlatTradeOptions>(builder.Configuration.GetSection(FlatTradeOptions.SectionName));
builder.Services.AddHttpClient<FlatTradeAuthClient>();

builder.Services.AddSingleton<LiveDataService>();

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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
