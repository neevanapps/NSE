using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Data;

/// <summary>Where in a write the test seam <see cref="PostgresRunStore.FaultHook"/> is called.</summary>
public enum StoreFaultPoint
{
    /// <summary>After the fence check, before the transaction starts: nothing was applied.</summary>
    BeforeApply = 0,

    /// <summary>All statements executed, the commit not yet issued: throwing here rolls everything back.</summary>
    DuringApply = 1,

    /// <summary>After the commit succeeded and before the caller learns about it.</summary>
    AfterCommit = 2,
}

/// <summary>
/// The PostgreSQL run store (contract section A.19.5): <see cref="IAsyncRunStore"/> on <see cref="TradingDbContext"/>. Properties:
/// <list type="bullet">
/// <item><b>One transaction per write.</b> A step, its outputs, its control applications, the typed ownership rows (orders, positions, fills, ledger, audit,
/// rejections), the risk-ledger cache, the checkpoint and the final record are committed together or not at all.</item>
/// <item><b>Idempotent with equality checks.</b> Re-applying the identical step returns <see cref="CommitResult.AlreadyCommitted"/>; any difference under the same
/// deterministic identity is a <see cref="ConflictFaultException"/> and nothing is overwritten.</item>
/// <item><b>Single writer.</b> <c>Acquire</c> takes a session-level advisory lock on a dedicated connection (it vanishes with the process or the connection) and bumps
/// the epoch in <c>run_owner</c>. Every write re-reads the epoch <c>FOR UPDATE</c> inside its own transaction, so a writer that lost ownership cannot change anything.</item>
/// <item><b>Unknown outcomes.</b> A failure before the commit statement is a <see cref="StoreUnavailableException"/> (not applied); a failure of the commit statement itself,
/// or cancellation once the write was submitted, is an <see cref="UnknownCommitOutcomeException"/> that the caller resolves by identity.</item>
/// <item><b>Compatibility.</b> The first operation checks the schema version row; a database newer or older than this code is refused with a visible halt.</item>
/// </list>
/// The store never creates a database, never migrates and never reads or writes outside the <c>trading</c> schema.
/// </summary>
public sealed class PostgresRunStore : IAsyncRunStore, IAsyncDisposable
{
    private sealed record Held(NpgsqlConnection? Connection);

    private readonly string _connectionString;
    private readonly DbContextOptions<TradingDbContext> _options;
    private readonly ConcurrentDictionary<(string RunId, long Epoch), Held> _held = new();
    private readonly SemaphoreSlim _compatGate = new(1, 1);
    private bool _compatible;

