using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Host;

public enum SupplementalOutcome
{
    /// <summary>The package carried no supplemental metrics (nothing to store).</summary>
    Skipped,
    /// <summary>The deterministic row was missing and has been inserted (backfill after a mid-session deployment, or the normal live insert).</summary>
    Inserted,
    /// <summary>A row of the same <c>MetricsVersion</c> exists and matches the recomputed values.</summary>
    Verified,
    /// <summary>A row of the same <c>MetricsVersion</c> exists but differs from the recomputed values. It is left untouched.</summary>
    Mismatch,
    /// <summary>The sidecar write itself failed (for example a missing table). Core adaptive processing is unaffected.</summary>
    Failed,
}

/// <summary>
/// Sidecar persistence for supplemental observations (08-Oct plan, section 71). Insert-if-missing and verify-if-present with its
/// own comparison; it never touches, weakens or relies on <see cref="AdaptiveObserverPersistence"/>'s core-row parity checks.
/// A sidecar problem is logged and counted but never throws into the core observer: the core ledger remains the restart authority.
/// </summary>
public sealed class AdaptiveSupplementalPersistence(ILogger<AdaptiveSupplementalPersistence> logger)
{
    const double DoubleTolerance = 1e-9;
    int _mismatches;
    int _failures;

    /// <summary>Count of verify mismatches seen by this process (diagnostic health signal; rows are never overwritten).</summary>
    public int MismatchCount => Volatile.Read(ref _mismatches);

    /// <summary>Count of sidecar write failures seen by this process.</summary>
    public int FailureCount => Volatile.Read(ref _failures);

    public async Task<SupplementalOutcome> PersistOrVerifyFuturesAsync(
        AdaptiveObserverDbContext db,
        long sessionId,
        AdaptiveCompletedBarPackage package,
        CancellationToken ct)
    {
        if (package.Microstructure is not { } micro)
        {
            return SupplementalOutcome.Skipped;
        }

        var expected = MapFutures(sessionId, package.FutureBar.BarSeq, micro);
        try
        {
            var existing = await db.FuturesSupplemental.AsNoTracking()
                .SingleOrDefaultAsync(x => x.SessionId == sessionId && x.BarSeq == expected.BarSeq
                    && x.MetricsVersion == expected.MetricsVersion, ct);
            if (existing is null)
            {
                db.FuturesSupplemental.Add(expected);
                await db.SaveChangesAsync(ct);
                return SupplementalOutcome.Inserted;
            }

            var difference = FirstDifference(existing, expected);
            if (difference is null)
            {
                return SupplementalOutcome.Verified;
            }

            Interlocked.Increment(ref _mismatches);
            logger.LogError(
                "Adaptive futures supplemental metrics mismatch (row left untouched): session={SessionId}, bar={BarSeq}, version={Version}, {Difference}",
                sessionId, expected.BarSeq, expected.MetricsVersion, difference);
            await RecordInvalidAsync(db, sessionId, AdaptiveProjectionComponents.FuturesSupplemental, expected.MetricsVersion, expected.BarSeq, difference, ct);
            return SupplementalOutcome.Mismatch;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The context is shared with core persistence: make sure a failed sidecar insert cannot be re-saved with the next core write.
            try
            {
                if (db.Entry(expected).State != EntityState.Detached)
                {
                    db.Entry(expected).State = EntityState.Detached;
                }
            }
            catch (Exception)
            {
                // The context itself is unusable; nothing more to protect.
            }

            Interlocked.Increment(ref _failures);
            logger.LogError(ex, "Adaptive futures supplemental write failed (core observer unaffected): session={SessionId}, bar={BarSeq}",
                sessionId, expected.BarSeq);
            return SupplementalOutcome.Failed;
        }
    }

