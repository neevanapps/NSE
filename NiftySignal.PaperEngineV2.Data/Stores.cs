using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.PaperEngineV2.Data;

public sealed record SchemaVerdict(bool Compatible, IReadOnlyList<string> Problems)
{
    public static SchemaVerdict Ok { get; } = new(true, []);
}

/// <summary>
/// Start-up compatibility check: reads the single schema_versions row and NOTHING else - it never creates, migrates or alters anything. Any problem (schema or row absent, version older or newer, journal schema
/// different, database unreachable) is incompatible, and the caller then keeps V2 CLOSED (no frames journaled, no entries) without affecting NPT1.
/// </summary>
public static class V2SchemaCheck
{
    public static async Task<SchemaVerdict> CheckAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        try
        {
            await using var cmd = db.CreateCommand($"SELECT schema_version, journal_schema FROM {V2Schema.SchemaName}.pev2_schema_versions WHERE component = $1");
            cmd.Parameters.AddWithValue(V2Schema.Component);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return new SchemaVerdict(false, [$"schema_versions has no '{V2Schema.Component}' row"]);
            var problems = new List<string>();
            var v = r.GetInt32(0);
            if (v > V2Schema.CurrentVersion) problems.Add($"database schema version {v} is newer than this code understands ({V2Schema.CurrentVersion})");
            else if (v < V2Schema.CurrentVersion) problems.Add($"database schema version {v} is older than this code requires ({V2Schema.CurrentVersion}); the owner must run the reviewed idempotent script first");
            if (!string.Equals(r.GetString(1), V2Schema.JournalSchema, StringComparison.Ordinal)) problems.Add($"journal schema '{r.GetString(1)}' differs from '{V2Schema.JournalSchema}'");
            return problems.Count == 0 ? SchemaVerdict.Ok : new SchemaVerdict(false, problems);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            return new SchemaVerdict(false, ["the pev2 schema is not installed; the owner must run the reviewed idempotent script"]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SchemaVerdict(false, [$"the database could not be checked: {ex.GetType().Name}"]);
        }
    }
}

/// <summary>
/// The durable <see cref="IFrameJournal"/> over PostgreSQL. Appends are idempotent per key (INSERT ... ON CONFLICT DO NOTHING, then a comparison with the stored row decides DuplicateSame / ConflictDifferent;
/// a stored row is never overwritten) and every append is one auto-committed statement, so a returned append is on disk. <see cref="IsDurable"/> is true only because this implementation really commits.
/// </summary>
public sealed class PostgresFrameJournal(NpgsqlDataSource db) : IFrameJournal
{
    const string S = V2Schema.SchemaName;

    public bool IsDurable => true;

    public async ValueTask<AppendOutcome> AppendFrameAsync(FrameRecord r, CancellationToken ct)
    {
        await using var ins = db.CreateCommand($"INSERT INTO {S}.pev2_frames (session_id, bar_seq, frame_hash, payload_json, bar_end_utc_ticks, last_source_tick_id, verified) VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT (session_id, bar_seq) DO NOTHING");
        ins.Parameters.AddWithValue(r.SessionId); ins.Parameters.AddWithValue(r.BarSeq); ins.Parameters.AddWithValue(r.FrameHash); ins.Parameters.AddWithValue(r.PayloadJson);
        ins.Parameters.AddWithValue(r.BarEndAvailableAt.UtcTicks); ins.Parameters.AddWithValue(r.LastSourceTickId); ins.Parameters.AddWithValue(r.Verified);
        if (await ins.ExecuteNonQueryAsync(ct) == 1) return AppendOutcome.Appended;
        await using var sel = db.CreateCommand($"SELECT frame_hash FROM {S}.pev2_frames WHERE session_id = $1 AND bar_seq = $2");
        sel.Parameters.AddWithValue(r.SessionId); sel.Parameters.AddWithValue(r.BarSeq);
        var stored = (string?)await sel.ExecuteScalarAsync(ct);
        return string.Equals(stored, r.FrameHash, StringComparison.Ordinal) ? AppendOutcome.DuplicateSame : AppendOutcome.ConflictDifferent;
    }

