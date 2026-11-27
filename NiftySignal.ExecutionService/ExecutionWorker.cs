using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Notifications;
using NiftySignal.PaperEngineV2.Alerts;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Runtime;
using NiftySignal.Persistence;
using NiftySignal.Trading.Core.Costs;

namespace NiftySignal.ExecutionService;

/// <summary>The strategies and cost model the service runs, loaded and checked before anything is started.</summary>
public sealed record ExecutionSetup(IReadOnlyList<CompiledStrategy> Strategies, IReadOnlyList<StrategyConfig> Disabled, ExecutionOptions Execution, SessionLotSizes Lots)
{
    /// <summary>Throws on any invalid option or rejected strategy file: the service never runs a partial or guessed configuration.</summary>
    public static ExecutionSetup Load(ExecutionServiceOptions options, string baseDirectory)
    {
        var errors = options.Validate();
        if (errors.Count > 0) throw new InvalidOperationException("Execution configuration is invalid: " + string.Join("; ", errors));
        var directory = Path.GetFullPath(options.StrategyDirectory!, baseDirectory);
        if (!Directory.Exists(directory)) throw new InvalidOperationException($"strategy directory '{directory}' does not exist");
        var documents = Directory.EnumerateFiles(directory, "*.json").OrderBy(x => x, StringComparer.Ordinal).Select(f => (f, File.ReadAllText(f))).ToList();
        var set = StrategySet.Load(documents);
        if (!set.SetAccepted) throw new InvalidOperationException("strategy set rejected: " + string.Join("; ", set.SetErrors.Select(e => e.Message)));
        if (set.Rejected.Count > 0)
            throw new InvalidOperationException("strategy files rejected: " + string.Join(" | ", set.Rejected.Select(r => $"{r.Item1}: {string.Join("; ", r.Item2.Select(e => e.Message))}")));
        if (set.Active.Count == 0) throw new InvalidOperationException("no enabled strategy");

        var brokerage = new FlatBrokerage { Unit = BrokerageUnit.PerOrder, Amount = options.BrokeragePerOrder!.Value };
        var schedule = options.CostScheduleVersion == "npt1-frozen-fees-v1" ? NseIndexOptionsCostSchedules.Npt1FrozenV1(brokerage) : NseIndexOptionsCostSchedules.From20260401(brokerage);
        var lots = new SessionLotSizes();
        var execution = new ExecutionOptions(options.UnresolvedAfter, [schedule], lots, options.Times);
        var failures = set.Active.Select(a => StrategyRuntime.CheckActivation(a.Config, execution)).OfType<ActivationFailure>().ToList();
        if (failures.Count > 0) throw new InvalidOperationException("strategies cannot be activated: " + string.Join("; ", failures.Select(f => $"{f.StrategyId} {f.Detail}")));
        return new ExecutionSetup(set.Active, set.DisabledValid, execution, lots);
    }
}

public sealed class ExecutionWorker(
    ExecutionServiceOptions options, IConfiguration configuration, ITelegramTextSender telegram, TimeProvider clock, ILogger<ExecutionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            log.LogWarning("NiftySignalExecution is disabled (Execution:Enabled=false): no connection is opened and nothing is executed.");
            return;
        }

        var setup = ExecutionSetup.Load(options, AppContext.BaseDirectory);
        foreach (var c in setup.Strategies.Select(s => s.Config).Concat(setup.Disabled))
            log.LogInformation("Strategy {Strategy} v{Version} {State} hash {Hash}: timing {Timing}, entries from {From}, warm-up {Bars} bars, {Lots} lot(s), capital {Capital}, daily loss limit {Loss}, max daily trades {Trades}",
                c.Identity.StrategyId, c.Identity.Version, c.Enabled ? "ENABLED" : "disabled", c.Identity.Hash, c.Timing, c.Filters.EntryFrom, c.Filters.MinCompletedBars, c.Lots, c.VirtualCapital, c.Risk.MaxDailyLoss, c.Risk.MaxDailyTrades);

        var trading = Connection("NiftySignalDb");
        var ledger = Connection("TradingPaperLive");
        var observer = new NpgsqlConnectionStringBuilder(trading) { Database = AdaptiveObserverDbContext.DatabaseName }.ConnectionString;
        var observerOptions = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(observer).Options;
        var tradingOptions = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(trading).Options;
        await using var db = NpgsqlDataSource.Create(ledger);

        var alerts = new ServiceAlerts(telegram, clock, options.AlertReminder, log);
        var feed = new DbExecutionFeed(() => new AdaptiveObserverDbContext(observerOptions), () => new NiftySignalDbContext(tradingOptions), options);
        await using var coordinator = new ExecutionCoordinator(options, db, feed, setup.Strategies, setup.Execution, setup.Lots, alerts, clock, log, setup.Disabled);
        // sending runs on its own tasks: a slow Telegram call never delays protection
        var sender = alerts.RunSenderAsync(stoppingToken);
        var outbox = DispatchOutboxAsync(db, coordinator, stoppingToken);

        var failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = options.PollInterval;
                try
                {
                    await coordinator.RunCycleAsync(stoppingToken);
                    failures = 0;
                    if (!coordinator.IsOwner) delay = TimeSpan.FromSeconds(5);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    failures++;
                    if (failures <= 3 || failures % 60 == 0) log.LogError(ex, "Execution cycle failed ({Failures} in a row).", failures);
                    alerts.Report("cycle", $"execution cycle is failing ({ex.GetType().Name}): entries and exits are NOT being processed until it recovers");
                    delay = TimeSpan.FromMilliseconds(Math.Min(5000, 250 * failures));
                }

                await Task.Delay(delay, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }

        await Task.WhenAll(sender, outbox);
    }

    string Connection(string name)
    {
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"ConnectionStrings:{name} is not set.");
        // every call is bounded: a hung database must not hang the protection loop
        return new NpgsqlConnectionStringBuilder(value) { Timeout = 5, CommandTimeout = 10 }.ConnectionString;
    }

    async Task DispatchOutboxAsync(NpgsqlDataSource db, ExecutionCoordinator coordinator, CancellationToken ct)
    {
        var a = options.Alerts;
        if (!(a.Opened || a.Closed || a.OrderRejected || a.PartialFill || a.ExecutionFailure)) return;
        var dispatcher = new AlertDispatcher(db, new TelegramAlertTransport(telegram), a, clock);
        var resolved = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (coordinator.IsOwner)
                    {
                        if (!resolved) { await dispatcher.ResolveInterruptedAsync(ct); resolved = true; }
                        DispatchReport report;
                        do { report = await dispatcher.DispatchAsync(AlertDispatcher.MaxBatch, ct); } while (report.Claimed >= AlertDispatcher.MaxBatch);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { log.LogWarning(ex, "Alert dispatch failed; alerts stay in the durable outbox and are retried."); }

                await Task.Delay(TimeSpan.FromSeconds(2), clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
