using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Scheduling;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>
/// Seeded pseudo-random day: tied timestamps, long quiet gaps, deadlines that coincide with events, and handlers that chain
/// further deadlines (execution maturity -> expiry -> retry). Deterministic: the handler logic below uses no randomness.
/// </summary>
internal sealed record Scenario(
    IReadOnlyList<IReadOnlyList<EngineEvent>> Groups,
    IReadOnlyList<(DateTimeOffset Time, DeadlineClass Class, int Variant, string Key)> Deadlines,
    DateTimeOffset Start,
    DateTimeOffset Horizon);

internal static class Scenarios
{
    public static Scenario Random(int seed, int events)
    {
        var rng = new Random(seed);
        var start = Clock.Ist(9, 0);
        var times = new SortedSet<long>();
        while (times.Count < events / 3)
        {
            // Mostly dense mid-day activity, with occasional quiet gaps (the set is sparse around 12:00-14:00).
            var seconds = rng.Next(0, 6 * 3600 + 30 * 60);
            if (seconds is > 3 * 3600 and < 5 * 3600 && rng.Next(10) != 0)
            {
                continue;
            }

            times.Add(start.AddSeconds(seconds).AddMilliseconds(rng.Next(0, 4) * 250).UtcTicks);
        }

        var groups = new List<IReadOnlyList<EngineEvent>>();
        long id = 1;
        foreach (var ticks in times)
        {
            var at = new DateTimeOffset(ticks, TimeSpan.Zero);
            var group = new List<EngineEvent>();
            var n = rng.Next(1, 6);
            for (var i = 0; i < n; i++)
            {
                group.Add(rng.Next(4) switch
                {
                    0 => Ev.Spot(at, id++),
                    1 => Ev.Opt(at, id++, "OPT" + rng.Next(3)),
                    2 => Ev.Fut(at, id++),
                    _ => Ev.Bar(at, id++),
                });
            }

            groups.Add(group);
        }

        var deadlines = new List<(DateTimeOffset, DeadlineClass, int, string)>
        {
            (Clock.Ist(15, 0), DeadlineClass.SessionBoundary, ScheduledDeadline.GlobalOrdinal, "session:EntryClose"),
            (Clock.Ist(15, 10), DeadlineClass.SessionBoundary, ScheduledDeadline.GlobalOrdinal, "session:FinalClose"),
            (Clock.Ist(15, 35), DeadlineClass.SessionBoundary, ScheduledDeadline.GlobalOrdinal, "session:SessionEnd"),
        };
        for (var i = 0; i < 40; i++)
        {
            // Half of the timers land exactly on an event instant to exercise deadline-equals-event ties.
            var at = i % 2 == 0 && groups.Count > 0
                ? groups[rng.Next(groups.Count)][0].AvailableAt
                : start.AddSeconds(rng.Next(0, 7 * 3600)).AddMilliseconds(rng.Next(0, 1000));
            deadlines.Add((at, (DeadlineClass)rng.Next(4), rng.Next(-1, 3), "t" + rng.Next(5) + (i % 7 == 0 ? "chain0" : string.Empty)));
        }

        return new Scenario(groups, deadlines, start, Clock.Ist(15, 35));
    }

    public static Dictionary<int, List<DateTimeOffset>> RandomHeartbeats(Scenario scenario, Random rng)
    {
        var result = new Dictionary<int, List<DateTimeOffset>>();
        var previous = scenario.Start;
        for (var i = 0; i <= scenario.Groups.Count; i++)
        {
            var upper = i < scenario.Groups.Count ? scenario.Groups[i][0].AvailableAt : scenario.Horizon;
            var list = new List<DateTimeOffset>();
            var count = rng.Next(0, 4);
            for (var j = 0; j < count; j++)
            {
                var span = (upper - previous).Ticks;
                var pick = rng.Next(4) switch
                {
                    0 => upper,
                    1 => previous,
                    2 => scenario.Deadlines[rng.Next(scenario.Deadlines.Count)].Time,
                    _ => previous.AddTicks(span <= 0 ? 0 : rng.NextInt64(0, span + 1)),
                };
                if (pick >= previous && pick <= upper)
                {
                    list.Add(pick);
                }
            }

            list.Sort();
            result[i] = list;
            previous = upper;
        }

        return result;
    }

    public static string[] Run(Scenario scenario, Dictionary<int, List<DateTimeOffset>>? heartbeats, Action<EngineLoop>? afterStep = null)
    {
        var handler = new RecordingHandler();
        handler.EventHook = (e, s) =>
        {
            if (e is QuoteEvent { Kind: EventKind.OptionQuote } && e.SourceId % 17 == 0)
            {
                s.Schedule(s.Now.AddMilliseconds(1500), DeadlineClass.ExecutionMaturity, (int)(e.SourceId % 3), "maturity");
            }
        };
        handler.DeadlineHook = (d, s) =>
        {
            if (d.Key == "maturity")
            {
                s.Schedule(s.Now.AddSeconds(5), DeadlineClass.ExecutionMaturity, d.VariantOrdinal, "expiry");
            }
            else if (d.Key == "expiry")
            {
                s.Schedule(s.Now.AddSeconds(1), DeadlineClass.RiskRetry, d.VariantOrdinal, "retry");
            }
            else if (d.Key.EndsWith("chain0", StringComparison.Ordinal) && d.Time < Clock.Ist(15, 0))
            {
                s.Schedule(s.Now.AddMilliseconds(250), DeadlineClass.StrategyTimer, d.VariantOrdinal, "chain1");
            }
            else if (d.Key == "chain1")
            {
                s.Schedule(s.Now, DeadlineClass.StrategyTimer, d.VariantOrdinal, "chain2");
            }
        };

        var loop = new EngineLoop(handler, scenario.Start);
        foreach (var (time, deadlineClass, variant, key) in scenario.Deadlines)
        {
            loop.Schedule(time, deadlineClass, variant, key);
        }

        for (var i = 0; i < scenario.Groups.Count; i++)
        {
            if (heartbeats is not null && heartbeats.TryGetValue(i, out var beats))
            {
                foreach (var beat in beats)
                {
                    loop.AdvanceTo(beat);
                    afterStep?.Invoke(loop);
                }
            }

            loop.ProcessGroup(scenario.Groups[i]);
            afterStep?.Invoke(loop);
        }

        if (heartbeats is not null && heartbeats.TryGetValue(scenario.Groups.Count, out var tail))
        {
            foreach (var beat in tail)
            {
                loop.AdvanceTo(beat);
                afterStep?.Invoke(loop);
            }
        }

        loop.Finish(scenario.Horizon);
        afterStep?.Invoke(loop);
        return handler.Log.ToArray();
    }
}
