using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Ingestion.FlatTrade;

/// <summary>
/// The daily 08:45 job (plan section 3.1): ATM +/- 10 strikes for the nearest and next
/// weekly expiry, plus the underlying spot index, current-month future, and India VIX --
/// ~84 option instruments + 3 underlying. Built around a live-verified discovery (2026-09-04) that
/// simplifies the plan's original design: FlatTrade's GetOptionChain endpoint resolves
/// strikes around a reference price server-side, so this class doesn't need to compute
/// ATM from the raw instrument master itself -- it only uses the master
/// (<see cref="IInstrumentMasterProvider"/>) to discover which two expiries are nearest
/// and to find the current-month future, then lets GetOptionChain do the strike selection.
/// </summary>
public sealed class InstrumentUniverseResolver(
    IInstrumentMasterProvider masterProvider,
    FlatTradeAuthClient authClient,
    ILogger<InstrumentUniverseResolver> logger)
{
    /// <summary>NSE:26000, "Nifty 50" -- confirmed live 2026-09-04 via FlatTrade's NSE_Equity.csv scrip master segment.</summary>
    public const string NiftySpotToken = "26000";

    /// <summary>NSE:26017, "INDIAVIX" -- same scrip master segment as <see cref="NiftySpotToken"/>, confirmed live 2026-09-04.</summary>
    public const string IndiaVixToken = "26017";

    const string Underlying = "NIFTY";
    const int StrikeCountEachSide = 10;

    public async Task<IReadOnlyList<Instrument>> ResolveAsync(string sessionToken, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var master = await masterProvider.GetInstrumentMasterAsync(asOfDate, cancellationToken);

        var expiries = master
            .Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate is not null)
            .Select(i => i.ExpiryDate!.Value)
            .Distinct()
            .OrderBy(d => d)
            .Take(2)
            .ToList();

        if (expiries.Count < 2)
        {
            throw new InvalidOperationException(
                $"Expected at least 2 distinct {Underlying} option expiries in the instrument master, found {expiries.Count}.");
        }

        var nearestFuture = master
            .Where(i => i.InstrumentType == InstrumentType.Future && i.ExpiryDate is not null)
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No {Underlying} futures found in the instrument master.");

        var spotQuote = await authClient.GetQuotesAsync(sessionToken, Exchange.Nse, NiftySpotToken, cancellationToken);
        var midPrice = spotQuote.PreviousClose ?? spotQuote.LastPrice;

        logger.LogInformation(
            "Resolving instrument universe for {AsOfDate}: expiries {Nearest}/{Next}, mid price {MidPrice}",
            asOfDate, expiries[0], expiries[1], midPrice);

        var instruments = new List<Instrument>
        {
            new()
            {
                Token = NiftySpotToken,
                Exchange = Exchange.Nse,
                TradingSymbol = "Nifty 50",
                InstrumentType = InstrumentType.Index,
                Underlying = Underlying,
                LotSize = 1,
                TickSize = 0.05m,
                AsOfDate = asOfDate,
            },
            new()
            {
                Token = nearestFuture.Token,
                Exchange = nearestFuture.Exchange,
                TradingSymbol = nearestFuture.TradingSymbol,
                InstrumentType = InstrumentType.Future,
                ExpiryDate = nearestFuture.ExpiryDate,
                Underlying = Underlying,
                LotSize = nearestFuture.LotSize,
                TickSize = nearestFuture.TickSize,
                AsOfDate = asOfDate,
            },
            new()
            {
                Token = IndiaVixToken,
                Exchange = Exchange.Nse,
                TradingSymbol = "India VIX",
                InstrumentType = InstrumentType.Vix,
                Underlying = Underlying,
                LotSize = 1,
                // Not in the scrip master CSV; India VIX quotes to 2 decimals on NSE's
                // display, same "starting point, re-confirm if it looks mis-rounded"
                // status as the F&O DefaultTickSize in FlatTradeInstrumentMasterProvider.
                TickSize = 0.01m,
                AsOfDate = asOfDate,
            },
        };

        foreach (var expiry in expiries)
        {
            var anchor = master.First(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate == expiry);
            var chain = await authClient.GetOptionChainAsync(
                sessionToken, Exchange.Nfo, anchor.TradingSymbol, midPrice, StrikeCountEachSide, cancellationToken);

            instruments.AddRange(chain.Select(c => new Instrument
            {
                Token = c.Token,
                Exchange = c.Exchange,
                TradingSymbol = c.TradingSymbol,
                InstrumentType = InstrumentType.Option,
                OptionType = c.OptionType,
                StrikePrice = c.StrikePrice,
                ExpiryDate = expiry,
                Underlying = Underlying,
                LotSize = c.LotSize,
                TickSize = c.TickSize,
                AsOfDate = asOfDate,
            }));
        }

        logger.LogInformation("Resolved {Count} instruments for {AsOfDate}", instruments.Count, asOfDate);
        return instruments;
    }
}