    /// <summary>Same insert-if-missing / verify-if-present contract for the options sidecar.</summary>
    public async Task<SupplementalOutcome> PersistOrVerifyOptionsAsync(
        AdaptiveObserverDbContext db,
        AdaptiveSessionStateRow session,
        AdaptiveCompletedBarPackage package,
        CancellationToken ct)
    {
        if (package.OptionsSupplemental is not { } options)
        {
            return SupplementalOutcome.Skipped;
        }

        var expected = MapOptions(session.Id, package.FutureBar.BarSeq, options, session.ObservationStartUtc ?? session.OpeningWindowStartUtc);
        try
        {
            var existing = await db.OptionsSupplemental.AsNoTracking()
                .SingleOrDefaultAsync(x => x.SessionId == session.Id && x.BarSeq == expected.BarSeq
                    && x.MetricsVersion == expected.MetricsVersion, ct);
            if (existing is null)
            {
                db.OptionsSupplemental.Add(expected);
                await db.SaveChangesAsync(ct);
                return SupplementalOutcome.Inserted;
            }

            var difference = FirstDifference(existing, expected);
            if (difference is null)
            {
                return SupplementalOutcome.Verified;
            }

            Interlocked.Increment(ref _mismatches);
            logger.LogError(
                "Adaptive options supplemental metrics mismatch (row left untouched): session={SessionId}, bar={BarSeq}, version={Version}, {Difference}",
                session.Id, expected.BarSeq, expected.MetricsVersion, difference);
            await RecordInvalidAsync(db, session.Id, AdaptiveProjectionComponents.OptionsSupplemental, expected.MetricsVersion, expected.BarSeq, difference, ct);
            return SupplementalOutcome.Mismatch;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                if (db.Entry(expected).State != EntityState.Detached)
                {
                    db.Entry(expected).State = EntityState.Detached;
                }
            }
            catch (Exception)
            {
                // The context itself is unusable; nothing more to protect.
            }

            Interlocked.Increment(ref _failures);
            logger.LogError(ex, "Adaptive options supplemental write failed (core observer unaffected): session={SessionId}, bar={BarSeq}",
                session.Id, expected.BarSeq);
            return SupplementalOutcome.Failed;
        }
    }

    /// <summary>
    /// Durably marks a bar's sidecar as known-invalid (insert-if-missing; the sidecar row itself is never touched). A failure to write the
    /// marker is logged and swallowed: the core observer must never be affected by sidecar health bookkeeping.
    /// </summary>
    async Task RecordInvalidAsync(AdaptiveObserverDbContext db, long sessionId, string component, string version, int barSeq, string detail, CancellationToken ct)
    {
        AdaptiveProjectionHealthRow? marker = null;
        try
        {
            if (await db.ProjectionHealth.AsNoTracking().AnyAsync(x => x.SessionId == sessionId && x.Component == component
                    && x.Version == version && x.BarSeq == barSeq, ct))
            {
                return;
            }

            marker = new AdaptiveProjectionHealthRow
            {
                SessionId = sessionId, Component = component, Version = version, BarSeq = barSeq,
                Detail = detail.Length > 512 ? detail[..512] : detail, DetectedAtUtc = DateTimeOffset.UtcNow,
            };
            db.ProjectionHealth.Add(marker);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            logger.LogError(ex, "Could not record supplemental invalid marker: session={SessionId}, bar={BarSeq}, component={Component}", sessionId, barSeq, component);
            try
            {
                // Detach only the marker: the context is shared with core persistence and must not lose anything else.
                if (marker is not null && db.Entry(marker).State != EntityState.Detached) db.Entry(marker).State = EntityState.Detached;
            }
            catch (Exception)
            {
                // The context itself is unusable; nothing more to protect.
            }
        }
    }

    public static AdaptiveOptionsSupplementalRow MapOptions(long sessionId, int barSeq, OptionsSupplementalBar b, DateTimeOffset observationStartUtc) => new()
    {
        SessionId = sessionId,
        BarSeq = barSeq,
        MetricsVersion = OptionsSupplementalBar.MetricsVersion,
        ObservationStartUtc = observationStartUtc,
        CenterStrike = b.CenterStrike,
        UnavailableReason = b.UnavailableReason,
        CeOiStart = b.CeOiStart,
        CeOiEnd = b.CeOiEnd,
        CeOiDelta = b.CeOiDelta,
        PeOiStart = b.PeOiStart,
        PeOiEnd = b.PeOiEnd,
        PeOiDelta = b.PeOiDelta,
        CeMidStart = b.CeMidStart,
        CeMidEnd = b.CeMidEnd,
        CeMidDelta = b.CeMidDelta,
        PeMidStart = b.PeMidStart,
        PeMidEnd = b.PeMidEnd,
        PeMidDelta = b.PeMidDelta,
        CePosition = b.CePosition,
        PePosition = b.PePosition,
        CeIvStart = b.CeIvStart,
        CeIvEnd = b.CeIvEnd,
        CeDeltaIv = b.CeDeltaIv,
        PeIvStart = b.PeIvStart,
        PeIvEnd = b.PeIvEnd,
        PeDeltaIv = b.PeDeltaIv,
        IvSkewStart = b.IvSkewStart,
        IvSkewEnd = b.IvSkewEnd,
        DeltaSkew = b.DeltaSkew,
        BarCeQuantity = b.BarCeQuantity,
        BarPeQuantity = b.BarPeQuantity,
        VolPcr = b.VolPcr,
        RollCeQuantity = b.RollCeQuantity,
        RollPeQuantity = b.RollPeQuantity,
        RollVolPcr = b.RollVolPcr,
        DayCeVolume = b.DayCeVolume,
        DayPeVolume = b.DayPeVolume,
        UniverseTokenCount = b.UniverseTokenCount,
        TokensObserved = b.TokensObserved,
        CeMicroDevTimeWeighted = b.CeMicroDevTimeWeighted,
        CeOfi = b.CeOfi,
        PeMicroDevTimeWeighted = b.PeMicroDevTimeWeighted,
        PeOfi = b.PeOfi,
        CeActivityPerSecond = b.CeActivityPerSecond,
        PeActivityPerSecond = b.PeActivityPerSecond,
        StraddleMidStart = b.StraddleMidStart,
        StraddleMidEnd = b.StraddleMidEnd,
        StraddleDelta = b.StraddleDelta,
    };

    public static AdaptiveFuturesSupplementalRow MapFutures(long sessionId, int barSeq, FuturesMicrostructureBar m) => new()
    {
        SessionId = sessionId,
        BarSeq = barSeq,
        MetricsVersion = FuturesMicrostructureBar.MetricsVersion,
        TobStart = m.TobStart,
        TobTimeWeighted = m.TobTimeWeighted,
        TobEnd = m.TobEnd,
        TobChange = m.TobChange,
        TobMin = m.TobMin,
        TobMax = m.TobMax,
        MicroDevStart = m.MicroDevStart,
        MicroDevTimeWeighted = m.MicroDevTimeWeighted,
        MicroDevEnd = m.MicroDevEnd,
        MicroDevChange = m.MicroDevChange,
        Ofi = m.Ofi,
        OfiTransitions = m.OfiTransitions,
        BookStateChanges = m.BookStateChanges,
        InvalidBookEvents = m.InvalidBookEvents,
        ValidBookSeconds = m.ValidBookSeconds,
        InvalidBookSeconds = m.InvalidBookSeconds,
        TobUsableSeconds = m.TobUsableSeconds,
        MicroDevUsableSeconds = m.MicroDevUsableSeconds,
    };

    static string? FirstDifference<TRow>(TRow actual, TRow expected) where TRow : class
    {
        foreach (var property in typeof(TRow).GetProperties())
        {
            if (property.Name == "Id") continue;
            var a = property.GetValue(actual);
            var e = property.GetValue(expected);
            if (a is double av && e is double ev && double.IsFinite(av) && double.IsFinite(ev) && Math.Abs(av - ev) <= DoubleTolerance) continue;
            if (Equals(a, e)) continue;
            return $"{property.Name}: persisted={a ?? "null"}, recomputed={e ?? "null"}";
        }

        return null;
    }
}
