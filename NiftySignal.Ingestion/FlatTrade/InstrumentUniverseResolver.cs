using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Ingestion.FlatTrade;

/// <summary>
/// The daily 08:45 job (plan section 3.1): ATM +/- 15 strikes for the NEAREST weekly expiry only
/// (2026-11 change: previously nearest + next weekly, +/- 10 strikes), plus the underlying spot index,
/// current-month future, and India VIX -- up to roughly 60-62 option instruments + 3 underlying. The chain is centred on the
/// previous close (the exact contract count depends on the broker's count semantics and is logged, not assumed), so the usable headroom around the live ATM is smaller than 15 strikes (see the Market
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

    /// <summary>
    /// Validates a GetOptionChain response against the instrument master instead of trusting it: only contracts of the requested (nearest) expiry are kept, a token or a
    /// (strike, CE/PE) pair appears once, and coverage is checked for the expected ladder = the listed strikes within <see cref="StrikeCountEachSide"/> steps of the strike nearest
    /// the reference price (ties: the lower strike). The broker's exact count semantics (strikes each side vs. total, whether the ATM strike is added) are NOT assumed:
    /// incomplete coverage is LOGGED with the actual counts, never padded, never filled from another expiry, and the universe size is never asserted as a fixed number.
    /// </summary>
    internal IReadOnlyList<OptionChainEntry> ValidateChain(IReadOnlyList<OptionChainEntry> chain, IReadOnlyList<Instrument> master, DateOnly expiry, decimal midPrice, DateOnly asOfDate)
    {
        var masterByToken = master.Where(i => i.InstrumentType == InstrumentType.Option).GroupBy(i => i.Token).ToDictionary(g => g.Key, g => g.First());
        var accepted = new List<OptionChainEntry>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var seenSlots = new HashSet<(decimal, OptionType)>();
        int wrongExpiry = 0, duplicateToken = 0, duplicateSlot = 0, notInMaster = 0;
        foreach (var c in chain)
        {
            if (masterByToken.TryGetValue(c.Token, out var m) && m.ExpiryDate != expiry)
            {
                wrongExpiry++;                                                    // a contract of another expiry is never accepted into the nearest-expiry universe
                continue;
            }

            if (m is null) notInMaster++;
            if (!seenTokens.Add(c.Token)) { duplicateToken++; continue; }
            if (!seenSlots.Add((c.StrikePrice, c.OptionType))) { duplicateSlot++; continue; }
            accepted.Add(c);
        }

        var ladder = master.Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate == expiry && i.StrikePrice is not null)
            .Select(i => i.StrikePrice!.Value).Distinct().OrderBy(x => x).ToArray();
        var expected = new List<decimal>();
        if (ladder.Length > 0)
        {
            var centre = 0;
            for (var i = 1; i < ladder.Length; i++)
                if (Math.Abs(ladder[i] - midPrice) < Math.Abs(ladder[centre] - midPrice)) centre = i;                // strict <: a tie keeps the lower strike
            for (var i = Math.Max(0, centre - StrikeCountEachSide); i <= Math.Min(ladder.Length - 1, centre + StrikeCountEachSide); i++) expected.Add(ladder[i]);
        }

        var have = accepted.GroupBy(a => a.StrikePrice).ToDictionary(g => g.Key, g => g.Select(a => a.OptionType).ToHashSet());
        var missing = expected.SelectMany(k => new[] { OptionType.Call, OptionType.Put }
            .Where(t => !(have.TryGetValue(k, out var types) && types.Contains(t))).Select(t => $"{k:0.##}{(t == OptionType.Call ? "CE" : "PE")}")).ToList();
        var unpairedStrikes = have.Count(kv => !(kv.Value.Contains(OptionType.Call) && kv.Value.Contains(OptionType.Put)));

        if (missing.Count > 0 || wrongExpiry + duplicateToken + duplicateSlot > 0 || unpairedStrikes > 0 || ladder.Length == 0)
        {
            logger.LogWarning(
                "NIFTY option-chain coverage INCOMPLETE for expiry {Expiry} (as-of {AsOfDate}, reference {Mid}): received {Received}, accepted {Accepted} over {Strikes} strikes; expected ATM+/-{Each} = {ExpectedStrikes} strikes x CE/PE; missing {MissingCount} [{Missing}]; strikes without both sides {Unpaired}; rejected: wrong-expiry {Wrong}, duplicate-token {DupToken}, duplicate-strike-side {DupSlot}. The next expiry is NOT substituted.",
                expiry, asOfDate, midPrice, chain.Count, accepted.Count, have.Count, StrikeCountEachSide, expected.Count, missing.Count, string.Join(',', missing.Take(12)) + (missing.Count > 12 ? ",..." : ""), unpairedStrikes, wrongExpiry, duplicateToken, duplicateSlot);
        }
        else
        {
            logger.LogInformation("NIFTY option chain complete for expiry {Expiry}: {Accepted} contracts over {Strikes} strikes (ATM+/-{Each}, CE and PE each).", expiry, accepted.Count, have.Count, StrikeCountEachSide);
        }

        if (notInMaster > 0) logger.LogWarning("{Count} option-chain contracts are not in the instrument master, so their expiry could not be cross-checked.", notInMaster);
        return accepted;
    }

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

            var accepted = ValidateChain(chain, master, expiry, midPrice, asOfDate);
            if (accepted.Count == 0)
                throw new InvalidOperationException($"GetOptionChain returned no usable {Underlying} option contracts for expiry {expiry:yyyy-MM-dd} (received {chain.Count}); the universe cannot be built.");

            instruments.AddRange(accepted.Select(c => new Instrument
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
