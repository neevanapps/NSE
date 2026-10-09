using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NiftySignal.Domain;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Scheduling;

namespace NiftySignal.Tests.Trading.Core;

internal static class Clock
{
    public static readonly DateOnly Day = new(2026, 9, 22);

    /// <summary>An IST wall time on <see cref="Day"/> as a UTC instant.</summary>
    public static DateTimeOffset Ist(int hour, int minute, int second = 0, int millisecond = 0)
        => new DateTimeOffset(Day.ToDateTime(new TimeOnly(hour, minute, second, millisecond)), IstTime.Offset).ToUniversalTime();

    public static DateTimeOffset IstTicks(int hour, int minute, int second, long extraTicks)
        => Ist(hour, minute, second).AddTicks(extraTicks);
}

internal static class Ev
{
    public static QuoteEvent Opt(DateTimeOffset at, long id, string token = "OPT", decimal? bid = 100m, decimal? ask = 101m)
        => new(at, EventKind.OptionQuote, id, token, bid, ask);

    public static QuoteEvent Fut(DateTimeOffset at, long id, string token = "FUT", decimal? bid = 24000m, decimal? ask = 24001m)
        => new(at, EventKind.FutureQuote, id, token, bid, ask);

    public static QuoteEvent Spot(DateTimeOffset at, long id, string token = "SPOT")
        => new(at, EventKind.SpotQuote, id, token, null, null, 24000m);

    public static BarCompletedEvent Bar(DateTimeOffset at, long seq, string token = "FUT") => new(at, seq, token);
}

/// <summary>Records every callback with the engine time it saw; asserts the loop's own invariants while doing so.</summary>
internal sealed class RecordingHandler : IEngineHandler
{
    public List<string> Log { get; } = new();

    public List<DateTimeOffset> Times { get; } = new();

    public Action<EngineEvent, IEngineScheduler>? EventHook { get; set; }

    public Action<ScheduledDeadline, IEngineScheduler>? DeadlineHook { get; set; }

    public void OnEvent(EngineEvent engineEvent, IEngineScheduler scheduler)
    {
        Assert.Equal(engineEvent.AvailableAt, scheduler.Now);
        Log.Add(FormattableString.Invariant($"E {scheduler.Now:O} {engineEvent.Kind} {engineEvent.SourceId}"));
        Times.Add(scheduler.Now);
        EventHook?.Invoke(engineEvent, scheduler);
    }

    public void OnDeadline(ScheduledDeadline deadline, IEngineScheduler scheduler)
    {
        Assert.Equal(deadline.Time, scheduler.Now);
        Log.Add(FormattableString.Invariant($"D {scheduler.Now:O} {deadline.Class} {deadline.VariantOrdinal} {deadline.Key}"));
        Times.Add(scheduler.Now);
        DeadlineHook?.Invoke(deadline, scheduler);
    }

    public string Digest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Log))));
}

internal static class Culture
{
    /// <summary>Runs <paramref name="action"/> on a worker task under the given culture without touching the caller's culture.</summary>
    public static T Under<T>(string cultureName, Func<T> action)
        => Task.Run(() =>
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return action();
        }).GetAwaiter().GetResult();
}
