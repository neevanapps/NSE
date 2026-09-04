using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NiftySignal.Domain.Enums;
using Microsoft.Extensions.Options;

namespace NiftySignal.Ingestion.FlatTrade;

public sealed record ScripMatch(Exchange Exchange, string TradingSymbol, string Token);

public sealed record FlatTradeSessionToken(string Token, string ClientId);

/// <summary>Only the fields the instrument-universe job needs from GetQuotes -- not a full touchline snapshot.</summary>
public sealed record QuoteSnapshot(decimal LastPrice, decimal? PreviousClose);

public sealed record OptionChainEntry(
    Exchange Exchange, string TradingSymbol, string Token, OptionType OptionType, decimal StrikePrice, int LotSize, decimal TickSize);

/// <summary>
/// The browser-based authorize -> request_code -> signed token exchange (plan section
/// 4.3), plus scrip search (used to resolve an instrument by name without the full
/// instrument master). Endpoints and the hash formula confirmed 2026-09-03 against the
/// live docs at pi.flattrade.in/docs, and the token exchange verified live against a real
/// account the same day (via spikes/FlatTradeSpike) -- the docs claimed the response uses
/// a "status" field, but the real response uses "stat" like every other FlatTrade
/// endpoint. Docs were wrong on that one detail; this now matches the verified live shape.
/// </summary>
public sealed class FlatTradeAuthClient(HttpClient http, IOptions<FlatTradeOptions> options)
{
    readonly FlatTradeOptions _options = options.Value;

    public string AuthorizeUrl => _options.AuthorizeUrl;

    /// <summary>Accepts either a bare request_code or the full pasted redirect URL FlatTrade sends it back in.</summary>
    public static string ExtractRequestCode(string pasted)
    {
        pasted = pasted.Trim();
        foreach (var key in new[] { "request_code=", "code=" })
        {
            var idx = pasted.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var start = idx + key.Length;
            var end = pasted.IndexOf('&', start);
            return end < 0 ? pasted[start..] : pasted[start..end];
        }

        return pasted;
    }

