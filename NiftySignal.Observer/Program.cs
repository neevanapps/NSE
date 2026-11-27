using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain;
using NiftySignal.Domain.Configuration;
using NiftySignal.Host;
using NiftySignal.Persistence;
using Serilog;

// A Windows Service starts in C:\Windows\System32: relative paths (logs) are resolved against the exe's folder.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console().CreateBootstrapLogger();

try
{
    // the post-deploy verification looks for this line: it proves which source revision the running process was built from
    var build = BuildIdentity.Current();
    Log.Information("Build identity: {SourceBranch} @ {CommitSha}, build UTC {BuildUtc}", build.SourceBranch, build.CommitSha, build.BuildUtc);

    var builder = Host.CreateApplicationBuilder(args);
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
    if (AdaptiveObserverRegistration.ProcessMode(builder.Configuration) != AdaptiveObserverProcess.Separate)
        throw new InvalidOperationException($"{AdaptiveObserverRegistration.ProcessSetting} is not Separate: the Host runs the Observer in this configuration.");
    builder.Services.AddWindowsService(o => o.ServiceName = "NiftySignalObserver");
    builder.Services.AddSerilog((services, configuration) => configuration.ReadFrom.Configuration(builder.Configuration).ReadFrom.Services(services).Enrich.FromLogContext());
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection(PricingOptions.SectionName));
    // The Host's tick database: read only here. Its schema is the Host's to migrate.
    builder.Services.AddDbContext<NiftySignalDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("NiftySignalDb")));
    builder.Services.AddAdaptiveObserverDatabase(builder.Configuration);
    builder.Services.AddAdaptiveObserver(builder.Configuration);

    var host = builder.Build();
    using (var scope = host.Services.CreateScope())
        scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>().Database.Migrate();
    await host.RunAsync();
}
catch (OperationCanceledException)
{
    // a service stop that arrives while the host is still shutting down
}
catch (Exception ex)
{
    // A non-zero exit lets the service recovery actions restart the process; it continues from its newest checkpoint.
    Log.Fatal(ex, "NiftySignalObserver terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
