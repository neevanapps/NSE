using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.PaperEngineV2.Data;

/// <summary>
/// Exclusive ownership of the execution ledger, and the path by which a writer changes it. Two existing mechanisms, applied together at the persistence boundary:
/// <list type="bullet">
/// <item>a PostgreSQL session advisory lock held on one dedicated connection. Every fenced write is a transaction ON THAT CONNECTION, so it can only commit while the session that holds the lock is alive:
/// once the lock is gone (and another writer can have it) nothing of this instance can still commit.</item>
/// <item>the execution epoch (<c>pev2_execution_state</c>, when the owner has installed it). Taking ownership claims the next epoch; every write locks the state row and requires that epoch, in the same
/// transaction. Both writers (the execution service and the in-Host runtime) claim it.</item>
/// </list>
/// An instance that has lost ownership stays lost: nothing it is asked to write afterwards is accepted, even if the process takes ownership again with a new instance.
/// </summary>
public sealed class ExecutionOwnership : IAsyncDisposable
{
    public const long LockKey = 0x5045_5632_4558_4543;      // "PEV2EXEC"
    public const string Component = "execution";
    const string S = V2Schema.SchemaName;

    readonly SemaphoreSlim _gate = new(1, 1);              // one connection, several writer threads
    NpgsqlConnection? _connection;
    volatile bool _lost;

    ExecutionOwnership(NpgsqlConnection connection) => _connection = connection;

    /// <summary>The epoch claimed with this ownership; null only where the execution-state table is not installed (then no execution service can run).</summary>
    public long? Epoch { get; private set; }

