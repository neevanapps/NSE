using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.Notifications;

namespace NiftySignal.Tests.Notifications;

public class TelegramNotifierTests
{
    static TelegramOptions TestOptions => new() { BotToken = "test-token-123", ChatId = "747400966" };

    [Fact]
    public async Task SendAsync_PostsToTheBotTokenSpecificEndpoint()
    {
        var handler = new RecordingHttpMessageHandler();
        using var http = new HttpClient(handler);
        var notifier = new TelegramNotifier(http, Options.Create(TestOptions), NullLogger<TelegramNotifier>.Instance);

        await notifier.SendAsync(NotificationCategory.TradeEntry, "entered NIFTY25SEP26C25000 @ 175", CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Contains("bottest-token-123/sendMessage", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SendAsync_IncludesTheChatIdAndMessageText_InTheRequestBody()
    {
        var handler = new RecordingHttpMessageHandler();
        using var http = new HttpClient(handler);
        var notifier = new TelegramNotifier(http, Options.Create(TestOptions), NullLogger<TelegramNotifier>.Instance);

        await notifier.SendAsync(NotificationCategory.TradeEntry, "entered NIFTY25SEP26C25000 @ 175", CancellationToken.None);

        Assert.Contains("747400966", handler.LastRequestBody);
        Assert.Contains("entered NIFTY25SEP26C25000", handler.LastRequestBody);
    }

    [Fact]
    public async Task SendAsync_DoesNotThrow_WhenTelegramRespondsWithAnErrorStatus()
    {
        var handler = new RecordingHttpMessageHandler(System.Net.HttpStatusCode.Unauthorized, """{"ok":false,"description":"Unauthorized"}""");
        using var http = new HttpClient(handler);
        var notifier = new TelegramNotifier(http, Options.Create(TestOptions), NullLogger<TelegramNotifier>.Instance);

        // Should not throw -- a failed notification must never interrupt the caller.
        await notifier.SendAsync(NotificationCategory.TradeEntry, "message", CancellationToken.None);
    }

    [Fact]
    public async Task SendAsync_DoesNotThrow_WhenTheHttpCallItselfThrows()
    {
        using var http = new HttpClient(new ThrowingHttpMessageHandler());
        var notifier = new TelegramNotifier(http, Options.Create(TestOptions), NullLogger<TelegramNotifier>.Instance);

        await notifier.SendAsync(NotificationCategory.ConnectionFailure, "network is down", CancellationToken.None);
    }
}
