using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NiftySignal.Ingestion.Upstox;

public sealed class UpstoxAuthClient(HttpClient http, IOptions<UpstoxOptions> options)
{
    public static string ExtractCode(string pasted)
    {
        pasted = pasted.Trim();
        if (!Uri.TryCreate(pasted, UriKind.Absolute, out var uri)) return pasted;
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "code")
                return Uri.UnescapeDataString(parts[1].Replace("+", " "));
        }
        throw new InvalidOperationException("The redirect URL contains no authorization code.");
    }

    public string AuthorizeUrl => "https://api.upstox.com/v2/login/authorization/dialog?response_type=code&client_id="
        + Uri.EscapeDataString(options.Value.ApiKey) + "&redirect_uri=" + Uri.EscapeDataString(options.Value.RedirectUri);

    public async Task<string> ExchangeAsync(string code, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ApiKey) || string.IsNullOrWhiteSpace(o.ApiSecret) || string.IsNullOrWhiteSpace(o.RedirectUri))
            throw new InvalidOperationException("Configure Upstox ApiKey, ApiSecret and RedirectUri in Dashboard appsettings.Local.json.");
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code.Trim(), ["client_id"] = o.ApiKey, ["client_secret"] = o.ApiSecret,
            ["redirect_uri"] = o.RedirectUri, ["grant_type"] = "authorization_code",
        });
        using var response = await http.PostAsync(UpstoxOptions.ApiBase + "v2/login/authorization/token", body, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Upstox token exchange failed (HTTP {(int)response.StatusCode}). Check the code, redirect URI and credentials.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Upstox returned no access token.");
    }

    public static DateTimeOffset RegularExpiry(DateTimeOffset now)
    {
        var ist = now.ToOffset(TimeSpan.FromHours(5.5));
        var cutoff = new DateTimeOffset(ist.Year, ist.Month, ist.Day, 3, 30, 0, ist.Offset);
        return (ist < cutoff ? cutoff : cutoff.AddDays(1)).ToUniversalTime();
    }

    public async Task ValidateMarketDataTokenAsync(string token, CancellationToken ct)
    {
        using var request = UpstoxRestClient.Request("v2/market-quote/ltp?instrument_key=" + Uri.EscapeDataString("NSE_INDEX|Nifty 50"), token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Upstox market-data token validation failed (HTTP {(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.GetProperty("status").GetString() != "success")
            throw new InvalidOperationException("Upstox market-data validation was unsuccessful.");
    }
}
