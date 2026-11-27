using Microsoft.EntityFrameworkCore;

namespace NiftySignal.AdaptiveObserverData;

/// <summary>Writes <see cref="AdaptiveBarPublicationRow"/>s. Both statements are idempotent and never change an existing row.</summary>
public static class AdaptiveBarPublisher
{
    public const string UnknownIntegrity = "sidecar integrity unknown";

    public static Task PublishAsync(AdaptiveObserverDbContext db, long sessionId, int barSeq, bool verified, DateTimeOffset now, CancellationToken ct)
    {
        var reason = verified ? null : UnknownIntegrity;
        return db.Database.ExecuteSqlAsync(
            $@"INSERT INTO adaptive_bar_publications (""SessionId"", ""BarSeq"", ""Verified"", ""UnverifiedReason"", ""PublishedAtUtc"")
               VALUES ({sessionId}, {barSeq}, {verified}, {reason}, {now.ToUniversalTime()}) ON CONFLICT (""SessionId"", ""BarSeq"") DO NOTHING", ct);
    }

    /// <summary>Publishes every persisted bar of the session that has no publication yet (bars rebuilt by a recovery). A bar at or after <paramref name="unverifiedFromBar"/> is published as unverified; a bar at or after <paramref name="beforeBar"/> is left alone.</summary>
    public static Task<int> PublishMissingAsync(AdaptiveObserverDbContext db, long sessionId, int? unverifiedFromBar, DateTimeOffset now, CancellationToken ct, int? beforeBar = null)
    {
        var cap = unverifiedFromBar ?? int.MaxValue;
        var before = beforeBar ?? int.MaxValue;
        return db.Database.ExecuteSqlAsync(
            $@"INSERT INTO adaptive_bar_publications (""SessionId"", ""BarSeq"", ""Verified"", ""UnverifiedReason"", ""PublishedAtUtc"")
               SELECT b.""SessionId"", b.""BarSeq"", b.""BarSeq"" < {cap}, CASE WHEN b.""BarSeq"" < {cap} THEN NULL ELSE {UnknownIntegrity} END, {now.ToUniversalTime()}
               FROM adaptive_future_bars b WHERE b.""SessionId"" = {sessionId} AND b.""BarSeq"" < {before}
               ON CONFLICT (""SessionId"", ""BarSeq"") DO NOTHING", ct);
    }
}
