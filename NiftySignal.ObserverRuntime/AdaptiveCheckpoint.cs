using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Host;

/// <summary>
/// Where reading has to resume for one source stream: every row with Id at or below <see cref="ResumeAfterId"/> has been consumed, and so has every later row at or below its token's entry in <see cref="SkipThrough"/>.
/// </summary>
public sealed record AdaptiveSourcePosition(long ResumeAfterId, Dictionary<string, long> SkipThrough);

/// <summary>
/// The Observer's complete processing state at one boundary: the engine (bars, partial volume bar, rolling and option-flow accumulators, quote baselines, Level-1 and Top-5 accumulators, previous depth observations),
/// the duplicate memories, the ticks read but not yet processed, and the source positions that continue exactly after them. It holds no order or position state (the execution service owns that); persisted bars,
/// sidecars, observations and publications stay authoritative in the database and are reconciled on restore.
/// </summary>
public sealed class AdaptiveCheckpointState
{
    public required long SessionId { get; init; }
    public required AdaptiveObserverEngine Engine { get; init; }
    public required AdaptiveIncrementalTickNormalizer Normalizer { get; init; }
    public required AdaptiveSourcePosition Position { get; init; }
    public required List<ObserverTokenTick> Unprocessed { get; init; }
    public DateTimeOffset? LastProcessedAvailableAt { get; init; }
    public long? LastProcessedTickId { get; init; }
    public required List<int> TriggerBars { get; init; }
    public FuturesDepth5Feed? Depth5Feed { get; init; }
    public AdaptiveSourcePosition? Depth5Position { get; init; }
    public FuturesDepth5Feed? OptionsDepth5Feed { get; init; }
    public AdaptiveSourcePosition? OptionsDepth5Position { get; init; }
    /// <summary>Set by the store at save time: persisted market rows per table up to the last completed bar. A restore requires at least these to be present.</summary>
    public Dictionary<string, int> PersistedRows { get; set; } = new();
}

