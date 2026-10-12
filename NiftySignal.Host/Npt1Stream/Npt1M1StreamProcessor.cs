using Microsoft.Extensions.Logging;
using NiftySignal.Npt1M1;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Live;

namespace NiftySignal.Host.Npt1Stream;

/// <summary>Durable store of the finalized M1 frames. <c>true</c> = the frame is stored (or was already stored with the identical hash); <c>false</c> = a stored frame with the same identity has a DIFFERENT hash (divergence).</summary>
public interface IM1FrameStore
{
    Task<bool> EnsureAsync(M1Bar bar, DateTimeOffset nowUtc, CancellationToken ct);
}

/// <summary>Where the producer's output goes: the NPT1 paper trading run (implemented by <see cref="PaperLiveCoordinator"/> through <see cref="CoordinatorFeed"/>).</summary>
public interface IPaperFeed
{
    void OnGroup(DateTimeOffset at, IReadOnlyList<LiveQuote> quotes, IReadOnlyList<LiveBar> bars);

    void OnHeartbeat(DateTimeOffset watermark);

    void Finish();
}

public sealed class CoordinatorFeed(PaperLiveCoordinator coordinator) : IPaperFeed
{
    public void OnGroup(DateTimeOffset at, IReadOnlyList<LiveQuote> quotes, IReadOnlyList<LiveBar> bars) => coordinator.OnGroup(at, quotes, bars);

    public void OnHeartbeat(DateTimeOffset watermark) => coordinator.OnHeartbeat(watermark);

    public void Finish() => coordinator.FinishSession();
}

/// <summary>Counters and latency samples of the M1 stream (operational measurements; never an input to trading).</summary>
public sealed class Npt1M1Telemetry
{
    private readonly List<double> _barLatencyMs = new();
    private readonly List<double> _pollMs = new();

    public long Polls { get; private set; }

    public long RowsRead { get; private set; }

    public long GroupsReleased { get; private set; }

    public long GroupsDelivered { get; private set; }

    public long GroupsWarmupOnly { get; private set; }

    public long GroupsSkippedCatchUp { get; private set; }

    public long BarsProduced { get; private set; }

    public long DivergentFrames { get; private set; }

    public IReadOnlyList<double> BarLatencyMillis => _barLatencyMs;

    public IReadOnlyList<double> PollMillis => _pollMs;

    internal void Poll(double millis, int rows)
    {
        Polls++;
        RowsRead += rows;
        _pollMs.Add(millis);
    }

    internal void Released(int groups) => GroupsReleased += groups;

    internal void Delivered() => GroupsDelivered++;

    internal void WarmupOnly() => GroupsWarmupOnly++;

    internal void SkippedCatchUp() => GroupsSkippedCatchUp++;

    internal void Bar(double latencyMillis)
    {
        BarsProduced++;
        _barLatencyMs.Add(latencyMillis);
    }

    internal void Divergent() => DivergentFrames++;

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
        {
            return 0d;
        }

        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// Turns what the M1 producer releases into the NPT1 paper run's input. It owns the policy of WHICH groups reach the trading engine:
/// <list type="bullet">
/// <item>groups before 09:30 carry their finalized bars ONLY (warm-up: the strategies' windows include the completed pre-09:30 bars, no quote and no trade is possible);</item>
/// <item>groups from the live boundary on (the later of 09:30 and the moment this process began observing the day) are delivered completely (quotes and bars);</item>
/// <item>groups in between completed while nobody was observing (a restart, a late deployment) and are NOT given to the engine as decisions: no trade is ever fabricated for downtime. The engine sees a gap in bar numbers, blocks entries through its integrity machinery and the strategies re-warm.</item>
/// </list>
/// Every finalized frame is stored (or verified against the stored copy) BEFORE the engine sees it; a divergent frame is withheld, which degrades the feed and blocks entries.
/// </summary>
public sealed class Npt1M1StreamProcessor(IM1FrameStore frames, IPaperFeed feed, DateTimeOffset warmupEndUtc, DateTimeOffset liveBoundaryUtc, Npt1M1Telemetry telemetry, ILogger log, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task HandleAsync(IReadOnlyList<M1Group> groups, CancellationToken ct)
    {
        telemetry.Released(groups.Count);
        foreach (var group in groups)
        {
            var liveBars = new List<LiveBar>(group.Bars.Count);
            foreach (var bar in group.Bars)
            {
                var ok = await frames.EnsureAsync(bar, _clock(), ct);
                if (!ok)
                {
                    telemetry.Divergent();
                    log.LogError("NPT1 M1 frame {Seq} differs from the stored frame of the same identity; the bar is withheld from the engine and entries stay blocked.", bar.Event.BarSeq);
                }

                telemetry.Bar((_clock() - bar.Event.AvailableAt).TotalMilliseconds);
                liveBars.Add(new LiveBar(bar.Event.BarSeq, bar.Event.AvailableAt, bar.Event.Info!, ok, bar.Frame));
            }

            if (group.AvailableAt < warmupEndUtc)
            {
                if (liveBars.Count > 0)
                {
                    feed.OnGroup(group.AvailableAt, Array.Empty<LiveQuote>(), liveBars);
                    telemetry.WarmupOnly();
                }

                continue;
            }

            if (group.AvailableAt < liveBoundaryUtc)
            {
                telemetry.SkippedCatchUp();
                continue;
            }

            feed.OnGroup(group.AvailableAt, group.Quotes.Select(q => ToLive(q.Event)).ToList(), liveBars);
            telemetry.Delivered();
        }
    }

    public static LiveQuote ToLive(QuoteEvent q)
        => new(q.Token, q.SourceId, q.Kind == EventKind.FutureQuote, q.Bid ?? 0m, q.Ask ?? 0m, q.Last ?? 0m, q.BidQty ?? 0L, q.AskQty ?? 0L);
}
