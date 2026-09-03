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
/// Endpoints, the hash formula, and the response shape (including that the token-exchange
/// response uses "status", not the "stat" field every other FlatTrade endpoint uses) are
/// confirmed 2026-09-03 against the live docs at pi.flattrade.in/docs. Still not exercised
/// against a real account.
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

        if (!string.Equals(parsed.Status, "Ok", StringComparison.OrdinalIgnoreCase) || parsed.Token is null)
        {
            throw new InvalidOperationException($"Token exchange failed: {parsed.Emsg ?? body}");
        }

        return parsed.Token;
    }

    public async Task<IReadOnlyList<ScripMatch>> SearchScripAsync(
        string uid, string accessToken, string exch, string searchText, CancellationToken ct)
    {
        var jData = JsonSerializer.Serialize(new SearchScripRequest(uid, exch, Uri.EscapeDataString(searchText)));
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

    record TokenExchangeResponse(string? Status, string? Emsg, string? Token, string? Client);

    record SearchScripRequest(string Uid, string Exch, string Stext);

    record SearchScripResponse(string? Stat, List<ScripItem>? Values);

    record ScripItem(string Exch, string Tsym, string Token);
}
