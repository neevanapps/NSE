using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Notifications;

namespace NiftySignal.Dashboard.Services;

public sealed class AdaptiveScreenshotWorker(IDbContextFactory<AdaptiveObserverDbContext> factory,
    AdaptiveScreenshotProcessor processor, IOptions<AdaptiveScreenshotOptions> options,
    IOptions<TelegramOptions> telegram, IHostApplicationLifetime lifetime, ILogger<AdaptiveScreenshotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) { logger.LogInformation("Adaptive Telegram screenshots disabled."); return; }
        try { options.Value.LocalUri(); }
        catch (InvalidOperationException) { logger.LogError("Screenshot BaseUrl must be a loopback HTTP(S) origin; worker disabled."); return; }
        if (string.IsNullOrWhiteSpace(telegram.Value.BotToken) || string.IsNullOrWhiteSpace(telegram.Value.ChatId))
        { logger.LogError("Adaptive screenshots require Dashboard Telegram configuration; no images will be sent."); return; }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                await db.Database.OpenConnectionAsync(stoppingToken);
                // Connection-scoped PostgreSQL lease: two Dashboards cannot deliver the same outbox.
                var held = await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_lock(73063005) AS \"Value\"").SingleAsync(stoppingToken);
                if (held)
                {
                    try
                    {
                        await AdaptiveScreenshotSchedule.RecoverInterruptedAsync(db, stoppingToken);
                        var now = DateTimeOffset.UtcNow;
                        await AdaptiveScreenshotSchedule.EnqueueAsync(db, DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(5.5)).DateTime), now, stoppingToken);
                        await processor.ProcessOneAsync(db, now, stoppingToken);
                    }
                    finally { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(73063005)", CancellationToken.None); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Adaptive screenshot cycle failed ({ErrorType}); market calculations are unaffected.", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
