using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Domain.Abstractions;

public interface IMarketDataProvider
{
    MarketDataProvider Provider { get; }
    Task<IReadOnlyList<Instrument>> ResolveAsync(MarketDataCredential credential, DateOnly day, CancellationToken ct, IReadOnlySet<string>? existingUnderlyings = null);
    Task<Instrument?> ResolveOptionAsync(MarketDataCredential credential, DateOnly day, DateOnly expiry, decimal strike, OptionType side, CancellationToken ct);
    ILiveTickSource CreateFeed(MarketDataCredential credential, IReadOnlyList<Instrument> instruments, IDataGapRecorder gaps);
}
