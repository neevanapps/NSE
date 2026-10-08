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

    /// <summary>Same insert-if-missing / verify-if-present contract for the futures-minus-spot basis sidecar (separate table, own MetricsVersion).</summary>
    public async Task<SupplementalOutcome> PersistOrVerifyBasisAsync(
        AdaptiveObserverDbContext db,
        long sessionId,
        AdaptiveCompletedBarPackage package,
        CancellationToken ct)
    {
        if (package.Basis is not { } basis)
        {
            return SupplementalOutcome.Skipped;
        }

        var expected = MapBasis(sessionId, package.FutureBar.BarSeq, basis);
        try
        {
            var existing = await db.BasisSupplemental.AsNoTracking()
                .SingleOrDefaultAsync(x => x.SessionId == sessionId && x.BarSeq == expected.BarSeq
                    && x.MetricsVersion == expected.MetricsVersion, ct);
            if (existing is null)
            {
                db.BasisSupplemental.Add(expected);
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
                "Adaptive basis supplemental metrics mismatch (row left untouched): session={SessionId}, bar={BarSeq}, version={Version}, {Difference}",
                sessionId, expected.BarSeq, expected.MetricsVersion, difference);
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
            logger.LogError(ex, "Adaptive basis supplemental write failed (core observer unaffected): session={SessionId}, bar={BarSeq}",
                sessionId, expected.BarSeq);
            return SupplementalOutcome.Failed;
        }
    }

    public static AdaptiveBasisSupplementalRow MapBasis(long sessionId, int barSeq, FuturesBasisBar b) => new()
    {
        SessionId = sessionId,
        BarSeq = barSeq,
        MetricsVersion = FuturesBasisBar.MetricsVersion,
        BasisStart = b.BasisStart,
        BasisTimeWeighted = b.BasisTimeWeighted,
        BasisEnd = b.BasisEnd,
        DeltaBasis = b.DeltaBasis,
        SpotAgeStartSeconds = b.SpotAgeStartSeconds,
        SpotAgeEndSeconds = b.SpotAgeEndSeconds,
        SpotAgeMaxSeconds = b.SpotAgeMaxSeconds,
        BasisStateChanges = b.BasisStateChanges,
        CoveredSeconds = b.CoveredSeconds,
        UncoveredSeconds = b.UncoveredSeconds,
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
