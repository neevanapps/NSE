using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Scheduling;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>
/// CP2 prerequisite review of the CP1 clock: scheduled deadline time (ScheduledDeadline.Time, what a callback sees as Now) is
/// distinct from the loop's processing clock (EngineLoop.Now, which a heartbeat may have moved past the deadline). The invariant
/// under test: no deadline is ever left outstanding behind the clock, so a late dispatch can never move time backward.
/// </summary>
public class ClockInvariantTests
{
    private static EngineLoop NewLoop(RecordingHandler h) => new(h, Clock.Ist(9, 0));

    [Fact]
    public void HeartbeatAfterADeadline_FiresItAtItsOwnTime_WhileTheLoopClockMovesToTheWatermark()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        loop.Schedule(Clock.Ist(10, 0), DeadlineClass.StrategyTimer, 0, "d");

        loop.AdvanceTo(Clock.Ist(10, 5));

        Assert.Equal(new[] { $"D {Clock.Ist(10, 0):O} StrategyTimer 0 d" }, h.Log);   // callback saw 10:00, not 10:05
        Assert.Equal(Clock.Ist(10, 5), loop.Now);                                    // processing clock is the watermark
        Assert.Empty(loop.PendingDeadlines);
    }

    [Fact]
    public void HeartbeatExactlyAtADeadline_LeavesItPendingAtTheClock_AndNeverMovesTimeBackward()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        loop.Schedule(Clock.Ist(10, 0), DeadlineClass.StrategyTimer, 0, "d");

        loop.AdvanceTo(Clock.Ist(10, 0));

        Assert.Empty(h.Log);
        Assert.Equal(Clock.Ist(10, 0), loop.Now);
        Assert.Equal(Clock.Ist(10, 0), Assert.Single(loop.PendingDeadlines).Time);   // not behind the clock

        // A repeated or older heartbeat is a no-op.
        loop.AdvanceTo(Clock.Ist(10, 0));
        loop.AdvanceTo(Clock.Ist(9, 59));
        Assert.Empty(h.Log);
        Assert.Equal(Clock.Ist(10, 0), loop.Now);

        // The next heartbeat fires it with Now == its own time, which equals (never precedes) the loop clock.
        loop.AdvanceTo(Clock.Ist(10, 0).AddTicks(1));
        Assert.Equal(new[] { $"D {Clock.Ist(10, 0):O} StrategyTimer 0 d" }, h.Log);
        Assert.Equal(Clock.Ist(10, 0).AddTicks(1), loop.Now);
    }

    [Fact]
    public void EqualTimeGroup_DeliveredAfterAHeartbeatAtThatInstant_StillSeesTheDeadlineFirst()
    {
        var baseline = new RecordingHandler();
        var loopA = NewLoop(baseline);
        loopA.Schedule(Clock.Ist(10, 0), DeadlineClass.SessionBoundary, -1, "b");
        loopA.ProcessGroup(new EngineEvent[] { Ev.Opt(Clock.Ist(10, 0), 1), Ev.Bar(Clock.Ist(10, 0), 2) });

        var delayed = new RecordingHandler();
        var loopB = NewLoop(delayed);
        loopB.Schedule(Clock.Ist(10, 0), DeadlineClass.SessionBoundary, -1, "b");
        loopB.AdvanceTo(Clock.Ist(9, 59, 59, 999));
        loopB.AdvanceTo(Clock.Ist(10, 0));           // heartbeat exactly at the group's instant: deadline must not fire yet
        Assert.Empty(delayed.Log);
        loopB.ProcessGroup(new EngineEvent[] { Ev.Bar(Clock.Ist(10, 0), 2), Ev.Opt(Clock.Ist(10, 0), 1) });   // delayed, shuffled delivery

        Assert.Equal(baseline.Log, delayed.Log);
        Assert.Equal(new[] { "D", "E", "E" }, delayed.Log.Select(l => l[..1]).ToArray());
    }

    [Fact]
    public void GroupDeliveredAfterTheWatermarkPassedIt_IsRejected_NotReordered()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        loop.Schedule(Clock.Ist(10, 0), DeadlineClass.StrategyTimer, 0, "d");

        loop.AdvanceTo(Clock.Ist(10, 0).AddTicks(1));   // deadline fired; watermark is past 10:00

        Assert.Throws<LateEventException>(() => loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(10, 0), 1) }));
        Assert.Single(h.Log);
    }

    [Fact]
    public void CallbackInsideAHeartbeat_CanChainDeadlinesBeforeTheWatermark_ButNotAfter()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        h.DeadlineHook = (d, s) =>
        {
            if (d.Key == "first")
            {
                s.Schedule(s.Now.AddMinutes(1), DeadlineClass.StrategyTimer, 0, "inside");
                s.Schedule(s.Now.AddMinutes(10), DeadlineClass.StrategyTimer, 0, "beyond");
            }
        };
        loop.Schedule(Clock.Ist(10, 0), DeadlineClass.StrategyTimer, 0, "first");

        loop.AdvanceTo(Clock.Ist(10, 5));

        Assert.Equal(2, h.Log.Count);
        Assert.Contains("inside", h.Log[1]);
        Assert.Equal("beyond", Assert.Single(loop.PendingDeadlines).Key);
        Assert.True(loop.PendingDeadlines[0].Time >= loop.Now);
    }

    [Fact]
    public void RandomisedOperations_NeverLeaveADeadlineBehindTheClock_AndNeverMoveTimeBackward()
    {
        var rng = new Random(31337);
        for (var seed = 1; seed <= 40; seed++)
        {
            var scenario = Scenarios.Random(seed, events: 150);
            var heartbeats = Scenarios.RandomHeartbeats(scenario, rng);
            var previousNow = DateTimeOffset.MinValue;
            var steps = 0;

            var log = Scenarios.Run(scenario, heartbeats, loop =>
            {
                steps++;
                Assert.True(loop.Now >= previousNow, "engine clock moved backward");
                previousNow = loop.Now;
                foreach (var pending in loop.PendingDeadlines)
                {
                    Assert.True(pending.Time >= loop.Now, $"deadline {pending.Key} at {pending.Time:O} left behind clock {loop.Now:O}");
                }
            });

            Assert.True(steps > 0);
            // Every callback ran at its scheduled time and in non-decreasing order of that time.
            var times = log.Select(l => DateTimeOffset.Parse(l.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(times.OrderBy(t => t).ToList(), times);
        }
    }
}
