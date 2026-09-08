using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence;

/// <summary>EF-backed <see cref="IDataGapRecorder"/> -- see that interface for why Ingestion doesn't reference this project directly.</summary>
public sealed class EfDataGapRecorder(NiftySignalDbContext db) : IDataGapRecorder
{
    public async Task<long> RecordGapStartedAsync(DateTimeOffset startedAt, string reason, CancellationToken cancellationToken)
    {
        // Opens at 1, not 0 -- the failure that started the gap is itself a failed attempt.
        var gap = new DataGap { StartedAt = startedAt, Reason = reason, ReconnectAttempts = 1 };
        db.DataGaps.Add(gap);
        await db.SaveChangesAsync(cancellationToken);
        return gap.Id;
    }

    public async Task RecordGapAttemptAsync(long gapId, string reason, CancellationToken cancellationToken)
    {
        var gap = await db.DataGaps.FindAsync([gapId], cancellationToken);
        if (gap is null)
        {
            return;
        }

        gap.ReconnectAttempts++;
        // Only worth storing when it differs -- a gap full of identical messages tells us
        // nothing the opening Reason didn't already say.
        if (!string.Equals(gap.Reason, reason, StringComparison.Ordinal))
        {
            gap.LastReason = reason;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordGapEndedAsync(long gapId, DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        var gap = await db.DataGaps.FindAsync([gapId], cancellationToken);
        if (gap is not null)
        {
            gap.EndedAt = endedAt;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task CloseAllOpenGapsAsync(DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        var openGaps = await db.DataGaps.Where(g => g.EndedAt == null).ToListAsync(cancellationToken);
        foreach (var gap in openGaps)
        {
            gap.EndedAt = endedAt;
        }
        if (openGaps.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
