using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Ingestion.FlatTrade;

/// <summary>
/// Sibling to <see cref="FlatTradeInstrumentMasterProvider"/> for the Sensex/Bank Nifty raw-tick-
/// collection task (write-only/archive, future backtesting only -- see
/// <see cref="SensexBankNiftyInstrumentUniverseResolver"/> for the resolution logic built on top
/// of this). Deliberately NOT a modification of <see cref="FlatTradeInstrumentMasterProvider"/> --
/// Nifty's own provider/resolver must stay byte-identical, so this is a separate class registered
/// separately in DI, reusing <see cref="FlatTradeInstrumentMasterProvider.ParseNiftyInstruments"/>
/// unchanged (that method is already fully parameterized on <c>underlying</c> despite its name --
/// it has no NIFTY-specific logic beyond the "NIFTY" string literal Nifty's own caller passes in).
///
/// Bank Nifty trades in the same NFO index-derivatives segment Nifty itself does -- confirmed live
/// 2026-09-22 by fetching FlatTrade's public Nfo_Index_Derivatives.csv directly and finding
/// BANKNIFTY rows already present, so no new download is needed for it. Sensex trades on BSE's
/// derivatives segment (BFO) -- confirmed live the same day that
/// https://flattrade.s3.ap-south-1.amazonaws.com/scripmaster/Bfo_Index_Derivatives.csv is public
/// (no auth), returns SENSEX rows, and needs no different parsing shape (same 9-column header as
/// the NFO file).
/// </summary>
public sealed class SensexBankNiftyInstrumentMasterProvider(
    HttpClient http,
    ILogger<SensexBankNiftyInstrumentMasterProvider> logger) : IInstrumentMasterProvider
{
    public const string BfoIndexDerivativesUrl = "https://flattrade.s3.ap-south-1.amazonaws.com/scripmaster/Bfo_Index_Derivatives.csv";

    public const string SensexUnderlying = "SENSEX";
    public const string BankNiftyUnderlying = "BANKNIFTY";

    public async Task<IReadOnlyList<Instrument>> GetInstrumentMasterAsync(DateOnly asOfDate, CancellationToken cancellationToken)
    {
        // Same public NFO CSV FlatTradeInstrumentMasterProvider already downloads for Nifty --
        // fetched again here rather than shared, since the two providers are deliberately
        // independent (no shared mutable state, no risk of one's failure affecting the other).
        var nfoCsv = await http.GetStringAsync(FlatTradeInstrumentMasterProvider.NfoIndexDerivativesUrl, cancellationToken);
        var bfoCsv = await http.GetStringAsync(BfoIndexDerivativesUrl, cancellationToken);

        var bankNifty = FlatTradeInstrumentMasterProvider.ParseNiftyInstruments(nfoCsv, BankNiftyUnderlying, asOfDate);
        var sensex = FlatTradeInstrumentMasterProvider.ParseNiftyInstruments(bfoCsv, SensexUnderlying, asOfDate);

        logger.LogInformation(
            "Loaded {BankNiftyCount} BANKNIFTY (NFO) + {SensexCount} SENSEX (BFO) instruments from FlatTrade's scrip masters (as of {AsOfDate})",
            bankNifty.Count, sensex.Count, asOfDate);

        return [.. bankNifty, .. sensex];
    }
}
