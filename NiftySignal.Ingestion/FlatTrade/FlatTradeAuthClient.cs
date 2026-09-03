using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NiftySignal.Domain.Enums;
using Microsoft.Extensions.Options;

namespace NiftySignal.Ingestion.FlatTrade;

public sealed record ScripMatch(Exchange Exchange, string TradingSymbol, string Token);

/// <summary>
/// The browser-based authorize -> request_code -> signed token exchange (plan section
/// 4.3), plus scrip search (used to resolve an instrument by name without the full
/// instrument master). Endpoints and the token-exchange hash formula are corroborated
/// against FlatTrade's own auth service independently of the reference code this was
/// adapted from, but that exact call was never exercised end-to-end before this was
/// written -- if <see cref="ExchangeRequestCodeForTokenAsync"/> comes back non-OK on the
/// first real run, the likely fix is switching the request body from JSON to real
/// application/x-www-form-urlencoded fields; see the comment inline.
/// </summary>
public sealed class FlatTradeAuthClient(HttpClient http, IOptions<FlatTradeOptions> options)
{
    readonly FlatTradeOptions _options = options.Value;

    public string AuthorizeUrl => _options.AuthorizeUrl;

    public async Task<string> ExchangeRequestCodeForTokenAsync(string requestCode, CancellationToken cancellationToken)
    {
        var hash = Sha256Hex(_options.ApiKey + requestCode + _options.ApiSecret);

        // Sent as real JSON here. The one reference implementation this was checked
        // against sent this same JSON string under a form-urlencoded content type
        // (likely a bug in an unexercised code path) -- if this fails, try
        // FormUrlEncodedContent with the same three fields instead.
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

        return parsed.Token;
    }

    public async Task<IReadOnlyList<ScripMatch>> SearchScripAsync(
        string sessionToken, Exchange exchange, string searchText, CancellationToken cancellationToken)
    {
        var exchCode = ExchangeCode(exchange);
        var jData = JsonSerializer.Serialize(new SearchScripRequest(_options.UserId, exchCode, Uri.EscapeDataString(searchText)));
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

    record TokenExchangeResponse(string? Stat, string? Emsg, string? Token);

    record SearchScripRequest(string Uid, string Exch, string Stext);

    record SearchScripResponse(string? Stat, List<ScripItem>? Values);

    record ScripItem(string Exch, string Tsym, string Token);
}