    public async Task<FlatTradeSessionToken> ExchangeRequestCodeForTokenAsync(string requestCode, CancellationToken cancellationToken)
    {
        var hash = Sha256Hex(_options.ApiKey + requestCode + _options.ApiSecret);

        // Confirmed as real JSON (application/json), per the docs' own example.
        var payload = JsonSerializer.Serialize(new TokenExchangeRequest(_options.ApiKey, requestCode, hash));
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await http.PostAsync(_options.TokenExchangeUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        var parsed = JsonSerializer.Deserialize<TokenExchangeResponse>(body, JsonOpts)
            ?? throw new InvalidOperationException("Empty response from FlatTrade token exchange.");

        if (!string.Equals(parsed.Stat, "Ok", StringComparison.OrdinalIgnoreCase) || parsed.Token is null)
        {
            throw new InvalidOperationException($"FlatTrade token exchange failed: {parsed.Emsg ?? body}");
        }

        return new FlatTradeSessionToken(parsed.Token, parsed.Client ?? _options.UserId);
    }

    public async Task<IReadOnlyList<ScripMatch>> SearchScripAsync(
        string sessionToken, Exchange exchange, string searchText, CancellationToken cancellationToken)
    {
        var exchCode = ExchangeCode(exchange);
        // jData goes in as literal JSON text in the body (matches FlatTrade's own curl
        // examples), NOT URL-encoded -- the endpoint does a raw string split on "jData="/
        // "&jKey=" rather than form-decoding, so percent-encoding it produces "jData is not
        // valid json object". The real live bug (2026-09-04, via spikes/FlatTradeSpike) was
        // upstream of that: Serialize was called without JsonOpts, so field names came out
        // PascalCase ("Uid") instead of the "uid" the server expects, which is what actually
        // caused "uid is Missing".
        var jData = JsonSerializer.Serialize(new SearchScripRequest(_options.UserId, exchCode, searchText), JsonOpts);
        using var content = new StringContent($"jData={jData}&jKey={sessionToken}", Encoding.UTF8, "text/plain");

        using var response = await http.PostAsync(_options.RestBaseUrl + "SearchScrip", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        var parsed = JsonSerializer.Deserialize<SearchScripResponse>(body, JsonOpts);
        if (parsed?.Values is null)
        {
            return [];
        }

        return [.. parsed.Values.Select(v => new ScripMatch(ParseExchange(v.Exch), v.Tsym, v.Token))];
    }

    /// <summary>
    /// Live-verified 2026-09-04: the docs' response-field table for GetQuotes omits "c"
    /// (previous close), but the real response includes it -- same pattern as the other
    /// doc gaps found today. "lp" reflects previous close before the first trade of the
    /// day (exchanges carry it forward), so either field works for the instrument-universe
    /// job's ATM calculation; PreviousClose is preferred when present per plan 3.1.
    /// </summary>
    public async Task<QuoteSnapshot> GetQuotesAsync(string sessionToken, Exchange exchange, string token, CancellationToken cancellationToken)
    {
        var jData = JsonSerializer.Serialize(new GetQuotesRequest(_options.UserId, ExchangeCode(exchange), token), JsonOpts);
        using var content = new StringContent($"jData={jData}&jKey={sessionToken}", Encoding.UTF8, "text/plain");

        using var response = await http.PostAsync(_options.RestBaseUrl + "GetQuotes", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        var parsed = JsonSerializer.Deserialize<GetQuotesResponse>(body, JsonOpts)
            ?? throw new InvalidOperationException("Empty response from FlatTrade GetQuotes.");

        if (!string.Equals(parsed.Stat, "Ok", StringComparison.OrdinalIgnoreCase) || parsed.Lp is null)
        {
            throw new InvalidOperationException($"FlatTrade GetQuotes failed: {parsed.Emsg ?? body}");
        }

        return new QuoteSnapshot(
            decimal.Parse(parsed.Lp, CultureInfo.InvariantCulture),
            decimal.TryParse(parsed.C, NumberStyles.Number, CultureInfo.InvariantCulture, out var prevClose) ? prevClose : null);
    }

    /// <summary>
    /// Live-verified 2026-09-04: <paramref name="anchorTradingSymbol"/> only has to be
    /// *any* option or future of the target underlying+expiry -- its own strike is
    /// ignored, the returned chain is centered purely on <paramref name="midPrice"/>.
    /// Returns <paramref name="strikeCount"/> strikes on each side, for both CE and PE
    /// (matches the docs' math: strikeCount x 4 contracts total).
    /// </summary>
    public async Task<IReadOnlyList<OptionChainEntry>> GetOptionChainAsync(
        string sessionToken, Exchange exchange, string anchorTradingSymbol, decimal midPrice, int strikeCount, CancellationToken cancellationToken)
    {
        var jData = JsonSerializer.Serialize(
            new GetOptionChainRequest(_options.UserId, ExchangeCode(exchange), anchorTradingSymbol, midPrice.ToString(CultureInfo.InvariantCulture), strikeCount.ToString(CultureInfo.InvariantCulture)),
            JsonOpts);
        using var content = new StringContent($"jData={jData}&jKey={sessionToken}", Encoding.UTF8, "text/plain");

        using var response = await http.PostAsync(_options.RestBaseUrl + "GetOptionChain", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        var parsed = JsonSerializer.Deserialize<GetOptionChainResponse>(body, JsonOpts);
        if (!string.Equals(parsed?.Stat, "Ok", StringComparison.OrdinalIgnoreCase) || parsed?.Values is null)
        {
            throw new InvalidOperationException($"FlatTrade GetOptionChain failed: {parsed?.Emsg ?? body}");
        }

        return [.. parsed.Values
            .Where(v => v.Optt is "CE" or "PE")
            .Select(v => new OptionChainEntry(
                ParseExchange(v.Exch),
                v.Tsym,
                v.Token,
                v.Optt == "CE" ? OptionType.Call : OptionType.Put,
                decimal.Parse(v.Strprc, CultureInfo.InvariantCulture),
                int.Parse(v.Ls, CultureInfo.InvariantCulture),
                decimal.Parse(v.Ti, CultureInfo.InvariantCulture)))];
    }

    public static string ExchangeCode(Exchange exchange) => exchange switch
    {
        Exchange.Nse => "NSE",
        Exchange.Nfo => "NFO",
        Exchange.Bse => "BSE",
        Exchange.Bfo => "BFO",
        Exchange.Mcx => "MCX",
        Exchange.Cds => "CDS",
        _ => throw new ArgumentOutOfRangeException(nameof(exchange)),
    };

    public static Exchange ParseExchange(string code) => code switch
    {
        "NSE" => Exchange.Nse,
        "NFO" => Exchange.Nfo,
        "BSE" => Exchange.Bse,
        "BFO" => Exchange.Bfo,
        "MCX" => Exchange.Mcx,
        "CDS" => Exchange.Cds,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unrecognized FlatTrade exchange code."),
    };

    static string Sha256Hex(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    record TokenExchangeRequest(
        [property: JsonPropertyName("api_key")] string ApiKey,
        [property: JsonPropertyName("request_code")] string RequestCode,
        [property: JsonPropertyName("api_secret")] string ApiSecret);

    record TokenExchangeResponse(string? Stat, string? Emsg, string? Token, string? Client);

    record SearchScripRequest(string Uid, string Exch, string Stext);

    record SearchScripResponse(string? Stat, List<ScripItem>? Values);

    record ScripItem(string Exch, string Tsym, string Token);

    record GetQuotesRequest(string Uid, string Exch, string Token);

    record GetQuotesResponse(string? Stat, string? Emsg, string? Lp, string? C);

    record GetOptionChainRequest(string Uid, string Exch, string Tsym, string Strprc, string Cnt);

    record GetOptionChainResponse(string? Stat, string? Emsg, List<OptionChainItem>? Values);

    record OptionChainItem(string Exch, string Tsym, string Token, string Optt, string Strprc, string Ls, string Ti);
}