    /// <summary>The ownership, or null when another writer holds it. Never waits for the lock.</summary>
    public static async Task<ExecutionOwnership?> TryAcquireAsync(NpgsqlDataSource db, CancellationToken ct, string? owner = null, DateTimeOffset? now = null)
    {
        var connection = await db.OpenConnectionAsync(ct);
        ExecutionOwnership? ownership = null;
        try
        {
            await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection) { Parameters = { new() { Value = LockKey } } })
                if ((bool)(await cmd.ExecuteScalarAsync(ct))!) ownership = new ExecutionOwnership(connection);
            if (ownership is not null)
            {
                await using var installed = new NpgsqlCommand($"SELECT to_regclass('{S}.pev2_execution_state') IS NOT NULL", connection);
                if ((bool)(await installed.ExecuteScalarAsync(ct))!)
                {
                    // waits for a fenced write of a superseded writer that is still committing, so it is complete before this owner reads anything
                    await using var claim = ClaimCommand(connection, owner ?? $"{Environment.MachineName}:{Environment.ProcessId}", now ?? DateTimeOffset.UtcNow);
                    ownership.Epoch = (long)(await claim.ExecuteScalarAsync(ct))!;
                }

                return ownership;
            }
        }
        catch
        {
            if (ownership is not null) await ownership.DisposeAsync(); else await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    internal static NpgsqlCommand ClaimCommand(NpgsqlConnection connection, string owner, DateTimeOffset now) => new(
        $"INSERT INTO {S}.pev2_execution_state (component, epoch, owner, heartbeat_utc_ticks, trade_date, status, snapshot_json) VALUES ($1, 1, $2, $3, '', 'Starting', '') " +
        "ON CONFLICT (component) DO UPDATE SET epoch = pev2_execution_state.epoch + 1, owner = EXCLUDED.owner RETURNING epoch", connection)
    { Parameters = { new() { Value = Component }, new() { Value = owner }, new() { Value = now.UtcTicks } } };

    /// <summary>False once the owning connection is gone (the lock went with it) or a write found this owner superseded.</summary>
    public async Task<bool> IsHeldAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_lost || _connection is not { } c) return false;
            await using var cmd = new NpgsqlCommand("SELECT 1", c) { CommandTimeout = 5 };
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lost = true;
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Runs <paramref name="work"/> in one transaction that commits only while this instance still owns the ledger.</summary>
    public async Task<T> WriteAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var connection = Live();
            try
            {
                await using var tx = await connection.BeginTransactionAsync(ct);
                if (Epoch is not null)
                {
                    await using var fence = Fence(connection, tx);
                    Verify(await fence.ExecuteScalarAsync(ct));
                }

                var result = await work(connection, tx);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (ex is not (OwnershipLostException or OperationCanceledException) && connection.State != ConnectionState.Open)
            {
                throw Lost("the ownership connection was lost during a write", ex);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>The synchronous form of <see cref="WriteAsync{T}"/> for the engine's synchronous sinks.</summary>
    public T Write<T>(Func<NpgsqlConnection, NpgsqlTransaction, T> work)
    {
        _gate.Wait();
        try
        {
            var connection = Live();
            try
            {
                using var tx = connection.BeginTransaction();
                if (Epoch is not null)
                {
                    using var fence = Fence(connection, tx);
                    Verify(fence.ExecuteScalar());
                }

                var result = work(connection, tx);
                tx.Commit();
                return result;
            }
            catch (Exception ex) when (ex is not OwnershipLostException && connection.State != ConnectionState.Open)
            {
                throw Lost("the ownership connection was lost during a write", ex);
            }
        }
        finally { _gate.Release(); }
    }

    NpgsqlConnection Live() => !_lost && _connection is { State: ConnectionState.Open } c ? c : throw Lost("this writer no longer holds the paper ledger");

    static NpgsqlCommand Fence(NpgsqlConnection c, NpgsqlTransaction tx) =>
        new($"SELECT epoch FROM {S}.pev2_execution_state WHERE component = $1 FOR SHARE", c, tx) { Parameters = { new() { Value = Component } } };

    void Verify(object? current)
    {
        if (current is not long epoch || epoch != Epoch) throw Lost($"epoch {Epoch} is no longer the execution owner");
    }

    OwnershipLostException Lost(string message, Exception? inner = null)
    {
        _lost = true;
        return new OwnershipLostException(message, inner);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _lost = true;
            if (Interlocked.Exchange(ref _connection, null) is not { } c) return;
            try
            {
                // explicit: a pooled connection would otherwise keep the lock until its next reset
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", c) { Parameters = { new() { Value = LockKey } }, CommandTimeout = 5 };
                await cmd.ExecuteScalarAsync();
            }
            catch (Exception) { }
            await c.DisposeAsync();
        }
        finally { _gate.Release(); }
    }
}

public sealed class OwnershipLostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A ledger-schema write through the owner's fence when one is given; every production writer gives one. Without it (tools and tests that only read, or that write a scratch ledger) the statement runs on a
/// pooled connection and is not fenced.
/// </summary>
public static class Pev2Write
{
    public static T Run<T>(NpgsqlDataSource db, ExecutionOwnership? writer, Func<NpgsqlConnection, NpgsqlTransaction?, T> work)
    {
        if (writer is not null) return writer.Write(work);
        using var connection = db.OpenConnection();
        return work(connection, null);
    }

    public static async Task<T> RunAsync<T>(NpgsqlDataSource db, ExecutionOwnership? writer, Func<NpgsqlConnection, NpgsqlTransaction?, Task<T>> work, CancellationToken ct)
    {
        if (writer is not null) return await writer.WriteAsync(work, ct);
        await using var connection = await db.OpenConnectionAsync(ct);
        return await work(connection, null);
    }
}

