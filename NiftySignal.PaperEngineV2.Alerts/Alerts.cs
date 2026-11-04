using Npgsql;
using NiftySignal.Notifications;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.PaperEngineV2.Alerts;

public enum AlertClass { Opened, Closed, OrderRejected, PartialFill, ExecutionFailure }

public enum AlertStatus { Pending = 0, Sent = 1, Uncertain = 2, Failed = 3, Expired = 4, Sending = 5 }

/// <summary>
/// Per-event opt-in. EVERYTHING is off by default: a strategy trading in the shadow engine sends nothing unless the owner enables that class. <see cref="PartialFill"/> exists for the contract but V2 v1 is
/// all-or-none, so no partial-fill event is ever produced (the class can be enabled; nothing will ever match it).
/// </summary>
public sealed record AlertOptions(bool Opened = false, bool Closed = false, bool OrderRejected = false, bool PartialFill = false, bool ExecutionFailure = false,
    int MaxAttempts = 5, TimeSpan? MaxAge = null)
{
    public static readonly AlertOptions AllOff = new();

    public TimeSpan EffectiveMaxAge => MaxAge ?? TimeSpan.FromMinutes(15);

    public bool Enabled(AlertClass c) => c switch
    {
        AlertClass.Opened => Opened, AlertClass.Closed => Closed, AlertClass.OrderRejected => OrderRejected, AlertClass.PartialFill => PartialFill, AlertClass.ExecutionFailure => ExecutionFailure, _ => false,
    };
}

public enum SendOutcome { Sent, Rejected, Uncertain }

public sealed record AlertSendResult(SendOutcome Outcome, long? MessageId = null, int RetryAfterSeconds = 60, string? Error = null);

public interface IAlertTransport
{
    Task<AlertSendResult> SendAsync(string text, CancellationToken ct);
}

/// <summary>Adapter over the existing acknowledged Telegram text sender (Sent only on a message id; Uncertain is never resent automatically).</summary>
public sealed class TelegramAlertTransport(ITelegramTextSender sender) : IAlertTransport
{
    public async Task<AlertSendResult> SendAsync(string text, CancellationToken ct)
    {
        var r = await sender.SendAsync(text, ct);
        return new AlertSendResult(r.Outcome switch { TelegramDocumentOutcome.Sent => SendOutcome.Sent, TelegramDocumentOutcome.Rejected => SendOutcome.Rejected, _ => SendOutcome.Uncertain }, r.MessageId, r.RetryAfterSeconds, r.Error);
    }
}

/// <summary>The entry facts an alert needs that the fill event itself does not carry (resolved from the durable ledger, so they survive a restart).</summary>
public sealed record EntryContext(string PositionId, OptionRight Right, string Token, double Strike, DateOnly Expiry, decimal FillPrice, DateTimeOffset EntryAt, string SignalId);

