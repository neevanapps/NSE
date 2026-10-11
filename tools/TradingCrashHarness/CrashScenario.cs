using NiftySignal.Domain;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Signals;

namespace TradingCrashHarness;

/// <summary>Three concurrent variants of one TEST/PLUMBING family (no strategy, no edge): X1 long with TP1 and a stop, X2 short with a 20-minute max hold, X3 long with a wider stop.</summary>
public static class CrashScenario
{
    public static readonly DateOnly Day = new(2026, 9, 22);
    public const string Session = "adaptive-session:2026-09-22:1";

    public static DateTimeOffset Ist(int h, int m, int s = 0) => new DateTimeOffset(Day.ToDateTime(new TimeOnly(h, m, s)), IstTime.Offset).ToUniversalTime();

    public static IReadOnlyList<OptionContract> Universe() => new[]
    {
        new OptionContract("CE23900", OptionSide.Call, 23900, Day, 65, 0.05m, "NIFTY26SEP23900CE"),
        new OptionContract("PE23900", OptionSide.Put, 23900, Day, 65, 0.05m, "NIFTY26SEP23900PE"),
    };

    public static TradingConfig Config()
    {
        var specs = new[]
        {
            Variant("X1", new ExitSpec { TradeSlPct = 0.10m, Tp1Pct = 0.10m, Tp1Proportion = 0.5m }),
            Variant("X2", new ExitSpec { MaxHold = TimeSpan.FromMinutes(20) }),
            Variant("X3", new ExitSpec { TradeSlPct = 0.15m }),
        };
        return new TradingConfig
        {
            ConfigVersion = "crash-harness",
            Costs = new CostsConfig { Schedules = new[] { NseIndexOptionsCostSchedules.From20260401(new FlatBrokerage { Amount = 20m, Unit = BrokerageUnit.PerOrder }) } },
            Session = SessionConfig.PaperLiveV1(),
            Variants = specs.ToDictionary(v => v.StrategyId + "/" + v.VariantId),
            Risk = RiskProfile.PaperLiveV1(10_000_000m),
        };
    }

    private static VariantSpec Variant(string id, ExitSpec exit) => new() { StrategyId = "X", VariantId = id, EntryCondition = new ImmediateEntrySpec(), Exit = exit };

    public static IReadOnlyList<IStrategyFamily> Families()
        => new IStrategyFamily[]
        {
            new PlumbingFamily("X", new Dictionary<string, PlumbingVariantScript>
            {
                ["X1"] = new(new[] { new PlumbingWindow(1, 1, StrategyDirection.Long) }),
                ["X2"] = new(new[] { new PlumbingWindow(1, 1, StrategyDirection.Short) }),
                ["X3"] = new(new[] { new PlumbingWindow(1, 1, StrategyDirection.Long) }),
            }),
        };

    public static LiveRunSpec Spec(string runId)
        => new(Day, Universe(), Config(), Families().ToDictionary(f => f.StrategyId), new RunSettings { RunId = runId, RunMode = "PaperLive" }, Session, "crash-harness-feed/1", "crash-harness-readiness/1",
            Durable: new DurableOptions { CheckpointEverySteps = 1 });

    /// <summary>The inputs in delivery order: groups (a List of events) and clock advances (a DateTimeOffset).</summary>
    public static IReadOnlyList<object> Steps()
    {
        var steps = new List<object>();
        long id = 0;
        var bar = 0;
        QuoteEvent Q(DateTimeOffset at, decimal bid, string token) => new(at, EventKind.OptionQuote, ++id, token, bid, bid + 1m, null, 100000, 100000);
        void Bar(DateTimeOffset at, decimal ce, decimal pe, double advance)
        {
            steps.Add(new List<EngineEvent> { Q(at, ce, "CE23900"), Q(at, pe, "PE23900"), new BarCompletedEvent(at, ++bar, "FUT") });
            steps.Add(at.AddSeconds(advance));
        }

        void Move(DateTimeOffset at, decimal? ce, decimal? pe)
        {
            var events = new List<EngineEvent>();
            if (ce is { } c)
            {
                events.Add(Q(at, c, "CE23900"));
            }

            if (pe is { } p)
            {
                events.Add(Q(at, p, "PE23900"));
            }

            steps.Add(events);
            steps.Add(at.AddSeconds(1.5));
        }

        var t = Ist(10, 0);
        Bar(t, 124m, 124m, 1.1);
        Move(t.AddSeconds(2.2), 140m, null);
        Move(t.AddSeconds(5.2), 100m, null);
        Bar(Ist(10, 5), 124m, 124m, 1.5);
        Move(Ist(10, 20, 30), null, 124m);
        steps.Add(Ist(10, 22));
        Bar(Ist(10, 30), 124m, 124m, 1.5);
        return steps;
    }

    /// <summary>Delivers the steps the way a restarted source does: nothing at or below the durable cursors, everything else.</summary>
    public static void Deliver(LiveEngineRun run, IEnumerable<object> steps)
    {
        foreach (var step in steps)
        {
            if (step is List<EngineEvent> group)
            {
                var events = group.Where(e => e switch
                {
                    BarCompletedEvent b => run.DurableBarCursor is not { } bar || b.BarSeq > bar,
                    QuoteEvent q => run.DurableQuoteCursor is not { } quote || q.SourceId > quote,
                    _ => true,
                }).ToList();
                if (events.Count > 0)
                {
                    run.Offer(events);
                }
            }
            else
            {
                run.Advance((DateTimeOffset)step);
            }
        }
    }
}