    public async ValueTask<AppendOutcome> AppendDispositionAsync(DispositionRecord r, CancellationToken ct)
    {
        await using var ins = db.CreateCommand($"INSERT INTO {S}.pev2_dispositions (session_id, bar_seq, entries_allowed, block_reasons, processing_lag_ms, run_length, previous_frame_usable, processed_without_journal) VALUES ($1,$2,$3,$4,$5,$6,$7,$8) ON CONFLICT (session_id, bar_seq) DO NOTHING");
        ins.Parameters.AddWithValue(r.SessionId); ins.Parameters.AddWithValue(r.BarSeq); ins.Parameters.AddWithValue(r.EntriesAllowed); ins.Parameters.AddWithValue((int)r.BlockReasons);
        ins.Parameters.AddWithValue(r.ProcessingLagMs); ins.Parameters.AddWithValue(r.RunLength); ins.Parameters.AddWithValue(r.PreviousFrameUsable); ins.Parameters.AddWithValue(r.ProcessedWithoutJournal);
        if (await ins.ExecuteNonQueryAsync(ct) == 1) return AppendOutcome.Appended;
        await using var sel = db.CreateCommand($"SELECT entries_allowed, block_reasons, processing_lag_ms, run_length, previous_frame_usable, processed_without_journal FROM {S}.pev2_dispositions WHERE session_id = $1 AND bar_seq = $2");
        sel.Parameters.AddWithValue(r.SessionId); sel.Parameters.AddWithValue(r.BarSeq);
        await using var rd = await sel.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return AppendOutcome.ConflictDifferent;
        var same = rd.GetBoolean(0) == r.EntriesAllowed && rd.GetInt32(1) == (int)r.BlockReasons && rd.GetInt32(2) == r.ProcessingLagMs && rd.GetInt32(3) == r.RunLength && rd.GetBoolean(4) == r.PreviousFrameUsable && rd.GetBoolean(5) == r.ProcessedWithoutJournal;
        return same ? AppendOutcome.DuplicateSame : AppendOutcome.ConflictDifferent;
    }

    public async ValueTask AppendGapAsync(GapRecord r, CancellationToken ct)
    {
        await using var ins = db.CreateCommand($"INSERT INTO {S}.pev2_gaps (session_id, from_bar_seq, to_bar_seq, cause, producer_signals) VALUES ($1,$2,$3,$4,$5) ON CONFLICT (session_id, from_bar_seq, to_bar_seq) DO NOTHING");
        ins.Parameters.AddWithValue(r.SessionId); ins.Parameters.AddWithValue(r.FromBarSeq); ins.Parameters.AddWithValue(r.ToBarSeq); ins.Parameters.AddWithValue((int)r.Cause); ins.Parameters.AddWithValue((int)r.ProducerSignals);
        await ins.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<JournalSession> ReadSessionAsync(long sessionId, CancellationToken ct)
    {
        var frames = new List<FrameRecord>(); var disp = new List<DispositionRecord>(); var gaps = new List<GapRecord>();
        await using (var c = db.CreateCommand($"SELECT bar_seq, frame_hash, payload_json, bar_end_utc_ticks, last_source_tick_id, verified, journal_seq FROM {S}.pev2_frames WHERE session_id = $1 ORDER BY journal_seq"))
        {
            c.Parameters.AddWithValue(sessionId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) frames.Add(new FrameRecord(FrameRecord.CurrentSchema, sessionId, r.GetInt32(0), r.GetString(1), r.GetString(2), new DateTimeOffset(r.GetInt64(3), TimeSpan.Zero), r.GetInt64(4), r.GetBoolean(5), r.GetInt64(6)));
        }

        await using (var c = db.CreateCommand($"SELECT bar_seq, entries_allowed, block_reasons, processing_lag_ms, run_length, previous_frame_usable, processed_without_journal, journal_seq FROM {S}.pev2_dispositions WHERE session_id = $1 ORDER BY journal_seq"))
        {
            c.Parameters.AddWithValue(sessionId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) disp.Add(new DispositionRecord(FrameRecord.CurrentSchema, sessionId, r.GetInt32(0), r.GetBoolean(1), (IntegrityFlags)r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetBoolean(5), r.GetBoolean(6), r.GetInt64(7)));
        }

        await using (var c = db.CreateCommand($"SELECT from_bar_seq, to_bar_seq, cause, producer_signals, id FROM {S}.pev2_gaps WHERE session_id = $1 ORDER BY id"))
        {
            c.Parameters.AddWithValue(sessionId);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) gaps.Add(new GapRecord(FrameRecord.CurrentSchema, sessionId, r.GetInt32(0), r.GetInt32(1), (GapCause)r.GetInt32(2), (IntegrityFlags)r.GetInt32(3), Durable: true, r.GetInt64(4)));
        }

        return new JournalSession(frames, disp, gaps);
    }
}

/// <summary>Serialises ledger events to a stable JSON payload and back (the kind selects the type). Records round-trip exactly (instants as ISO-8601 UTC, numbers as shortest round-trip text).</summary>
public static class EventSerializer
{
    static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };

    static readonly Dictionary<string, Type> Types = new[]
    {
        typeof(EntryRejected), typeof(EntryOrderPlaced), typeof(EntryFilled), typeof(ExitRequested), typeof(ExitFilled), typeof(PositionUnresolved), typeof(RiskBreached), typeof(StrategyFaulted),
    }.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);       // each record's Kind equals its type name (asserted by a test)

    public static string Serialize(PaperEvent e) => JsonSerializer.Serialize(e, e.GetType(), Options);

    public static PaperEvent Deserialize(string kind, string json)
    {
        if (!Types.TryGetValue(kind, out var t)) throw new InvalidOperationException($"unknown ledger event kind '{kind}'");
        return (PaperEvent)JsonSerializer.Deserialize(json, t, Options)!;
    }
}

