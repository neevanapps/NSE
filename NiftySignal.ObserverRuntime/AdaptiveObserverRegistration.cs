using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserverData;
using Npgsql;

namespace NiftySignal.Host;

/// <summary>Which process runs the Adaptive Observer.</summary>
public enum AdaptiveObserverProcess
{
    /// <summary>The NiftySignalObserver service (the default).</summary>
    Separate,
    /// <summary>Inside the Host, as before the process split (needed by the in-Host paper bridges).</summary>
    InHost,
}

public static class AdaptiveObserverRegistration
{
    public const string ProcessSetting = "Adaptive:Observer:Process";

    public static AdaptiveObserverProcess ProcessMode(IConfiguration configuration) => configuration[ProcessSetting] switch
    {
        null or "" => AdaptiveObserverProcess.Separate,
        var text when Enum.TryParse<AdaptiveObserverProcess>(text, ignoreCase: true, out var mode) && Enum.IsDefined(mode) => mode,
        var text => throw new InvalidOperationException($"{ProcessSetting} must be Separate or InHost, not '{text}'."),
    };

    /// <summary>The observer database on the same server and credentials as ConnectionStrings:NiftySignalDb.</summary>
    public static string ObserverConnectionString(IConfiguration configuration) => new NpgsqlConnectionStringBuilder(
        configuration.GetConnectionString("NiftySignalDb") is { Length: > 0 } source ? source : throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set."))
    { Database = AdaptiveObserverDbContext.DatabaseName }.ConnectionString;

    /// <summary>Everything the live Observer needs, except the source database context and the pricing options, which the process registers.</summary>
    public static IServiceCollection AddAdaptiveObserver(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DashboardPushOptions>(configuration.GetSection(DashboardPushOptions.SectionName));
        services.TryAddSingleton<DashboardPushClient>();
        services.AddSingleton<IObserverOwnership>(sp => new PostgresObserverOwnership(ObserverConnectionString(configuration), sp.GetRequiredService<ILogger<PostgresObserverOwnership>>()));
        services.AddSingleton<AdaptiveSourceTickReader>();
        // Futures Top-5 microstructure sidecar (S4). Adaptive:Top5Microstructure:Enabled defaults to FALSE; disabled, the gateway is inert (no depth read, no state, no sidecar row).
        services.Configure<Top5MicrostructureOptions>(configuration.GetSection(Top5MicrostructureOptions.SectionName));
        services.AddSingleton<AdaptiveDepthTickReader>();
        services.AddOptions<AdaptiveRecoveryOptions>().Bind(configuration.GetSection(AdaptiveRecoveryOptions.SectionName))
            .Validate(o => o.IsValid(), $"{AdaptiveRecoveryOptions.SectionName}: a value is outside its allowed range.").ValidateOnStart();
        services.AddSingleton<FuturesDepth5Gateway>();
        services.AddSingleton<AdaptiveHistoricalBootstrapService>();
        // New adaptive sessions use the fixed 13,650-contract clock (config Adaptive:Clock:Mode = OpeningVolumeV1 restores the legacy estimator for NEW sessions). Existing sessions keep their frozen model.
        services.AddSingleton(sp => new AdaptiveSessionCoordinator(
            sp.GetRequiredService<AdaptiveSourceTickReader>(),
            sp.GetRequiredService<IOptionsMonitor<NiftySignal.Domain.Configuration.PricingOptions>>(),
            sp.GetRequiredService<ILogger<AdaptiveSessionCoordinator>>(),
            Enum.TryParse<NiftySignal.AdaptiveObserver.AdaptiveClockMode>(configuration["Adaptive:Clock:Mode"], true, out var clockMode)
                ? clockMode : NiftySignal.AdaptiveObserver.AdaptiveClockMode.Fixed13650,
            sp.GetRequiredService<IOptions<AdaptiveRecoveryOptions>>()));
        services.AddSingleton<AdaptiveObserverPersistence>();
        services.AddSingleton<AdaptiveSupplementalPersistence>();
        services.AddSingleton<AdaptiveSessionSupplementalService>();
        services.AddSingleton<AdaptiveWeak2ObservationService>();
        services.AddSingleton<AdaptiveCheckpointStore>();
        services.AddSingleton<AdaptiveStateRecoveryService>();
        services.AddSingleton<AdaptiveEndedSessionRecoveryService>();
        services.AddHostedService<AdaptiveObserverWorker>();
        return services;
    }

    public static IServiceCollection AddAdaptiveObserverDatabase(this IServiceCollection services, IConfiguration configuration) =>
        services.AddDbContext<AdaptiveObserverDbContext>(options => options.UseNpgsql(ObserverConnectionString(configuration)));
}
