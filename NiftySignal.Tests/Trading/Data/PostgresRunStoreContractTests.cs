using Npgsql;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>
/// The PostgreSQL implementation of the store contract fixture: faults are injected at the store's own seams, process death ends the lock session on the server,
/// a takeover is the real operator action, and a cancellation races a real commit. Needs the local test database (skipped visibly otherwise).
/// </summary>
public sealed class PostgresContractFixture : IStoreContractFixture
{
    private readonly PostgresRunStore _store;
    private readonly List<(StoreOperation Operation, int Nth, StoreFaultMode Mode)> _faults = new();
    private readonly Dictionary<StoreOperation, int> _calls = new();
    private CancellationTokenSource? _cancelOnCommit;

    public PostgresContractFixture(bool clean = true)
    {
        if (clean)
        {
            PostgresTestDatabaseClean.CleanAsync().GetAwaiter().GetResult();
        }

        _store = new PostgresRunStore(PostgresTestDatabase.ConnectionString!) { FaultHook = OnHook };
    }

    public IAsyncRunStore Store => _store;

    public PostgresRunStore Postgres => _store;

    public void Inject(StoreOperation operation, int nth, StoreFaultMode mode)
    {
        lock (_calls)
        {
            _faults.Add((operation, _calls.GetValueOrDefault(operation) + nth, mode));
        }
    }

    public void SimulateProcessDeath(string ownerId)
    {
        using var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        connection.Open();
        using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND application_name LIKE @p", connection);
        kill.Parameters.AddWithValue("p", $"nse-trading-lock:%:{ownerId}");
        kill.ExecuteNonQuery();
        for (var i = 0; i < 200; i++)
        {
            using var check = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name LIKE @p", connection);
            check.Parameters.AddWithValue("p", $"nse-trading-lock:%:{ownerId}");
            if ((long)check.ExecuteScalar()! == 0)
            {
                return;
            }

            Thread.Sleep(25);
        }
    }

    public Task<OwnershipToken> TakeOverAsync(string runId, string newOwnerId) => _store.TakeOverAsync(runId, newOwnerId);

    public void CancelAfterNextCommitSubmitted(CancellationTokenSource source) => _cancelOnCommit = source;

    private void OnHook(StoreOperation operation, StoreFaultPoint point)
    {
        if (point == StoreFaultPoint.BeforeApply)
        {
            lock (_calls)
            {
                _calls[operation] = _calls.GetValueOrDefault(operation) + 1;
            }
        }

        (StoreOperation Operation, int Nth, StoreFaultMode Mode)? fault;
        lock (_calls)
        {
            var n = _calls.GetValueOrDefault(operation);
            fault = _faults.Cast<(StoreOperation Operation, int Nth, StoreFaultMode Mode)?>().FirstOrDefault(f => f!.Value.Operation == operation && f.Value.Nth == n);
        }

        switch (point, fault?.Mode)
        {
            case (StoreFaultPoint.BeforeApply, StoreFaultMode.FailBeforeApply):
                throw new StoreUnavailableException("injected: unavailable before apply");
            case (StoreFaultPoint.BeforeApply, StoreFaultMode.UnknownNotApplied):
                throw new UnknownCommitOutcomeException("injected: outcome unknown (not applied)");
            case (StoreFaultPoint.DuringApply, StoreFaultMode.FailDuringApply):
                throw new StoreUnavailableException("injected: failed while applying");
            case (StoreFaultPoint.AfterCommit, StoreFaultMode.UnknownApplied):
                throw new UnknownCommitOutcomeException("injected: outcome unknown (applied)");
        }

        if (point == StoreFaultPoint.DuringApply && operation == StoreOperation.Commit && _cancelOnCommit is { } source)
        {
            _cancelOnCommit = null;
            source.Cancel();
        }
    }

    public void Dispose() => _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

[Collection("PostgresTradingTests")]
public sealed class PostgresRunStoreContractTests : RunStoreContractTests
{
    protected override IStoreContractFixture CreateFixture()
    {
        return new PostgresContractFixture();
    }
}
