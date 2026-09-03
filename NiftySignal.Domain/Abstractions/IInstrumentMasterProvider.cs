using NiftySignal.Domain.Entities;

namespace NiftySignal.Domain.Abstractions;

/// <summary>
/// Resolves the day's tradeable instrument universe (plan section 3.1: the 08:45 job).
/// Contract only for now -- FlatTrade's actual instrument-master file format/URL hasn't
/// been confirmed yet (Noren-family brokers typically publish a downloadable
/// exchange-segment file, e.g. Shoonya's NFO_symbols.txt.zip, but FlatTrade's exact
/// equivalent needs confirming against a live account, not guessed). The implementation
/// in NiftySignal.Ingestion is the next thing to build once the FlatTrade API key is
/// approved; everything else in the system can be built against this interface today.
/// </summary>
public interface IInstrumentMasterProvider
{
    Task<IReadOnlyList<Instrument>> GetInstrumentMasterAsync(DateOnly asOfDate, CancellationToken cancellationToken);
}
