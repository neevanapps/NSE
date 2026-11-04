using Npgsql;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.PaperEngineV2.Data;

/// <summary>Whether the optional observability tables (added by the owner-run AddObservability script) exist. Reads the catalog only; never creates anything.</summary>
public static class ObservabilitySchema
{
    public static async Task<bool> HasAsync(NpgsqlDataSource db, string table, CancellationToken ct)
    {
        try
        {
            await using var cmd = db.CreateCommand("SELECT to_regclass($1) IS NOT NULL");
            cmd.Parameters.AddWithValue($"{V2Schema.SchemaName}.{table}");
            return (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Append-only, idempotent persistence of the opportunity log (INSERT ... ON CONFLICT DO NOTHING on the deterministic id). It is OBSERVATION ONLY: the engine calls it after a bar is finished and contains every failure.
/// A missing table or a permission problem disables it after the FIRST failure (so a missing migration costs one failed statement, not one per bar) and is reported through <see cref="DisabledReason"/>;
/// a transient failure is counted and the next row is tried again.
/// </summary>
public sealed class PostgresOpportunityStore(NpgsqlDataSource db, TimeProvider clock) : IOpportunitySink
{
    const string S = V2Schema.SchemaName;
    long _failures, _written, _duplicates;
    volatile string? _disabled;

    public long Failures => Interlocked.Read(ref _failures);

    public long Written => Interlocked.Read(ref _written);

    public long Duplicates => Interlocked.Read(ref _duplicates);

    public string? DisabledReason => _disabled;

    public void Append(OpportunityRecord r)
    {
        if (_disabled is not null) return;
        try
        {
            using var cmd = db.CreateCommand($"INSERT INTO {S}.pev2_opportunities (opportunity_id, strategy_id, version, config_hash, session_id, trade_date, bar_seq, stage, resolved_bar_seq, decision_utc_ticks, direction, category, category_name, reason, detail, in_position, order_eligible, order_attempted, order_result, intent_id, token, event_ref, frame_ref, entries_allowed, coverage, recorded_utc_ticks) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25,$26) ON CONFLICT (opportunity_id) DO NOTHING");
            var p = cmd.Parameters;
            p.AddWithValue(r.OpportunityId); p.AddWithValue(r.StrategyId); p.AddWithValue(r.Version); p.AddWithValue(r.ConfigHash); p.AddWithValue(r.SessionId); p.AddWithValue(r.TradeDate.ToString("yyyy-MM-dd"));
            p.AddWithValue(r.BarSeq); p.AddWithValue(r.Stage.ToString()); p.AddWithValue(r.ResolvedBarSeq); p.AddWithValue(r.DecisionAt.UtcTicks); p.AddWithValue(r.Direction); p.AddWithValue((int)r.Category);
            p.AddWithValue(r.Category.ToString()); p.AddWithValue(r.Reason); p.AddWithValue(r.Detail); p.AddWithValue(r.InPosition); p.AddWithValue(r.OrderEligible); p.AddWithValue(r.OrderAttempted);
            p.AddWithValue(r.OrderResult); p.AddWithValue((object?)r.IntentId ?? DBNull.Value); p.AddWithValue((object?)r.Token ?? DBNull.Value); p.AddWithValue((object?)r.EventRef ?? DBNull.Value);
            p.AddWithValue(r.FrameRef); p.AddWithValue(r.EntriesAllowed); p.AddWithValue(r.Coverage); p.AddWithValue(clock.GetUtcNow().UtcTicks);
            if (cmd.ExecuteNonQuery() == 1) Interlocked.Increment(ref _written); else Interlocked.Increment(ref _duplicates);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName or PostgresErrorCodes.InsufficientPrivilege)
        {
            _disabled = ex.SqlState == PostgresErrorCodes.InsufficientPrivilege ? "no INSERT permission on pev2.pev2_opportunities" : "pev2.pev2_opportunities does not exist (the owner has not run the AddObservability script)";
            Interlocked.Increment(ref _failures);
            throw;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failures);
            throw;
        }
    }
}
