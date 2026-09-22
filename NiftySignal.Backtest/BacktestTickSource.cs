using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Persistence;

namespace NiftySignal.Backtest;

/// <summary>
/// The backtest half of plan section 1.5's non-negotiable design rule: "backtest mode
/// feeds it from the database replaying stored ticks in timestamp order." This is the
/// only thing that differs from live -- everything downstream (Features, Scoring, Rules)
/// consumes the same ITickSource interface FlatTradeTickSource implements, with zero
/// conditional logic branching on which one is running.
///
/// Replays across every tracked instrument by default (not one at a time), ordered purely
/// by exchange timestamp, since Features/Scoring need index-level aggregation across
/// strikes simultaneously -- narrowing to one instrument would misrepresent how the live
/// feed actually interleaves.
///
/// Audit finding F32 (2026-09-11): now wired up by <see cref="BacktestRunner"/>, which drives
/// this source's replayed ticks through LiveFeatureEngine.OnTick/Sample/ComputeCadence on a
/// clock keyed off tick timestamps, then through LiveTradingEngine.EvaluateCadenceAsync (the
/// same entry/exit evaluation used live), into PaperTradeSimulator, into
/// PerformanceReportBuilder. Correction to this comment's earlier claim (2026-09-08 draft): a
/// full read of LiveTradingEngine.cs found it does NOT hardcode DateTimeOffset.UtcNow anywhere
/// -- EvaluateCadenceAsync derives `now` entirely from snapshot.ComputedAt and threads that one
/// value through every downstream call, so it was already look-ahead-safe before BacktestRunner
/// existed. Only MarketDataIngestionWorker (the live-only orchestration loop, not replayed here)
/// uses UtcNow.
/// </summary>
public sealed class BacktestTickSource(
    NiftySignalDbContext db,
    DateTimeOffset from,
    DateTimeOffset to,
    IReadOnlyCollection<string>? instrumentTokens = null) : ITickSource
{
    public async IAsyncEnumerable<Tick> ReadTicksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = db.Ticks.Where(t => t.ExchangeTimestamp >= from && t.ExchangeTimestamp < to);

        if (instrumentTokens is { Count: > 0 })
        {
            query = query.Where(t => instrumentTokens.Contains(t.Token));
        }

        // ThenBy Id: a stable tiebreak for ticks sharing the same exchange timestamp
        // (plausible at 1-second-ish granularity across ~84 instruments), so replay order
        // is deterministic rather than however the database happens to return rows.
        var ordered = query.OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id);

        await foreach (var tick in ordered.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return tick;
        }
    }
}
