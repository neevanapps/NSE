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
/// Endpoints and the token-exchange hash formula are confirmed against FlatTrade's own
/// auth service (authapi.flattrade.in) independently of the reference iBuzz code, but the
/// token-exchange call was never exercised end-to-end in that reference app -- if it 404s
/// or rejects the content type, the likely fix is switching PostTokenExchange from a raw
/// JSON body to real application/x-www-form-urlencoded fields (see the comment there).
/// </summary>
public sealed class FlatTradeRestClient(HttpClient http, string apiKey, string apiSecret)
{
    const string TokenExchangeUrl = "https://authapi.flattrade.in/trade/apitoken";
    const string RestBaseUrl = "https://piconnect.flattrade.in/PiConnectTP/";

    public string AuthorizeUrl => $"https://auth.flattrade.in/?app_key={apiKey}";

    public async Task<string> ExchangeRequestCodeForTokenAsync(string requestCode, CancellationToken ct)
    {
        var hash = Sha256Hex(apiKey + requestCode + apiSecret);

        // Reference implementation sent this as a JSON string body under a
        // form-urlencoded content type (likely a bug in that unused code path, tolerated
        // by a lenient server). Sending it as real JSON here since that's what the bytes
        // actually were; switch to FormUrlEncodedContent below if this comes back non-OK.
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
        string uid, string susertoken, string exch, string searchText, CancellationToken ct)
    {
        var jData = JsonSerializer.Serialize(new SearchScripRequest(uid, exch, Uri.EscapeDataString(searchText)));
        using var content = new StringContent($"jData={jData}&jKey={susertoken}", Encoding.UTF8, "text/plain");

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

    record TokenExchangeResponse(string? Stat, string? Emsg, string? Token);

    record SearchScripRequest(string Uid, string Exch, string Stext);

    record SearchScripResponse(string? Stat, List<ScripItem>? Values);

    record ScripItem(string Exch, string Tsym, string Token);
}
