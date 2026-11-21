using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Ingestion.FlatTrade;

/// <summary>
/// The daily 08:45 job (plan section 3.1): ATM +/- 15 strikes for the NEAREST weekly expiry only
/// (2026-11 change: previously nearest + next weekly, +/- 10 strikes), plus the underlying spot index,
/// current-month future, and India VIX -- ~62 option instruments + 3 underlying. The chain is centred on the
/// previous close, so the usable headroom around the live ATM is smaller than 15 strikes (see the Market
/// Summary Upper grid, which needs ATM+3). Built around a live-verified discovery (2026-09-04) that
/// simplifies the plan's original design: FlatTrade's GetOptionChain endpoint resolves
/// strikes around a reference price server-side, so this class doesn't need to compute
/// ATM from the raw instrument master itself -- it only uses the master
/// (<see cref="IInstrumentMasterProvider"/>) to discover the nearest expiry
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
    /// <summary>Strikes requested each side of the previous-close centre (2026-11: 10 -> 15). The ladder is NOT re-centred intraday, so headroom around the live ATM shrinks as spot drifts from the previous close.</summary>
    public const int StrikeCountEachSide = 15;

    public async Task<IReadOnlyList<Instrument>> ResolveAsync(string sessionToken, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var master = await masterProvider.GetInstrumentMasterAsync(asOfDate, cancellationToken);

        // Only the nearest weekly expiry: the earliest listed option expiry on or after the as-of date (an already-expired
        // row of a stale master is never picked). No next-week expiry is resolved or subscribed.
        var nearestExpiry = master
            .Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate is not null && i.ExpiryDate.Value >= asOfDate)
            .Select(i => i.ExpiryDate!.Value)
            .Distinct()
            .OrderBy(d => d)
            .Cast<DateOnly?>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No {Underlying} option expiry on or after {asOfDate:yyyy-MM-dd} in the instrument master.");
        var expiries = new List<DateOnly> { nearestExpiry };

        var nearestFuture = master
            .Where(i => i.InstrumentType == InstrumentType.Future && i.ExpiryDate is not null)
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No {Underlying} futures found in the instrument master.");

        var spotQuote = await authClient.GetQuotesAsync(sessionToken, Exchange.Nse, NiftySpotToken, cancellationToken);
        var midPrice = spotQuote.PreviousClose ?? spotQuote.LastPrice;

        logger.LogInformation(
            "Resolving instrument universe for {AsOfDate}: nearest expiry {Nearest}, +/-{Strikes} strikes, mid price {MidPrice}",
            asOfDate, nearestExpiry, StrikeCountEachSide, midPrice);

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
