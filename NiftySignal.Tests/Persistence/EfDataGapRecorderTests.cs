using Microsoft.EntityFrameworkCore;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.Persistence;

public class EfDataGapRecorderTests
{
    static NiftySignalDbContext NewDb() => new(
        new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    static readonly DateTimeOffset At = new(2026, 9, 7, 6, 45, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordGapStarted_CountsTheOpeningFailure_AsTheFirstAttempt()
    {
        // A gap that reconnects first try should read as 1 attempt, not 0 -- the failure that
        // opened it was itself a failed attempt, and 0 would make "attempts" mean something
        // different from "failures during this outage".
        await using var db = NewDb();
        var recorder = new EfDataGapRecorder(db);

        var id = await recorder.RecordGapStartedAsync(At, "socket closed", CancellationToken.None);

        var gap = await db.DataGaps.FindAsync(id);
        Assert.Equal(1, gap!.ReconnectAttempts);
        Assert.Equal("socket closed", gap.Reason);
        Assert.Null(gap.LastReason);
    }

    [Fact]
    public async Task RecordGapAttempt_IncrementsTheCount_AndKeepsTheOpeningReasonIntact()
    {
        await using var db = NewDb();
        var recorder = new EfDataGapRecorder(db);
        var id = await recorder.RecordGapStartedAsync(At, "socket closed", CancellationToken.None);

        await recorder.RecordGapAttemptAsync(id, "502 from the gateway", CancellationToken.None);
        await recorder.RecordGapAttemptAsync(id, "502 from the gateway", CancellationToken.None);

        var gap = await db.DataGaps.FindAsync(id);
        Assert.Equal(3, gap!.ReconnectAttempts);
        // Reason still names how the outage began; LastReason names how it kept failing --
        // "dropped by the server, then refused on the way back in" is the pattern worth seeing.
        Assert.Equal("socket closed", gap.Reason);
        Assert.Equal("502 from the gateway", gap.LastReason);
    }

    [Fact]
    public async Task RecordGapAttempt_LeavesLastReasonNull_WhenEveryFailureIsTheSame()
    {
        // Storing a duplicate of Reason would add noise without adding information.
        await using var db = NewDb();
        var recorder = new EfDataGapRecorder(db);
        var id = await recorder.RecordGapStartedAsync(At, "socket closed", CancellationToken.None);

        await recorder.RecordGapAttemptAsync(id, "socket closed", CancellationToken.None);

        var gap = await db.DataGaps.FindAsync(id);
        Assert.Equal(2, gap!.ReconnectAttempts);
        Assert.Null(gap.LastReason);
    }

    [Fact]
    public async Task RecordGapAttempt_IsANoOp_ForAGapIdThatNoLongerExists()
    {
        await using var db = NewDb();
        var recorder = new EfDataGapRecorder(db);

        await recorder.RecordGapAttemptAsync(9999, "whatever", CancellationToken.None);

        Assert.Empty(await db.DataGaps.ToListAsync());
    }
}
