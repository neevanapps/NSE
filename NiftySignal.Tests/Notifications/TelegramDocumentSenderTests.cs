using System.Net;
using Microsoft.Extensions.Options;
using NiftySignal.Notifications;

namespace NiftySignal.Tests.Notifications;

public sealed class TelegramDocumentSenderTests
{
    [Theory]
    [InlineData("{\"ok\":true,\"result\":{\"message_id\":42}}", 200, TelegramDocumentOutcome.Sent)]
    [InlineData("{\"ok\":false,\"parameters\":{\"retry_after\":17}}", 429, TelegramDocumentOutcome.Rejected)]
    [InlineData("{\"ok\":true,\"result\":{}}", 200, TelegramDocumentOutcome.Uncertain)]
    [InlineData("bad response", 502, TelegramDocumentOutcome.Uncertain)]
    [InlineData("{\"ok\":false}", 401, TelegramDocumentOutcome.Rejected)]
    public async Task SendDocument_RequiresAcknowledgmentAndUsesMultipart(string json, int httpStatus, TelegramDocumentOutcome outcome)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        await File.WriteAllBytesAsync(path, [137,80,78,71]);
        try {
            var handler = new Handler(json, httpStatus);
            using var sender = new TelegramDocumentSender(new HttpClient(handler), Options.Create(new TelegramOptions { BotToken = "test-token", ChatId = "chat" }));
            var result = await sender.SendAsync(path, "Caption", default);
            Assert.Equal(outcome, result.Outcome);
            Assert.Contains("chat_id", handler.Body);
            Assert.Contains("image/png", handler.Body);
            Assert.Contains("Caption", handler.Body);
            Assert.EndsWith("/bottest-token/sendDocument", handler.Url);
            if (httpStatus == 429) Assert.Equal(17, result.RetryAfterSeconds);
            if (outcome == TelegramDocumentOutcome.Sent) Assert.Equal(42, result.MessageId);
        } finally { File.Delete(path); }
    }

    [Fact]
    public async Task SendDocument_TransportFailure_IsUncertainWithoutExposingToken()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png"); await File.WriteAllBytesAsync(path, [1]);
        try {
            using var sender = new TelegramDocumentSender(new HttpClient(new FailingHandler()), Options.Create(new TelegramOptions { BotToken = "secret-token", ChatId = "chat" }));
            var result = await sender.SendAsync(path, "Caption", default);
            Assert.Equal(TelegramDocumentOutcome.Uncertain, result.Outcome);
            Assert.DoesNotContain("secret-token", result.Error!);
        } finally { File.Delete(path); }
    }

    sealed class Handler(string json, int status) : HttpMessageHandler
    {
        public string Body = ""; public string Url = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken); Url = request.RequestUri!.ToString();
            return new((HttpStatusCode)status) { Content = new StringContent(json) };
        }
    }
    sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("URL secret-token");
    }
}
