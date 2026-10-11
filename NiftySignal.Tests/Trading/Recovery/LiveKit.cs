using NiftySignal.Tests.Trading.Core;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Recovery;

internal abstract record ScriptStep;

internal sealed record GroupStep(IReadOnlyList<EngineEvent> Events) : ScriptStep;

internal sealed record AdvanceStep(DateTimeOffset Watermark) : ScriptStep;

/// <summary>
/// A deterministic market script for the live run tests: the same group / advance sequence a bridge would deliver (a CE and a PE quote plus a finalized bar per bar, market
/// moves, clock advances), built once so that an uninterrupted run, a crashed-and-resumed run and a re-delivering source all see exactly the same inputs.
/// </summary>
internal sealed class LiveScript
{
    public const string Ce = PaperKit.Ce;
    public const string Pe = PaperKit.Pe;

    private long _id;
    private int _seq;

    public List<ScriptStep> Steps { get; } = new();

    public DateTimeOffset Bar(int hour, int minute, int second = 0, int millisecond = 0, decimal ceBid = 124m, decimal ceAsk = 125m, decimal peBid = 124m, decimal peAsk = 125m, double advance = 1.5, BarInfo? info = null)
    {
        var at = Clock.Ist(hour, minute, second, millisecond);
        _seq++;
        Steps.Add(new GroupStep(new EngineEvent[] { Quote(at, ceBid, ceAsk, Ce), Quote(at, peBid, peAsk, Pe), new BarCompletedEvent(at, _seq, "FUT", info is null ? null : info with { EndAvailableAt = at }) }));
        Steps.Add(new AdvanceStep(at.AddSeconds(advance)));
        return at;
    }

    public DateTimeOffset Move(DateTimeOffset at, decimal? ceBid = null, decimal? peBid = null, decimal spread = 1m, double advance = 1.5)
    {
        var events = new List<EngineEvent>();
        if (ceBid is { } c)
        {
            events.Add(Quote(at, c, c + spread, Ce));
        }

        if (peBid is { } p)
        {
            events.Add(Quote(at, p, p + spread, Pe));
        }

        Steps.Add(new GroupStep(events));
        Steps.Add(new AdvanceStep(at.AddSeconds(advance)));
        return at;
    }

    public void Advance(DateTimeOffset watermark) => Steps.Add(new AdvanceStep(watermark));

    public void Raw(params EngineEvent[] events) => Steps.Add(new GroupStep(events));

    public QuoteEvent Quote(DateTimeOffset at, decimal? bid, decimal? ask, string token) => new(at, EventKind.OptionQuote, ++_id, token, bid, ask, null, 100000, 100000);

    /// <summary>The number of steps that were scripted so far (a restart point).</summary>
    public int Count => Steps.Count;
}

internal static class LiveHarness
{
    public const string Session = "adaptive-session:2026-09-22:1";

    public static IReadOnlyList<OptionContract> Universe() => Kit.StandardUniverse().Select(c => c with { Symbol = $"NIFTY26SEP{c.Strike:0}{(c.Side == OptionSide.Call ? "CE" : "PE")}" }).ToList();

    public static LiveRunSpec Spec(TradingConfig config, IEnumerable<IStrategyFamily> families, string runId = "live-1", DurableOptions? durable = null, string session = Session)
        => new(
            Clock.Day,
            Universe(),
            config,
            families.ToDictionary(f => f.StrategyId, f => f),
            new RunSettings { RunId = runId, RunMode = "PaperLive" },
            session,
            "test-finalized-bars+option-quotes/1",
            "test-readiness/1",
            Durable: durable ?? new DurableOptions { CheckpointEverySteps = 1 });

    /// <summary>
    /// Delivers the script the way a restarted source does: events at or below the durable cursors (finalized bar, quote id) are not delivered again, everything else is,
    /// and an advance is offered again (it is a no-op when it fires nothing).
    /// </summary>
    public static void Deliver(LiveEngineRun run, IEnumerable<ScriptStep> steps)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case GroupStep g:
                {
                    var events = g.Events.Where(e => e switch
                    {
                        BarCompletedEvent b => run.DurableBarCursor is not { } bar || b.BarSeq > bar,
                        QuoteEvent q => run.DurableQuoteCursor is not { } quote || q.SourceId > quote,
                        _ => true,
                    }).ToList();
                    if (events.Count > 0)
                    {
                        run.Offer(events);
                    }

                    break;
                }

                case AdvanceStep a:
                    run.Advance(a.Watermark);
                    break;
            }
        }
    }

    public static IReadOnlyList<string> Payloads(LiveEngineRun run) => run.Engine.Outputs.Select(OutputCodec.Encode).ToList();

    public static PlumbingVariantScript Short(params (long From, long To)[] windows)
        => new(windows.Select(w => new PlumbingWindow(w.From, w.To, StrategyDirection.Short)).ToList());

    /// <summary>Crash seam: dies (abruptly, nothing after it runs) at the Nth reach of the given point.</summary>
    public sealed class CrashAt(CrashPoint point, int nth) : ICrashPoints
    {
        private int _seen;

        public bool Fired { get; private set; }

        public void Reached(CrashPoint reached, long stepSeq, string detail)
        {
            if (reached == point && ++_seen == nth)
            {
                Fired = true;
                throw new SimulatedProcessDeath($"{reached} step {stepSeq} ({detail})");
            }
        }
    }
}
