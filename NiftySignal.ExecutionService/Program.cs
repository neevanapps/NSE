using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NiftySignal.Domain;
using NiftySignal.ExecutionService;
using NiftySignal.Notifications;
using Serilog;

// A Windows Service starts in C:\Windows\System32: relative paths (logs, strategy files) are resolved against the exe's folder.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console().CreateBootstrapLogger();

try
{
    // the post-deploy verification looks for this line: it proves which source revision the running process was built from
    var build = BuildIdentity.Current();
    Log.Information("Build identity: {SourceBranch} @ {CommitSha}, build UTC {BuildUtc}", build.SourceBranch, build.CommitSha, build.BuildUtc);

    var builder = Host.CreateApplicationBuilder(args);
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
    builder.Services.AddWindowsService(o => o.ServiceName = "NiftySignalExecution");
    builder.Services.AddSerilog((services, configuration) => configuration.ReadFrom.Configuration(builder.Configuration).ReadFrom.Services(services).Enrich.FromLogContext());
    builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
    builder.Services.AddHttpClient<ITelegramTextSender, TelegramTextSender>(c => c.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(ExecutionServiceOptions.Bind(builder.Configuration));
    builder.Services.AddHostedService<ExecutionWorker>();
    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "NiftySignalExecution terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