/// <summary>
/// Durable checkpoints in the observer database (audit finding F73). A save is one inserted row. Retention is per session: the newest <see cref="AdaptiveRecoveryOptions.CheckpointRetention"/> rows this build and
/// configuration can use, and separately the newest as many rows of any other identity (so going back to the previous build still finds its own). Only checkpoint rows are ever removed; ticks, bars, metrics and
/// execution records are not touched, and the rows kept for a completed session stay. A candidate is used only when
/// its identity (session, frozen configuration, state format and the exact Observer and Host builds) matches, its payload hash matches, its state restores, the persisted bars it describes are still there and equal, and none of the market rows stored with them has gone.
/// Anything else is passed over for an older one, and finally for a full replay.
/// </summary>
public sealed class AdaptiveCheckpointStore(
    AdaptiveObserverPersistence persistence,
    ILogger<AdaptiveCheckpointStore> logger,
    IOptions<AdaptiveRecoveryOptions>? recoveryOptions = null,
    TimeProvider? clock = null)
{
    readonly AdaptiveRecoveryOptions _options = recoveryOptions?.Value ?? new AdaptiveRecoveryOptions();
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    long? _intervalStart;

    /// <summary>Most rows one cleanup removes; a larger excess (a lowered retention) goes over the following saves.</summary>
    public const int MaxRemovedPerCleanup = 20;

    public bool Enabled => _options.CheckpointsEnabled;

    /// <summary>True when the configured interval has passed since <see cref="StartInterval"/> or the last save attempt.</summary>
    public bool IsDue() => Enabled && (_intervalStart is not { } start || _clock.GetElapsedTime(start) >= TimeSpan.FromSeconds(_options.CheckpointIntervalSeconds));

    /// <summary>Starts the interval now, so a recovery does not write a checkpoint before the interval has passed.</summary>
    public void StartInterval() => _intervalStart = _clock.GetTimestamp();

    /// <summary>
    /// Identity of everything a stored state depends on. The build is identified by the module versions of the Observer and Host assemblies, which change with any change to their code,
    /// so state written by one build is never continued by another.
    /// </summary>
    public static string Identity(AdaptiveObserverSessionContext context, string? spotToken, bool futuresDepth, bool optionsDepth)
    {
        var s = context.Session;
        var text = JsonSerializer.Serialize(new object?[]
        {
            ObserverStateSerializer.FormatVersion, s.Id, s.TradeDate, s.ModelVersion, s.AtmRuleVersion, s.StrongThreshold, s.RiskFreeRate, s.BaseBarVolume, s.RollingWindowBars, s.FutureToken,
            s.OptionUniverseJson, spotToken, context.SignalStartUtc, s.ObservationStartUtc, s.OpeningWindowStartUtc, s.OpeningWindowEndUtc, futuresDepth, optionsDepth,
            typeof(AdaptiveObserverEngine).Module.ModuleVersionId, typeof(AdaptiveCheckpointStore).Module.ModuleVersionId,
        });
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Saves a checkpoint, then applies retention. Contained: a failure is logged and never disturbs the Observer. <paramref name="restoredId"/> is given while a replay that started from that checkpoint is not yet
    /// verified: it and everything older stay, because they are what the next attempt falls back to.
    /// </summary>
    public async Task<bool> TrySaveAsync(AdaptiveObserverDbContext db, string identity, AdaptiveCheckpointState state, CancellationToken ct, long? restoredId = null)
    {
        StartInterval();
        AdaptiveCheckpointRow? row = null;
        try
        {
            using var activity = AdaptiveLiveTelemetry.Source.StartActivity("checkpoint.save");
            var lastBar = state.Engine.FutureBars.Where(x => x.IsComplete).Select(x => x.BarSeq).DefaultIfEmpty(0).Max();
            state.PersistedRows = await persistence.CountMarketRowsAsync(db, state.SessionId, lastBar, ct);
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(ObserverStateSerializer.Serialize(state));
            var payload = compressed.ToArray();
            row = new AdaptiveCheckpointRow
            {
                SessionId = state.SessionId, CreatedAtUtc = _clock.GetUtcNow(), ProcessedThroughUtc = state.LastProcessedAvailableAt,
                LastBarSeq = lastBar,
                Identity = identity, FormatVersion = ObserverStateSerializer.FormatVersion, PayloadSha256 = Convert.ToHexString(SHA256.HashData(payload)), Payload = payload,
            };
            db.Checkpoints.Add(row);
            await db.SaveChangesAsync(ct);
            db.Entry(row).State = EntityState.Detached;
            await PruneAsync(db, state.SessionId, identity, ct, restoredId);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Adaptive checkpoint of session {SessionId} could not be saved; a restart would replay from an earlier one or from the start of the day.", state.SessionId);
            if (row is not null && db.Entry(row).State != EntityState.Detached) db.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>The newest checkpoint of the session that passes every check and is not past <paramref name="throughUtc"/>; null when there is none (replay the whole day).</summary>
    public async Task<(long Id, AdaptiveCheckpointState State)?> FindAsync(AdaptiveObserverDbContext db, long sessionId, string identity, DateTimeOffset throughUtc, CancellationToken ct)
    {
        if (!Enabled) return null;
        using var activity = AdaptiveLiveTelemetry.Source.StartActivity("checkpoint.restore");
        // A row that disappears between the listing and its load (removed by a cleanup) means the listing is out of date: list again, a bounded number of times.
        for (var pass = 0; pass < 3; pass++)
        {
            var candidates = await db.Checkpoints.AsNoTracking().Where(x => x.SessionId == sessionId).OrderByDescending(x => x.Id)
                .Select(x => new { x.Id, x.Identity, x.FormatVersion, x.ProcessedThroughUtc }).ToListAsync(ct);
            var vanished = false;
            foreach (var candidate in candidates)
            {
                if (candidate.Identity != identity || candidate.FormatVersion != ObserverStateSerializer.FormatVersion)
                {
                    logger.LogWarning("Adaptive checkpoint {Id} belongs to another build or configuration and is not used.", candidate.Id);
                    continue;
                }

                if (candidate.ProcessedThroughUtc > throughUtc) continue;
                try
                {
                    var row = await db.Checkpoints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.Id, ct);
                    if (row is null) { vanished = true; break; }
                    AdaptiveCheckpointState state;
                    try
                    {
                        if (Convert.ToHexString(SHA256.HashData(row.Payload)) != row.PayloadSha256) throw new InvalidOperationException("The stored payload does not match its hash.");
                        using var plain = new MemoryStream();
                        using (var gzip = new GZipStream(new MemoryStream(row.Payload), CompressionMode.Decompress)) gzip.CopyTo(plain);
                        state = ObserverStateSerializer.Deserialize<AdaptiveCheckpointState>(plain.ToArray());
                        if (state.SessionId != sessionId) throw new InvalidOperationException("The stored state belongs to another session.");
                    }
                    catch (Exception ex)
                    {
                        // The row itself is bad and can never be used: it is removed, so it does not take the place of a usable one.
                        logger.LogError(ex, "Adaptive checkpoint {Id} is damaged and is removed; trying an older one.", candidate.Id);
                        await TryRemoveAsync(db, [candidate.Id], ct);
                        continue;
                    }

                    // A disagreement with the persisted history, or a read failure, may not be the row's fault: it is passed over, not removed.
                    await persistence.VerifyFutureBarsAsync(db, sessionId, state.Engine.FutureBars, ct);
                    var present = await persistence.CountMarketRowsAsync(db, sessionId, row.LastBarSeq, ct);
                    var missing = state.PersistedRows.Where(x => present.GetValueOrDefault(x.Key) < x.Value).Select(x => $"{x.Key} {present.GetValueOrDefault(x.Key)} of {x.Value}").ToList();
                    if (missing.Count > 0) throw new InvalidOperationException($"Persisted rows the state relies on are missing: {string.Join(", ", missing)}.");
                    return (candidate.Id, state);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Adaptive checkpoint {Id} is unusable; trying an older one.", candidate.Id);
                }
            }

            if (!vanished) break;
        }

        return null;
    }

    /// <summary>Applies retention to one session (see the class summary): oldest first, at most <see cref="MaxRemovedPerCleanup"/> rows, failures contained. Payloads are not read.</summary>
    public async Task PruneAsync(AdaptiveObserverDbContext db, long sessionId, string identity, CancellationToken ct, long? restoredId = null)
    {
        try
        {
            var rows = await db.Checkpoints.AsNoTracking().Where(x => x.SessionId == sessionId).OrderByDescending(x => x.Id)
                .Select(x => new { x.Id, x.Identity, x.FormatVersion }).ToListAsync(ct);
            var expired = rows.GroupBy(x => x.Identity == identity && x.FormatVersion == ObserverStateSerializer.FormatVersion)
                .SelectMany(group => group.Skip(_options.CheckpointRetention))
                .Select(x => x.Id).Where(id => restoredId is null || id > restoredId).Order().Take(MaxRemovedPerCleanup).ToList();
            await TryRemoveAsync(db, expired, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Old adaptive checkpoints of session {SessionId} could not be removed; the next save tries again.", sessionId);
        }
    }

    /// <summary>Removes a checkpoint whose replay failed, together with any written after it, so the next attempt starts from an older one.</summary>
    public async Task InvalidateFromAsync(AdaptiveObserverDbContext db, long sessionId, long id, CancellationToken ct)
    {
        var doomed = await db.Checkpoints.AsNoTracking().Where(x => x.SessionId == sessionId && x.Id >= id).Select(x => x.Id).ToListAsync(ct);
        await RemoveAsync(db, doomed, ct);
    }

    async Task TryRemoveAsync(AdaptiveObserverDbContext db, IReadOnlyList<long> ids, CancellationToken ct)
    {
        try { await RemoveAsync(db, ids, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogError(ex, "Adaptive checkpoint rows could not be removed."); }
    }

    static async Task RemoveAsync(AdaptiveObserverDbContext db, IReadOnlyList<long> ids, CancellationToken ct)
    {
        // Keys only: the payloads are never loaded just to delete them. One row at a time: a row another cleanup already removed is not an error.
        foreach (var id in ids)
        {
            var stub = db.Entry(new AdaptiveCheckpointRow { Id = id, Identity = "", PayloadSha256 = "", Payload = [] });
            stub.State = EntityState.Deleted;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { }
            finally { stub.State = EntityState.Detached; }
        }
    }
}
