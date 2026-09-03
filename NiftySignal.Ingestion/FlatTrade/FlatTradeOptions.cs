namespace NiftySignal.Ingestion.FlatTrade;

/// <summary>Bound from the "FlatTrade" config section. ApiKey/ApiSecret belong in appsettings.Local.json only.</summary>
public sealed class FlatTradeOptions
{
    public const string SectionName = "FlatTrade";

    public required string UserId { get; set; }

    public required string ApiKey { get; set; }

    public required string ApiSecret { get; set; }

    public string RestBaseUrl { get; set; } = "https://piconnect.flattrade.in/PiConnectTP/";

    public string WebSocketUrl { get; set; } = "wss://piconnect.flattrade.in/PiConnectWSTp/";

    public string AuthorizeUrl => $"https://auth.flattrade.in/?app_key={ApiKey}";

    public string TokenExchangeUrl { get; set; } = "https://authapi.flattrade.in/trade/apitoken";
}
