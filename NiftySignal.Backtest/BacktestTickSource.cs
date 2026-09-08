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
/// PENDING (audit finding F32, 2026-09-08 third-party review -- see fix plan): this class and
/// PerformanceReportBuilder are both real and tested, but nothing wires them together yet --
/// no BacktestRunner drives this source's replayed ticks through LiveFeatureEngine.OnTick/
/// Sample/ComputeCadence on a clock keyed off tick timestamps (not DateTimeOffset.UtcNow, which
/// MarketDataIngestionWorker/LiveTradingEngine currently hardcode), then through the
/// entry/exit evaluation LiveTradingEngine performs live, into PaperTradeSimulator, into
/// PerformanceReportBuilder. Right now there is genuinely no way to backtest anything -- this is
/// the single biggest gap relative to the project's own "validate before trusting" plan, and the
/// prerequisite for validating every sign/weight question already tracked elsewhere (F23-F28).
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
