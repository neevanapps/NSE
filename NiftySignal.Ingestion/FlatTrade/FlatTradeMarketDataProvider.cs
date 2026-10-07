using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Ingestion.FlatTrade;

public sealed class FlatTradeMarketDataProvider(
    InstrumentUniverseResolver nifty,
    SensexBankNiftyInstrumentUniverseResolver others,
    FlatTradeAuthClient auth,
    IInstrumentMasterProvider master,
    IOptions<FlatTradeOptions> options,
    ILoggerFactory loggers) : IMarketDataProvider
{
    public MarketDataProvider Provider => MarketDataProvider.FlatTrade;

    public async Task<IReadOnlyList<Instrument>> ResolveAsync(MarketDataCredential credential, DateOnly day, CancellationToken ct, IReadOnlySet<string>? existingUnderlyings = null)
    {
        var result = new List<Instrument>();
        if (existingUnderlyings?.Contains("NIFTY") != true) result.AddRange(await nifty.ResolveAsync(credential.Token, day, ct));
        try
        {
            if (existingUnderlyings?.Contains("SENSEX") != true || existingUnderlyings?.Contains("BANKNIFTY") != true)
                result.AddRange(await others.ResolveAsync(credential.Token, day, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger<FlatTradeMarketDataProvider>().LogWarning(ex, "Secondary index resolution failed; continuing with NIFTY");
        }
        return result;
    }

    public async Task<Instrument?> ResolveOptionAsync(MarketDataCredential credential, DateOnly day, DateOnly expiry, decimal strike, OptionType side, CancellationToken ct)
    {
        var instruments = await master.GetInstrumentMasterAsync(day, ct);
        var anchor = instruments.FirstOrDefault(x => x.Underlying == "NIFTY" && x.ExpiryDate == expiry && x.InstrumentType == InstrumentType.Option);
        if (anchor is null) return null;
        var chain = await auth.GetOptionChainAsync(credential.Token, Exchange.Nfo, anchor.TradingSymbol, strike, 2, ct);
        var match = chain.FirstOrDefault(x => x.StrikePrice == strike && x.OptionType == side);
        return match is null ? null : new Instrument
        {
            Token = match.Token, Exchange = match.Exchange, TradingSymbol = match.TradingSymbol,
            InstrumentType = InstrumentType.Option, Underlying = "NIFTY", ExpiryDate = expiry,
            StrikePrice = strike, OptionType = side, LotSize = match.LotSize, TickSize = match.TickSize,
            AsOfDate = day, Subscribed = false,
        };
    }

    public ILiveTickSource CreateFeed(MarketDataCredential credential, IReadOnlyList<Instrument> instruments, IDataGapRecorder gaps) =>
        new FlatTradeTickSource(options.Value, credential.ClientId, credential.Token,
            instruments.Select(x => (x.Exchange, x.Token)).ToList(), gaps, loggers.CreateLogger<FlatTradeTickSource>());
}
