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
            // If the known-invalid marker cannot be stored, integrity is UNKNOWN (not "known invalid"): report Failed so nothing consumes the row.
            return await RecordInvalidAsync(db, sessionId, AdaptiveProjectionComponents.FuturesSupplemental, expected.MetricsVersion, expected.BarSeq, difference, ct)
                ? SupplementalOutcome.Mismatch : SupplementalOutcome.Failed;
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
            return await RecordInvalidAsync(db, sessionId, AdaptiveProjectionComponents.BasisSupplemental, expected.MetricsVersion, expected.BarSeq, difference, ct)
                ? SupplementalOutcome.Mismatch : SupplementalOutcome.Failed;
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
        Status = b.Status,
    };

    /// <summary>
    /// Per-strike sidecar (option-strike-v3): inserts the bar's rows when none exist, otherwise verifies the stored rows against the recomputed ones and leaves them untouched
    /// (a mismatch is logged and counted, never overwritten). Failures are swallowed after logging: the core observer and every consumer of the other sidecars are unaffected.
    /// </summary>
    public async Task<SupplementalOutcome> PersistOrVerifyStrikesAsync(AdaptiveObserverDbContext db, long sessionId, AdaptiveCompletedBarPackage package, CancellationToken ct)
    {
        if (package.OptionStrikes is not { Count: > 0 } strikes)
        {
            return SupplementalOutcome.Skipped;
        }

        var expected = strikes.Select(x => MapStrike(sessionId, package.FutureBar.BarSeq, package.FutureBar.EndAvailableAtUtc, x)).ToList();
        AdaptiveStrikeAttestationRow? attestation = null;
        try
        {
            var version = OptionStrikeBarMetrics.MetricsVersion;
            var existing = await db.OptionStrikeBars.AsNoTracking()
                .Where(x => x.SessionId == sessionId && x.BarSeq == package.FutureBar.BarSeq && x.MetricsVersion == version)
                .ToListAsync(ct);
            if (existing.Count == 0)
            {
                // Rows and their attestation commit together (one SaveChanges = one transaction): never rows without proof that they were written from the verified package, never proof without rows.
                db.OptionStrikeBars.AddRange(expected);
                attestation = NewAttestation(sessionId, package.FutureBar.BarSeq, version, expected);
                db.StrikeAttestations.Add(attestation);
                await db.SaveChangesAsync(ct);
                return SupplementalOutcome.Inserted;
            }

            var byToken = existing.ToDictionary(x => x.Token, StringComparer.Ordinal);
            string? difference = existing.Count != expected.Count ? $"row count {existing.Count} vs {expected.Count}" : null;
            var badTokens = new List<string>();
            foreach (var row in expected)
            {
                var d = byToken.TryGetValue(row.Token, out var stored) ? StrikeDifference(stored, row) : $"token {row.Token} missing";
                if (d is not null)
                {
                    badTokens.Add(row.Token);
                    difference ??= d;
                }
            }

            if (difference is null)
            {
                // Verified against the recomputation: record (or refresh) the POSITIVE attestation of exactly these stored rows. If it cannot be written the rows simply stay unattested (= withheld by every reader) and the bar is retried.
                return await EnsureAttestationAsync(db, sessionId, version, package.FutureBar.BarSeq, existing, ct) ? SupplementalOutcome.Verified : SupplementalOutcome.Failed;
            }

            Interlocked.Increment(ref _mismatches);
            logger.LogError("Adaptive per-strike projection mismatch (rows left untouched): session={SessionId}, bar={BarSeq}, {Difference}", sessionId, package.FutureBar.BarSeq, difference);
            // A4: durably mark the affected strikes of this bar UNVERIFIED (reuses the known-invalid ledger; no schema change). A count-only mismatch with no differing token marks the whole bar.
            // FAIL CLOSED: Mismatch is reported only once the marker durably covers every affected token. If it cannot be written the outcome is Failed (integrity unknown), so the caller
            // retries instead of treating the bar as handled; the original strike rows are never touched either way.
            return await RecordUnverifiedStrikesAsync(db, sessionId, version, package.FutureBar.BarSeq, badTokens, difference, ct)
                ? SupplementalOutcome.Mismatch
                : SupplementalOutcome.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                foreach (var row in expected)
                {
                    if (db.Entry(row).State != EntityState.Detached)
                    {
                        db.Entry(row).State = EntityState.Detached;
                    }
                }

                if (attestation is not null && db.Entry(attestation).State != EntityState.Detached) db.Entry(attestation).State = EntityState.Detached;
            }
            catch (Exception)
            {
                // The context itself is unusable; nothing more to protect.
            }

            Interlocked.Increment(ref _failures);
            logger.LogError(ex, "Adaptive per-strike projection write failed (core observer unaffected): session={SessionId}, bar={BarSeq}", sessionId, package.FutureBar.BarSeq);
            return SupplementalOutcome.Failed;
        }
    }

    /// <summary>
    /// Close-ATM sidecar (close-atm-v1): inserts the bar's close ATM when missing, otherwise verifies the stored row against the recomputed one and leaves it untouched. A mismatch is durably marked
    /// known-invalid (component bar-close-atm) so readers show that bar's close ATM as unavailable; if the marker cannot be written the outcome is Failed (integrity unknown, retried), never Mismatch.
    /// </summary>
    public async Task<SupplementalOutcome> PersistOrVerifyCloseAtmAsync(AdaptiveObserverDbContext db, long sessionId, AdaptiveCompletedBarPackage package, CancellationToken ct)
    {
        if (package.CloseAtm is not { } atm)
        {
            return SupplementalOutcome.Skipped;
        }

        var barSeq = package.FutureBar.BarSeq;
        var expected = new AdaptiveBarCloseAtmRow
        {
            SessionId = sessionId, BarSeq = barSeq, Version = BarCloseAtm.Version, AtmStrike = atm.Strike, Spot = atm.Spot,
            UnavailableReason = atm.UnavailableReason is { Length: > 256 } r ? r[..256] : atm.UnavailableReason,
        };
        try
        {
            var stored = await db.BarCloseAtm.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId && x.BarSeq == barSeq && x.Version == BarCloseAtm.Version, ct);
            if (stored is null)
            {
                db.BarCloseAtm.Add(expected);
                await db.SaveChangesAsync(ct);
                return SupplementalOutcome.Inserted;
            }

            var same = stored.AtmStrike == expected.AtmStrike && NullableEqual(stored.Spot, expected.Spot) && string.Equals(stored.UnavailableReason, expected.UnavailableReason, StringComparison.Ordinal);
            if (same)
            {
                return SupplementalOutcome.Verified;
            }

            Interlocked.Increment(ref _mismatches);
            var difference = $"close ATM stored {stored.AtmStrike?.ToString() ?? "null"} vs recomputed {expected.AtmStrike?.ToString() ?? "null"}";
            logger.LogError("Adaptive close-ATM mismatch (row left untouched): session={SessionId}, bar={BarSeq}, {Difference}", sessionId, barSeq, difference);
            return await RecordInvalidAsync(db, sessionId, AdaptiveProjectionComponents.BarCloseAtm, BarCloseAtm.Version, barSeq, difference, ct)
                ? SupplementalOutcome.Mismatch
                : SupplementalOutcome.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                if (db.Entry(expected).State != EntityState.Detached) db.Entry(expected).State = EntityState.Detached;
            }
            catch (Exception)
            {
                // The context itself is unusable.
            }

            Interlocked.Increment(ref _failures);
            logger.LogError(ex, "Adaptive close-ATM write failed (core observer unaffected): session={SessionId}, bar={BarSeq}", sessionId, barSeq);
            return SupplementalOutcome.Failed;
        }
    }

    static bool NullableEqual(double? a, double? b) => a is null || b is null ? a is null && b is null : Math.Abs(a.Value - b.Value) <= DoubleTolerance;

    public static AdaptiveOptionStrikeBarRow MapStrike(long sessionId, int barSeq, DateTimeOffset endUtc, OptionStrikeBarMetrics m) => new()
    {
        SessionId = sessionId,
        BarSeq = barSeq,
        MetricsVersion = OptionStrikeBarMetrics.MetricsVersion,
        EndAvailableAtUtc = endUtc,
        Expiry = m.Expiry,
        Strike = m.Strike,
        Side = m.Side,
        Token = m.Token,
        TradingSymbol = m.TradingSymbol,
        PriceStart = m.PriceStart,
        PriceEnd = m.PriceEnd,
        PriceChange = m.PriceChange,
        TotalQuantity = m.TotalQuantity,
        StrictBuy = m.StrictBuy,
        StrictSell = m.StrictSell,
        StrictUnknown = m.StrictUnknown,
        StrictDelta = m.StrictDelta,
        EnrichedBuy = m.EnrichedBuy,
        EnrichedSell = m.EnrichedSell,
        EnrichedUnknown = m.EnrichedUnknown,
        EnrichedDelta = m.EnrichedDelta,
        TradeUpdates = m.TradeUpdates,
        OiStart = m.OiStart,
        OiEnd = m.OiEnd,
        OiDelta = m.OiDelta,
        IvStart = m.IvStart,
        IvEnd = m.IvEnd,
        IvChange = m.IvChange,
        ActivityPerSecond = m.ActivityPerSecond,
        QuoteAgeEndSeconds = m.QuoteAgeEndSeconds,
        TobTimeWeighted = m.TobTimeWeighted,
        MicroDevTimeWeighted = m.MicroDevTimeWeighted,
        TobUsableSeconds = m.TobUsableSeconds,
        NetOfi = m.NetOfi,
        GrossOfi = m.GrossOfi,
        OfiTransitions = m.OfiTransitions,
        DeltaStart = m.DeltaStart,
        DeltaEnd = m.DeltaEnd,
        DeltaChange = m.DeltaChange,
        VegaStart = m.VegaStart,
        VegaEnd = m.VegaEnd,
        VegaChange = m.VegaChange,
    };

    /// <summary>
    /// Compares EVERY column of a stored per-strike row with the recomputed one (reflection, so a column added later is covered automatically) - the same column set
    /// <see cref="StrikeAttestation.Digest"/> hashes - so a row can only be attested when nothing the digest covers disagrees. Doubles use the tolerance of the other sidecars.
    /// The one deliberate exclusion is the bar-end instant (the database stores microseconds, the engine 100 ns ticks): it is compared with a 1 ms tolerance here, and is not part of the digest.
    /// </summary>
    static string? StrikeDifference(AdaptiveOptionStrikeBarRow stored, AdaptiveOptionStrikeBarRow expected)
    {
        if ((stored.EndAvailableAtUtc - expected.EndAvailableAtUtc).Duration() > TimeSpan.FromMilliseconds(1))
            return $"{expected.Token}.EndAvailableAtUtc persisted={stored.EndAvailableAtUtc:O}, recomputed={expected.EndAvailableAtUtc:O}";
        var d = FirstDifference(stored, expected, nameof(AdaptiveOptionStrikeBarRow.EndAvailableAtUtc));
        return d is null ? null : $"{expected.Token}.{d}";
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
            return await RecordInvalidAsync(db, session.Id, AdaptiveProjectionComponents.OptionsSupplemental, expected.MetricsVersion, expected.BarSeq, difference, ct)
                ? SupplementalOutcome.Mismatch : SupplementalOutcome.Failed;
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

    static AdaptiveStrikeAttestationRow NewAttestation(long sessionId, int barSeq, string version, IReadOnlyCollection<AdaptiveOptionStrikeBarRow> rows) => new()
    {
        SessionId = sessionId, BarSeq = barSeq, MetricsVersion = version, TokenCount = rows.Count, Digests = StrikeAttestation.Format(rows), VerifiedAtUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>Upserts the attestation of the bar's stored (already compared) rows; true when a matching attestation is durable afterwards.</summary>
    async Task<bool> EnsureAttestationAsync(AdaptiveObserverDbContext db, long sessionId, string version, int barSeq, IReadOnlyCollection<AdaptiveOptionStrikeBarRow> stored, CancellationToken ct)
    {
        AdaptiveStrikeAttestationRow? row = null;
        var added = false;
        try
        {
            var digests = StrikeAttestation.Format(stored);
            row = await db.StrikeAttestations.SingleOrDefaultAsync(x => x.SessionId == sessionId && x.BarSeq == barSeq && x.MetricsVersion == version, ct);
            if (row is null)
            {
                row = NewAttestation(sessionId, barSeq, version, stored.ToList());
                db.StrikeAttestations.Add(row);
                added = true;
            }
            else if (row.Digests == digests && row.TokenCount == stored.Count)
            {
                return true;
            }
            else
            {
                row.Digests = digests; row.TokenCount = stored.Count; row.VerifiedAtUtc = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            logger.LogError(ex, "Could not record the per-strike verification attestation (rows stay withheld until it is written): session={SessionId}, bar={BarSeq}", sessionId, barSeq);
            try { if (row is not null && db.Entry(row).State != EntityState.Detached) db.Entry(row).State = added ? EntityState.Detached : EntityState.Unchanged; } catch (Exception) { /* context unusable */ }
            return false;
        }
    }

    /// <summary>
    /// A4 marker write for per-strike bars: inserts the bar's marker, or UNIONS the newly affected tokens into an existing one (a later mismatch with different tokens must not be lost;
    /// a whole-bar marker absorbs everything). One SaveChanges = one atomic row change; a concurrent-writer conflict is retried. Returns true only when the durable marker covers every
    /// token in <paramref name="badTokens"/> (an empty list = whole bar).
    /// </summary>
    internal async Task<bool> RecordUnverifiedStrikesAsync(AdaptiveObserverDbContext db, long sessionId, string version, int barSeq, IReadOnlyCollection<string> badTokens, string difference, CancellationToken ct)
    {
        const string component = AdaptiveProjectionComponents.OptionStrikeBars;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            AdaptiveProjectionHealthRow? row = null;
            AdaptiveStrikeAttestationRow? attestationRow = null;
            var added = false;
            try
            {
                row = await db.ProjectionHealth.SingleOrDefaultAsync(x => x.SessionId == sessionId && x.Component == component && x.Version == version && x.BarSeq == barSeq, ct);
                if (row is null)
                {
                    row = new AdaptiveProjectionHealthRow { SessionId = sessionId, Component = component, Version = version, BarSeq = barSeq, Detail = UnverifiedStrikeBars.FormatDetail(badTokens, difference), DetectedAtUtc = DateTimeOffset.UtcNow };
                    db.ProjectionHealth.Add(row);
                    added = true;
                }
                else
                {
                    var existing = UnverifiedStrikeBars.ParseTokens(row.Detail);
                    if (existing is null) return true;                                                   // already the whole bar
                    var union = UnverifiedStrikeBars.Union(existing, badTokens);
                    if (union is not null && union.SetEquals(existing)) return true;                      // nothing new
                    row.Detail = UnverifiedStrikeBars.FormatDetail(union ?? [], difference);
                    row.DetectedAtUtc = DateTimeOffset.UtcNow;
                }

                // The marker and the revocation of the bar's attestation are ONE SaveChanges: both or neither.
                attestationRow = await db.StrikeAttestations.SingleOrDefaultAsync(x => x.SessionId == sessionId && x.BarSeq == barSeq && x.MetricsVersion == version, ct);
                if (attestationRow is not null) db.StrikeAttestations.Remove(attestationRow);
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    if (attestationRow is not null && db.Entry(attestationRow).State != EntityState.Detached) db.Entry(attestationRow).State = EntityState.Unchanged;
                    if (row is not null && db.Entry(row).State != EntityState.Detached) db.Entry(row).State = added ? EntityState.Detached : EntityState.Unchanged;   // only the marker: the context is shared with core persistence
                }
                catch (Exception)
                {
                    // The context itself is unusable.
                }

                if (attempt == 2 || (!added && ex is not DbUpdateException))
                {
                    Interlocked.Increment(ref _failures);
                    logger.LogError(ex, "Could not record UNVERIFIED strike marker: session={SessionId}, bar={BarSeq}, tokens={Tokens}", sessionId, barSeq, badTokens.Count == 0 ? "*" : string.Join(',', badTokens));
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Durably marks a bar's sidecar as known-invalid (insert-if-missing; the sidecar row itself is never touched). A failure to write the
    /// marker is logged and swallowed: the core observer must never be affected by sidecar health bookkeeping.
    /// </summary>
    async Task<bool> RecordInvalidAsync(AdaptiveObserverDbContext db, long sessionId, string component, string version, int barSeq, string detail, CancellationToken ct)
    {
        AdaptiveProjectionHealthRow? marker = null;
        try
        {
            if (await db.ProjectionHealth.AsNoTracking().AnyAsync(x => x.SessionId == sessionId && x.Component == component
                    && x.Version == version && x.BarSeq == barSeq, ct))
            {
                return true;
            }

            marker = new AdaptiveProjectionHealthRow
            {
                SessionId = sessionId, Component = component, Version = version, BarSeq = barSeq,
                Detail = detail.Length > 512 ? detail[..512] : detail, DetectedAtUtc = DateTimeOffset.UtcNow,
            };
            db.ProjectionHealth.Add(marker);
            await db.SaveChangesAsync(ct);
            return true;
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

            return false;
        }
    }

    /// <summary>
    /// Persists/verifies both sidecars of a completed bar. Returns true when the integrity of each is established: Skipped, Inserted, Verified,
    /// or Mismatch (durably marked known-invalid, so consumers treat it as unavailable). Returns false when integrity is UNKNOWN (a write/verify
    /// failed): the paper path must not treat that bar as verified. One immediate retry absorbs a transient failure; the caller retries later.
    /// </summary>
    public async Task<bool> PersistAndVerifyAsync(AdaptiveObserverDbContext db, AdaptiveSessionStateRow session, AdaptiveCompletedBarPackage package, CancellationToken ct)
    {
        // The per-strike projection and the close ATM are display sidecars: their outcome never changes the verification result the paper path consumes (a missing table must not halt paper trading).
        // A Failed outcome is NOT dropped: the bar is queued (separately per sidecar) and retried on every later call, and on restart recovery replays every bar through this method.
        // A strike row without a durable attestation is withheld by every reader, so a failed or abandoned strike retry never needs a fence: absence of proof already means unverified.
        await RetryAsync(db, session.Id, package, _strikeRetry, StrikeRetryCapacity, PersistOrVerifyStrikesAsync, "per-strike", ct);
        await RetryAsync(db, session.Id, package, _closeAtmRetry, CloseAtmRetryCapacity, PersistOrVerifyCloseAtmAsync, "close-ATM", ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var futures = await PersistOrVerifyFuturesAsync(db, session.Id, package, ct);
            var options = await PersistOrVerifyOptionsAsync(db, session, package, ct);
            var basis = await PersistOrVerifyBasisAsync(db, session.Id, package, ct);
            if (futures != SupplementalOutcome.Failed && options != SupplementalOutcome.Failed && basis != SupplementalOutcome.Failed) return true;
        }

        return false;
    }

    readonly Dictionary<(long Session, int Bar), AdaptiveCompletedBarPackage> _strikeRetry = new();
    readonly Dictionary<(long Session, int Bar), AdaptiveCompletedBarPackage> _closeAtmRetry = new();
    internal int StrikeRetryCapacity { get; set; } = 200;
    internal int CloseAtmRetryCapacity { get; set; } = 200;

    /// <summary>Number of bars whose per-strike verification/attestation is still queued for retry.</summary>
    public int PendingStrikeRetryCount { get { lock (_strikeRetry) return _strikeRetry.Count; } }

    /// <summary>Number of bars whose close ATM is still queued for retry (independent of the strike queue).</summary>
    public int PendingCloseAtmRetryCount { get { lock (_closeAtmRetry) return _closeAtmRetry.Count; } }

    async Task RetryAsync(AdaptiveObserverDbContext db, long sessionId, AdaptiveCompletedBarPackage package, Dictionary<(long Session, int Bar), AdaptiveCompletedBarPackage> queue, int capacity,
        Func<AdaptiveObserverDbContext, long, AdaptiveCompletedBarPackage, CancellationToken, Task<SupplementalOutcome>> persist, string what, CancellationToken ct)
    {
        var key = (sessionId, package.FutureBar.BarSeq);
        AdaptiveCompletedBarPackage[] earlier;
        // A bounded number of the oldest queued bars is retried per call, so a persistent failure (e.g. a table not migrated yet) cannot multiply into a retry storm every bar.
        lock (queue) earlier = queue.Where(x => x.Key.Session == sessionId && x.Key.Bar != key.BarSeq).OrderBy(x => x.Key.Bar).Select(x => x.Value).Take(3).ToArray();
        foreach (var e in earlier)
        {
            if (await persist(db, sessionId, e, ct) != SupplementalOutcome.Failed)
                lock (queue) queue.Remove((sessionId, e.FutureBar.BarSeq));
        }

        var outcome = await persist(db, sessionId, package, ct);
        lock (queue)
        {
            if (outcome == SupplementalOutcome.Failed)
            {
                queue[key] = package;
                while (queue.Count > capacity)
                {
                    var oldest = queue.Keys.OrderBy(k => k.Bar).First();
                    queue.Remove(oldest);
                    logger.LogError("Retry queue overflow ({What}): dropped bar {BarSeq} of session {SessionId}; it stays withheld/unavailable until restart recovery re-verifies it", what, oldest.Bar, oldest.Session);
                }
            }
            else
            {
                queue.Remove(key);
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
        GrossOfi = m.GrossOfi,
    };

    static string? FirstDifference<TRow>(TRow actual, TRow expected, params string[] skip) where TRow : class
    {
        foreach (var property in typeof(TRow).GetProperties())
        {
            if (property.Name == "Id" || skip.Contains(property.Name)) continue;
            var a = property.GetValue(actual);
            var e = property.GetValue(expected);
            if (a is double av && e is double ev && double.IsFinite(av) && double.IsFinite(ev) && Math.Abs(av - ev) <= DoubleTolerance) continue;
            if (Equals(a, e)) continue;
            return $"{property.Name}: persisted={a ?? "null"}, recomputed={e ?? "null"}";
        }

        return null;
    }
}
