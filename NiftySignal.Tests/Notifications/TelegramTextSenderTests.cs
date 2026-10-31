using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NiftySignal.Notifications;

namespace NiftySignal.Tests.Notifications;

/// <summary>Acknowledged Telegram text delivery (<see cref="TelegramTextSender"/>). Moved verbatim from the removed commentary delivery tests: the sender itself is kept (reusable for paper-trade alerts).</summary>
public sealed class TelegramTextSenderTests
{
    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request; Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request);
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static TelegramTextSender Sender(StubHandler handler) => new(new HttpClient(handler), Options.Create(new TelegramOptions { BotToken = "123456:secret-token", ChatId = "42" }));

    [Fact]
    public async Task TextSender_ReportsSentOnlyWithAnAcknowledgedMessageId_AndUsesTheColonTokenPath()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"message_id\":777}}")));
        var result = await Sender(handler).SendAsync("hello", default);
        Assert.Equal(TelegramDocumentOutcome.Sent, result.Outcome); Assert.Equal(777, result.MessageId);
        Assert.Equal("/bot123456:secret-token/sendMessage", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("api.telegram.org", handler.Request.RequestUri.Host);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("42", body.RootElement.GetProperty("chat_id").GetString()); Assert.Equal("hello", body.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task TextSender_ClassifiesRefusalAsRejected_AndAnythingUnacknowledgedAsUncertain_WithoutLeakingTheToken()
    {
        var refused = await Sender(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.TooManyRequests, "{\"ok\":false,\"parameters\":{\"retry_after\":17}}")))).SendAsync("x", default);
        Assert.Equal(TelegramDocumentOutcome.Rejected, refused.Outcome); Assert.Equal(17, refused.RetryAfterSeconds);

        var ok200NoAck = await Sender(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"ok\":true,\"result\":{}}")))).SendAsync("x", default);
        Assert.Equal(TelegramDocumentOutcome.Uncertain, ok200NoAck.Outcome); Assert.Null(ok200NoAck.MessageId);

        var garbage = await Sender(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "not json")))).SendAsync("x", default);
        Assert.Equal(TelegramDocumentOutcome.Uncertain, garbage.Outcome);

        var broken = await Sender(new StubHandler(_ => throw new HttpRequestException("POST https://api.telegram.org/bot123456:secret-token/sendMessage failed"))).SendAsync("x", default);
        Assert.Equal(TelegramDocumentOutcome.Uncertain, broken.Outcome);
        Assert.DoesNotContain("secret-token", broken.Error);

        var missing = await new TelegramTextSender(new HttpClient(new StubHandler(_ => throw new InvalidOperationException())), Options.Create(new TelegramOptions { BotToken = "", ChatId = "" })).SendAsync("x", default);
        Assert.Equal(TelegramDocumentOutcome.Rejected, missing.Outcome);
    }
}