/// <summary>Maps a ledger event to its alert class and plain-text message. Every message is labelled V2 SHADOW (paper only); no credential, token or config value ever enters a message.</summary>
public static class AlertFormatter
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public static AlertClass? ClassOf(PaperEvent e) => e switch
    {
        EntryFilled => AlertClass.Opened,
        ExitFilled => AlertClass.Closed,
        EntryRejected => AlertClass.OrderRejected,
        StrategyFaulted or PositionUnresolved => AlertClass.ExecutionFailure,
        _ => null,
    };

    public static string AlertId(PaperEvent e, AlertClass c) => $"{e.EventId}#{c}";

    static string Time(DateTimeOffset t) => (t + Ist).ToString("HH:mm:ss") + " IST";

    static string Cut(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public const string Banner = "V2 SHADOW \u2014 PAPER ONLY";

    static string Hold(TimeSpan d) => d < TimeSpan.Zero ? "n/a" : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s" : $"{d.Minutes}m {d.Seconds}s";

    static string Contract(EntryContext c) => $"{c.Right.ToString().ToUpperInvariant()} {c.Token} | expiry {c.Expiry:dd-MMM-yyyy} | strike {c.Strike:0}";

    /// <param name="version">The strategy's configured version (shown next to its id) when known.</param>
    /// <param name="entry">The entry facts of the position (strike, expiry, signal id, entry time and price) when they could be resolved; the alert degrades to the ledger event's own fields when not.</param>
    public static string Format(PaperEvent e, int? version = null, EntryContext? entry = null)
    {
        var head = $"{Banner} | {e.StrategyId}{(version is { } v ? $" v{v}" : "")} | ";
        return head + e switch
        {
            EntryFilled f when entry is not null => $"ENTRY {Contract(entry)} | fill {f.FillPrice:0.00} x{f.Quantity} (fees {f.Fees:0.00}) | signal {entry.SignalId} | bar {f.BarSeq} | {Time(f.At)}",
            ExitFilled x when entry is not null => $"EXIT {Contract(entry)} x{x.Quantity} | entry {entry.FillPrice:0.00} @ {Time(entry.EntryAt)} | exit {x.FillPrice:0.00} @ {Time(x.At)} | reason {x.Reason} | gross {x.GrossPnl:0.00} | fees {x.GrossPnl - x.NetPnl:0.00} (entry+exit) | net {x.NetPnl:0.00} | held {Hold(x.At - entry.EntryAt)} | bar {x.BarSeq}",
            EntryFilled f => $"ENTRY {f.Right.ToString().ToUpperInvariant()} {f.Token} | fill {f.FillPrice:0.00} x{f.Quantity} (fees {f.Fees:0.00}) | bar {f.BarSeq} | {Time(f.At)}",
            ExitFilled x => $"EXIT {x.PositionId.Split(':').Last()} x{x.Quantity} @ {x.FillPrice:0.00} | reason {x.Reason} | gross {x.GrossPnl:0.00} | fees {x.GrossPnl - x.NetPnl:0.00} (entry+exit) | net {x.NetPnl:0.00} | bar {x.BarSeq} | {Time(x.At)}",
            EntryRejected r => $"ORDER REJECTED {r.Right?.ToString().ToUpperInvariant() ?? "-"} | {r.Reason} | {Cut(r.Detail)} | bar {r.BarSeq} | {Time(r.At)}",
            StrategyFaulted s => $"EXECUTION FAILURE: strategy stopped | {Cut(s.Message)} | bar {s.BarSeq} | {Time(s.At)}",
            PositionUnresolved u => $"EXECUTION FAILURE: position UNRESOLVED (no exit fill) | {Cut(u.Detail)} | bar {u.BarSeq} | {Time(u.At)}",
            _ => throw new ArgumentException($"no alert text for {e.Kind}", nameof(e)),
        };
    }
}

/// <summary>
/// Turns ledger events into outbox rows (idempotent by alert id = event id + class, so replay after a restart enqueues nothing twice and repairs a crash between the ledger write and the enqueue) for the
/// ENABLED classes only. Wraps the durable event store; the ledger write happens first, so a failure of the outbox never loses the ledger event. A failure is counted and rethrown to the engine's sink containment.
/// </summary>
public sealed class AlertingEventSink(IPaperEventSink inner, NpgsqlDataSource db, AlertOptions options, TimeProvider clock, Func<string, int?>? versionOf = null) : IPaperEventSink
{
    long _failures, _enqueued;

    public long Failures => Interlocked.Read(ref _failures);

    public long Enqueued => Interlocked.Read(ref _enqueued);

    EntryContext? ResolveEntry(PaperEvent e) => EntryContextLookup.Resolve(db, e);

