using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Scheduling;

namespace NiftySignal.Tests.Trading.Core;

public class EngineLoopTests
{
    private static EngineLoop NewLoop(RecordingHandler h) => new(h, Clock.Ist(9, 0));

    [Fact]
    public void QuietGap_DeadlinesFireAtTheirOwnTimeBeforeTheLaterEvent()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        loop.Schedule(Clock.Ist(15, 0), DeadlineClass.SessionBoundary, ScheduledDeadline.GlobalOrdinal, "session:EntryClose");
        loop.Schedule(Clock.Ist(15, 10), DeadlineClass.SessionBoundary, ScheduledDeadline.GlobalOrdinal, "session:FinalClose");

        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(11, 0), 1) });
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(15, 20), 2) });

        Assert.Equal(
            new[]
            {
                $"E {Clock.Ist(11, 0):O} OptionQuote 1",
                $"D {Clock.Ist(15, 0):O} SessionBoundary -1 session:EntryClose",
                $"D {Clock.Ist(15, 10):O} SessionBoundary -1 session:FinalClose",
                $"E {Clock.Ist(15, 20):O} OptionQuote 2",
            },
            h.Log);
    }

    [Fact]
    public void DeadlineEqualToEventTime_FiresBeforeTheEvent()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        loop.Schedule(Clock.Ist(15, 0), DeadlineClass.SessionBoundary, ScheduledDeadline.GlobalOrdinal, "session:EntryClose");

        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(15, 0), 1) });

        Assert.StartsWith("D ", h.Log[0]);
        Assert.StartsWith("E ", h.Log[1]);
    }

    [Fact]
    public void EventOneTickBeforeDeadline_IsDeliveredBeforeIt()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        loop.Schedule(Clock.Ist(15, 0), DeadlineClass.SessionBoundary, ScheduledDeadline.GlobalOrdinal, "session:EntryClose");

        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(15, 0).AddTicks(-1), 1) });
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(15, 0).AddTicks(1), 2) });

        Assert.Equal(new[] { "E", "D", "E" }, h.Log.Select(l => l[..1]).ToArray());
    }

    [Fact]
    public void FutureEvents_AreNeverVisibleToAnEarlierDeadline()
    {
        var h = new RecordingHandler();
        var seenAtDeadline = new List<(DateTimeOffset Deadline, DateTimeOffset LatestEvent)>();
        var latestEvent = DateTimeOffset.MinValue;
        h.EventHook = (e, _) => latestEvent = e.AvailableAt;
        h.DeadlineHook = (d, _) => seenAtDeadline.Add((d.Time, latestEvent));
        var loop = NewLoop(h);
        foreach (var minute in new[] { 5, 10, 20, 30 })
        {
            loop.Schedule(Clock.Ist(12, minute), DeadlineClass.StrategyTimer, 0, "t" + minute);
        }

        // The 12:30 deadline sits between the groups at 12:25 and 12:35; no handler may see 12:35 data when it fires.
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(12, 1), 1) });
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(12, 25), 2) });
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(12, 35), 3) });

        Assert.All(seenAtDeadline, x => Assert.True(x.LatestEvent <= x.Deadline, $"deadline {x.Deadline:O} saw event {x.LatestEvent:O}"));
        Assert.Equal(h.Times.OrderBy(t => t).ToList(), h.Times);
    }

    [Fact]
    public void ChainedDeadlines_FireBeforeTheNextEvent_WithTheirOwnTime()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        h.DeadlineHook = (d, s) =>
        {
            if (d.Key == "order-maturity")
            {
                s.Schedule(s.Now.AddSeconds(5), DeadlineClass.ExecutionMaturity, 0, "order-expiry");
            }
            else if (d.Key == "order-expiry")
            {
                s.Schedule(s.Now.AddSeconds(1), DeadlineClass.RiskRetry, 0, "retry");
            }
        };
        loop.Schedule(Clock.Ist(13, 0), DeadlineClass.ExecutionMaturity, 0, "order-maturity");

        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(13, 1), 1) });

        Assert.Equal(
            new[]
            {
                $"D {Clock.Ist(13, 0):O} ExecutionMaturity 0 order-maturity",
                $"D {Clock.Ist(13, 0, 5):O} ExecutionMaturity 0 order-expiry",
                $"D {Clock.Ist(13, 0, 6):O} RiskRetry 0 retry",
                $"E {Clock.Ist(13, 1):O} OptionQuote 1",
            },
            h.Log);
    }

    [Fact]
    public void DeadlineCreatedByAnEventForTheSameInstant_DrainsBeforeTheGroupCallReturns()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        h.EventHook = (e, s) => s.Schedule(s.Now, DeadlineClass.ExecutionMaturity, 0, "immediate");

        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(10, 0), 1) });

        Assert.EndsWith("immediate", h.Log[^1]);
        Assert.Equal(0, loop.Stats.PendingDeadlines);
    }

    [Fact]
    public void SchedulingInThePast_IsRejected()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(10, 0), 1) });

        Assert.Throws<DeadlineInPastException>(() => loop.Schedule(Clock.Ist(9, 59), DeadlineClass.StrategyTimer, 0, "x"));
    }

    [Fact]
    public void CancelledDeadline_NeverFires()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        var handle = loop.Schedule(Clock.Ist(10, 0), DeadlineClass.StrategyTimer, 0, "x");
        Assert.True(loop.Cancel(handle));

        loop.Finish(Clock.Ist(15, 35));

        Assert.Empty(h.Log);
    }

    [Fact]
    public void GroupOrder_IsIndependentOfArgumentOrder()
    {
        var t = Clock.Ist(10, 0);
        var events = new EngineEvent[] { Ev.Bar(t, 1), Ev.Fut(t, 9), Ev.Opt(t, 4), Ev.Spot(t, 2), Ev.Opt(t, 3) };
        string[]? baseline = null;
        var rng = new Random(99);
        for (var i = 0; i < 50; i++)
        {
            var h = new RecordingHandler();
            var loop = NewLoop(h);
            loop.ProcessGroup(events.OrderBy(_ => rng.Next()).ToArray());
            baseline ??= h.Log.ToArray();
            Assert.Equal(baseline, h.Log);
        }

        Assert.Equal(new[] { "SpotQuote 2", "OptionQuote 3", "OptionQuote 4", "FutureQuote 9", "BarCompleted 1" }, baseline!.Select(l => string.Join(' ', l.Split(' ')[2..])).ToArray());
    }

    [Fact]
    public void ExactDuplicate_IsIgnored_ConflictingDuplicate_Faults()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        var q = Ev.Opt(Clock.Ist(10, 0), 1);
        loop.ProcessGroup(new[] { q, q });
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(10, 1), 2) });
        loop.ProcessGroup(new[] { q });

        Assert.Equal(2, loop.Stats.EventsDelivered);
        Assert.Equal(2, loop.Stats.DuplicatesIgnored);
        Assert.Throws<ConflictingDuplicateEventException>(() => loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(10, 2), 1, bid: 5m) }));
    }

    [Fact]
    public void LateGroup_AtOrBeforeTheProcessedBoundary_Faults()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(10, 0), 1) });

        Assert.Throws<LateEventException>(() => loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(10, 0), 2) }));
        Assert.Throws<LateEventException>(() => loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(9, 59), 3) }));
    }

    [Fact]
    public void GroupBehindAWatermark_IsLate()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        loop.Schedule(Clock.Ist(10, 0), DeadlineClass.StrategyTimer, 0, "x");
        loop.AdvanceTo(Clock.Ist(10, 0).AddTicks(1));

        // The watermark moved past 10:00, so an event at 10:00 can no longer be delivered.
        Assert.Throws<LateEventException>(() => loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(10, 0), 1) }));
    }

    [Fact]
    public void Watermark_FiresDeadlinesBeforeItButNotAtIt_AndFinishFiresTheRest()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        loop.Schedule(Clock.Ist(14, 0), DeadlineClass.StrategyTimer, 0, "a");
        loop.Schedule(Clock.Ist(14, 5), DeadlineClass.StrategyTimer, 0, "b");

        loop.AdvanceTo(Clock.Ist(14, 5));
        Assert.Single(h.Log);

        // An event exactly at the watermark is still allowed, and the deadline at the same instant still precedes it.
        loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(14, 5), 1) });
        Assert.Equal(new[] { "D", "D", "E" }, h.Log.Select(l => l[..1]).ToArray());
        Assert.Contains("a", h.Log[0]);
        Assert.Contains("b", h.Log[1]);
    }

    [Fact]
    public void Heartbeats_NeverChangeTheDispatchSequence()
    {
        var scenario = Scenarios.Random(seed: 7, events: 200);
        var baseline = Scenarios.Run(scenario, heartbeats: null);

        var rng = new Random(2024);
        for (var run = 0; run < 100; run++)
        {
            var heartbeats = Scenarios.RandomHeartbeats(scenario, rng);
            var log = Scenarios.Run(scenario, heartbeats);
            Assert.Equal(baseline, log);
        }
    }

    [Fact]
    public void RepeatedRuns_AreByteIdentical_SequentiallyAndInParallel_UnderDifferentCultures()
    {
        var scenario = Scenarios.Random(seed: 11, events: 300);
        var baseline = Scenarios.Run(scenario, heartbeats: null);

        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(baseline, Scenarios.Run(scenario, heartbeats: null));
        }

        var cultures = new[] { "en-US", "tr-TR", "de-DE", "ar-SA", "th-TH", "ja-JP", "fr-FR", "hi-IN" };
        var results = new string[cultures.Length * 2][];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            results[i] = Culture.Under(cultures[i % cultures.Length], () => Scenarios.Run(scenario, heartbeats: null)));

        Assert.All(results, r => Assert.Equal(baseline, r));
    }

    [Fact]
    public void Finished_LoopRejectsFurtherUse()
    {
        var loop = NewLoop(new RecordingHandler());
        loop.Finish(Clock.Ist(15, 35));

        Assert.Throws<EngineFinishedException>(() => loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(15, 36), 1) }));
        Assert.Throws<EngineFinishedException>(() => loop.Schedule(Clock.Ist(15, 40), DeadlineClass.StrategyTimer, 0, "x"));
    }

    [Fact]
    public void ReentrantUse_IsRejected()
    {
        var h = new RecordingHandler();
        var loop = NewLoop(h);
        h.EventHook = (_, _) => loop.AdvanceTo(Clock.Ist(11, 0));

        Assert.Throws<InvalidOperationException>(() => loop.ProcessGroup(new[] { Ev.Opt(Clock.Ist(10, 0), 1) }));
    }
}
