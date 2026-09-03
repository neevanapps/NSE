using NiftySignal.Domain.Entities;

namespace NiftySignal.Domain.Abstractions;

/// <summary>
/// Resolves the day's tradeable instrument universe (plan section 3.1: the 08:45 job).
/// Implemented by <c>FlatTradeInstrumentMasterProvider</c> in NiftySignal.Ingestion, which
/// downloads FlatTrade's public (no-auth) scrip master CSV -- confirmed 2026-09-03, see
/// plan section 4.3. That implementation resolves the full option/future chain for an
/// underlying; narrowing to ATM +/- 10 strikes (needs a spot/previous-close price) is a
/// separate concern for whatever orchestrates the 08:45 job, not yet built.
/// </summary>
public interface IInstrumentMasterProvider
{
    Task<IReadOnlyList<Instrument>> GetInstrumentMasterAsync(DateOnly asOfDate, CancellationToken cancellationToken);
}