    public void Append(PaperEvent e)
    {
        inner.Append(e);
        if (AlertFormatter.ClassOf(e) is not { } c || !options.Enabled(c)) return;
        try
        {
            var now = clock.GetUtcNow().UtcTicks;
            using var cmd = db.CreateCommand($"INSERT INTO {V2Schema.SchemaName}.pev2_alert_outbox (alert_id, alert_class, strategy_id, event_id, message_text, status, attempts, created_utc_ticks, next_attempt_utc_ticks) VALUES ($1,$2,$3,$4,$5,0,0,$6,$6) ON CONFLICT (alert_id) DO NOTHING");
            cmd.Parameters.AddWithValue(AlertFormatter.AlertId(e, c)); cmd.Parameters.AddWithValue(c.ToString()); cmd.Parameters.AddWithValue(e.StrategyId); cmd.Parameters.AddWithValue(e.EventId);
            cmd.Parameters.AddWithValue(AlertFormatter.Format(e, versionOf?.Invoke(e.StrategyId), ResolveEntry(e))); cmd.Parameters.AddWithValue(e.At.UtcTicks);
            if (cmd.ExecuteNonQuery() == 1) Interlocked.Increment(ref _enqueued);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failures);
            throw;
        }
    }
}

public static class EntryContextLookup
{
    /// <summary>
    /// The entry facts for an EntryFilled / ExitFilled from the durable ledger (this strategy's most recent order and fill events up to the event itself). Never throws: a missing row or a read failure returns
    /// null and the alert falls back to the fields the event carries. A bounded read (the newest 40 order / fill events), never a table scan.
    /// </summary>
    public static EntryContext? Resolve(NpgsqlDataSource db, PaperEvent e)
    {
        if (e is not (EntryFilled or ExitFilled)) return null;
        try
        {
            var events = new List<PaperEvent>();
            using (var cmd = db.CreateCommand($"SELECT kind, payload_json FROM {V2Schema.SchemaName}.pev2_events WHERE strategy_id = $1 AND kind IN ('EntryOrderPlaced','EntryFilled') AND at_utc_ticks <= $2 ORDER BY at_utc_ticks DESC, event_id DESC LIMIT 40"))
            {
                cmd.Parameters.AddWithValue(e.StrategyId); cmd.Parameters.AddWithValue(e.At.UtcTicks);
                using var r = cmd.ExecuteReader();
                while (r.Read()) events.Add(EventSerializer.Deserialize(r.GetString(0), r.GetString(1)));
            }

            if (e is EntryFilled self && !events.Any(x => x.EventId == self.EventId)) events.Add(self);
            var positionId = e is EntryFilled ef ? ef.PositionId : ((ExitFilled)e).PositionId;
            var fill = events.OfType<EntryFilled>().FirstOrDefault(f => f.PositionId == positionId);
            var placed = fill is null ? null : events.OfType<EntryOrderPlaced>().FirstOrDefault(p => p.IntentId == fill.IntentId);
            if (fill is null || placed is null) return null;
            return new EntryContext(positionId, fill.Right, fill.Token, placed.Strike, placed.Expiry, fill.FillPrice, fill.At, placed.IntentId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed record DispatchReport(int Claimed, int Sent, int Retrying, int Uncertain, int Failed, int Expired);

/// <summary>
/// Sends pending alerts. Delivery semantics (mirroring the acknowledged Telegram sender): a row is CLAIMED (status Sending, attempt counted, committed) before the send; Sent only on an acknowledged message id;
/// a definitive refusal schedules a retry (up to <see cref="AlertOptions.MaxAttempts"/>, then Failed); an unknown outcome - or a claim interrupted by a crash - becomes Uncertain and is NEVER resent automatically.
/// An alert older than <see cref="AlertOptions.EffectiveMaxAge"/> is Expired instead of sent, so a restart that replays history can never produce a burst of stale notifications.
/// </summary>
public sealed class AlertDispatcher(NpgsqlDataSource db, IAlertTransport transport, AlertOptions options, TimeProvider clock)
{
    const string S = V2Schema.SchemaName;
    public const int MaxBatch = 50;

    /// <summary>Resolves claims left in Sending by an interrupted process to Uncertain (call once at start-up).</summary>
    public async Task<int> ResolveInterruptedAsync(CancellationToken ct)
    {
        await using var c = db.CreateCommand($"UPDATE {S}.pev2_alert_outbox SET status = 2, last_error = 'claim interrupted before the outcome was recorded' WHERE status = 5");
        return await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<DispatchReport> DispatchAsync(int batch, CancellationToken ct)
    {
        if (batch is < 1 or > MaxBatch) throw new ArgumentOutOfRangeException(nameof(batch), $"batch must be in [1, {MaxBatch}]");
        var now = clock.GetUtcNow();
        var oldest = (now - options.EffectiveMaxAge).UtcTicks;
        int sent = 0, retrying = 0, uncertain = 0, failed = 0, expired = 0;

        await using (var ex = db.CreateCommand($"UPDATE {S}.pev2_alert_outbox SET status = 4, last_error = 'stale: older than the maximum alert age' WHERE status = 0 AND created_utc_ticks < $1"))
        {
            ex.Parameters.AddWithValue(oldest);
            expired = await ex.ExecuteNonQueryAsync(ct);
        }

        var claimed = new List<(string Id, string Text, int Attempts)>();
        await using (var claim = db.CreateCommand($"UPDATE {S}.pev2_alert_outbox SET status = 5, attempts = attempts + 1 WHERE alert_id IN (SELECT alert_id FROM {S}.pev2_alert_outbox WHERE status = 0 AND next_attempt_utc_ticks <= $1 ORDER BY created_utc_ticks, alert_id LIMIT $2 FOR UPDATE SKIP LOCKED) RETURNING alert_id, message_text, attempts"))
        {
            claim.Parameters.AddWithValue(now.UtcTicks); claim.Parameters.AddWithValue(batch);
            await using var r = await claim.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) claimed.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));
        }

        foreach (var (id, text, attempts) in claimed.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            AlertSendResult result;
            try { result = await transport.SendAsync(text, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { result = new AlertSendResult(SendOutcome.Uncertain, Error: $"transport threw {ex.GetType().Name}"); }

            switch (result.Outcome)
            {
                case SendOutcome.Sent:
                    await ExecAsync($"UPDATE {S}.pev2_alert_outbox SET status = 1, sent_utc_ticks = $2, telegram_message_id = $3, last_error = NULL WHERE alert_id = $1", ct, id, now.UtcTicks, (object?)result.MessageId ?? DBNull.Value);
                    sent++;
                    break;
                case SendOutcome.Rejected when attempts < options.MaxAttempts:
                    await ExecAsync($"UPDATE {S}.pev2_alert_outbox SET status = 0, next_attempt_utc_ticks = $2, last_error = $3 WHERE alert_id = $1", ct, id, now.AddSeconds(Math.Clamp(result.RetryAfterSeconds, 1, 86_400)).UtcTicks, (object?)result.Error ?? DBNull.Value);
                    retrying++;
                    break;
                case SendOutcome.Rejected:
                    await ExecAsync($"UPDATE {S}.pev2_alert_outbox SET status = 3, last_error = $2 WHERE alert_id = $1", ct, id, (object?)result.Error ?? DBNull.Value);
                    failed++;
                    break;
                default:
                    await ExecAsync($"UPDATE {S}.pev2_alert_outbox SET status = 2, last_error = $2 WHERE alert_id = $1", ct, id, result.Error ?? "send outcome unknown");
                    uncertain++;
                    break;
            }
        }

        return new DispatchReport(claimed.Count, sent, retrying, uncertain, failed, expired);
    }

    async Task ExecAsync(string sql, CancellationToken ct, params object[] args)
    {
        await using var c = db.CreateCommand(sql);
        foreach (var a in args) c.Parameters.AddWithValue(a);
        await c.ExecuteNonQueryAsync(ct);
    }
}
