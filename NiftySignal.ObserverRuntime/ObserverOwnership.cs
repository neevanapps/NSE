using Npgsql;

namespace NiftySignal.Host;

/// <summary>Whether this process may process and persist Observer state now.</summary>
public interface IObserverOwnership
{
    /// <summary>True while this process holds the ownership, acquiring it when free. Never waits for another owner.</summary>
    Task<bool> EnsureAsync(CancellationToken ct);
}

/// <summary>
/// Exactly one Observer writes: a PostgreSQL session advisory lock on the observer database, held on one dedicated connection for as long as the process lives. A second Observer (another service instance,
/// or a Host configured to run it in process) does nothing until the lock is free. The lock goes with the connection, so a crashed owner frees it.
/// </summary>
public sealed class PostgresObserverOwnership(string connectionString, ILogger<PostgresObserverOwnership> logger, TimeProvider? clock = null) : IObserverOwnership, IAsyncDisposable
{
    public const long LockKey = 0x4144_4150_544F_4253;      // "ADAPTOBS"
    static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    NpgsqlConnection? _connection;
    long _checked;
    bool _reportedBusy;

    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        if (_connection is { } held)
        {
            if (_clock.GetElapsedTime(_checked) < CheckInterval) return true;
            try
            {
                await using var ping = new NpgsqlCommand("SELECT 1", held) { CommandTimeout = 5 };
                await ping.ExecuteScalarAsync(ct);
                _checked = _clock.GetTimestamp();
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Adaptive observer ownership was lost with its database connection; processing stops until it is held again.");
                await DisposeAsync();
                return false;
            }
        }

        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var take = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection) { Parameters = { new() { Value = LockKey } } };
            if ((bool)(await take.ExecuteScalarAsync(ct))!)
            {
                _connection = connection;
                _checked = _clock.GetTimestamp();
                _reportedBusy = false;
                logger.LogInformation("Adaptive observer ownership acquired.");
                return true;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        if (!_reportedBusy) logger.LogError("Another process owns the adaptive observer; this one processes nothing until that one stops.");
        _reportedBusy = true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the unpooled connection releases the lock.
        if (Interlocked.Exchange(ref _connection, null) is { } connection) await connection.DisposeAsync();
    }
}
