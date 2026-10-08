using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Host;
using NiftySignal.Domain.Entities;

sealed record DayData(DateOnly Day, Instrument Future, List<Instrument> Options, DateOnly Weekly, List<ObserverOptionInstrument> Chain, string[] Tokens,
    IReadOnlyList<(string Token, ObserverRawTick Tick)> Raw, Instrument? Spot = null);

sealed record Staged(DayData Day, DateTimeOffset[] ReceivedAt);

sealed record SimResult(Dictionary<string, object> Summary, string ObserverDb, DateTimeOffset EndData);

sealed class Feeder(Staged staged, string connectionString)
{
    int _lastInserted; int _cursor;
    public int Inserted => _lastInserted;
    public int Total => staged.ReceivedAt.Length;

    /// <summary>True once no remaining tick has ReceivedAt &lt;= <paramref name="cap"/> (everything that will ever be ingested has been made visible).</summary>
    public bool IngestComplete(DateTimeOffset cap) => _cursor >= staged.ReceivedAt.Length || staged.ReceivedAt[_cursor] > cap;

    /// <summary>Makes every tick with ReceivedAt &lt;= <paramref name="visibleUntil"/> visible, strictly in Id order (as live ingestion assigns Ids).</summary>
    public async Task<int> FeedAsync(DateTimeOffset visibleUntil, DateTimeOffset? ingestCap = null)
    {
        var limit = ingestCap is { } cap && cap < visibleUntil ? cap : visibleUntil;
        while (_cursor < staged.ReceivedAt.Length && staged.ReceivedAt[_cursor] <= limit) _cursor++;
        if (_cursor == _lastInserted) return 0;
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO ticks SELECT * FROM ticks_all WHERE \"Id\" > @a AND \"Id\" <= @b", conn);
        cmd.Parameters.AddWithValue("a", (long)_lastInserted); cmd.Parameters.AddWithValue("b", (long)_cursor);
        await cmd.ExecuteNonQueryAsync();
        var n = _cursor - _lastInserted; _lastInserted = _cursor; return n;
    }
}

sealed class MemorySampler : IDisposable
{
    readonly CancellationTokenSource _cts = new(); readonly Task _task;
    public long PeakWorkingSet { get; private set; }
    public long PeakManagedHeap { get; private set; }

    public MemorySampler()
    {
        _task = Task.Run(async () =>
        {
            using var p = Process.GetCurrentProcess();
            while (!_cts.IsCancellationRequested)
            {
                p.Refresh(); PeakWorkingSet = Math.Max(PeakWorkingSet, p.WorkingSet64); PeakManagedHeap = Math.Max(PeakManagedHeap, GC.GetTotalMemory(false));
                try { await Task.Delay(50, _cts.Token); } catch (OperationCanceledException) { }
            }
        });
    }

    public void Dispose() { _cts.Cancel(); try { _task.Wait(1000); } catch (Exception) { } }
}

sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

sealed class LambdaFactory<T>(Func<T> create) : IDbContextFactory<T> where T : DbContext
{
    public T CreateDbContext() => create();
    public Task<T> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(create());
}

sealed record CrashCase(string Name, string Why, Func<AdaptiveObserverWorker.LiveState, int, int, DateTimeOffset, bool> When);
