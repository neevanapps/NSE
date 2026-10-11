using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Session;

namespace NiftySignal.Tests.Trading.Core;

public class SessionScheduleTests
{
    private static SessionSchedule Schedule(SessionConfig? config = null) => SessionSchedule.Create(config ?? new SessionConfig(), Clock.Day);

    [Fact]
    public void Defaults_MatchTheConfirmedRequirements_AndDoNotAssumeALiquidationStart()
    {
        var config = new SessionConfig();

        Assert.Equal(new TimeOnly(9, 30, 0), config.EntryOpen);
        Assert.Equal(new TimeOnly(15, 0, 0), config.EntryClose);
        Assert.Equal(new TimeOnly(15, 10, 0), config.FinalClose);
        Assert.Null(config.LiquidationStart);
        Assert.Equal("Asia/Kolkata", config.TimeZoneId);
    }

    [Fact]
    public void IstWallTimes_ConvertToTheCorrectUtcInstants()
    {
        var s = Schedule();

        Assert.Equal(new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero), s.EntryOpenUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 9, 30, 0, TimeSpan.Zero), s.EntryCloseUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 9, 40, 0, TimeSpan.Zero), s.FinalCloseUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 18, 30, 0, TimeSpan.Zero), s.DayStartUtc);
    }

    [Theory]
    [InlineData(9, 29, 59, 9_999_999L, false)]
    [InlineData(9, 30, 0, 0L, true)]
    [InlineData(9, 30, 0, 1L, true)]
    [InlineData(14, 59, 59, 9_999_999L, true)]
    [InlineData(15, 0, 0, 0L, false)]
    [InlineData(15, 0, 0, 1L, false)]
    [InlineData(15, 9, 0, 0L, false)]
    public void EntryEligibility_IsInclusiveAtOpenAndExclusiveAtClose(int h, int m, int s, long ticks, bool expected)
    {
        var schedule = Schedule();
        var instant = Clock.Ist(h, m, s).AddTicks(ticks);

        Assert.Equal(expected, schedule.IsEntryEligible(instant));
    }

    [Fact]
    public void EntryEligibility_RespectsTheTradeDate()
    {
        var schedule = Schedule();

        Assert.False(schedule.IsEntryEligible(Clock.Ist(10, 0).AddDays(1)));
        Assert.False(schedule.IsEntryEligible(Clock.Ist(10, 0).AddDays(-1)));
    }

    [Fact]
    public void Phases_FollowTheBoundaries()
    {
        var s = Schedule();

        Assert.Equal(SessionPhase.BeforeEntryWindow, s.PhaseAt(Clock.Ist(9, 29, 59)));
        Assert.Equal(SessionPhase.EntryWindow, s.PhaseAt(Clock.Ist(9, 30)));
        Assert.Equal(SessionPhase.AfterEntryClose, s.PhaseAt(Clock.Ist(15, 0)));
        Assert.Equal(SessionPhase.AfterFinalClose, s.PhaseAt(Clock.Ist(15, 10)));
        Assert.Equal(SessionPhase.Ended, s.PhaseAt(Clock.Ist(15, 35)));
    }

    [Fact]
    public void QuietMarket_BoundariesFireAtTheirScheduledTimes_NotAtTheNextEvent()
    {
        var handler = new RecordingHandler();
        var schedule = Schedule(new SessionConfig { LiquidationStart = new TimeOnly(15, 5, 0) });
        var loop = new EngineLoop(handler, schedule.DayStartUtc);
        schedule.ScheduleBoundaries(loop);

        // Last market event at 11:00; the next arrives only at 15:20 -- four boundaries lie in the quiet gap.
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(11, 0), 1) });
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(15, 20), 2) });

        Assert.Equal(
            new[]
            {
                $"E {Clock.Ist(11, 0):O} OptionQuote 1",
                $"D {Clock.Ist(15, 0):O} SessionBoundary -1 session:EntryClose",
                $"D {Clock.Ist(15, 5):O} SessionBoundary -1 session:LiquidationStart",
                $"D {Clock.Ist(15, 10):O} SessionBoundary -1 session:FinalClose",
                $"E {Clock.Ist(15, 20):O} OptionQuote 2",
            },
            handler.Log);
    }

    [Fact]
    public void QuoteAtExactlyEntryClose_SeesTheEntryCloseBoundaryFirst()
    {
        var handler = new RecordingHandler();
        var schedule = Schedule();
        var loop = new EngineLoop(handler, schedule.DayStartUtc);
        schedule.ScheduleBoundaries(loop);
        var entryClosedWhenQuoteArrived = false;
        var boundaryFired = false;
        handler.DeadlineHook = (d, _) => boundaryFired |= d.Key == SessionSchedule.BoundaryKey(SessionBoundaryKind.EntryClose);
        handler.EventHook = (e, _) => entryClosedWhenQuoteArrived = boundaryFired && !schedule.IsEntryEligible(e.AvailableAt);

        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(15, 0), 1) });

        Assert.True(entryClosedWhenQuoteArrived);
    }

    [Fact]
    public void Boundaries_AreOrderedByTimeThenKind_AndSkipUnconfiguredLiquidation()
    {
        var plain = Schedule().Boundaries().Select(b => b.Kind).ToArray();
        var withLiquidation = Schedule(new SessionConfig { LiquidationStart = new TimeOnly(15, 5, 0) }).Boundaries().Select(b => b.Kind).ToArray();

        Assert.Equal(new[] { SessionBoundaryKind.EntryClose, SessionBoundaryKind.FinalClose, SessionBoundaryKind.SessionEnd }, plain);
        Assert.Equal(
            new[] { SessionBoundaryKind.EntryClose, SessionBoundaryKind.LiquidationStart, SessionBoundaryKind.FinalClose, SessionBoundaryKind.SessionEnd },
            withLiquidation);
    }

    [Theory]
    [InlineData("session:EntryClose", true)]
    [InlineData("session:FinalClose", true)]
    [InlineData("session:0", false)]
    [InlineData("session:Nope", false)]
    [InlineData("session:", false)]
    [InlineData("other:EntryClose", false)]
    public void BoundaryKeys_RoundTripAndRejectGarbage(string key, bool valid)
    {
        Assert.Equal(valid, SessionSchedule.TryParseBoundaryKey(key, out _));
    }

    [Fact]
    public void InvalidConfigs_AreRejectedWithReasons()
    {
        Assert.Throws<ArgumentException>(() => Schedule(new SessionConfig { EntryClose = new TimeOnly(9, 30, 0) }));
        Assert.Throws<ArgumentException>(() => Schedule(new SessionConfig { FinalClose = new TimeOnly(15, 0, 0) }));
        Assert.Throws<ArgumentException>(() => Schedule(new SessionConfig { SessionEnd = new TimeOnly(15, 9, 0) }));
        Assert.Throws<ArgumentException>(() => Schedule(new SessionConfig { LiquidationStart = new TimeOnly(15, 10, 0) }));
        Assert.Throws<ArgumentException>(() => Schedule(new SessionConfig { LiquidationStart = new TimeOnly(9, 30, 0) }));
        Assert.Throws<ArgumentException>(() => Schedule(new SessionConfig { TimeZoneId = "UTC" }));
    }

    [Fact]
    public void ScheduleBoundaries_ConvertsTheSameWallTimeOnDifferentDatesIndependently()
    {
        var a = SessionSchedule.Create(new SessionConfig(), new DateOnly(2026, 9, 8));
        var b = SessionSchedule.Create(new SessionConfig(), new DateOnly(2026, 10, 1));

        Assert.Equal(TimeSpan.FromDays(23), b.EntryCloseUtc - a.EntryCloseUtc);
    }
}
