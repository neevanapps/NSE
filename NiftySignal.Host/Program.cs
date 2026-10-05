using NiftySignal.AdaptiveObserverData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NiftySignal.Domain;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Configuration;
using NiftySignal.Host;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Notifications;
using NiftySignal.Persistence;
using NiftySignal.Rules;
using NiftySignal.Scoring;
using NiftySignal.VolumeBarData;
using Npgsql;
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
    var buildIdentity = BuildIdentity.Current();
    Log.Information("Build identity: {SourceBranch} @ {CommitSha}, build UTC {BuildUtc}",
        buildIdentity.SourceBranch, buildIdentity.CommitSha, buildIdentity.BuildUtc);

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

    // Phase A of docs/LIVE_PARITY_PLAN.md: the volume-bar database is a SEPARATE physical
    // database from NiftySignalDb (see VolumeBarPopulator.VolumeBarDatabaseName's own doc
    // comment) -- same server/credentials, different Database= override, same convention
    // NiftySignal.VolumeBarData's own CLI Program.cs already uses to reach it.
    builder.Services.AddDbContext<VolumeBarDbContext>(options =>
    {
        var baseConnectionString = builder.Configuration.GetConnectionString("NiftySignalDb")
            ?? throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set.");
        var volumeBarConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = VolumeBarPopulator.VolumeBarDatabaseName,
        }.ConnectionString;
        options.UseNpgsql(volumeBarConnectionString);
    });

    builder.Services.AddDbContext<AdaptiveObserverDbContext>(options =>
    {
        var baseConnectionString = builder.Configuration.GetConnectionString("NiftySignalDb")
            ?? throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set.");
        var adaptiveConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = AdaptiveObserverDbContext.DatabaseName,
        }.ConnectionString;
        options.UseNpgsql(adaptiveConnectionString);
    });

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

    // Sensex/Bank Nifty raw-tick-collection resolver (write-only/archive, future backtesting
    // only) -- registered as its own concrete types, not bound to IInstrumentMasterProvider
    // above, so Nifty's own resolution (InstrumentUniverseResolver -> IInstrumentMasterProvider
    // -> FlatTradeInstrumentMasterProvider) is provably untouched by this addition.
    builder.Services.AddHttpClient<SensexBankNiftyInstrumentMasterProvider>();
    builder.Services.AddScoped<SensexBankNiftyInstrumentUniverseResolver>();

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

    // Batch 5 (2026-09-13, live-wiring plan A9) -- the two new Core-score strategies' own
    // hot-reloaded, validate-before-swap risk config, same pattern as RulesetConfig/ScoreWeights
    // above. Both still read the EXISTING IValidatedOptions<RulesetConfig> registered above for
    // their shared Capital/Session/StrikeSelection/Costs/KillSwitch sections.
    builder.Services.Configure<CoreScoreHysteresisConfigOptions>(builder.Configuration.GetSection(CoreScoreHysteresisConfigOptions.SectionName));
    builder.Services.Configure<CoreScoreCrossoverConfigOptions>(builder.Configuration.GetSection(CoreScoreCrossoverConfigOptions.SectionName));
    builder.Services.AddSingleton<IValidatedOptions<CoreScoreHysteresisConfig>>(sp => new ValidatedOptionsMonitor<CoreScoreHysteresisConfigOptions, CoreScoreHysteresisConfig>(
        sp.GetRequiredService<IOptionsMonitor<CoreScoreHysteresisConfigOptions>>(),
        options => options.ToConfig(),
        CoreScoreHysteresisConfigValidator.Validate,
        sp.GetRequiredService<ILogger<ValidatedOptionsMonitor<CoreScoreHysteresisConfigOptions, CoreScoreHysteresisConfig>>>()));
    builder.Services.AddSingleton<IValidatedOptions<CoreScoreCrossoverConfig>>(sp => new ValidatedOptionsMonitor<CoreScoreCrossoverConfigOptions, CoreScoreCrossoverConfig>(
        sp.GetRequiredService<IOptionsMonitor<CoreScoreCrossoverConfigOptions>>(),
        options => options.ToConfig(),
        CoreScoreCrossoverConfigValidator.Validate,
        sp.GetRequiredService<ILogger<ValidatedOptionsMonitor<CoreScoreCrossoverConfigOptions, CoreScoreCrossoverConfig>>>()));

    // LiveTradingEngine stays registered (left in place, fully functional, just no longer called
    // from the cadence loop -- see MarketDataIngestionWorker's own A10 cutover comment) in case
    // it's ever wanted again. The two new engines are what the cadence loop actually calls now.
    builder.Services.AddSingleton<LiveTradingEngine>();
    builder.Services.AddSingleton<CoreScoreHysteresisTradingEngine>();
    builder.Services.AddSingleton<CoreScoreCrossoverTradingEngine>();

    builder.Services.Configure<DashboardPushOptions>(builder.Configuration.GetSection(DashboardPushOptions.SectionName));
    builder.Services.AddSingleton<DashboardPushClient>();

    builder.Services.AddHostedService<MarketDataIngestionWorker>();

    // Phase G of docs/LIVE_PARITY_PLAN.md (perf fix): Singleton, owned exclusively by
    // LiveVolumeBarWriter's own single-threaded poll loop -- see LiveOptionSeriesCache's own doc
    // comment for why that's safe without a lock.
    builder.Services.AddSingleton<LiveOptionSeriesCache>();

    // Live performance incident fix (docs/LIVE_PARITY_PLAN.md, dated entry): same Singleton,
    // single-writer-owned shape as LiveOptionSeriesCache above, keeping the future-side
    // VolumeBarBuilder warm across polls instead of re-replaying the whole day-so-far every 10s --
    // see LiveVolumeBarBuilderCache's own doc comment.
    builder.Services.AddSingleton<LiveVolumeBarBuilderCache>();

    // Phase A of docs/LIVE_PARITY_PLAN.md: writes VolumeBarRow/OptionAtmBarRow/OptionDepthBarRow/
    // OptionMaxPainBarRow rows live, by polling-and-replaying NiftySignalDbContext.Ticks -- see
    // LiveVolumeBarWriter's own doc comment for why this is a separate worker rather than hooked
    // into MarketDataIngestionWorker's own tick loop. Deliberately does NOT compute or persist any
    // score, nor place any paper trades -- that's Phase C/D, not yet wired.
    builder.Services.AddHostedService<LiveVolumeBarWriter>();

    // Phase C of docs/LIVE_PARITY_PLAN.md: computes the live OptionsScoreThreeWaySwitchMaxPainConfirmed
    // score for every bar LiveVolumeBarWriter writes and records entry SIGNALS under the same
    // percentile/Max-Pain/trading-hours rules the offline backtest uses -- see LiveOptionsScoreEngine's
    // own doc comment for why this is a separate polling worker rather than literally chained after
    // LiveVolumeBarWriter. Still no paper trading (no strike selection, no fills) -- that's Phase D.
    builder.Services.AddHostedService<LiveOptionsScoreEngine>();

    // Futures-crossover live-wiring task (2026-09-21, docs/LIVE_PARITY_PLAN.md "Futures crossover:
    // wired live" section): the locked 8-fast/40-slow/5-point-threshold SMA crossover on
    // SessionGatedDepthDurationConfirmed's own FuturesScore -- same polling-worker shape as
    // LiveOptionsScoreEngine above, entirely independent of it (see LiveFuturesCrossoverEngine's own
    // doc comment). Purely additive/local: registering this hosted service does not touch
    // deploy.ps1 or the VM in any way -- it only starts running the next time THIS Host process is
    // built and actually run.
    builder.Services.AddHostedService<LiveFuturesCrossoverEngine>();

    // Adaptive Market Observer V1: watch-only, independent of the legacy score/trading workers.
    // It reads authoritative persisted raw ticks, writes its own isolated database and never
    // creates a paper or real order.
    builder.Services.AddSingleton<AdaptiveSourceTickReader>();
    builder.Services.AddSingleton<AdaptiveSessionCoordinator>();
    builder.Services.AddSingleton<AdaptiveObserverPersistence>();
    builder.Services.AddSingleton<AdaptiveWeak2ObservationService>();
    builder.Services.AddSingleton<AdaptiveStateRecoveryService>();
    builder.Services.AddHostedService<AdaptiveObserverWorker>();

    var host = builder.Build();

    // Self-provisioning: a fresh machine gets its schema on first run.
    using (var scope = host.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.Database.Migrate();

        // Phase A: same self-provisioning as NiftySignalDb above, for the separate volume-bar
        // database this Host process now writes to for the first time.
        var volumeBarDb = scope.ServiceProvider.GetRequiredService<VolumeBarDbContext>();
        volumeBarDb.Database.Migrate();

        var adaptiveObserverDb = scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>();
        adaptiveObserverDb.Database.Migrate();

        // Force eager construction (and therefore eager startup validation -- see
        // ValidatedOptionsMonitor's own doc comment) rather than waiting for the first cadence
        // hours from now to discover a malformed Ruleset/ScoreWeights config section.
        scope.ServiceProvider.GetRequiredService<IValidatedOptions<RulesetConfig>>();
        scope.ServiceProvider.GetRequiredService<IValidatedOptions<ScoreWeights>>();
        scope.ServiceProvider.GetRequiredService<IValidatedOptions<CoreScoreHysteresisConfig>>();
        scope.ServiceProvider.GetRequiredService<IValidatedOptions<CoreScoreCrossoverConfig>>();
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

    // 2026-09-21 (docs/LIVE_PARITY_PLAN.md "Exception alerting"): a full host crash is the single
    // most important thing to be alerted about -- more than any per-poll error, which already gets
    // its own Telegram alert via LiveVolumeBarWriter/LiveOptionsScoreEngine. Best-effort only: this
    // is reached outside the DI container (it may never have finished building, or may have failed
    // to build at all), so it reads Telegram config directly from the same appsettings files rather
    // than resolving ITelegramNotifier, and is wrapped so nothing it does can throw a new exception
    // that would prevent Log.Fatal/CloseAndFlush above/below from running.
    await TryNotifyFatalCrashAsync(ex);
}
finally
{
    Log.CloseAndFlush();
}

// Best-effort, standalone (no DI) Telegram send for a full host crash -- deliberately does not
// reuse TelegramNotifier/ITelegramNotifier (both require a built DI container this code path can't
// assume exists) and swallows every possible failure (missing/empty config, network error, bad
// response) so a broken notification path can never mask or block the real crash log above.
static async Task TryNotifyFatalCrashAsync(Exception ex)
{
    try
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .Build();

        var botToken = config["Telegram:BotToken"];
        var chatId = config["Telegram:ChatId"];
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            chat_id = chatId,
            text = $"NiftySignal Host CRASHED and is terminating: {ex.GetType().Name}: {ex.Message}",
        });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await http.PostAsync(url, content, cts.Token);
    }
    catch
    {
        // Best-effort only -- see this method's own doc comment. Never let a failed crash
        // notification prevent Log.Fatal/CloseAndFlush from completing normally.
    }
}
