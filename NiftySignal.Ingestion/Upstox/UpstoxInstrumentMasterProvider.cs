using System.IO.Compression;
using System.Text.Json;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Ingestion.Upstox;

public sealed class UpstoxInstrumentMasterProvider(HttpClient http)
{
    DateOnly? _cachedDay;
    IReadOnlyList<Instrument> _cached = [];

    public async Task<IReadOnlyList<Instrument>> GetAsync(DateOnly day, CancellationToken ct)
    {
        if (_cachedDay == day) return _cached;
        using var response = await http.GetAsync(UpstoxOptions.InstrumentsUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var json = await JsonDocument.ParseAsync(gzip, cancellationToken: ct);
        _cached = Parse(json.RootElement, day);
        _cachedDay = day;
        return _cached;
    }

    public static IReadOnlyList<Instrument> Parse(JsonElement root, DateOnly day)
    {
        var result = new List<Instrument>();
        foreach (var row in root.EnumerateArray())
        {
            var key = Text(row, "instrument_key");
            var name = Text(row, "name");
            var type = Text(row, "instrument_type");
            var underlying = Text(row, "underlying_key") switch
            {
                "NSE_INDEX|Nifty 50" => "NIFTY", "NSE_INDEX|Nifty Bank" => "BANKNIFTY", "BSE_INDEX|SENSEX" => "SENSEX", _ => name,
            };
            var index = key is "NSE_INDEX|Nifty 50" or "NSE_INDEX|Nifty Bank" or "BSE_INDEX|SENSEX" or "NSE_INDEX|India VIX";
            if (!index && (underlying is not ("NIFTY" or "BANKNIFTY" or "SENSEX") || type is not ("FUT" or "CE" or "PE"))) continue;
            var exchange = key.Split('|')[0] switch
            {
                "NSE_INDEX" => Exchange.Nse, "BSE_INDEX" => Exchange.Bse, "NSE_FO" => Exchange.Nfo, "BSE_FO" => Exchange.Bfo,
                _ => throw new InvalidOperationException("Unexpected Upstox segment for tracked contract."),
            };
            DateOnly? expiry = null;
            if (!index)
            {
                var epoch = row.GetProperty("expiry").GetInt64();
                expiry = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(epoch).ToOffset(TimeSpan.FromHours(5.5)).DateTime);
                if (expiry < day) continue;
            }
            var tickSize = index ? (key.EndsWith("India VIX", StringComparison.Ordinal) ? 0.01m : 0.05m)
                : row.GetProperty("tick_size").GetDecimal() / 100m; // instrument master uses paise; feed prices are rupees
            var lot = index ? 1 : row.GetProperty("lot_size").GetInt32();
            if (lot <= 0 || tickSize <= 0) throw new InvalidOperationException("Invalid Upstox lot/tick size.");
            var internalToken = InternalToken(key);
            if (internalToken.Length > 32) throw new InvalidOperationException("Upstox internal token exceeds persistence limit.");
            result.Add(new Instrument
            {
                Provider = MarketDataProvider.Upstox, NativeInstrumentKey = key, Token = internalToken,
                Exchange = exchange, TradingSymbol = Text(row, "trading_symbol"), AsOfDate = day,
                Underlying = key switch { "NSE_INDEX|Nifty 50" => "NIFTY", "NSE_INDEX|Nifty Bank" => "BANKNIFTY", "BSE_INDEX|SENSEX" => "SENSEX", "NSE_INDEX|India VIX" => "NIFTY", _ => underlying },
                InstrumentType = index ? (key.EndsWith("India VIX", StringComparison.Ordinal) ? InstrumentType.Vix : InstrumentType.Index) : type == "FUT" ? InstrumentType.Future : InstrumentType.Option,
                OptionType = type == "CE" ? OptionType.Call : type == "PE" ? OptionType.Put : OptionType.None,
                ExpiryDate = expiry, StrikePrice = type is "CE" or "PE" ? row.GetProperty("strike_price").GetDecimal() : null,
                LotSize = lot, TickSize = tickSize,
            });
        }
        if (result.Select(x => x.Token).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidOperationException("Duplicate Upstox instrument keys in master.");
        return result;
    }

    public static string InternalToken(string key) => "UP:" + key;
    static string Text(JsonElement row, string name) => row.TryGetProperty(name, out var x) ? x.GetString() ?? "" : "";
}
