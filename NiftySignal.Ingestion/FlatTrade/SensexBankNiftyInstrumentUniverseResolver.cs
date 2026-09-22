using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Ingestion.FlatTrade;

/// <summary>
/// Sensex/Bank Nifty raw-tick-collection instrument resolver (write-only/archive, future
/// backtesting only) -- a sibling of <see cref="InstrumentUniverseResolver"/>, not a modification
/// of it or a generalization it's parameterized through, so Nifty's own resolution stays provably
/// untouched (see that class's own doc comment; nothing here is called from or calls into it).
///
/// Deliberately narrower than <see cref="InstrumentUniverseResolver"/> in three ways, all load-
/// bearing per the task this was built for:
/// <list type="bullet">
/// <item><b>Current expiry only, per index, not the nearest-two Nifty itself resolves</b> --
/// Sensex's own current WEEKLY expiry, Bank Nifty's own current MONTHLY expiry. Verified live
/// 2026-09-22 against FlatTrade's real scrip masters (see this class's own resolution logic and
/// <c>docs/SENSEX_BANKNIFTY_TICK_COLLECTION.md</c> for the actual expiry dates found) that Bank
/// Nifty's current NFO master has ONLY monthly-spaced BANKNIFTY expiries -- no weekly rows exist
/// at all right now -- so "nearest expiry" and "current monthly expiry" are the same value today;
/// this is re-verified (not assumed) on every resolution via <see cref="LogExpirySpacing"/>, which
/// warns if that ever stops being true (see its own doc comment).</item>
/// <item><b>No spot-index subscription.</b> FlatTrade's public scrip masters used here (NFO/BFO
/// index-derivatives CSVs) don't carry a spot-index row, and the equity/index CSV filenames that
/// might (guessed from <see cref="InstrumentUniverseResolver.NiftySpotToken"/>'s own doc comment,
/// e.g. "NSE_Equity.csv") returned S3 AccessDenied when tried live 2026-09-22 -- likely gated
/// behind auth this class doesn't have a reason to acquire. The task's own spec only asks for
/// "options + future if available" for these two indices, not spot, so this is in-scope as
/// resolved rather than a silent gap: each index's own nearest FUTURE quote (not a spot quote) is
/// used as <see cref="FlatTradeAuthClient.GetOptionChainAsync"/>'s ATM-centering <c>midPrice</c>
/// instead -- economically close to spot (small, well-understood basis), and already exactly the
/// data this class needs to fetch for the future leg anyway.</item>
/// <item><b>No scoring/volume-bar/rank-tracker resolution of any kind</b> -- this class returns
/// <see cref="Instrument"/> rows only, the same write-only contract
/// <see cref="MarketDataIngestionWorker"/>'s tick-flush path already has; nothing here computes or
/// persists anything beyond the instrument list itself.</item>
/// </list>
/// </summary>
public sealed class SensexBankNiftyInstrumentUniverseResolver(
    SensexBankNiftyInstrumentMasterProvider masterProvider,
    FlatTradeAuthClient authClient,
    ILogger<SensexBankNiftyInstrumentUniverseResolver> logger)
{
    // Same ATM+/-10 band Nifty's own resolver uses (InstrumentUniverseResolver.StrikeCountEachSide)
    // -- a structural/data-collection width, not a scoring/entry/exit threshold, so CLAUDE.md's
    // "every rule used for backtesting must be dynamic/DTE-based" constant-discipline doesn't apply
    // here (that rule is explicitly scoped to decision thresholds; a strike band's width is one of
    // its own named exceptions). Chosen to match Nifty's own band purely for consistency across the
    // two data sets, not derived from anything Sensex/Bank-Nifty-specific.
    const int StrikeCountEachSide = 10;

    /// <summary>Below this expiry gap (in days) between an underlying's two nearest option expiries, that underlying's master is weekly-shaped, not monthly -- used only to WARN if Bank Nifty's master ever stops being the monthly-only shape confirmed live 2026-09-22, never to gate or change which expiry gets resolved (the resolver always takes the single nearest expiry, whatever its spacing). Not a scoring/entry threshold -- a diagnostic tripwire only.</summary>
    const int MonthlySpacingWarnThresholdDays = 20;

    public async Task<IReadOnlyList<Instrument>> ResolveAsync(string sessionToken, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var master = await masterProvider.GetInstrumentMasterAsync(asOfDate, cancellationToken);

        var instruments = new List<Instrument>();
        instruments.AddRange(await ResolveOneAsync(
            master, SensexBankNiftyInstrumentMasterProvider.SensexUnderlying, Exchange.Bfo, sessionToken, asOfDate, cancellationToken));
        instruments.AddRange(await ResolveOneAsync(
            master, SensexBankNiftyInstrumentMasterProvider.BankNiftyUnderlying, Exchange.Nfo, sessionToken, asOfDate, cancellationToken));

        logger.LogInformation("Resolved {Count} SENSEX/BANKNIFTY tick-collection-only instruments for {AsOfDate}", instruments.Count, asOfDate);
        return instruments;
    }

    async Task<List<Instrument>> ResolveOneAsync(
        IReadOnlyList<Instrument> master, string underlying, Exchange exchange, string sessionToken, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var result = new List<Instrument>();

        var expiries = master
            .Where(i => i.Underlying == underlying && i.InstrumentType == InstrumentType.Option && i.ExpiryDate is not null)
            .Select(i => i.ExpiryDate!.Value)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (expiries.Count == 0)
        {
            logger.LogWarning("{Underlying}: no option expiries found in the instrument master -- skipping for {AsOfDate}.", underlying, asOfDate);
            return result;
        }

        // CURRENT expiry only -- deliberately not Take(2) the way InstrumentUniverseResolver
        // resolves Nifty's own nearest-two. Never includes expiries[1..] (next week/next month).
        var currentExpiry = expiries[0];
        LogExpirySpacing(underlying, expiries);

        var nearestFuture = master
            .Where(i => i.Underlying == underlying && i.InstrumentType == InstrumentType.Future && i.ExpiryDate is not null)
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefault();

        if (nearestFuture is null)
        {
            logger.LogWarning(
                "{Underlying}: no future found in the instrument master -- skipping options too for {AsOfDate} (no ATM-anchor price available without one; see this class's own doc comment on why spot isn't used).",
                underlying, asOfDate);
            return result;
        }

        result.Add(new Instrument
        {
            Token = nearestFuture.Token,
            Exchange = nearestFuture.Exchange,
            TradingSymbol = nearestFuture.TradingSymbol,
            InstrumentType = InstrumentType.Future,
            ExpiryDate = nearestFuture.ExpiryDate,
            Underlying = underlying,
            LotSize = nearestFuture.LotSize,
            TickSize = nearestFuture.TickSize,
            AsOfDate = asOfDate,
        });

        decimal midPrice;
        try
        {
            var futureQuote = await authClient.GetQuotesAsync(sessionToken, exchange, nearestFuture.Token, cancellationToken);
            midPrice = futureQuote.PreviousClose ?? futureQuote.LastPrice;
        }
        catch (Exception ex)
        {
            // Best-effort: the future instrument row above is still useful on its own (raw tick
            // collection for it doesn't need an option chain) -- a quote failure loses only this
            // underlying's options for today, not the whole resolution, and never Nifty's own.
            logger.LogWarning(ex, "{Underlying}: failed to fetch the future's quote for ATM anchoring -- skipping options for {AsOfDate}.", underlying, asOfDate);
            return result;
        }

        var anchor = master.First(i => i.Underlying == underlying && i.InstrumentType == InstrumentType.Option && i.ExpiryDate == currentExpiry);
        var chain = await authClient.GetOptionChainAsync(sessionToken, exchange, anchor.TradingSymbol, midPrice, StrikeCountEachSide, cancellationToken);

        result.AddRange(chain.Select(c => new Instrument
        {
            Token = c.Token,
            Exchange = c.Exchange,
            TradingSymbol = c.TradingSymbol,
            InstrumentType = InstrumentType.Option,
            OptionType = c.OptionType,
            StrikePrice = c.StrikePrice,
            ExpiryDate = currentExpiry,
            Underlying = underlying,
            LotSize = c.LotSize,
            TickSize = c.TickSize,
            AsOfDate = asOfDate,
        }));

        logger.LogInformation(
            "{Underlying}: resolved current expiry {Expiry} only (future + {OptionCount} options), mid price {MidPrice} from the future's own quote.",
            underlying, currentExpiry, chain.Count, midPrice);

        return result;
    }

    /// <summary>
    /// Diagnostic only (see <see cref="MonthlySpacingWarnThresholdDays"/>'s own doc comment) --
    /// warns if an underlying whose master was confirmed monthly-only live 2026-09-22 (Bank Nifty)
    /// ever shows a tight expiry gap again (weekly rows returning), so a silent shift back to
    /// weekly-shaped expiries gets noticed rather than quietly resolved as if it were still "the
    /// monthly one." Never changes which expiry gets resolved -- always the nearest one,
    /// unconditionally.
    /// </summary>
    void LogExpirySpacing(string underlying, List<DateOnly> expiries)
    {
        if (expiries.Count < 2)
        {
            return;
        }

        var gapDays = expiries[1].DayNumber - expiries[0].DayNumber;
        if (underlying == SensexBankNiftyInstrumentMasterProvider.BankNiftyUnderlying && gapDays < MonthlySpacingWarnThresholdDays)
        {
            logger.LogWarning(
                "BANKNIFTY: nearest two option expiries are only {GapDays}d apart ({NearestExpiry} -> {NextExpiry}) -- this master was confirmed monthly-only (large gaps) on 2026-09-22; a small gap here may mean weekly BANKNIFTY expiries have returned. Still resolving the single nearest expiry as 'current expiry', but this assumption should be re-checked against real master data before trusting it.",
                gapDays, expiries[0], expiries[1]);
        }
    }
}
