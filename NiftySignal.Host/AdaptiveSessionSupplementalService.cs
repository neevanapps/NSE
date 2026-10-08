using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

/// <summary>
/// Freezes the per-session supplemental identity (08-Oct plan section 71.3): the NIFTY spot/index instrument used for basis.
/// Resolved once per session from the trade date's instrument master and reused unchanged on every restart, including an
/// "unresolved" outcome, so replayed sidecar values never depend on later instrument-master changes.
/// </summary>
public sealed class AdaptiveSessionSupplementalService(ILogger<AdaptiveSessionSupplementalService> logger)
{
    public const string MetricsVersion = "session-supplemental-v1";

    public async Task<string?> GetOrFreezeSpotTokenAsync(
        NiftySignalDbContext source,
        AdaptiveObserverDbContext observer,
        AdaptiveSessionStateRow session,
        CancellationToken ct)
    {
        var existing = await observer.SessionSupplemental.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionId == session.Id && x.MetricsVersion == MetricsVersion, ct);
        if (existing is not null)
        {
            return existing.SpotToken;
        }

        var candidates = await source.Instruments.AsNoTracking()
            .Where(i => i.AsOfDate == session.TradeDate && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Index)
            .Select(i => new { i.Token, i.TradingSymbol })
            .Distinct()
            .ToListAsync(ct);

        var unique = candidates.Count == 1 ? candidates[0] : null;
        var row = new AdaptiveSessionSupplementalRow
        {
            SessionId = session.Id,
            MetricsVersion = MetricsVersion,
            SpotToken = unique?.Token,
            SpotSymbol = unique?.TradingSymbol,
            ResolutionProvenance = unique is null
                ? $"unresolved: {candidates.Count} NIFTY Index instruments in the {session.TradeDate:yyyy-MM-dd} instrument master"
                : "unique NIFTY Index instrument in the trade date's instrument master",
            ResolvedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            observer.SessionSupplemental.Add(row);
            await observer.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (!ct.IsCancellationRequested)
        {
            // Another writer froze it first: adopt the frozen identity, never a second resolution.
            observer.Entry(row).State = EntityState.Detached;
            var frozen = await observer.SessionSupplemental.AsNoTracking()
                .SingleAsync(x => x.SessionId == session.Id && x.MetricsVersion == MetricsVersion, ct);
            return frozen.SpotToken;
        }

        logger.LogInformation("Adaptive session {SessionId} spot instrument frozen: {Spot} ({Provenance})",
            session.Id, row.SpotToken ?? "none", row.ResolutionProvenance);
        return row.SpotToken;
    }
}
