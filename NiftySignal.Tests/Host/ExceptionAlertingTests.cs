using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.Host;
using NiftySignal.Notifications;
using NiftySignal.Tests.Notifications;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.Host;

/// <summary>
/// 2026-09-21 (docs/LIVE_PARITY_PLAN.md "Exception alerting"): proves the new live pipeline's own
/// per-poll resilience catch blocks (audit findings F34/F49's "log and continue to next poll"
/// pattern, unchanged here) now ALSO send a <see cref="NotificationCategory.LivePipelineError"/>
/// Telegram alert, using the same <see cref="SpyTelegramNotifier"/> fake this test project's own
/// existing notification tests already use. Both <see cref="LiveVolumeBarWriter"/> and
/// <see cref="LiveOptionsScoreEngine"/> had their inline poll-loop try/catch extracted into a public
/// <c>PollOnceAsync</c> method purely so it's directly callable here without depending on real
/// wall-clock market-hours gating -- see each class's own doc comment on that method.
/// </summary>
public class ExceptionAlertingTests
{
    /// <summary>An <see cref="IServiceScopeFactory"/> that throws as soon as a scope is requested -- the simplest way to force an exception inside <see cref="LiveVolumeBarWriter.WritePendingBarsAsync"/>/<see cref="LiveOptionsScoreEngine.ProcessPendingBarsAsync"/> without needing a real (failing) database.</summary>
    sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("simulated poll failure");
    }

    [Fact]
    public async Task LiveVolumeBarWriter_PollOnceAsync_ExceptionInPoll_SendsTelegramAlert()
    {
        var spy = new SpyTelegramNotifier();
        var writer = new LiveVolumeBarWriter(
            new ThrowingScopeFactory(),
            new LiveOptionSeriesCache(),
            new LiveVolumeBarBuilderCache(),
            spy,
            NullLogger<LiveVolumeBarWriter>.Instance);

        var asOfDate = new DateOnly(2026, 9, 21);

        // Must not throw -- this is the "log and continue to next poll" resilience (F34/F49) that
        // must never change.
        await writer.PollOnceAsync(asOfDate, DateTimeOffset.UtcNow, finalizeDay: false, CancellationToken.None);

        var sent = Assert.Single(spy.SentMessages);
        Assert.Equal(NotificationCategory.LivePipelineError, sent.Category);
        Assert.Contains("LiveVolumeBarWriter", sent.Message, StringComparison.Ordinal);
        Assert.Contains("2026-09-21", sent.Message, StringComparison.Ordinal);
        Assert.Contains("simulated poll failure", sent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveOptionsScoreEngine_PollOnceAsync_ExceptionInPoll_SendsTelegramAlert()
    {
        var spy = new SpyTelegramNotifier();
        var engine = new LiveOptionsScoreEngine(
            new ThrowingScopeFactory(),
            spy,
            NullLogger<LiveOptionsScoreEngine>.Instance);

        var asOfDate = new DateOnly(2026, 9, 21);

        // Must not throw -- same F34/F49 resilience LiveVolumeBarWriter's own poll loop relies on.
        await engine.PollOnceAsync(asOfDate, CancellationToken.None);

        var sent = Assert.Single(spy.SentMessages);
        Assert.Equal(NotificationCategory.LivePipelineError, sent.Category);
        Assert.Contains("LiveOptionsScoreEngine", sent.Message, StringComparison.Ordinal);
        Assert.Contains("2026-09-21", sent.Message, StringComparison.Ordinal);
        Assert.Contains("simulated poll failure", sent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimitedTelegramNotifier_LivePipelineError_SecondSendWithinCooldownIsSuppressed()
    {
        // Proves the cooldown chosen for the new category actually prevents a repeating-every-10s
        // poll failure from spamming Telegram into rate-limiting itself, per the task's own
        // requirement -- same mechanism/reasoning as ConnectionFailure's existing 5-minute cooldown.
        var spy = new SpyTelegramNotifier();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var rateLimited = new RateLimitedTelegramNotifier(spy, time);

        await rateLimited.SendAsync(NotificationCategory.LivePipelineError, "first failure", CancellationToken.None);
        await rateLimited.SendAsync(NotificationCategory.LivePipelineError, "second failure (should be suppressed)", CancellationToken.None);

        Assert.Single(spy.SentMessages);

        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await rateLimited.SendAsync(NotificationCategory.LivePipelineError, "third failure (cooldown elapsed)", CancellationToken.None);

        Assert.Equal(2, spy.SentMessages.Count);
    }

    sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
