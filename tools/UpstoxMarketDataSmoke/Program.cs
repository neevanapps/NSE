using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Enums;
using NiftySignal.Ingestion;
using NiftySignal.Ingestion.Upstox;

// Read-only acceptance probe. Token lives only in memory; no database/credentials file or orders.
Console.Write("Upstox market-data access token (hidden): ");
if (Console.IsInputRedirected) throw new InvalidOperationException("Run interactively so credentials are not passed in a command or echoed.");
var chars = new List<char>();
while (true)
{
    var key = Console.ReadKey(intercept: true);
    if (key.Key == ConsoleKey.Enter) break;
    if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
    else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
}
Console.WriteLine();
var token = new string(chars.ToArray()); chars.Clear();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var rest = new UpstoxRestClient(http);
using var logs = LoggerFactory.Create(x => x.AddProvider(new SafeLogProvider()));
var provider = new UpstoxMarketDataProvider(new UpstoxInstrumentMasterProvider(http), rest, logs);
var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var counts = new Dictionary<string, int>(StringComparer.Ordinal);
try
{
    var instruments = await provider.ResolveAsync(new MarketDataCredential(token, ""), day, timeout.Token);
    Console.WriteLine($"Resolved {instruments.Count} instruments for {day}.");
    foreach (var group in instruments.GroupBy(x => (x.Underlying, x.InstrumentType))) Console.WriteLine($"  {group.Key}: {group.Count()}");
    var feed = provider.CreateFeed(new MarketDataCredential(token, ""), instruments, new ConsoleGaps());
    var last = new Dictionary<string, long>(StringComparer.Ordinal);
    await foreach (var tick in feed.ReadTicksAsync(timeout.Token))
    {
        counts[tick.Token] = counts.GetValueOrDefault(tick.Token) + 1;
        if (counts[tick.Token] <= 2)
            Console.WriteLine($"{tick.Token}: LTP={tick.LastPrice}, cumulative volume={tick.Volume}, OI={tick.OpenInterest}, depth={tick.Depth is not null}, snapshot={tick.IsSnapshot}, message UTC={tick.ExchangeTimestamp:O}, trade UTC={tick.LastTradeTimestamp:O}");
        if (last.TryGetValue(tick.Token, out var previous) && tick.Volume < previous) Console.WriteLine($"WARNING: cumulative volume decreased for {tick.Token}.");
        last[tick.Token] = tick.Volume;
    }
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Console.WriteLine("Two-minute read-only probe finished."); }
catch (Exception ex) { Console.WriteLine($"Probe failed: {ex.GetType().Name}. Check local authentication and connectivity."); Environment.ExitCode = 1; }
Console.WriteLine($"Received {counts.Values.Sum()} updates across {counts.Count} instruments.");
if (counts.Count == 0) { Console.WriteLine("No market data received; this probe has not passed."); Environment.ExitCode = 1; }

sealed class ConsoleGaps : IDataGapRecorder
{
    public Task<long> RecordGapStartedAsync(DateTimeOffset startedAt, string reason, CancellationToken cancellationToken) { Console.WriteLine(reason); return Task.FromResult(1L); }
    public Task RecordGapAttemptAsync(long gapId, string reason, CancellationToken cancellationToken) { Console.WriteLine(reason); return Task.CompletedTask; }
    public Task RecordGapEndedAsync(long gapId, DateTimeOffset endedAt, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CloseAllOpenGapsAsync(DateTimeOffset endedAt, CancellationToken cancellationToken) => Task.CompletedTask;
}

sealed class SafeLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
    public void Dispose() { }
}