/// <summary>One execution input as journaled. The frame is stored as its canonical payload; the previous frame is the one journaled before it (the disposition says whether it may be used).</summary>
public sealed record JournaledInput(
    DateTimeOffset Now, DateOnly TradeDate, long SessionId, IReadOnlyList<QuoteSnapshot> Quotes, VixReading? Vix, IReadOnlyList<ContractAverage> Averages, SafetyState Safety, string? Frame, FrameDisposition? Disposition)
{
    static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Serialize(ExecutionInput i) => JsonSerializer.Serialize(
        new JournaledInput(i.Now, i.TradeDate, i.SessionId, i.Quotes, i.Vix, i.Averages, i.Safety, i.Frame?.PayloadJson, i.Disposition), Options);

    public static JournaledInput Deserialize(string json) => JsonSerializer.Deserialize<JournaledInput>(json, Options) ?? throw new InvalidOperationException("empty execution journal row");

    public ExecutionInput ToInput(MarketStateFrame? previousJournaled)
    {
        var frame = Frame is null ? null : FrameSerializer.Read(Frame);
        var disposition = Disposition is null ? null : Disposition with { IsReplay = true };
        return new ExecutionInput(Now, TradeDate, SessionId, Quotes, Vix, Averages, Safety, frame, disposition is { PreviousFrameUsable: true } ? previousJournaled : null, disposition);
    }
}

public sealed record ExecutionStateView(long Epoch, string Owner, DateTimeOffset Heartbeat, string TradeDate, string Status, string SnapshotJson);

/// <summary>
/// The execution writer's durable store. Every write goes through the owner's fence (<see cref="ExecutionOwnership"/>), so a superseded writer can never commit. A state-changing input, its ledger
/// events and their alert outbox rows commit together or not at all.
/// </summary>
public sealed class ExecutionStore(NpgsqlDataSource db)
{
    const string S = V2Schema.SchemaName;
    const string Component = ExecutionOwnership.Component;

    /// <summary>True when the owner has run the execution-service script.</summary>
    public async Task<bool> IsInstalledAsync(CancellationToken ct) =>
        await ObservabilitySchema.HasAsync(db, "pev2_execution_state", ct) && await ObservabilitySchema.HasAsync(db, "pev2_execution_journal", ct);

    public async Task<ExecutionStateView?> ReadStateAsync(CancellationToken ct)
    {
        await using var c = db.CreateCommand($"SELECT epoch, owner, heartbeat_utc_ticks, trade_date, status, snapshot_json FROM {S}.pev2_execution_state WHERE component = $1");
        c.Parameters.AddWithValue(Component);
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ExecutionStateView(r.GetInt64(0), r.GetString(1), new DateTimeOffset(r.GetInt64(2), TimeSpan.Zero), r.GetString(3), r.GetString(4), r.GetString(5)) : null;
    }

    /// <summary>Takes the next fencing epoch without the ledger lock (<see cref="ExecutionOwnership.TryAcquireAsync"/> is how a writer takes it). The heartbeat and snapshot of the previous owner stay.</summary>
    public async Task<long> ClaimAsync(string owner, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var c = ExecutionOwnership.ClaimCommand(connection, owner, now);
        return (long)(await c.ExecuteScalarAsync(ct))!;
    }

    public async Task HeartbeatAsync(ExecutionOwnership writer, ExecutionSnapshot snapshot, CancellationToken ct)
    {
        var epoch = writer.Epoch ?? throw new OwnershipLostException("this writer holds no execution epoch");
        await writer.WriteAsync(async (connection, tx) =>
        {
            await using var c = new NpgsqlCommand($"UPDATE {S}.pev2_execution_state SET heartbeat_utc_ticks = $3, trade_date = $4, status = $5, snapshot_json = $6 WHERE component = $1 AND epoch = $2", connection, tx);
            c.Parameters.AddWithValue(Component); c.Parameters.AddWithValue(epoch); c.Parameters.AddWithValue(snapshot.At.UtcTicks);
            c.Parameters.AddWithValue(snapshot.TradeDate.ToString("yyyy-MM-dd")); c.Parameters.AddWithValue(snapshot.Status); c.Parameters.AddWithValue(SnapshotJson(snapshot));
            return await c.ExecuteNonQueryAsync(ct) == 1 ? true : throw new OwnershipLostException($"epoch {epoch} is no longer the execution owner");
        }, ct);
    }

    static readonly JsonSerializerOptions SnapshotOptions = new() { Converters = { new JsonStringEnumConverter() } };

