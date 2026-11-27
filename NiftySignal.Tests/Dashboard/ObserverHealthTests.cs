using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Notifications;

namespace NiftySignal.Tests.Dashboard;

public sealed class ObserverHealthTests
{
    static readonly DateTimeOffset Noon = new(2026, 11, 26, 12, 0, 0, TimeSpan.FromHours(5.5));   // a Thursday
    static readonly ObserverHealthOptions Options = new();

    static AdaptiveObserverRuntimeRow Row(AdaptiveRuntimeStatus status, int heartbeatAge = 1, int? recoveryAge = null) => new()
    {
        RuntimeStatus = status, LastHeartbeatUtc = Noon.ToUniversalTime().AddSeconds(-heartbeatAge),
        LastRecoveryStartedUtc = recoveryAge is { } r ? Noon.ToUniversalTime().AddSeconds(-r) : null, LastError = "boom", RecoveryFailureCount = 1,
    };

    [Theory]
    [InlineData(AdaptiveRuntimeStatus.Live, 30, null, ObserverCondition.Healthy)]
    [InlineData(AdaptiveRuntimeStatus.Live, 31, null, ObserverCondition.StaleHeartbeat)]
    [InlineData(AdaptiveRuntimeStatus.Rebuilding, 1, 300, ObserverCondition.Pending)]
    [InlineData(AdaptiveRuntimeStatus.Rebuilding, 1, 301, ObserverCondition.RecoveryTooLong)]
    [InlineData(AdaptiveRuntimeStatus.Rebuilding, 31, 40, ObserverCondition.StaleHeartbeat)]
    [InlineData(AdaptiveRuntimeStatus.RecoveryFailed, 1, null, ObserverCondition.RecoveryFailed)]
    [InlineData(AdaptiveRuntimeStatus.Degraded, 1, null, ObserverCondition.Degraded)]
    [InlineData(AdaptiveRuntimeStatus.Closed, 1, null, ObserverCondition.ClosedEarly)]
    public void ClassifiesTheRuntimeRow(AdaptiveRuntimeStatus status, int age, int? recoveryAge, ObserverCondition expected) =>
        Assert.Equal(expected, ObserverHealth.Evaluate(Row(status, age, recoveryAge), null, Noon, Options).Condition);

