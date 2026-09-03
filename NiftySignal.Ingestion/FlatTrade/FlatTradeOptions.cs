namespace NiftySignal.Ingestion.FlatTrade;

/// <summary>
/// Bound from the "FlatTrade" config section. ApiKey/ApiSecret belong in
/// appsettings.Local.json only. Endpoints confirmed 2026-09-03 against the live docs at
/// pi.flattrade.in/docs, including a "PiConnectTP" -> "PiConnectAPI" / "PiConnectWSTp" ->
/// "PiConnectWSAPI" breaking change documented in that site's own changelog -- these are
/// NOT the URLs found in older reference code (e.g. iBuzz), which predate that change.
/// </summary>
public sealed class FlatTradeOptions
{
    public const string SectionName = "FlatTrade";

    public required string UserId { get; set; }

    public required string ApiKey { get; set; }

    public required string ApiSecret { get; set; }

    public string RestBaseUrl { get; set; } = "https://piconnect.flattrade.in/PiConnectAPI/";

    public string WebSocketUrl { get; set; } = "wss://piconnect.flattrade.in/PiConnectWSAPI/";

    public string AuthorizeUrl => $"https://auth.flattrade.in/?app_key={ApiKey}";

    public string TokenExchangeUrl { get; set; } = "https://authapi.flattrade.in/trade/apitoken";
}
