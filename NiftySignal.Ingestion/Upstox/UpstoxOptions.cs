namespace NiftySignal.Ingestion.Upstox;

public sealed class UpstoxOptions
{
    public const string SectionName = "Upstox";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public const string ApiBase = "https://api.upstox.com/";
    public const string InstrumentsUrl = "https://assets.upstox.com/market-quote/instruments/exchange/complete.json.gz";
}
