using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using Npgsql;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Read-only adaptive observer projection. SignalR only invalidates this view; all values are read
/// from the committed adaptive database so Dashboard restart/reconnect cannot invent state.
/// </summary>
public sealed class AdaptiveObserverDataService(
    IDbContextFactory<AdaptiveObserverDbContext> dbFactory,
    ILogger<AdaptiveObserverDataService> logger)
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    long? _notifiedSessionId;
    int? _notifiedBarSeq;

    public event Action? Updated;

    public long? LastNotifiedSessionId => _notifiedSessionId;
    public int? LastNotifiedBarSeq => _notifiedBarSeq;

    public void NotifyStateChanged(long sessionId, int barSeq)
    {
        _notifiedSessionId = sessionId;
        _notifiedBarSeq = barSeq;
        Updated?.Invoke();
    }

    public async Task<AdaptiveObserverHeaderSnapshot?> LoadHeaderAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await FindTodaySessionAsync(db, ct);
        if (session is null)
        {
            return null;
        }

        var runtime = await db.Runtime.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id, ct);
        return runtime is null ? null : new AdaptiveObserverHeaderSnapshot(session, runtime,
            await LoadValidBarCountAsync(db, session, runtime.LastCompletedBarSeq, ct));
    }

    public async Task<AdaptiveObserverSnapshot?> LoadSnapshotAsync(int requestedRows, CancellationToken ct = default, long? captureSessionId = null, int? throughBarSeq = null)
    {
        var rowCount = requestedRows switch
        {
            <= 5 => 5,
            <= 10 => 10,
            _ => 15,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = captureSessionId is { } id
            ? await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            : await FindTodaySessionAsync(db, ct);
        if (session is null)
        {
            return null;
        }

        var runtime = await db.Runtime.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id, ct);
        if (runtime is null)
        {
            return null;
        }

        if (throughBarSeq is { } boundary)
        {
            if (captureSessionId is null || boundary < 0 || boundary > runtime.LastCompletedBarSeq)
                throw new InvalidOperationException("Capture boundary is not committed.");
            runtime.LastCompletedBarSeq = boundary;
            runtime.CurrentPartialBarVolume = 0;
            runtime.CurrentPartialBarStartedAtUtc = null;
            runtime.LastProcessedSourceAvailableAtUtc = null;
        }
        var futureBars = await db.FutureBars.AsNoTracking()
            .Where(x => x.SessionId == session.Id && x.BarSeq <= runtime.LastCompletedBarSeq)
            .OrderByDescending(x => x.BarSeq)
            .Take(rowCount)
            .ToListAsync(ct);

        var seqs = futureBars.Select(x => x.BarSeq).ToArray();
        if (throughBarSeq is { } expected && expected > 0 && futureBars.FirstOrDefault()?.BarSeq != expected)
            throw new InvalidOperationException("Capture boundary bar is missing.");
        if (throughBarSeq is not null) runtime.LastProcessedSourceAvailableAtUtc = futureBars.FirstOrDefault()?.EndAvailableAtUtc;
        var rolling = seqs.Length == 0
            ? new List<AdaptiveRollingStateRow>()
            : await db.RollingStates.AsNoTracking()
                .Where(x => x.SessionId == session.Id && seqs.Contains(x.EndBarSeq))
                .ToListAsync(ct);
        var rollingBySeq = rolling.ToDictionary(x => x.EndBarSeq);

        var futures = futureBars
            .Select(x => new AdaptiveFuturesGridRow(x, rollingBySeq.GetValueOrDefault(x.BarSeq)))
            .ToList();

        var optionRows = seqs.Length == 0
            ? new List<AdaptiveOptionBandBarRow>()
            : await db.OptionBandBars.AsNoTracking()
                .Where(x => x.SessionId == session.Id && seqs.Contains(x.BarSeq))
                .ToListAsync(ct);
        var callBySeq = optionRows.Where(x => x.Side == NiftySignal.Domain.Enums.OptionType.Call).ToDictionary(x => x.BarSeq);
        var putBySeq = optionRows.Where(x => x.Side == NiftySignal.Domain.Enums.OptionType.Put).ToDictionary(x => x.BarSeq);
        var options = futureBars.Select(x => new AdaptiveOptionPairGridRow(
                x.BarSeq,
                x.EndAvailableAtUtc,
                callBySeq.GetValueOrDefault(x.BarSeq),
                putBySeq.GetValueOrDefault(x.BarSeq)))
            .ToList();

        var residuals = seqs.Length == 0
            ? new List<AdaptiveOptionResidualBarRow>()
            : await db.OptionResidualBars.AsNoTracking()
                .Where(x => x.SessionId == session.Id && seqs.Contains(x.BarSeq))
                .OrderByDescending(x => x.BarSeq)
                .ThenBy(x => x.Variant)
                .ToListAsync(ct);

        // Every grid shares the same completed sequences. A missing/pre-09:30 diagnostic
        // remains an explicit unavailable row, rather than silently shortening Grid 3.
        var residualByKey = residuals.ToDictionary(x => (x.BarSeq,x.Variant));
        residuals = futureBars.SelectMany(bar => new[] { ResidualVariant.Atm, ResidualVariant.AtmPlusMinus2 }
            .Select(variant => residualByKey.GetValueOrDefault((bar.BarSeq,variant))
                ?? new AdaptiveOptionResidualBarRow { SessionId=session.Id, BarSeq=bar.BarSeq, Variant=variant,
                    EndAvailableAtUtc=bar.EndAvailableAtUtc, Relationship="NEUTRAL", IsAvailable=false,
                    UnavailableReason="Diagnostic row unavailable for this completed bar." }))
            .ToList();

        var anchors = await db.ResidualAnchorComponents.AsNoTracking()
            .Where(x => x.SessionId == session.Id)
            .OrderBy(x => x.Side)
            .ThenBy(x => x.Strike)
            .ToListAsync(ct);

        var invalidFutures = await LoadInvalidBarsAsync(db, session.Id, AdaptiveProjectionComponents.FuturesSupplemental, FuturesMicrostructureBar.MetricsVersion, seqs, ct);
        var invalidOptions = await LoadInvalidBarsAsync(db, session.Id, AdaptiveProjectionComponents.OptionsSupplemental, OptionsSupplementalBar.MetricsVersion, seqs, ct);
        // Known-invalid sidecar bars are withheld (shown unavailable), never displayed as trustworthy values.
        var futuresSupplemental = (await LoadFuturesSupplementalAsync(db, session.Id, seqs, ct))
            .Where(x => !invalidFutures.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        var optionsSupplemental = (await LoadOptionsSupplementalAsync(db, session.Id, seqs, ct))
            .Where(x => !invalidOptions.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        return new AdaptiveObserverSnapshot(session, runtime, futures, options, residuals, anchors,
            await LoadValidBarCountAsync(db, session, runtime.LastCompletedBarSeq, ct),
            futuresSupplemental,
            optionsSupplemental,
            seqs.Length == 0 ? [] : await db.OptionResidualBars.AsNoTracking()
                .Where(x => x.SessionId == session.Id && x.BarSeq == seqs.Min() - 1).ToListAsync(ct),
            invalidFutures, invalidOptions);
    }

    /// <summary>Bars (of the displayed range) whose sidecar carries a durable failed-verification marker. Tolerates a not-yet-migrated schema.</summary>
    static async Task<IReadOnlySet<int>> LoadInvalidBarsAsync(
        AdaptiveObserverDbContext db, long sessionId, string component, string version, int[] seqs, CancellationToken ct)
    {
        if (seqs.Length == 0) return new HashSet<int>();
        try
        {
            var bars = await db.ProjectionHealth.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.Component == component && x.Version == version && seqs.Contains(x.BarSeq))
                .Select(x => x.BarSeq).ToListAsync(ct);
            return bars.ToHashSet();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new HashSet<int>();
        }
    }

    /// <summary>Options sidecar read; same missing-relation tolerance as <see cref="LoadFuturesSupplementalAsync"/>.</summary>
    static async Task<IReadOnlyDictionary<int, AdaptiveOptionsSupplementalRow>> LoadOptionsSupplementalAsync(
        AdaptiveObserverDbContext db, long sessionId, int[] seqs, CancellationToken ct)
    {
        if (seqs.Length == 0) return new Dictionary<int, AdaptiveOptionsSupplementalRow>();
        try
        {
            var version = OptionsSupplementalBar.MetricsVersion;
            var rows = await db.OptionsSupplemental.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.MetricsVersion == version && seqs.Contains(x.BarSeq))
                .ToListAsync(ct);
            return rows.ToDictionary(x => x.BarSeq);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new Dictionary<int, AdaptiveOptionsSupplementalRow>();
        }
    }

    /// <summary>
    /// Sidecar read (08-Oct plan section 71.4): supplemental metrics are optional, so a missing relation (Dashboard started before the
    /// Host migrated) means "no supplemental metrics", never a failed panel.
    /// </summary>
    static async Task<IReadOnlyDictionary<int, AdaptiveFuturesSupplementalRow>> LoadFuturesSupplementalAsync(
        AdaptiveObserverDbContext db, long sessionId, int[] seqs, CancellationToken ct)
    {
        if (seqs.Length == 0) return new Dictionary<int, AdaptiveFuturesSupplementalRow>();
        try
        {
            var version = FuturesMicrostructureBar.MetricsVersion;
            var rows = await db.FuturesSupplemental.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.MetricsVersion == version && seqs.Contains(x.BarSeq))
                .ToListAsync(ct);
            return rows.ToDictionary(x => x.BarSeq);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new Dictionary<int, AdaptiveFuturesSupplementalRow>();
        }
    }

    static async Task<int> LoadValidBarCountAsync(AdaptiveObserverDbContext db, AdaptiveSessionStateRow session,
        int boundary, CancellationToken ct)
    {
        var bars = await db.FutureBars.AsNoTracking()
            .Where(x => x.SessionId == session.Id && x.BarSeq <= boundary
                && x.BarSeq > boundary - AdaptiveReadinessPolicy.RequiredBars)
            .ToListAsync(ct);
        return AdaptiveReadinessPolicy.CountValidSuffix(bars.Select(x => (x.BarSeq,
            AdaptiveReadinessPolicy.IsValid(x.Volume, session.BaseBarVolume, x.Open, x.High, x.Low,
                x.Close, x.TradeUpdates, x.StartAvailableAtUtc, x.EndAvailableAtUtc))), boundary);
    }

    async Task<AdaptiveSessionStateRow?> FindTodaySessionAsync(AdaptiveObserverDbContext db, CancellationToken ct)
    {
        var todayIst = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IstOffset).DateTime);
        try
        {
            return await db.Sessions.AsNoTracking()
                .Where(x => x.TradeDate == todayIst && x.ModelVersion == OpeningVolumeProjectionV1.ModelVersion)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Adaptive dashboard read failed while locating today's session.");
            throw;
        }
    }
}