    public static string SnapshotJson(ExecutionSnapshot s) => JsonSerializer.Serialize(s, SnapshotOptions);

    public static ExecutionSnapshot? ReadSnapshot(string json) => json.Length == 0 ? null : JsonSerializer.Deserialize<ExecutionSnapshot>(json, SnapshotOptions);

    /// <summary>
    /// The next inputs of the day after journal row <paramref name="afterId"/>, in the order applied (the identity id; one fenced writer at a time, so ids never commit out of order). Empty when there are no more.
    /// A restart reads the whole day page by page; the journal itself is never trimmed.
    /// </summary>
    public async Task<IReadOnlyList<(long Id, JournaledInput Input)>> ReadJournalPageAsync(DateOnly tradeDate, long afterId, int limit, CancellationToken ct)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var c = db.CreateCommand($"SELECT id, payload_json FROM {S}.pev2_execution_journal WHERE trade_date = $1 AND id > $2 ORDER BY id LIMIT $3");
        c.Parameters.AddWithValue(tradeDate.ToString("yyyy-MM-dd")); c.Parameters.AddWithValue(afterId); c.Parameters.AddWithValue(limit);
        var list = new List<(long, JournaledInput)>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetInt64(0), JournaledInput.Deserialize(r.GetString(1))));
        return list;
    }

    public async Task CommitAsync(ExecutionOwnership writer, ExecutionInput input, IReadOnlyList<PaperEvent> events, IReadOnlyList<(string AlertId, string AlertClass, PaperEvent Event, string Text)> alerts, CancellationToken ct)
    {
        var epoch = writer.Epoch ?? throw new OwnershipLostException("this writer holds no execution epoch");
        await writer.WriteAsync(async (connection, tx) =>
        {
            await using var batch = new NpgsqlBatch(connection, tx);
            var journal = new NpgsqlBatchCommand($"INSERT INTO {S}.pev2_execution_journal (trade_date, epoch, at_utc_ticks, payload_json) VALUES ($1,$2,$3,$4)");
            journal.Parameters.AddWithValue(input.TradeDate.ToString("yyyy-MM-dd")); journal.Parameters.AddWithValue(epoch); journal.Parameters.AddWithValue(input.Now.UtcTicks); journal.Parameters.AddWithValue(JournaledInput.Serialize(input));
            batch.BatchCommands.Add(journal);
            foreach (var e in events)
            {
                // a plain INSERT: an event id that already exists means the state diverged from the ledger, and the whole step must fail visibly
                var c = new NpgsqlBatchCommand($"INSERT INTO {S}.pev2_events (event_id, strategy_id, config_hash, kind, bar_seq, at_utc_ticks, payload_json) VALUES ($1,$2,$3,$4,$5,$6,$7)");
                c.Parameters.AddWithValue(e.EventId); c.Parameters.AddWithValue(e.StrategyId); c.Parameters.AddWithValue(e.ConfigHash); c.Parameters.AddWithValue(e.Kind);
                c.Parameters.AddWithValue(e.BarSeq); c.Parameters.AddWithValue(e.At.UtcTicks); c.Parameters.AddWithValue(EventSerializer.Serialize(e));
                batch.BatchCommands.Add(c);
            }

            foreach (var (alertId, alertClass, e, text) in alerts)
            {
                var c = new NpgsqlBatchCommand($"INSERT INTO {S}.pev2_alert_outbox (alert_id, alert_class, strategy_id, event_id, message_text, status, attempts, created_utc_ticks, next_attempt_utc_ticks) VALUES ($1,$2,$3,$4,$5,0,0,$6,$6) ON CONFLICT (alert_id) DO NOTHING");
                c.Parameters.AddWithValue(alertId); c.Parameters.AddWithValue(alertClass); c.Parameters.AddWithValue(e.StrategyId); c.Parameters.AddWithValue(e.EventId); c.Parameters.AddWithValue(text); c.Parameters.AddWithValue(e.At.UtcTicks);
                batch.BatchCommands.Add(c);
            }

            await batch.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);
    }
}