/// <summary>
/// The durable ledger sink. <see cref="Append"/> is synchronous and idempotent by event id (replaying the journal after a restart re-offers the same events: INSERT ... ON CONFLICT DO NOTHING, and a stored payload
/// that DIFFERS from the regenerated one is counted in <see cref="Conflicts"/> and never overwritten). A failure is counted in <see cref="Failures"/> - the engine contains it and the ledger is then NOT complete until
/// a reconcile; <see cref="IsComplete"/> says so honestly.
/// </summary>
public sealed class PostgresEventStore(NpgsqlDataSource db) : IPaperEventSink
{
    const string S = V2Schema.SchemaName;
    long _failures, _conflicts, _written;

    public long Failures => Interlocked.Read(ref _failures);

    public long Conflicts => Interlocked.Read(ref _conflicts);

    public long Written => Interlocked.Read(ref _written);

    public bool IsComplete => Failures == 0 && Conflicts == 0;

    public void Append(PaperEvent e)
    {
        try
        {
            var payload = EventSerializer.Serialize(e);
            using var ins = db.CreateCommand($"INSERT INTO {S}.pev2_events (event_id, strategy_id, config_hash, kind, bar_seq, at_utc_ticks, payload_json) VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT (event_id) DO NOTHING");
            ins.Parameters.AddWithValue(e.EventId); ins.Parameters.AddWithValue(e.StrategyId); ins.Parameters.AddWithValue(e.ConfigHash); ins.Parameters.AddWithValue(e.Kind);
            ins.Parameters.AddWithValue(e.BarSeq); ins.Parameters.AddWithValue(e.At.UtcTicks); ins.Parameters.AddWithValue(payload);
            if (ins.ExecuteNonQuery() == 1) { Interlocked.Increment(ref _written); return; }
            using var sel = db.CreateCommand($"SELECT payload_json FROM {S}.pev2_events WHERE event_id = $1");
            sel.Parameters.AddWithValue(e.EventId);
            if (!string.Equals((string?)sel.ExecuteScalar(), payload, StringComparison.Ordinal)) Interlocked.Increment(ref _conflicts);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failures);
            throw;                                  // the engine contains and counts it; rethrowing keeps its SinkFailures honest
        }
    }

    /// <summary>A bounded read (at most <see cref="MaxRead"/> rows) in time order, optionally for one strategy.</summary>
    public const int MaxRead = 10_000;

    public async Task<IReadOnlyList<PaperEvent>> ReadAsync(DateTimeOffset fromInclusive, DateTimeOffset toExclusive, string? strategyId, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > MaxRead) throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be in [1, {MaxRead}]");
        await using var c = db.CreateCommand($"SELECT kind, payload_json FROM {S}.pev2_events WHERE at_utc_ticks >= $1 AND at_utc_ticks < $2 AND ($3::text IS NULL OR strategy_id = $3) ORDER BY at_utc_ticks, event_id LIMIT $4");
        c.Parameters.AddWithValue(fromInclusive.UtcTicks); c.Parameters.AddWithValue(toExclusive.UtcTicks); c.Parameters.AddWithValue((object?)strategyId ?? DBNull.Value); c.Parameters.AddWithValue(limit);
        var list = new List<PaperEvent>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(EventSerializer.Deserialize(r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Records the exact canonical strategy configuration a ledger event's config hash refers to (idempotent).</summary>
    public async Task RecordConfigAsync(StrategyConfig config, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = db.CreateCommand($"INSERT INTO {S}.pev2_strategy_configs (config_hash, strategy_id, version, canonical_json, recorded_utc_ticks) VALUES ($1,$2,$3,$4,$5) ON CONFLICT (config_hash) DO NOTHING");
        c.Parameters.AddWithValue(config.Identity.Hash); c.Parameters.AddWithValue(config.Identity.StrategyId); c.Parameters.AddWithValue(config.Identity.Version); c.Parameters.AddWithValue(config.CanonicalJson); c.Parameters.AddWithValue(now.UtcTicks);
        await c.ExecuteNonQueryAsync(ct);
    }
}

public sealed record ReconcileReport(int Regenerated, int MissingInStore, int ExtraInStore, int Different)
{
    public bool Clean => MissingInStore == 0 && ExtraInStore == 0 && Different == 0;
}

/// <summary>
/// Compares the ledger regenerated by deterministic replay of the journal with the stored ledger over the same time window. The ledger is a pure function of the journal: after a crash anything missing is re-appended by
/// the replay itself (idempotent sink), and any DIFFERENCE or stored event the replay cannot reproduce is reported (never silently accepted, never overwritten).
/// </summary>
public static class LedgerReconciler
{
    public static async Task<ReconcileReport> ReconcileAsync(IReadOnlyList<PaperEvent> regenerated, PostgresEventStore store, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct)
    {
        var stored = await store.ReadAsync(fromInclusive, toExclusive, null, PostgresEventStore.MaxRead, ct);
        var byId = stored.ToDictionary(e => e.EventId, StringComparer.Ordinal);
        int missing = 0, different = 0;
        foreach (var e in regenerated.Where(e => e.At >= fromInclusive && e.At < toExclusive))
        {
            if (!byId.Remove(e.EventId, out var s)) missing++;
            else if (!s.Equals(e)) different++;
        }

        return new ReconcileReport(regenerated.Count, missing, byId.Count, different);
    }
}
