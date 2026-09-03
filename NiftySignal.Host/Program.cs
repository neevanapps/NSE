using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Configuration;
using NiftySignal.Host;
using NiftySignal.Persistence;
using Serilog;

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

    builder.Services.AddDbContext<NiftySignalDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("NiftySignalDb")));

    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();

    // Self-provisioning: a fresh machine gets its schema on first run.
    using (var scope = host.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.Database.Migrate();
    }

    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "NiftySignal Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
