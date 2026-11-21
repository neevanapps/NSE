using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>Test seeding helper: writes the positive attestation the Host would have written for directly-seeded current-version strike rows (readers withhold unattested ones).</summary>
public static class StrikeAttestSeed
{
    public static async Task AttestStrikesAsync(this AdaptiveObserverDbContext db)
    {
        await db.SaveChangesAsync();
        var version = OptionStrikeBarMetrics.MetricsVersion;
        var have = (await db.StrikeAttestations.AsNoTracking().Select(x => new { x.SessionId, x.BarSeq }).ToListAsync()).Select(x => (x.SessionId, x.BarSeq)).ToHashSet();
        foreach (var g in (await db.OptionStrikeBars.AsNoTracking().Where(x => x.MetricsVersion == version).ToListAsync()).GroupBy(x => (x.SessionId, x.BarSeq)))
        {
            if (have.Contains(g.Key)) continue;
            db.StrikeAttestations.Add(new AdaptiveStrikeAttestationRow { SessionId = g.Key.SessionId, BarSeq = g.Key.BarSeq, MetricsVersion = version, TokenCount = g.Count(), Digests = StrikeAttestation.Format(g), VerifiedAtUtc = DateTimeOffset.UtcNow });
        }

        await db.SaveChangesAsync();
    }
}
