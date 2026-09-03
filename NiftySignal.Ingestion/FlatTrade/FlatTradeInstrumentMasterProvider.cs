using System.Globalization;
using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Ingestion.FlatTrade;

/// <summary>
/// Downloads FlatTrade's publicly available NFO index-derivatives scrip master (no auth
/// required -- confirmed 2026-09-03 by fetching it directly) and filters it to one
/// underlying. This resolves every option/future FlatTrade currently lists for that
/// underlying; narrowing that down to ATM +/- 10 strikes (plan section 3.1) is a separate
/// concern for whatever orchestrates the 08:45 job, since that needs a spot/previous-close
/// price this class has no reason to know about.
/// </summary>
public sealed class FlatTradeInstrumentMasterProvider(
    HttpClient http,
    ILogger<FlatTradeInstrumentMasterProvider> logger) : IInstrumentMasterProvider
{
    public const string NfoIndexDerivativesUrl = "https://flattrade.s3.ap-south-1.amazonaws.com/scripmaster/Nfo_Index_Derivatives.csv";

    // NSE index derivatives tick size isn't in the scrip master CSV; 0.05 is the
    // exchange-wide standard for index F&O. Worth re-confirming if strike prices ever
    // look mis-rounded.
    const decimal DefaultTickSize = 0.05m;

    readonly string _underlying = "NIFTY";

    public async Task<IReadOnlyList<Instrument>> GetInstrumentMasterAsync(DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var csv = await http.GetStringAsync(NfoIndexDerivativesUrl, cancellationToken);
        var instruments = ParseNiftyInstruments(csv, _underlying, asOfDate);

        logger.LogInformation(
            "Loaded {Count} {Underlying} instruments from FlatTrade's scrip master (as of {AsOfDate})",
            instruments.Count, _underlying, asOfDate);

        return instruments;
    }

    /// <summary>Pure parsing, separated from the HTTP call so it's testable with a canned CSV string.</summary>
    public static IReadOnlyList<Instrument> ParseNiftyInstruments(string csv, string underlying, DateOnly asOfDate)
    {
        using var reader = new StringReader(csv);
        ValidateHeader(reader.ReadLine());

        var instruments = new List<Instrument>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length < 9)
            {
                continue;
            }

            if (!string.Equals(fields[3], underlying, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            instruments.Add(MapRow(fields, asOfDate));
        }

        return instruments;
    }

    static Instrument MapRow(string[] fields, DateOnly asOfDate)
    {
        // Exchange,Token,Lotsize,Symbol,Tradingsymbol,Instrument,Expiry,Strike,Optiontype
        var instrumentTypeCode = fields[5];
        var optionTypeCode = fields[8];
        var isOption = optionTypeCode is "CE" or "PE";

        return new Instrument
        {
            Token = fields[1],
            Exchange = FlatTradeAuthClient.ParseExchange(fields[0]),
            TradingSymbol = fields[4],
            InstrumentType = instrumentTypeCode == "OPTIDX" ? InstrumentType.Option : InstrumentType.Future,
            OptionType = optionTypeCode switch
            {
                "CE" => OptionType.Call,
                "PE" => OptionType.Put,
                _ => OptionType.None,
            },
            StrikePrice = isOption ? decimal.Parse(fields[7], CultureInfo.InvariantCulture) : null,
            ExpiryDate = DateOnly.ParseExact(fields[6], "dd-MMM-yyyy", CultureInfo.InvariantCulture),
            Underlying = fields[3],
            LotSize = int.Parse(fields[2], CultureInfo.InvariantCulture),
            TickSize = DefaultTickSize,
            AsOfDate = asOfDate,
        };
    }

    static void ValidateHeader(string? header)
    {
        const string Expected = "Exchange,Token,Lotsize,Symbol,Tradingsymbol,Instrument,Expiry,Strike,Optiontype";
        if (!string.Equals(header?.Trim(), Expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"FlatTrade scrip master CSV header changed shape (expected \"{Expected}\", got \"{header}\") -- column mapping needs updating.");
        }
    }
}