    public PostgresRunStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", TradingDbContext.SchemaName))
            .Options;
    }

    /// <summary>Test seam: called at the points of <see cref="StoreFaultPoint"/> in every write; a throw models a failure at that point.</summary>
    public Action<StoreOperation, StoreFaultPoint>? FaultHook { get; set; }

    /// <summary>The application name of the connection that holds the advisory lock of a run: lets an operator (or a test) see and end the owning session.</summary>
    public static string LockApplicationName(string runId, string ownerId) => Truncate($"nse-trading-lock:{runId}:{ownerId}", 63);

    public static long AdvisoryKey(string runId) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("nse-trading-run:" + runId)), 0);

    // ------------------------------------------------------------------ ownership

    public async Task<OwnershipToken> AcquireAsync(string runId, string ownerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureCompatibleAsync(cancellationToken).ConfigureAwait(false);
        var builder = new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false, ApplicationName = LockApplicationName(runId, ownerId), KeepAlive = 15 };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection))
            {
                lockCommand.Parameters.AddWithValue("key", AdvisoryKey(runId));
                if (!(bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                {
                    throw new OwnershipConflictException(runId, await HolderOfAsync(runId, cancellationToken).ConfigureAwait(false) ?? "another session");
                }
            }

            long epoch;
            await using (var bump = new NpgsqlCommand(
                "INSERT INTO trading.run_owner (run_id, epoch, owner_id, acquired_at_utc) VALUES (@r, 1, @o, now()) " +
                "ON CONFLICT (run_id) DO UPDATE SET epoch = trading.run_owner.epoch + 1, owner_id = EXCLUDED.owner_id, acquired_at_utc = now() RETURNING epoch",
                connection))
            {
                bump.Parameters.AddWithValue("r", runId);
                bump.Parameters.AddWithValue("o", ownerId);
                epoch = (long)(await bump.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            _held[(runId, epoch)] = new Held(connection);
            return new OwnershipToken(runId, ownerId, epoch);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Operator action after a lease expiry: ends the session of the current holder (its advisory lock disappears with it), then acquires the run with a higher epoch.
    /// The previous holder's token is fenced out and it can change nothing any more, even if its process is still alive.
    /// </summary>
    public async Task<OwnershipToken> TakeOverAsync(string runId, string newOwnerId, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        await EnsureCompatibleAsync(cancellationToken).ConfigureAwait(false);
        await TerminateLockSessionsAsync(runId, cancellationToken).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            try
            {
                return await AcquireAsync(runId, newOwnerId, cancellationToken).ConfigureAwait(false);
            }
            catch (OwnershipConflictException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Ends every session that holds (or is waiting for) the run's lock, by its application name. Used by a takeover and by crash tests.</summary>
    public async Task TerminateLockSessionsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND application_name LIKE @p", connection);
        command.Parameters.AddWithValue("p", EscapeLike(Truncate($"nse-trading-lock:{runId}:", 63)) + "%");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // the lock is released when the backend has actually exited: wait for that, not only for the signal
        for (var i = 0; i < 200; i++)
        {
            await using var check = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND application_name LIKE @p", connection);
            check.Parameters.AddWithValue("p", EscapeLike(Truncate($"nse-trading-lock:{runId}:", 63)) + "%");
            if ((long)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 0)
            {
                return;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ReleaseAsync(OwnershipToken token, CancellationToken cancellationToken = default)
    {
        if (_held.TryRemove((token.RunId, token.Epoch), out var held) && held.Connection is { } connection)
        {
            try
            {
                if (connection.State == ConnectionState.Open)
                {
                    await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                    unlock.Parameters.AddWithValue("key", AdvisoryKey(token.RunId));
                    await unlock.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is NpgsqlException or IOException or InvalidOperationException)
            {
                // the session is gone already: so is the lock
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // ------------------------------------------------------------------ reads

    public async Task<RunSnapshot> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureCompatibleAsync(cancellationToken).ConfigureAwait(false);
        await using var db = NewContext();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);       // one consistent snapshot of the whole run

        var run = await db.Runs.Include(r => r.Variants).AsSplitQuery().SingleOrDefaultAsync(r => r.RunId == runId, cancellationToken).ConfigureAwait(false);
        var controls = await db.ControlJournal.Where(c => c.RunId == runId).OrderBy(c => c.JournalSeq).ToListAsync(cancellationToken).ConfigureAwait(false);
        var steps = await db.Steps.Include(s => s.Digests).AsSplitQuery().Where(s => s.RunId == runId).OrderBy(s => s.StepSeq).ToListAsync(cancellationToken).ConfigureAwait(false);
        var applications = await db.ControlApplications.Where(a => a.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var outputs = await db.Outputs.Where(o => o.RunId == runId).OrderBy(o => o.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
        var inputs = await db.Inputs.Where(i => i.RunId == runId).OrderBy(i => i.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
        var checkpoints = await db.Checkpoints.Where(c => c.RunId == runId).OrderBy(c => c.StepSeq).ToListAsync(cancellationToken).ConfigureAwait(false);
        var finalRecord = await db.FinalRecords.SingleOrDefaultAsync(f => f.RunId == runId, cancellationToken).ConfigureAwait(false);
        var finalLines = finalRecord is null ? new List<FinalLineEntity>() : await db.FinalLines.Where(l => l.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var log = await db.RecoveryLog.Where(l => l.RunId == runId).OrderBy(l => l.LogSeq).ToListAsync(cancellationToken).ConfigureAwait(false);

        var byStep = applications.GroupBy(a => a.StepSeq).ToDictionary(g => g.Key, g => g.ToList());
        var outputRecords = outputs.Select(EntityMapper.ToRecord).ToList();
        return new RunSnapshot(
            run is null ? null : EntityMapper.ToRecord(run),
            controls.Select(EntityMapper.ToRecord).ToList(),
            steps.Select(s => EntityMapper.ToRecord(s, byStep.GetValueOrDefault(s.StepSeq) ?? new List<ControlApplicationEntity>())).ToList(),
            outputRecords,
            checkpoints.Select(EntityMapper.ToRecord).ToList(),
            ProjectionBuilder.Build(outputRecords),             // the generic projections are a pure function of the outputs; the TYPED tables are checked against the outputs by VerifyDerivedAsync
            finalRecord is null ? null : EntityMapper.ToRecord(finalRecord, finalLines),
            run?.Completed ?? false,
            log.Select(EntityMapper.ToRecord).ToList())
        {
            Inputs = inputs.Select(EntityMapper.ToRecord).ToList(),
        };
    }

    // ------------------------------------------------------------------ writes

    public Task<AppendResult> CreateRunAsync(OwnershipToken token, RunHeader header, CancellationToken cancellationToken = default)
        => WriteAsync(token, StoreOperation.CreateRun, cancellationToken, async (db, _) =>
        {
            if (!header.HashIsValid)
            {
                throw new ConflictFaultException("The header's content hash does not match its content.");
            }

            var existing = await db.Runs.AsNoTracking().Where(r => r.RunId == header.RunId).Select(r => r.ContentHash).SingleOrDefaultAsync().ConfigureAwait(false);
            if (existing is not null)
            {
                return existing == header.ContentHash ? AppendResult.Duplicate : throw new ConflictFaultException($"Run '{header.RunId}' already exists with a different header.");
            }

            if (header.RunId != token.RunId)
            {
                throw new ConflictFaultException($"The header is for run '{header.RunId}' but ownership is of run '{token.RunId}'.");
            }

            foreach (var account in header.Variants.Select(v => v.AccountId).Distinct(StringComparer.Ordinal))
            {
                db.Accounts.Add(EntityMapper.AccountOf(header.RunId, new VariantIdentity(string.Empty, string.Empty, string.Empty, string.Empty, account)));
            }

            db.Runs.Add(EntityMapper.ToEntity(header));
            await db.SaveChangesAsync().ConfigureAwait(false);
            return AppendResult.Appended;
        });

    public Task<AppendResult> AppendControlAsync(OwnershipToken token, ControlRecord record, CancellationToken cancellationToken = default)
        => WriteAsync(token, StoreOperation.AppendControl, cancellationToken, async (db, _) =>
        {
            if (!record.HashIsValid)
            {
                throw new ConflictFaultException($"Control '{record.Id}' has an invalid content hash.");
            }

            if (!await db.Runs.AnyAsync(r => r.RunId == token.RunId).ConfigureAwait(false))
            {
                throw new ConflictFaultException("The run has no header.");
            }

            var existing = await db.ControlJournal.AsNoTracking().Where(c => c.RunId == token.RunId && c.ControlId == record.Id).Select(c => c.ContentHash).SingleOrDefaultAsync().ConfigureAwait(false);
            if (existing is not null)
            {
                return existing == record.ContentHash ? AppendResult.Duplicate : throw new ConflictFaultException($"Control '{record.Id}' already exists with different content.");
            }

            var next = (await db.ControlJournal.Where(c => c.RunId == token.RunId).MaxAsync(c => (long?)c.JournalSeq).ConfigureAwait(false) ?? 0) + 1;
            db.ControlJournal.Add(EntityMapper.ToEntity(token.RunId, record, next));
            await db.SaveChangesAsync().ConfigureAwait(false);
            return AppendResult.Appended;
        });

    public async Task AppendRecoveryLogAsync(OwnershipToken token, RecoveryLogEntry entry, CancellationToken cancellationToken = default)
        => await WriteAsync(token, null, cancellationToken, async (db, _) =>
        {
            if (!await db.Runs.AnyAsync(r => r.RunId == token.RunId).ConfigureAwait(false))
            {
                throw new ConflictFaultException("The run has no header.");
            }

            var next = (await db.RecoveryLog.Where(l => l.RunId == token.RunId).MaxAsync(l => (long?)l.LogSeq).ConfigureAwait(false) ?? 0) + 1;
            db.RecoveryLog.Add(EntityMapper.ToEntity(token.RunId, entry, next, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync().ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);

    public Task<CommitResult> CommitAsync(OwnershipToken token, CommitBatch batch, CancellationToken cancellationToken = default)
        => WriteAsync(token, StoreOperation.Commit, cancellationToken, (db, _) => CommitCoreAsync(db, token, batch));

    private static async Task<CommitResult> CommitCoreAsync(TradingDbContext db, OwnershipToken token, CommitBatch batch)
    {
        var runId = token.RunId;
        var step = batch.Step;
        var run = await db.Runs.SingleOrDefaultAsync(r => r.RunId == runId).ConfigureAwait(false) ?? throw new ConflictFaultException("The run has no header.");
        ValidateStateless(batch);

        var stepCount = await db.Steps.Where(s => s.RunId == runId).CountAsync().ConfigureAwait(false);
        if (step.StepSeq > stepCount)
        {
            throw new SequenceGapException($"Step {step.StepSeq} would leave a gap after step {stepCount - 1}.");
        }

        if (step.StepSeq < stepCount)
        {
            await VerifyIdenticalAsync(db, runId, batch).ConfigureAwait(false);
            return CommitResult.AlreadyCommitted;
        }

        var outputCount = await db.Outputs.Where(o => o.RunId == runId).CountAsync().ConfigureAwait(false);
        if (step.OutputFrom != outputCount)
        {
            throw new SequenceGapException($"Step {step.StepSeq} starts at output {step.OutputFrom} but {outputCount} outputs are durable.");
        }

        if (batch.Inputs.Count > 0)
        {
            var inputCount = await db.Inputs.Where(i => i.RunId == runId).CountAsync().ConfigureAwait(false);
            if (batch.Inputs[0].Seq != inputCount)
            {
                throw new SequenceGapException($"Step {step.StepSeq} starts at input {batch.Inputs[0].Seq} but {inputCount} inputs are durable.");
            }
        }

        if (step.AppliedControls.Count > 0)
        {
            var journaled = await db.ControlJournal.Where(c => c.RunId == runId && step.AppliedControls.Contains(c.ControlId)).Select(c => c.ControlId).ToListAsync().ConfigureAwait(false);
            var missing = step.AppliedControls.FirstOrDefault(id => !journaled.Contains(id, StringComparer.Ordinal));
            if (missing is not null)
            {
                throw new ConflictFaultException($"Step {step.StepSeq} lists control '{missing}' as applied, but it was never journaled.");
            }
        }

        db.Steps.Add(EntityMapper.ToEntity(runId, step));
        db.ControlApplications.AddRange(EntityMapper.ApplicationsOf(runId, step));
        db.Outputs.AddRange(batch.Outputs.Select(o => EntityMapper.ToEntity(runId, o)));
        db.Inputs.AddRange(batch.Inputs.Select(i => EntityMapper.ToEntity(runId, i)));
        await db.SaveChangesAsync().ConfigureAwait(false);          // outputs and the step first: every typed row below points at an output by a foreign key

        var variants = await db.Variants.AsNoTracking().Where(v => v.RunId == runId).ToListAsync().ConfigureAwait(false);
        var identities = variants.Select(v => new VariantIdentity(v.VariantId, v.VariantHash, v.StrategyId, v.StrategyVersion, v.AccountId)).ToList();
        var slots = await EntrySlotsAsync(db, runId, batch.Outputs).ConfigureAwait(false);
        var mapped = ProjectionMapper.Map(runId, batch.Outputs, identities, id => slots.TryGetValue(id, out var s) ? s : null);

        // A step may close a position and open the next one in the same slot (a bar-clocked reversal: the exit and the opposite entry fill on the same completed bar). The partial unique index allows ONE open
        // position per slot, so the closures of positions that already exist are written FIRST; only then do this step's new positions take the slot.
        var openedInThisStep = mapped.Positions.Select(p => p.PositionId).ToHashSet(StringComparer.Ordinal);
        foreach (var closure in mapped.Closures.Where(c => !openedInThisStep.Contains(c.PositionId)))
        {
            var existing = await db.Positions.SingleOrDefaultAsync(p => p.RunId == runId && p.PositionId == closure.PositionId).ConfigureAwait(false)
                           ?? throw new ConflictFaultException($"Output {closure.ClosedOutputSeq} closes position '{closure.PositionId}', which does not exist.");
            if (existing.ClosedOutputSeq is not null)
            {
                throw new ConflictFaultException($"Position '{closure.PositionId}' is already closed at output {existing.ClosedOutputSeq}.");
            }

            ProjectionMapper.ApplyClosure(existing, closure);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        db.Orders.AddRange(mapped.Orders);
        db.Positions.AddRange(mapped.Positions);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // the risk-ledger cache of this step: the previous step's snapshot plus this step's outputs
        var previousRisk = await db.RiskCheckpoints.AsNoTracking().Where(r => r.RunId == runId).OrderByDescending(r => r.StepSeq).FirstOrDefaultAsync().ConfigureAwait(false);
        var ledger = previousRisk is null ? new RiskLedger() : RiskLedger.FromText(previousRisk.CountersText);
        if (ledger.LastOutputSeq != step.OutputFrom - 1)
        {
            throw new ConflictFaultException($"The risk-ledger cache ends at output {ledger.LastOutputSeq} but step {step.StepSeq} starts at output {step.OutputFrom}.");
        }

        foreach (var o in batch.Outputs)
        {
            ledger.Apply(o.Seq, OutputCodec.Decode(o.Payload));
        }

        db.RiskCheckpoints.Add(new RiskCheckpointEntity { RunId = runId, StepSeq = step.StepSeq, SourceOutputSeq = ledger.LastOutputSeq, CountersText = ledger.ToText(), CountersHash = ledger.Hash() });

        foreach (var closure in mapped.Closures.Where(c => openedInThisStep.Contains(c.PositionId)))
        {
            var position = db.Positions.Local.FirstOrDefault(p => p.RunId == runId && p.PositionId == closure.PositionId)
                           ?? await db.Positions.SingleOrDefaultAsync(p => p.RunId == runId && p.PositionId == closure.PositionId).ConfigureAwait(false)
                           ?? throw new ConflictFaultException($"Output {closure.ClosedOutputSeq} closes position '{closure.PositionId}', which does not exist.");
            if (position.ClosedOutputSeq is not null)
            {
                throw new ConflictFaultException($"Position '{closure.PositionId}' is already closed at output {position.ClosedOutputSeq}.");
            }

            ProjectionMapper.ApplyClosure(position, closure);
        }

        db.Fills.AddRange(mapped.Fills);
        db.AuditEntries.AddRange(mapped.Audit);
        db.Rejections.AddRange(mapped.Rejections);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.LedgerEntries.AddRange(mapped.Ledger);

        if (batch.Checkpoint is { } checkpoint)
        {
            db.Checkpoints.Add(EntityMapper.ToEntity(runId, checkpoint));
        }

        foreach (var snapshot in batch.PositionSnapshots)
        {
            var entity = await db.PositionSnapshots.SingleOrDefaultAsync(r => r.RunId == runId && r.PositionId == snapshot.PositionId).ConfigureAwait(false);
            if (entity is null)
            {
                db.PositionSnapshots.Add(EntityMapper.ToEntity(runId, snapshot, step.StepSeq, step.OutputToExclusive - 1));
            }
            else
            {
                EntityMapper.Apply(entity, snapshot, step.StepSeq, step.OutputToExclusive - 1);
            }
        }

        if (batch.Final is { } final)
        {
            var (record, lines) = EntityMapper.ToEntity(runId, final);
            db.FinalRecords.Add(record);
            db.FinalLines.AddRange(lines);
            run.Completed = true;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return CommitResult.Committed;
    }

    private static void ValidateStateless(CommitBatch batch)
    {
        var step = batch.Step;
        if (!step.HashIsValid)
        {
            throw new ConflictFaultException($"Step {step.StepSeq}: content hash does not match its content.");
        }

        if (batch.Outputs.Count != step.OutputToExclusive - step.OutputFrom)
        {
            throw new ConflictFaultException($"Step {step.StepSeq}: output range [{step.OutputFrom},{step.OutputToExclusive}) disagrees with {batch.Outputs.Count} outputs.");
        }

        for (var i = 0; i < batch.Outputs.Count; i++)
        {
            var o = batch.Outputs[i];
            if (!o.HashIsValid)
            {
                throw new ConflictFaultException($"Output {o.Seq}: content hash does not match its payload.");
            }

            if (o.Seq != step.OutputFrom + i || o.StepSeq != step.StepSeq)
            {
                throw new SequenceGapException($"Output {o.Seq} is out of sequence in step {step.StepSeq}.");
            }
        }

        for (var i = 0; i < batch.Inputs.Count; i++)
        {
            var input = batch.Inputs[i];
            if (!input.HashIsValid)
            {
                throw new ConflictFaultException($"Input {input.Seq}: content hash does not match its content.");
            }

            if (input.StepSeq != step.StepSeq || (i > 0 && input.Seq != batch.Inputs[i - 1].Seq + 1))
            {
                throw new SequenceGapException($"Input {input.Seq} is out of sequence in step {step.StepSeq}.");
            }
        }

        if (batch.Projections.Any(p => !p.HashIsValid))
        {
            throw new ConflictFaultException($"Step {step.StepSeq}: a projection row's hash does not match its payload.");
        }

        // the generic projection rows are derived data: a batch that carries rows other than the ones its own outputs derive is refused
        var derived = ProjectionBuilder.Build(batch.Outputs);
        if (derived.Count != batch.Projections.Count || derived.Zip(batch.Projections).Any(p => p.First.Table != p.Second.Table || p.First.Key != p.Second.Key || p.First.ContentHash != p.Second.ContentHash))
        {
            throw new ConflictFaultException($"Step {step.StepSeq}: the projection rows are not the ones derived from its outputs.");
        }

        if (batch.Checkpoint is { } cp && cp.StepSeq != step.StepSeq)
        {
            throw new ConflictFaultException("The checkpoint belongs to a different step.");
        }

        if (batch.Final is { } final && (step.Kind != Adapters.UnitKind.Finish || !final.HashIsValid))
        {
            throw new ConflictFaultException("A final record is only valid on a Finish step with a matching hash.");
        }
    }

    private static async Task VerifyIdenticalAsync(TradingDbContext db, string runId, CommitBatch batch)
    {
        var step = batch.Step;
        var existing = await db.Steps.AsNoTracking().Where(s => s.RunId == runId && s.StepSeq == step.StepSeq).Select(s => s.ContentHash).SingleAsync().ConfigureAwait(false);
        if (existing != step.ContentHash)
        {
            throw new ConflictFaultException($"Step {step.StepSeq} already exists with different content.");
        }

        var durable = await db.Outputs.AsNoTracking().Where(o => o.RunId == runId && o.Seq >= step.OutputFrom && o.Seq < step.OutputToExclusive).OrderBy(o => o.Seq).Select(o => new { o.Seq, o.ContentHash }).ToListAsync().ConfigureAwait(false);
        if (durable.Count != batch.Outputs.Count || durable.Zip(batch.Outputs).Any(p => p.First.Seq != p.Second.Seq || p.First.ContentHash != p.Second.ContentHash))
        {
            throw new ConflictFaultException($"The outputs of step {step.StepSeq} already exist with different content.");
        }

        if (batch.Inputs.Count > 0)
        {
            var first = batch.Inputs[0].Seq;
            var last = batch.Inputs[^1].Seq;
            var durableInputs = await db.Inputs.AsNoTracking().Where(i => i.RunId == runId && i.Seq >= first && i.Seq <= last).OrderBy(i => i.Seq).Select(i => new { i.Seq, i.ContentHash }).ToListAsync().ConfigureAwait(false);
            if (durableInputs.Count != batch.Inputs.Count || durableInputs.Zip(batch.Inputs).Any(p => p.First.Seq != p.Second.Seq || p.First.ContentHash != p.Second.ContentHash))
            {
                throw new ConflictFaultException($"The inputs of step {step.StepSeq} already exist with different content.");
            }
        }

        if (batch.Checkpoint is { } cp && !await db.Checkpoints.AsNoTracking().AnyAsync(c => c.RunId == runId && c.StepSeq == cp.StepSeq && c.StateDigest == cp.StateDigest).ConfigureAwait(false))
        {
            throw new ConflictFaultException($"The checkpoint of step {step.StepSeq} differs from the durable one.");
        }

        if (batch.Final is { } final && !await db.FinalRecords.AsNoTracking().AnyAsync(f => f.RunId == runId && f.ContentHash == final.ContentHash).ConfigureAwait(false))
        {
            throw new ConflictFaultException("The final record differs from the durable one.");
        }
    }

    /// <summary>The slots reserved by entry orders created in EARLIER steps, for the entry fills of this batch (an order and the position it opens may be committed in different steps).</summary>
    private static async Task<Dictionary<string, int>> EntrySlotsAsync(TradingDbContext db, string runId, IReadOnlyList<OutputRecord> outputs)
    {
        var wanted = outputs.Where(o => o.Type == nameof(FillRecord)).Select(o => OutputCodec.Decode(o.Payload)).OfType<FillRecord>().Where(f => f.Side == TradeSide.Buy).Select(f => f.OrderId).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var rows = await db.Orders.AsNoTracking().Where(o => o.RunId == runId && o.Kind == "Entry" && wanted.Contains(o.OrderId)).Select(o => new { o.OrderId, o.SlotNo }).ToListAsync().ConfigureAwait(false);
        return rows.Where(r => r.SlotNo is not null).ToDictionary(r => r.OrderId, r => r.SlotNo!.Value, StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ derived-data verification

    /// <summary>
    /// Re-derives everything that is derived from the outputs and compares it with what the database holds: the typed rows (positions with their close columns, orders, fills,
    /// ledger entries, audit entries, rejections) and the risk-ledger cache (folded from every output, compared with the last snapshot and with every snapshot's own hash).
    /// Returns the problems found (empty = consistent). The outputs are the only ledger of record; a disagreement means a lost, altered or misordered write.
    /// </summary>
    public async Task<IReadOnlyList<string>> VerifyDerivedAsync(string runId, CancellationToken cancellationToken = default)
    {
        await EnsureCompatibleAsync(cancellationToken).ConfigureAwait(false);
        await using var db = NewContext();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();
        var variants = await db.Variants.Where(v => v.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var outputs = (await db.Outputs.Where(o => o.RunId == runId).OrderBy(o => o.Seq).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(EntityMapper.ToRecord).ToList();
        for (var i = 0; i < outputs.Count; i++)
        {
            if (outputs[i].Seq != i || !outputs[i].HashIsValid)
            {
                problems.Add($"output {i}: sequence or hash mismatch");
                return problems;
            }
        }

        var identities = variants.Select(v => new VariantIdentity(v.VariantId, v.VariantHash, v.StrategyId, v.StrategyVersion, v.AccountId)).ToList();
        var mapped = ProjectionMapper.Map(runId, outputs, identities);
        var expectedPositions = mapped.Positions.ToDictionary(p => p.PositionId, StringComparer.Ordinal);
        foreach (var closure in mapped.Closures)
        {
            ProjectionMapper.ApplyClosure(expectedPositions[closure.PositionId], closure);
        }

        Compare("positions", expectedPositions.Values, await db.Positions.Where(r => r.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false), r => r.PositionId, problems);
        Compare("orders", mapped.Orders, await db.Orders.Where(r => r.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false), r => r.OrderId, problems);
        Compare("fills", mapped.Fills, await db.Fills.Where(r => r.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false), r => r.FillId, problems);
        Compare("ledger_entries", mapped.Ledger, await db.LedgerEntries.Where(r => r.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false), r => r.FillId + "/" + r.Component, problems);
        Compare("audit_entries", mapped.Audit, await db.AuditEntries.Where(r => r.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false), r => r.OutputSeq.ToString(CultureInfo.InvariantCulture), problems);
        Compare("rejections", mapped.Rejections, await db.Rejections.Where(r => r.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false), r => r.OutputSeq.ToString(CultureInfo.InvariantCulture), problems);

        var risk = await db.RiskCheckpoints.Where(r => r.RunId == runId).OrderBy(r => r.StepSeq).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var checkpoint in risk.Where(r => r.CountersHash != OutputCodec.ContentHash(r.CountersText)))
        {
            problems.Add($"risk_checkpoints step {checkpoint.StepSeq}: hash does not match its text");
        }

        var folded = RiskLedger.Fold(outputs);
        if (risk.Count == 0)
        {
            if (outputs.Count > 0)
            {
                problems.Add("risk_checkpoints: no snapshot although outputs exist");
            }
        }
        else if (risk[^1].CountersText != folded.ToText())
        {
            problems.Add($"risk_checkpoints: the last snapshot (step {risk[^1].StepSeq}) disagrees with the risk ledger folded from the outputs");
        }

        return problems;
    }

    /// <summary>Like <see cref="VerifyDerivedAsync"/> but a disagreement halts: recovery must not trade on a ledger whose derived data disagrees with its outputs.</summary>
    public async Task RequireDerivedConsistentAsync(string runId, CancellationToken cancellationToken = default)
    {
        var problems = await VerifyDerivedAsync(runId, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            throw new RecoveryHaltException(HaltKind.CorruptLedger, "The derived trading data disagrees with the durable outputs; nothing was resumed.", problems);
        }
    }

    private static void Compare<T>(string table, IEnumerable<T> expected, IEnumerable<T> actual, Func<T, string> key, List<string> problems)
    {
        var properties = typeof(T).GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && (p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType == typeof(decimal) || p.PropertyType == typeof(DateOnly) || Nullable.GetUnderlyingType(p.PropertyType) is not null)).ToList();
        var want = expected.ToDictionary(key, StringComparer.Ordinal);
        var have = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var row in actual)
        {
            have[key(row)] = row;
        }

        foreach (var (k, e) in want)
        {
            if (!have.TryGetValue(k, out var a))
            {
                problems.Add($"{table}: row '{k}' is missing");
                continue;
            }

            var different = properties.Where(p => !Equals(p.GetValue(e), p.GetValue(a))).Select(p => p.Name).ToList();
            if (different.Count > 0)
            {
                problems.Add($"{table}: row '{k}' differs in {string.Join(", ", different)}");
            }
        }

        foreach (var k in have.Keys.Where(k => !want.ContainsKey(k)))
        {
            problems.Add($"{table}: unexpected row '{k}'");
        }
    }

    // ------------------------------------------------------------------ write plumbing

    private async Task<T> WriteAsync<T>(OwnershipToken token, StoreOperation? operation, CancellationToken cancellationToken, Func<TradingDbContext, OwnershipToken, Task<T>> body)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureCompatibleAsync(cancellationToken).ConfigureAwait(false);
        var commitStarted = false;
        try
        {
            await using (var probe = NewContext())
            {
                await FenceAsync(probe, token, lockRow: false).ConfigureAwait(false);
            }

            if (operation is { } op)
            {
                FaultHook?.Invoke(op, StoreFaultPoint.BeforeApply);
            }

            T result;
            await using (var db = NewContext())
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None).ConfigureAwait(false);
                await FenceAsync(db, token, lockRow: true).ConfigureAwait(false);          // from here on no other writer can change this run until we commit
                result = await body(db, token).ConfigureAwait(false);
                if (operation is { } during)
                {
                    FaultHook?.Invoke(during, StoreFaultPoint.DuringApply);
                }

                commitStarted = true;
                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (operation is { } after)
            {
                FaultHook?.Invoke(after, StoreFaultPoint.AfterCommit);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new UnknownCommitOutcomeException("cancellation was requested after the write was submitted; the outcome is unknown to the caller");
            }

            return result;
        }
        catch (Exception e) when (e is not (FencedOutException or ConflictFaultException or SequenceGapException or StoreUnavailableException or UnknownCommitOutcomeException or RecoveryHaltException or OperationCanceledException or SimulatedProcessDeath))
        {
            throw Translate(e, commitStarted);
        }
    }

    private async Task FenceAsync(TradingDbContext db, OwnershipToken token, bool lockRow)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT epoch, owner_id FROM trading.run_owner WHERE run_id = @r" + (lockRow ? " FOR UPDATE" : string.Empty);
        var parameter = command.CreateParameter();
        parameter.ParameterName = "r";
        parameter.Value = token.RunId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new FencedOutException(token.RunId, token.Epoch, 0);
        }

        var epoch = reader.GetInt64(0);
        var owner = reader.GetString(1);
        if (epoch != token.Epoch || owner != token.OwnerId || !_held.ContainsKey((token.RunId, token.Epoch)))
        {
            throw new FencedOutException(token.RunId, token.Epoch, epoch);
        }
    }

    private static Exception Translate(Exception e, bool commitStarted)
    {
        var postgres = e as PostgresException ?? (e as DbUpdateException)?.InnerException as PostgresException;
        if (commitStarted)
        {
            return new UnknownCommitOutcomeException($"the commit did not complete normally: {e.GetType().Name}: {e.Message}");
        }

        if (postgres is { SqlState: { } state } && state.StartsWith("23", StringComparison.Ordinal))
        {
            return new ConflictFaultException($"The database refused the write ({postgres.ConstraintName ?? postgres.SqlState}): {postgres.MessageText}");
        }

        return e is NpgsqlException or DbUpdateException or IOException or TimeoutException or System.Net.Sockets.SocketException
            ? new StoreUnavailableException($"the write was not applied: {e.GetType().Name}: {e.Message}")
            : e;
    }

    // ------------------------------------------------------------------ compatibility and helpers

    private async Task EnsureCompatibleAsync(CancellationToken cancellationToken)
    {
        if (_compatible)
        {
            return;
        }

        await _compatGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_compatible)
            {
                return;
            }

            await using var db = NewContext();
            SchemaVersionEntity? row;
            try
            {
                row = await db.SchemaVersions.AsNoTracking().SingleOrDefaultAsync(s => s.Component == TradingSchema.Component, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable || e.SqlState == PostgresErrorCodes.InvalidSchemaName)
            {
                throw new RecoveryHaltException(HaltKind.Compatibility, "The trading schema does not exist in this database; apply the reviewed migration first.", new[] { e.MessageText });
            }

            var verdict = SchemaCompatibility.CheckSchema(row);
            if (!verdict.Compatible)
            {
                throw new RecoveryHaltException(HaltKind.Compatibility, "The trading database is not compatible with this code: " + string.Join("; ", verdict.Problems), verdict.Problems);
            }

            _compatible = true;
        }
        finally
        {
            _compatGate.Release();
        }
    }

    private async Task<string?> HolderOfAsync(string runId, CancellationToken cancellationToken)
    {
        await using var db = NewContext();
        return await db.RunOwners.AsNoTracking().Where(o => o.RunId == runId).Select(o => o.OwnerId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    internal TradingDbContext NewContext() => new(_options);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _held.Keys.ToList())
        {
            if (_held.TryRemove(key, out var held) && held.Connection is { } connection)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        _compatGate.Dispose();
    }
}
