using NiftySignal.Domain.Entities;

namespace NiftySignal.Domain.Abstractions;

/// <summary>
/// The backtest/live parity boundary (plan section 1.5) -- the single non-negotiable
/// abstraction in this codebase. Live mode implements this over the FlatTrade WebSocket
/// (NiftySignal.Ingestion); backtest mode implements it by replaying stored ticks in
/// timestamp order (NiftySignal.Backtest). Everything downstream (features, scoring,
/// rules) consumes only this interface and must never branch on which implementation is
/// running -- if a caller ever needs an `if (isBacktest)`, this abstraction has a gap
/// that should be fixed here, not papered over at the call site.
/// </summary>
public interface ITickSource
{
    IAsyncEnumerable<Tick> ReadTicksAsync(CancellationToken cancellationToken);
}
