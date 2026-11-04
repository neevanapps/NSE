using Npgsql;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.PaperEngineV2.Runtime;

/// <summary>
/// Upserts live marks into <c>pev2.pev2_live_marks</c> (one NpgsqlBatch per sample, so one round trip per second). A missing table or a missing INSERT/UPDATE permission disables the store after the FIRST failure
/// (reported through <see cref="DisabledReason"/>, never thrown again), so a missing owner-run script costs one failed statement, not one per second. Other failures are thrown to the publisher, which contains them.
/// </summary>
public sealed class PostgresLiveMarkStore(NpgsqlDataSource db) : ILiveMarkStore
{
    const string S = V2Schema.SchemaName;
    public const string PublisherRow = "_publisher";
    volatile string? _disabled;

    public string? DisabledReason => _disabled;

    public async Task PublishAsync(IReadOnlyList<LiveMark> changed, MarkHeartbeat heartbeat, CancellationToken ct)
    {
        if (_disabled is not null) return;
        try
        {
            await using var batch = db.CreateBatch();
            foreach (var m in changed) batch.BatchCommands.Add(Upsert(m.StrategyId, m.PositionId, m.TradeDate.ToString("yyyy-MM-dd"), m.MarkSeq, m.Status.ToString(), LiveMarkJson.Serialize(m), heartbeat.At));
            var day = DateOnly.FromDateTime(heartbeat.At.UtcDateTime.AddHours(5.5)).ToString("yyyy-MM-dd");   // the heartbeat belongs to the IST day of the publisher clock, whatever the rows are
            batch.BatchCommands.Add(Upsert("*", PublisherRow, day, heartbeat.Seq, "Fresh", System.Text.Json.JsonSerializer.Serialize(heartbeat), heartbeat.At));
            await batch.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName or PostgresErrorCodes.InsufficientPrivilege)
        {
            _disabled = ex.SqlState == PostgresErrorCodes.InsufficientPrivilege ? "no INSERT/UPDATE permission on pev2.pev2_live_marks" : "pev2.pev2_live_marks does not exist (the owner has not run the AddObservability script)";
            throw;
        }
    }

    static NpgsqlBatchCommand Upsert(string strategy, string position, string day, long seq, string status, string payload, DateTimeOffset at)
    {
        var c = new NpgsqlBatchCommand($"INSERT INTO {S}.pev2_live_marks (strategy_id, position_id, trade_date, mark_seq, status, payload_json, published_utc_ticks) VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT (strategy_id, position_id) DO UPDATE SET trade_date = EXCLUDED.trade_date, mark_seq = EXCLUDED.mark_seq, status = EXCLUDED.status, payload_json = EXCLUDED.payload_json, published_utc_ticks = EXCLUDED.published_utc_ticks");
        c.Parameters.AddWithValue(strategy); c.Parameters.AddWithValue(position); c.Parameters.AddWithValue(day); c.Parameters.AddWithValue(seq); c.Parameters.AddWithValue(status); c.Parameters.AddWithValue(payload); c.Parameters.AddWithValue(at.UtcTicks);
        return c;
    }
}
