using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.Domain.Configuration;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Tests.Trading.Data;
using Microsoft.EntityFrameworkCore;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>The Observer runs in its own process (NiftySignalObserver): what that process contains, who may run it, and that only one writes.</summary>
[Collection("adaptive-postgres")]
public sealed class ObserverProcessIsolationTests
{
    static string Source(params string[] path) => File.ReadAllText(Path.Combine([TradingModelTests.RepoRoot(), .. path])).Replace("\r\n", "\n");

    static HashSet<string> ProjectClosure(string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string name)
        {
            if (!seen.Add(name)) return;
            foreach (Match m in Regex.Matches(Source(name, name + ".csproj"), @"<ProjectReference Include=""[^""]*?([A-Za-z0-9.]+)\.csproj""")) Visit(m.Groups[1].Value);
        }

        Visit(project);
        return seen;
    }

    [Fact]
    public void TheObserverProcess_ReferencesNoBrokerIngestionHostOrExecutionProject()
    {
        var closure = ProjectClosure("NiftySignal.Observer");
        Assert.Contains("NiftySignal.ObserverRuntime", closure);
        Assert.Contains("NiftySignal.Persistence", closure);
        Assert.DoesNotContain(closure, p => p is "NiftySignal.Host" or "NiftySignal.Ingestion" or "NiftySignal.Execution" or "NiftySignal.ExecutionService"
            || p.StartsWith("NiftySignal.PaperEngineV2", StringComparison.Ordinal) || p.StartsWith("NiftySignal.Trading", StringComparison.Ordinal));
        foreach (var project in closure)
            foreach (var file in Directory.EnumerateFiles(Path.Combine(TradingModelTests.RepoRoot(), project), "*.cs", SearchOption.TopDirectoryOnly))
                Assert.DoesNotContain("FlatTradeAuthClient", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.DoesNotContain("FlatTrade", Source("NiftySignal.Observer", "appsettings.json"), StringComparison.Ordinal);
        Assert.Contains("ServiceName = \"NiftySignalObserver\"", Source("NiftySignal.Observer", "Program.cs"));
    }

    static IConfiguration Configuration(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public async Task TheObserverProcessComposition_BuildsAndRunsExactlyOneWorker_WithNoInProcessConsumer()
    {
        var configuration = Configuration(("ConnectionStrings:NiftySignalDb", "Host=127.0.0.1;Port=1;Database=unused;Username=unused"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<PricingOptions>(configuration.GetSection(PricingOptions.SectionName));
        services.AddDbContext<NiftySignalDbContext>(o => o.UseNpgsql(configuration.GetConnectionString("NiftySignalDb")));
        services.AddAdaptiveObserverDatabase(configuration);
        services.AddAdaptiveObserver(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.IsType<AdaptiveObserverWorker>(Assert.Single(provider.GetServices<IHostedService>()));
        Assert.IsType<PostgresObserverOwnership>(provider.GetRequiredService<IObserverOwnership>());
        Assert.Empty(provider.GetServices<IAdaptiveLiveSink>());
    }

    [Fact]
    public void TheProcessSetting_DefaultsToSeparate_AndRefusesAnythingUnknown()
    {
        Assert.Equal(AdaptiveObserverProcess.Separate, AdaptiveObserverRegistration.ProcessMode(Configuration()));
        Assert.Equal(AdaptiveObserverProcess.InHost, AdaptiveObserverRegistration.ProcessMode(Configuration((AdaptiveObserverRegistration.ProcessSetting, "inhost"))));
        Assert.Throws<InvalidOperationException>(() => AdaptiveObserverRegistration.ProcessMode(Configuration((AdaptiveObserverRegistration.ProcessSetting, "Both"))));
        Assert.Throws<InvalidOperationException>(() => AdaptiveObserverRegistration.ProcessMode(Configuration((AdaptiveObserverRegistration.ProcessSetting, "7"))));
        Assert.Contains("\"Observer\": { \"Process\": \"Separate\" }", Source("NiftySignal.Host", "appsettings.json"));
    }

    [Fact]
    public void TheHost_RunsTheObserverOnlyWhenConfiguredInHost_AndStillIngestsWithoutIt()
    {
        var program = Source("NiftySignal.Host", "Program.cs");
        Assert.Matches(@"if \(observerInHost\)\n\s+\{\n\s+builder\.Services\.AddAdaptiveObserverDatabase\(builder\.Configuration\);\n\s+builder\.Services\.AddAdaptiveObserver\(builder\.Configuration\);", program);
        Assert.Matches(@"if \(observerInHost\) scope\.ServiceProvider\.GetRequiredService<AdaptiveObserverDbContext>\(\)\.Database\.Migrate\(\);", program);
        Assert.DoesNotContain("AddHostedService<AdaptiveObserverWorker>", program);
        Assert.Contains("builder.Services.AddHostedService<MarketDataIngestionWorker>();", program);
        Assert.Contains("need {AdaptiveObserverRegistration.ProcessSetting}=InHost", program.Replace("they need", "need"));
    }

    sealed class NeverOwner : IObserverOwnership
    {
        public int Asked;
        public Task<bool> EnsureAsync(CancellationToken ct) { Interlocked.Increment(ref Asked); return Task.FromResult(false); }
    }

    sealed class NoScopes : IServiceScopeFactory
    {
        public int Created;
        public IServiceScope CreateScope() { Interlocked.Increment(ref Created); throw new InvalidOperationException("no work is allowed without the ownership"); }
    }

    [Fact]
    public async Task WithoutTheOwnership_TheWorkerTouchesNoDatabase()
    {
        var owner = new NeverOwner();
        var scopes = new NoScopes();
        var worker = new AdaptiveObserverWorker(scopes, null!, null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<AdaptiveObserverWorker>.Instance, ownership: owner);
        await worker.StartAsync(default);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref owner.Asked) == 0 && DateTime.UtcNow < until) await Task.Delay(10);
        await worker.StopAsync(default);
        Assert.True(owner.Asked > 0);
        Assert.Equal(0, scopes.Created);
    }

    sealed class SteppedClock : TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [PgMigrationFact]
    public async Task OnlyOneProcessOwnsTheObserver_AndTheOwnershipEndsWithItsConnection()
    {
        await using var database = await TempAdaptiveDatabase.CreateAsync("niftysignal_adaptive_obs_test_");
        var clock = new SteppedClock();
        PostgresObserverOwnership Owner() => new(database.ConnectionString, NullLogger<PostgresObserverOwnership>.Instance, clock);
        await using var first = Owner();
        await using var second = Owner();
        Assert.True(await first.EnsureAsync(default));
        Assert.True(await first.EnsureAsync(default));
        Assert.False(await second.EnsureAsync(default));

        await first.DisposeAsync();
        Assert.True(await second.EnsureAsync(default));

        // The owner's server session ends (a database restart, a dropped connection): it must notice, and another process may take over.
        await database.ExecAsync($"SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype = 'advisory' AND pid <> pg_backend_pid() AND database = (SELECT oid FROM pg_database WHERE datname = '{database.Name}')");
        await using var third = Owner();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!await third.EnsureAsync(default) && DateTime.UtcNow < until) await Task.Delay(50);
        clock.Ticks += TimeSpan.FromSeconds(3).Ticks;                       // past the check interval
        Assert.False(await second.EnsureAsync(default));
        Assert.True(await third.EnsureAsync(default));
    }
}
