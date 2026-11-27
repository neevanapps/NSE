using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Runtime;
using Npgsql;

/// <summary>
/// Stands in for the execution service: writes its state heartbeat and live marks once a second through the production stores, with four fixed situations (open with a partial exit and a fresh quote; waiting for
/// Avg5; open with a stale quote; flat with two closed positions). The engine itself is not run: its rules are covered by tests.
/// </summary>
sealed class PaperFixture : IAsyncDisposable
{
    public static readonly string[] Strategies = ["TOTAL_ACTIVITY_5_10", "TOTAL_ACTIVITY_5_10_AVG5", "TOTAL_VOTE_5_OF_6", "TOTAL_VOTE_5_OF_6_AVG5"];
    public const decimal PartialNet = 900m;
    public const decimal ClosedNet = 500m - 220m;
    const decimal Entry = 100m, EntryFees = 15m, ExitFee = 20m;
    const int Remaining = 65;

    readonly NpgsqlDataSource _db;
    readonly CancellationTokenSource _stop = new();
    readonly DateOnly _day;
    readonly DateTimeOffset _entryAt = DateTimeOffset.UtcNow.AddMinutes(-5);
    ExecutionOwnership _owner = null!;
    long _seq;
    Task _loop = Task.CompletedTask;

    public volatile bool Silent;
    public volatile string? Condition;
    double _bid = 108;
    public double Bid { get => Volatile.Read(ref _bid); set => Volatile.Write(ref _bid, value); }

    PaperFixture(NpgsqlDataSource db, DateOnly day) => (_db, _day) = (db, day);

    public decimal Unrealized(double bid) => Mark(Open("p-open", 0), bid, DateTimeOffset.UtcNow, 0).EstNetLiquidation!.Value;

    public static async Task<PaperFixture> CreateAsync(string root, string connection, DateOnly day)
    {
        var db = NpgsqlDataSource.Create(connection);
        await using (var schema = db.CreateCommand(File.ReadAllText(Path.Combine(root, "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql"))))
            await schema.ExecuteNonQueryAsync();
        var fixture = new PaperFixture(db, day);
        fixture._owner = await ExecutionOwnership.TryAcquireAsync(db, default, "browser-validation") ?? throw new InvalidOperationException("another execution writer holds the scratch ledger");
        await fixture.PublishAsync();
        fixture._loop = fixture.RunAsync();
        return fixture;
    }

    async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await Task.Delay(500, _stop.Token); } catch (OperationCanceledException) { return; }
            if (!Silent) await PublishAsync();
        }
    }

    PositionView Open(string id, int strategy) => new(id, Strategies[strategy], OptionRight.Call, strategy == 0 ? "S-C7" : "S-C8", strategy == 0 ? 23000 : 23050, _day.AddDays(4), 130, PositionState.Open, Entry, 30m,
        null, null, null, null, null, 0m, 0m, 0, 3, Remaining, 0m, strategy == 0 ? PartialNet : 0m, 125m, strategy == 0 ? "Filled" : "Waiting", _entryAt);

    PositionView Closed(string id, decimal gross, decimal net, int minutesAgo) => new(id, Strategies[3], OptionRight.Put, "S-P7", 23000, _day.AddDays(4), 130, PositionState.Closed, Entry, 30m,
        110m, 25m, gross, net, ExitReason.OppositeSignal, 0m, 0m, 0, 4, 0, 0m, 0m, null, null, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

    PositionMark Mark(PositionView p, double bid, DateTimeOffset quoteAt, long seq)
    {
        var position = new MarkPosition(p.StrategyId, 1, _day, 1, p.PositionId, p.Right, p.Token, p.Strike, p.Expiry, p.RemainingQuantity, 2, p.EntryPrice, EntryFees, p.EntryAt!.Value, p.State, 2000, 0m, _ => ExitFee,
            0m, null, null, null);
        return PositionMarkCalculator.Compute(position, new MarkQuote(p.Token, bid, bid + 0.5, bid, quoteAt, 1), DateTimeOffset.UtcNow, seq);
    }

    async Task PublishAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var open = Open("p-open", 0);
        var stale = Open("p-stale", 2);
        var positions = new[] { open, stale, Closed("p-win", 560m, 500m, 40), Closed("p-loss", -200m, -220m, 20) };
        var strategies = Strategies.Select((id, i) => new StrategyStatus(id, 1, i % 2 == 0 ? EntryTiming.Immediate : EntryTiming.Avg5Pullback, false, i == 3 ? 2 : i == 1 ? 0 : 1, 5, 0m, 50_000m, false, 25,
            i == 1 ? new PendingEntryView("intent-1", OptionRight.Call, now.AddMinutes(-1), "S-C7", false, null) : null)).ToList();
        var seq = ++_seq;
        // marks first, then the state, as the service's two independent publications
        await new PostgresLiveMarkStore(_db).PublishAsync([Mark(open, Bid, now, seq), Mark(stale, 105, now.AddSeconds(-30), seq)], new MarkHeartbeat(seq, now, 2, seq, 0, 0), default);
        var snapshot = new ExecutionSnapshot(now, "Running", _day, 1, 60, true, Condition, null, strategies, positions, Condition is { } c ? [c] : []);
        await new ExecutionStore(_db).HeartbeatAsync(_owner, snapshot, default);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
        await _owner.DisposeAsync();
        await _db.DisposeAsync();
    }
}
