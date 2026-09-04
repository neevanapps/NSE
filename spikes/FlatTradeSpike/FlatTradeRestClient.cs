using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlatTradeSpike;

public sealed record ScripMatch(string Exch, string Tsym, string Token);

/// <summary>
/// FlatTrade's Pi Connect REST surface, just the two calls this spike needs: the
/// authorize-code token exchange, and a scrip search to find one live Nifty option
/// without needing the full instrument master (that's real Phase 1 work, not this spike).
///
/// Endpoints and the hash formula confirmed 2026-09-03 against the live docs at
/// pi.flattrade.in/docs, and the token exchange verified live against a real account the
/// same day -- the docs claimed the response uses a "status" field, but the real response
/// uses "stat" like every other FlatTrade endpoint. Docs were wrong on that one detail;
/// trust the live response shape here over the doc text.
/// </summary>
public sealed class FlatTradeRestClient(HttpClient http, string apiKey, string apiSecret)
{
    const string TokenExchangeUrl = "https://authapi.flattrade.in/trade/apitoken";
    const string RestBaseUrl = "https://piconnect.flattrade.in/PiConnectAPI/";

    public string AuthorizeUrl => $"https://auth.flattrade.in/?app_key={apiKey}";

    public async Task<string> ExchangeRequestCodeForTokenAsync(string requestCode, CancellationToken ct)
    {
        var hash = Sha256Hex(apiKey + requestCode + apiSecret);

        var payload = JsonSerializer.Serialize(new TokenExchangeRequest(apiKey, requestCode, hash));
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await http.PostAsync(TokenExchangeUrl, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Console.WriteLine($"[auth] {(int)response.StatusCode} {body}");

        var parsed = JsonSerializer.Deserialize<TokenExchangeResponse>(body, JsonOpts)
            ?? throw new InvalidOperationException("Empty response from token exchange.");

        if (!string.Equals(parsed.Stat, "Ok", StringComparison.OrdinalIgnoreCase) || parsed.Token is null)
        {
            throw new InvalidOperationException($"Token exchange failed: {parsed.Emsg ?? body}");
        }

        return parsed.Token;
    }

    public async Task<IReadOnlyList<ScripMatch>> SearchScripAsync(
        string uid, string accessToken, string exch, string searchText, CancellationToken ct)
    {
        // jData goes in as literal JSON text in the body (matches FlatTrade's own curl
        // examples), NOT URL-encoded -- the endpoint does a raw string split on "jData="/
        // "&jKey=" rather than form-decoding, so percent-encoding it produces "jData is not
        // valid json object". The real live bug (2026-09-04) was upstream of that: Serialize
        // was called without JsonOpts, so field names came out PascalCase ("Uid") instead of
        // the "uid" the server expects, which is what actually caused "uid is Missing".
        var jData = JsonSerializer.Serialize(new SearchScripRequest(uid, exch, searchText), JsonOpts);
        using var content = new StringContent($"jData={jData}&jKey={accessToken}", Encoding.UTF8, "text/plain");

        using var response = await http.PostAsync(RestBaseUrl + "SearchScrip", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var parsed = JsonSerializer.Deserialize<SearchScripResponse>(body, JsonOpts);
        if (parsed?.Values is null)
        {
            Console.WriteLine($"[search] no matches, raw response: {body}");
            return [];
        }

        return [.. parsed.Values.Select(v => new ScripMatch(v.Exch, v.Tsym, v.Token))];
    }

    static string Sha256Hex(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(bytes);
    }

    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    record TokenExchangeRequest(
        [property: JsonPropertyName("api_key")] string ApiKey,
        [property: JsonPropertyName("request_code")] string RequestCode,
        [property: JsonPropertyName("api_secret")] string ApiSecret);

    record TokenExchangeResponse(string? Stat, string? Emsg, string? Token, string? Client);

    record SearchScripRequest(string Uid, string Exch, string Stext);

    record SearchScripResponse(string? Stat, List<ScripItem>? Values);

    record ScripItem(string Exch, string Tsym, string Token);
}
