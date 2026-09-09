using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Configuration;
using NiftySignal.Host;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Notifications;
using NiftySignal.Persistence;
using NiftySignal.Rules;
using NiftySignal.Scoring;
using Serilog;

// Windows Services start with their working directory at C:\Windows\System32, not the
// exe's own folder -- without this, the relative "logs/niftysignal-.log" path in
// appsettings.json (and appsettings.Local.json's relative connection-string-adjacent
// paths, if any are ever added) silently lands in System32 instead. Live-caught 2026-09-04
// after installing this as a real Windows Service for the first time.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting NiftySignal Host");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    builder.Services.AddWindowsService(options => options.ServiceName = "NiftySignal");

    builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.Configure<KillSwitchOptions>(
        builder.Configuration.GetSection(KillSwitchOptions.SectionName));
    builder.Services.Configure<PricingOptions>(
        builder.Configuration.GetSection(PricingOptions.SectionName));

    builder.Services.AddDbContext<NiftySignalDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("NiftySignalDb")));

    builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
    builder.Services.AddHttpClient<TelegramNotifier>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ITelegramNotifier>(sp =>
        new RateLimitedTelegramNotifier(sp.GetRequiredService<TelegramNotifier>(), sp.GetRequiredService<TimeProvider>()));

    builder.Services.Configure<FlatTradeOptions>(builder.Configuration.GetSection(FlatTradeOptions.SectionName));
    builder.Services.AddHttpClient<FlatTradeAuthClient>();
    builder.Services.AddHttpClient<FlatTradeInstrumentMasterProvider>();
    builder.Services.AddScoped<IInstrumentMasterProvider>(sp => sp.GetRequiredService<FlatTradeInstrumentMasterProvider>());
    builder.Services.AddScoped<IDataGapRecorder, EfDataGapRecorder>();
    builder.Services.AddScoped<InstrumentUniverseResolver>();

    // Hot-reloaded, validate-before-swap config (2026-09-09, external review -- see
    // ValidatedOptionsMonitor's own doc comment for why plain IOptionsMonitor +
    // IValidateOptions doesn't actually give "keep the last-known-good value on a bad reload").
    builder.Services.Configure<RulesetConfigOptions>(builder.Configuration.GetSection(RulesetConfigOptions.SectionName));
    builder.Services.Configure<ScoreWeightsOptions>(builder.Configuration.GetSection(ScoreWeightsOptions.SectionName));
    builder.Services.AddSingleton<IValidatedOptions<RulesetConfig>>(sp => new ValidatedOptionsMonitor<RulesetConfigOptions, RulesetConfig>(
        sp.GetRequiredService<IOptionsMonitor<RulesetConfigOptions>>(),
        options => options.ToRulesetConfig(),
        RulesetConfigValidator.Validate,
        sp.GetRequiredService<ILogger<ValidatedOptionsMonitor<RulesetConfigOptions, RulesetConfig>>>()));
    builder.Services.AddSingleton<IValidatedOptions<ScoreWeights>>(sp => new ValidatedOptionsMonitor<ScoreWeightsOptions, ScoreWeights>(
        sp.GetRequiredService<IOptionsMonitor<ScoreWeightsOptions>>(),
        options => options.ToScoreWeights(),
        ScoreWeightsValidator.Validate,
        sp.GetRequiredService<ILogger<ValidatedOptionsMonitor<ScoreWeightsOptions, ScoreWeights>>>()));

    builder.Services.AddSingleton<LiveTradingEngine>();

    builder.Services.Configure<DashboardPushOptions>(builder.Configuration.GetSection(DashboardPushOptions.SectionName));
    builder.Services.AddSingleton<DashboardPushClient>();

    builder.Services.AddHostedService<MarketDataIngestionWorker>();

    var host = builder.Build();

    // Self-provisioning: a fresh machine gets its schema on first run.
    using (var scope = host.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.Database.Migrate();

        // Force eager construction (and therefore eager startup validation -- see
        // ValidatedOptionsMonitor's own doc comment) rather than waiting for the first cadence
        // hours from now to discover a malformed Ruleset/ScoreWeights config section.
        scope.ServiceProvider.GetRequiredService<IValidatedOptions<RulesetConfig>>();
        scope.ServiceProvider.GetRequiredService<IValidatedOptions<ScoreWeights>>();
    }

    host.Run();
}
catch (OperationCanceledException)
{
    // WindowsServiceLifetime.StopAsync throws this when the Windows Service stop request
    // arrives while the host is still mid-shutdown -- benign (the service does stop cleanly
    // immediately after). Without this, it falls into the catch below and gets logged as a
    // fatal/unexpected termination on every single stop. See NiftySignal.Dashboard/Program.cs
    // for the same fix, needed there because it has no top-level catch at all.
}
catch (Exception ex)
{
    Log.Fatal(ex, "NiftySignal Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
