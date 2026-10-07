using System.Net.Http.Headers;
using System.Text.Json;

namespace NiftySignal.Ingestion.Upstox;

public sealed class UpstoxRestClient(HttpClient http)
{
    public static HttpRequestMessage Request(string relative, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UpstoxOptions.ApiBase + relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    public async Task<decimal> PreviousCloseOrLastAsync(string token, string key, CancellationToken ct)
    {
        using var request = Request("v2/market-quote/quotes?instrument_key=" + Uri.EscapeDataString(key), token);
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var value = json.RootElement.GetProperty("data").EnumerateObject().Single().Value;
        var close = value.GetProperty("ohlc").GetProperty("close").GetDecimal();
        return close > 0 ? close : value.GetProperty("last_price").GetDecimal();
    }

    public async Task<Uri> AuthorizeFeedAsync(string token, CancellationToken ct)
    {
        using var request = Request("v3/feed/market-data-feed/authorize", token);
        using var response = await http.SendAsync(request, ct);
        EnsureSuccess(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var url = json.RootElement.GetProperty("data").GetProperty("authorized_redirect_uri").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "wss")
            throw new InvalidOperationException("Upstox returned an invalid WebSocket endpoint.");
        return uri;
    }

    static void EnsureSuccess(HttpResponseMessage response)
    {
        // Never include response bodies or authorized URLs: they may contain credentials.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Upstox request failed (HTTP {(int)response.StatusCode}).");
    }
}
