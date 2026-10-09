using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Session;

namespace NiftySignal.Tests.Trading.Core;

public class ReplayDriverTests
{
    private static (EngineLoop Loop, RecordingHandler Handler, SessionSchedule Schedule) Setup(SessionConfig? config = null)
    {
        var handler = new RecordingHandler();
        var schedule = SessionSchedule.Create(config ?? new SessionConfig(), Clock.Day);
        var loop = new EngineLoop(handler, schedule.DayStartUtc);
        schedule.ScheduleBoundaries(loop);
        return (loop, handler, schedule);
    }

    [Fact]
    public void FinalEventBeforeClose_StillRunsEveryDeadlineUpToTheHorizon()
    {
        var (loop, handler, schedule) = Setup();

        var result = ReplayDriver.Run(loop, new EngineEvent[] { Ev.Opt(Clock.Ist(11, 0), 1) }, schedule.SessionEndUtc);

        Assert.False(result.StoppedAtHorizon);
        Assert.Equal(0, result.Stats.PendingDeadlines);
        Assert.Equal(
            new[] { "E", "D", "D", "D" },
            handler.Log.Select(l => l[..1]).ToArray());
        Assert.Contains("session:EntryClose", handler.Log[1]);
        Assert.Contains("session:FinalClose", handler.Log[2]);
        Assert.Contains("session:SessionEnd", handler.Log[3]);
        Assert.Equal(schedule.SessionEndUtc, result.Stats.Now);
    }

    [Fact]
    public void EmptyStream_StillFiresEverySessionDeadline()
    {
        var (loop, handler, schedule) = Setup();

        var result = ReplayDriver.Run(loop, Array.Empty<EngineEvent>(), schedule.SessionEndUtc);

        Assert.Equal(3, result.Stats.DeadlinesFired);
        Assert.Equal(3, handler.Log.Count);
    }

    [Fact]
    public void HorizonBeforeLaterDeadlines_LeavesThemPending_AndDoesNotDeliverLaterEvents()
    {
        var (loop, handler, schedule) = Setup();

        var result = ReplayDriver.Run(
            loop,
            new EngineEvent[] { Ev.Opt(Clock.Ist(11, 0), 1), Ev.Opt(Clock.Ist(15, 5), 2), Ev.Opt(Clock.Ist(15, 6), 3) },
            Clock.Ist(15, 5));

        Assert.True(result.StoppedAtHorizon);
        Assert.Equal(2, result.Stats.EventsDelivered);
        Assert.Equal(2, result.Stats.PendingDeadlines);
        Assert.Equal(Clock.Ist(15, 5), result.Stats.Now);
        // EntryClose fired before the 15:05 event; FinalClose (15:10) and SessionEnd (15:35) are still pending.
        Assert.Equal(new[] { "E", "D", "E" }, handler.Log.Select(l => l[..1]).ToArray());
    }

    [Fact]
    public void EventAtTheHorizon_IsDelivered_AfterDeadlinesAtTheSameInstant()
    {
        var (loop, handler, _) = Setup();

        ReplayDriver.Run(loop, new EngineEvent[] { Ev.Opt(Clock.Ist(15, 0), 1) }, Clock.Ist(15, 0));

        Assert.Equal(new[] { "D", "E" }, handler.Log.Select(l => l[..1]).ToArray());
    }

    [Fact]
    public void UnorderedInput_Faults()
    {
        var (loop, _, schedule) = Setup();

        Assert.Throws<LateEventException>(() => ReplayDriver.Run(
            loop,
            new EngineEvent[] { Ev.Opt(Clock.Ist(11, 1), 1), Ev.Opt(Clock.Ist(11, 0), 2) },
            schedule.SessionEndUtc));
    }

    [Fact]
    public void ArrivalOrderWithinAnInstant_DoesNotMatter()
    {
        var t = Clock.Ist(10, 0);
        var a = new EngineEvent[] { Ev.Bar(t, 1), Ev.Fut(t, 2), Ev.Opt(t, 3), Ev.Opt(Clock.Ist(10, 1), 4) };
        var b = new EngineEvent[] { Ev.Opt(t, 3), Ev.Fut(t, 2), Ev.Bar(t, 1), Ev.Opt(Clock.Ist(10, 1), 4) };

        var first = Setup();
        var second = Setup();
        ReplayDriver.Run(first.Loop, a, first.Schedule.SessionEndUtc);
        ReplayDriver.Run(second.Loop, b, second.Schedule.SessionEndUtc);

        Assert.Equal(first.Handler.Log, second.Handler.Log);
    }

    [Fact]
    public void LiquidationStart_IsADeadlineOnlyWhenConfigured()
    {
        var without = Setup();
        var with = Setup(new SessionConfig { LiquidationStart = new TimeOnly(15, 5, 0) });

        ReplayDriver.Run(without.Loop, Array.Empty<EngineEvent>(), without.Schedule.SessionEndUtc);
        ReplayDriver.Run(with.Loop, Array.Empty<EngineEvent>(), with.Schedule.SessionEndUtc);

        Assert.DoesNotContain(without.Handler.Log, l => l.Contains("LiquidationStart"));
        Assert.Contains(with.Handler.Log, l => l.Contains($"D {Clock.Ist(15, 5):O}") && l.Contains("session:LiquidationStart"));
    }
}