    [Theory]
    [InlineData("2026-11-26T09:15:59+05:30", ObserverCondition.OutsideHours)]
    [InlineData("2026-11-26T09:19:00+05:30", ObserverCondition.Pending)]
    [InlineData("2026-11-26T09:20:00+05:30", ObserverCondition.NoSession)]
    [InlineData("2026-11-26T15:30:00+05:30", ObserverCondition.OutsideHours)]
    [InlineData("2026-11-28T12:00:00+05:30", ObserverCondition.OutsideHours)]
    public void MissingSessionDependsOnTheClock(string at, ObserverCondition expected) =>
        Assert.Equal(expected, ObserverHealth.Evaluate(null, null, DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), Options).Condition);

    [Fact]
    public void AlertsOncePerCondition_RemindsLater_AndReportsRecoveryOnce()
    {
        var tracker = new ObserverAlertTracker(Options);
        string? Step(ObserverCondition condition, int second)
        {
            var state = new ObserverHealthState(condition, condition.ToString(), Noon.AddSeconds(second));
            var message = tracker.Due(state);
            if (message is not null) tracker.Delivered(state);
            return message;
        }

        Assert.Null(Step(ObserverCondition.Healthy, 0));                       // nothing to recover from
        Assert.Contains("CRITICAL", Step(ObserverCondition.StaleHeartbeat, 5));
        Assert.Null(Step(ObserverCondition.StaleHeartbeat, 10));
        Assert.Contains("CRITICAL", Step(ObserverCondition.RecoveryFailed, 15));
        Assert.Null(Step(ObserverCondition.Pending, 20));                       // retrying is not recovery
        Assert.Null(Step(ObserverCondition.RecoveryFailed, 25));                // repeated failure does not repeat the message
        Assert.Contains("CRITICAL", Step(ObserverCondition.RecoveryFailed, 15 + 900));
        Assert.Contains("RECOVERED", Step(ObserverCondition.Healthy, 1000));
        Assert.Null(Step(ObserverCondition.Healthy, 1005));
        Assert.Contains("CRITICAL", Step(ObserverCondition.StaleHeartbeat, 1010));   // a new episode alerts again
        Assert.Null(Step(ObserverCondition.OutsideHours, 1015));
        Assert.Null(Step(ObserverCondition.Healthy, 1020));                     // the market close ended the episode
    }

    [Fact]
    public async Task Monitor_RetriesARejectedSend_AndDoesNotResendADeliveredOne()
    {
        var factory = new Factory(Guid.NewGuid().ToString());
        var sender = new Sender();
        var clock = new Clock(Noon);
        var monitor = new ObserverHealthMonitor(new AdaptiveObserverDataService(factory, NullLogger<AdaptiveObserverDataService>.Instance), sender,
            Microsoft.Extensions.Options.Options.Create(new ObserverHealthOptions()), clock, NullLogger<ObserverHealthMonitor>.Instance);
        var changes = 0;
        monitor.Changed += () => changes++;

        sender.Outcome = TelegramDocumentOutcome.Rejected;
        await monitor.CheckAsync(default);                 // no session for today at noon
        Assert.Equal(ObserverCondition.NoSession, monitor.Current.Condition);
        sender.Outcome = TelegramDocumentOutcome.Sent;
        await monitor.CheckAsync(default);
        await monitor.CheckAsync(default);

        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, text => Assert.Contains("no Observer session", text));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Monitor_ReportsAnUnreadableDatabaseOnlyAfterItPersists()
    {
        var sender = new Sender();
        var clock = new Clock(Noon);
        var monitor = new ObserverHealthMonitor(new AdaptiveObserverDataService(new Factory(null), NullLogger<AdaptiveObserverDataService>.Instance), sender,
            Microsoft.Extensions.Options.Options.Create(new ObserverHealthOptions()), clock, NullLogger<ObserverHealthMonitor>.Instance);

        await monitor.CheckAsync(default);
        Assert.Empty(sender.Sent);
        clock.Now = Noon.AddSeconds(31);
        await monitor.CheckAsync(default);

        Assert.Equal(ObserverCondition.Unreadable, monitor.Current.Condition);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Banner_IsShownOnlyWhileTheObserverIsCritical()
    {
        var clock = new Clock(Noon);
        var monitor = new ObserverHealthMonitor(new AdaptiveObserverDataService(new Factory(Guid.NewGuid().ToString()), NullLogger<AdaptiveObserverDataService>.Instance), new Sender(),
            Microsoft.Extensions.Options.Options.Create(new ObserverHealthOptions()), clock, NullLogger<ObserverHealthMonitor>.Instance);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, monitor);
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(provider, NullLoggerFactory.Instance);
        Task<string> Render() => renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<NiftySignal.Dashboard.Components.Dashboard.ObserverHealthBanner>()).ToHtmlString());

        Assert.DoesNotContain("observer-alert", await Render());
        await monitor.CheckAsync(default);
        var html = await Render();
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("NoSession", html);
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class Sender : ITelegramTextSender
    {
        public TelegramDocumentOutcome Outcome = TelegramDocumentOutcome.Sent;
        public List<string> Sent = [];
        public Task<TelegramDocumentResult> SendAsync(string text, CancellationToken ct)
        {
            Sent.Add(text);
            return Task.FromResult(new TelegramDocumentResult(Outcome));
        }
    }

    /// <summary>An empty in-memory observer database, or (null name) one that cannot be reached.</summary>
    sealed class Factory(string? name) : IDbContextFactory<AdaptiveObserverDbContext>
    {
        public AdaptiveObserverDbContext CreateDbContext() => name is null
            ? throw new InvalidOperationException("unreachable")
            : new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(name).Options);
    }
}
