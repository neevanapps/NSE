using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NiftySignal.Notifications;

namespace NiftySignal.ExecutionService;

/// <summary>
/// The service's own failure alerts, sent directly (they must work when the ledger database does not). One message when a condition appears, a reminder while it lasts, one when it clears. Sending runs on its
/// own task, so a slow or failing Telegram call never delays protection. Every message is labelled paper only.
/// </summary>
public sealed class ServiceAlerts(ITelegramTextSender? telegram, TimeProvider clock, TimeSpan reminder, ILogger? log = null)
{
    public const string Banner = "V2 EXECUTION (PAPER ONLY)";

    readonly object _gate = new();
    readonly Dictionary<string, (string Text, DateTimeOffset SentAt)> _active = new(StringComparer.Ordinal);
    readonly Channel<string> _outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

    public IReadOnlyCollection<string> Active { get { lock (_gate) return _active.Values.Select(v => v.Text).ToArray(); } }

    /// <summary>Replaces the set of outstanding conditions (key = condition, value = what to say).</summary>
    public void Update(IReadOnlyDictionary<string, string> conditions)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            foreach (var (key, text) in conditions)
            {
                var again = _active.TryGetValue(key, out var known);
                if (again && now - known.SentAt < reminder) { _active[key] = (text, known.SentAt); continue; }
                _active[key] = (text, now);
                Raise((again ? "STILL OPEN: " : "") + text);
            }

            foreach (var key in _active.Keys.Where(k => !conditions.ContainsKey(k)).ToArray())
            {
                Raise("RECOVERED: " + _active[key].Text);
                _active.Remove(key);
            }
        }
    }

    /// <summary>Adds or refreshes one condition without clearing the others (a failed cycle reports only what it knows).</summary>
    public void Report(string key, string text)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var again = _active.TryGetValue(key, out var known);
            if (again && now - known.SentAt < reminder) return;
            _active[key] = (text, now);
            Raise((again ? "STILL OPEN: " : "") + text);
        }
    }

    /// <summary>A one-off event.</summary>
    public void Raise(string text)
    {
        log?.LogWarning("Execution alert: {Alert}", text);
        _outbox.Writer.TryWrite($"{Banner} | {text}");
    }

    public async Task RunSenderAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(ct)) await SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>Sends what is queued now (tests).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        while (_outbox.Reader.TryRead(out var message)) await SendAsync(message, ct);
    }

    async Task SendAsync(string message, CancellationToken ct)
    {
        if (telegram is null) return;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var result = await telegram.SendAsync(message, ct);
                if (result.Outcome != TelegramDocumentOutcome.Rejected) return;            // an uncertain delivery is not resent
                log?.LogWarning("Execution alert rejected by Telegram: {Error}", result.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log?.LogWarning(ex, "Execution alert could not be sent."); }
            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt * 2), clock, ct);
        }
    }
}
