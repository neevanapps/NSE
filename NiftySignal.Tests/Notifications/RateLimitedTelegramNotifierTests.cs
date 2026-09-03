using NiftySignal.Notifications;

namespace NiftySignal.Tests.Notifications;

public class RateLimitedTelegramNotifierTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SendAsync_ForwardsTheFirstMessage_InAnyCategory()
    {
        var spy = new SpyTelegramNotifier();
        var limiter = new RateLimitedTelegramNotifier(spy, new ManualTimeProvider(Start));

        await limiter.SendAsync(NotificationCategory.ConnectionFailure, "first", CancellationToken.None);

        Assert.Single(spy.SentMessages);
    }

    [Fact]
    public async Task SendAsync_Suppresses_ASecondMessageInTheSameCategory_WithinTheCooldown()
    {
        var spy = new SpyTelegramNotifier();
        var time = new ManualTimeProvider(Start);
        var limiter = new RateLimitedTelegramNotifier(spy, time);

        await limiter.SendAsync(NotificationCategory.ConnectionFailure, "first", CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1)); // cooldown is 5 minutes
        await limiter.SendAsync(NotificationCategory.ConnectionFailure, "second", CancellationToken.None);

        Assert.Single(spy.SentMessages);
        Assert.Equal("first", spy.SentMessages[0].Message);
    }

    [Fact]
    public async Task SendAsync_Forwards_OnceTheCooldownHasElapsed()
    {
        var spy = new SpyTelegramNotifier();
        var time = new ManualTimeProvider(Start);
        var limiter = new RateLimitedTelegramNotifier(spy, time);

        await limiter.SendAsync(NotificationCategory.ConnectionFailure, "first", CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));
        await limiter.SendAsync(NotificationCategory.ConnectionFailure, "second", CancellationToken.None);

        Assert.Equal(2, spy.SentMessages.Count);
    }

    [Fact]
    public async Task SendAsync_NeverSuppresses_ACategoryWithNoConfiguredCooldown()
    {
        // TradeEntry etc. are already bounded by MaxTradesPerDay / the re-entry gap --
        // a fast sequence of them is real information, not spam.
        var spy = new SpyTelegramNotifier();
        var time = new ManualTimeProvider(Start);
        var limiter = new RateLimitedTelegramNotifier(spy, time);

        for (var i = 0; i < 5; i++)
        {
            await limiter.SendAsync(NotificationCategory.TradeEntry, $"trade {i}", CancellationToken.None);
        }

        Assert.Equal(5, spy.SentMessages.Count);
    }

    [Fact]
    public async Task SendAsync_TracksCooldownsIndependently_PerCategory()
    {
        var spy = new SpyTelegramNotifier();
        var time = new ManualTimeProvider(Start);
        var limiter = new RateLimitedTelegramNotifier(spy, time);

        await limiter.SendAsync(NotificationCategory.ConnectionFailure, "conn", CancellationToken.None);
        await limiter.SendAsync(NotificationCategory.KillSwitchToggle, "kill", CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(31)); // past KillSwitchToggle's 30s cooldown, not ConnectionFailure's 5min
        await limiter.SendAsync(NotificationCategory.ConnectionFailure, "conn2", CancellationToken.None); // still suppressed
        await limiter.SendAsync(NotificationCategory.KillSwitchToggle, "kill2", CancellationToken.None); // allowed

        Assert.Equal(3, spy.SentMessages.Count);
        Assert.DoesNotContain(spy.SentMessages, m => m.Message == "conn2");
        Assert.Contains(spy.SentMessages, m => m.Message == "kill2");
    }
}
