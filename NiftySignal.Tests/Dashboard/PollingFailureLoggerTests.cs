using Microsoft.Extensions.Logging;
using NiftySignal.Dashboard.Services;

namespace NiftySignal.Tests.Dashboard;

public sealed class PollingFailureLoggerTests
{
    sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class Capture : ILogger<PollingFailureLogger>
    {
        public List<(LogLevel Level, string Message, Exception? Ex)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    static (PollingFailureLogger L, Capture C, Clock T) Make() { var c = new Capture(); var t = new Clock(new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero)); return (new PollingFailureLogger(c, t, TimeSpan.FromSeconds(60)), c, t); }

    [Fact]
    public void IdenticalFailure_IsLoggedOncePerCooldown_WithContext_AndSuppressedCount()
    {
        var (l, c, t) = Make();
        var ex = new InvalidOperationException("boom");
        Assert.True(l.Report("OptionAverageQuoteRead", ex, "session=7, bar=42, strike=25100"));
        for (var i = 0; i < 5; i++) { t.Now += TimeSpan.FromSeconds(5); Assert.False(l.Report("OptionAverageQuoteRead", new InvalidOperationException("boom"), "session=7, bar=42, strike=25100")); }
        Assert.Single(c.Entries);
        Assert.Contains("session=7, bar=42, strike=25100", c.Entries[0].Message);
        Assert.Same(ex, c.Entries[0].Ex);

        t.Now += TimeSpan.FromSeconds(61);                                           // cooldown elapsed: logged again, reporting the 5 repeats it absorbed
        Assert.True(l.Report("OptionAverageQuoteRead", ex, "session=7, bar=43, strike=25100"));
        Assert.Equal(2, c.Entries.Count);
        Assert.Contains("5 identical failures suppressed", c.Entries[1].Message);
    }

    [Fact]
    public void Cancellation_LogsNothing()
    {
        var (l, c, _) = Make();
        Assert.False(l.Report("VixRead", new OperationCanceledException(), "x"));
        Assert.False(l.Report("VixRead", new TaskCanceledException(), "x"));
        Assert.Empty(c.Entries);
    }

    [Fact]
    public void ADifferentFailure_LogsSeparately_EvenInsideTheCooldown()
    {
        var (l, c, _) = Make();
        Assert.True(l.Report("MarketSummaryRead", new InvalidOperationException("a"), "ctx"));
        Assert.True(l.Report("MarketSummaryRead", new InvalidOperationException("b"), "ctx"));       // other message
        Assert.True(l.Report("MarketSummaryRead", new TimeoutException("a"), "ctx"));                // other type
        Assert.True(l.Report("VixRead", new InvalidOperationException("a"), "ctx"));                 // other site
        Assert.Equal(4, c.Entries.Count);
    }
}
