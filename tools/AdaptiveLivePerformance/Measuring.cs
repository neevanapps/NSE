using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NiftySignal.Host;

/// <summary>SQL command counters (validation-only; production never attaches this).</summary>
sealed class SqlCounter
{
    long _total, _reads, _writes;
    public long Total => Interlocked.Read(ref _total);
    public long Reads => Interlocked.Read(ref _reads);
    public long Writes => Interlocked.Read(ref _writes);

    public void Count(string sql)
    {
        Interlocked.Increment(ref _total);
        var head = sql.AsSpan().TrimStart();
        if (head.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || head.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _writes);
        else Interlocked.Increment(ref _reads);
    }

    public (long Total, long Reads, long Writes) Snapshot() => (Total, Reads, Writes);
}

sealed class CountingInterceptor(SqlCounter counter) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { counter.Count(command.CommandText); return result; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { counter.Count(command.CommandText); return ValueTask.FromResult(result); }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { counter.Count(command.CommandText); return result; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { counter.Count(command.CommandText); return ValueTask.FromResult(result); }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { counter.Count(command.CommandText); return result; }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { counter.Count(command.CommandText); return ValueTask.FromResult(result); }
}

static class Stats
{
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var rank = p / 100d * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank); var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static Dictionary<string, double> Summary(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        if (sorted.Count == 0) return new() { ["n"] = 0 };
        return new()
        {
            ["n"] = sorted.Count, ["mean"] = Math.Round(sorted.Average(), 4), ["p50"] = Math.Round(Percentile(sorted, 50), 4),
            ["p90"] = Math.Round(Percentile(sorted, 90), 4), ["p95"] = Math.Round(Percentile(sorted, 95), 4),
            ["p99"] = Math.Round(Percentile(sorted, 99), 4), ["max"] = Math.Round(sorted[^1], 4),
        };
    }
}

/// <summary>Collects per-stage durations (and SQL counts) from <see cref="AdaptiveLiveTelemetry"/> activities.</summary>
sealed class StageRecorder : IDisposable
{
    readonly ActivityListener _listener;
    readonly SqlCounter _sql;
    readonly object _gate = new();
    public Dictionary<string, List<double>> DurationsMs { get; } = new();
    public Dictionary<string, List<double>> SqlPerStage { get; } = new();
    /// <summary>Per completed bar: stage name to (milliseconds, sql commands).</summary>
    public Dictionary<int, Dictionary<string, (double Ms, double Sql)>> PerBar { get; } = new();
    public int BarsThisCycle;

    public StageRecorder(SqlCounter sql)
    {
        _sql = sql;
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == AdaptiveLiveTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => a.SetCustomProperty("sql0", sql.Total),
            ActivityStopped = a =>
            {
                var sqlDelta = sql.Total - (a.GetCustomProperty("sql0") is long s0 ? s0 : 0L);
                var ms = a.Duration.TotalMilliseconds;
                lock (_gate)
                {
                    if (!DurationsMs.TryGetValue(a.OperationName, out var list)) DurationsMs[a.OperationName] = list = new();
                    list.Add(ms);
                    if (!SqlPerStage.TryGetValue(a.OperationName, out var sl)) SqlPerStage[a.OperationName] = sl = new();
                    sl.Add(sqlDelta);
                    if (a.OperationName == "bar") BarsThisCycle++;
                    if (a.OperationName.StartsWith("bar.", StringComparison.Ordinal) && a.Parent?.GetTagItem("barSeq") is int seq)
                    {
                        if (!PerBar.TryGetValue(seq, out var stages)) PerBar[seq] = stages = new();
                        stages[a.OperationName] = (ms, sqlDelta);
                    }
                    else if (a.OperationName == "bar" && a.GetTagItem("barSeq") is int seq2)
                    {
                        if (!PerBar.TryGetValue(seq2, out var stages)) PerBar[seq2] = stages = new();
                        stages["bar"] = (ms, sqlDelta);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Reset() { lock (_gate) { DurationsMs.Clear(); SqlPerStage.Clear(); PerBar.Clear(); BarsThisCycle = 0; } }

    public Dictionary<string, object> Report() { lock (_gate) return DurationsMs.ToDictionary(k => k.Key, k => (object)new Dictionary<string, object>
    {
        ["ms"] = Stats.Summary(k.Value), ["sqlPerCall"] = Stats.Summary(SqlPerStage[k.Key]),
    }); }

    public void Dispose() => _listener.Dispose();
}
