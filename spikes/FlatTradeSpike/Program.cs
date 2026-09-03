using FlatTradeSpike;

const string WsUrl = "wss://piconnect.flattrade.in/PiConnectWSTp/";
var duration = args.Length > 0 && int.TryParse(args[0], out var seconds)
    ? TimeSpan.FromSeconds(seconds)
    : TimeSpan.FromMinutes(5);

var config = SpikeConfig.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "appsettings.Local.json"))
    ?? SpikeConfig.Load("appsettings.Local.json");

if (config is null)
{
    Console.WriteLine("""
        Missing appsettings.Local.json next to Program.cs. Create it (gitignored) with:
        { "UserId": "<flattrade client id>", "ApiKey": "<pi connect api key>", "ApiSecret": "<pi connect api secret>" }
        """);
    return 1;
}

using var http = new HttpClient();
var rest = new FlatTradeRestClient(http, config.ApiKey, config.ApiSecret);

Console.WriteLine("Open this URL in a browser and log in:");
Console.WriteLine(rest.AuthorizeUrl);
Console.WriteLine();
Console.WriteLine("Paste back the redirect URL (or just the request_code value):");
var pasted = Console.ReadLine() ?? "";
var requestCode = ExtractRequestCode(pasted);

Console.WriteLine("Exchanging request_code for a session token...");
var token = await rest.ExchangeRequestCodeForTokenAsync(requestCode, CancellationToken.None);
Console.WriteLine("Got session token.");

Console.WriteLine("Searching NFO for a live NIFTY option...");
var matches = await rest.SearchScripAsync(config.UserId, token, exch: "NFO", searchText: "NIFTY", CancellationToken.None);
var option = PickAnOption(matches);
if (option is null)
{
    Console.WriteLine("No NIFTY option found via SearchScrip. Raw matches:");
    foreach (var m in matches.Take(10))
    {
        Console.WriteLine($"  {m.Exch} {m.Tsym} {m.Token}");
    }
    return 1;
}

Console.WriteLine($"Using instrument: {option.Exch} {option.Tsym} (token {option.Token})");

var outputDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output");
Directory.CreateDirectory(outputDir);
var logPath = Path.Combine(outputDir, $"ticks-{DateTime.Now:yyyyMMdd-HHmmss}.log");

await using var feed = new FlatTradeFeedClient(WsUrl, config.UserId, token);
await feed.ConnectAsync(logPath, CancellationToken.None);
await feed.SubscribeTouchlineAsync(option.Exch, option.Token, CancellationToken.None);
await feed.SubscribeDepthAsync(option.Exch, option.Token, CancellationToken.None);

Console.WriteLine($"Logging ticks to {logPath} for {duration}...");
await Task.Delay(duration);

Console.WriteLine();
Console.WriteLine("Message counts by type:");
foreach (var (type, count) in feed.MessageCountsByType.OrderByDescending(kv => kv.Value))
{
    Console.WriteLine($"  {type}: {count}");
}
Console.WriteLine($"Log written to {logPath}");
return 0;

static string ExtractRequestCode(string pasted)
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

static ScripMatch? PickAnOption(IReadOnlyList<ScripMatch> matches) =>
    matches.FirstOrDefault(m => m.Tsym.Contains("NIFTY", StringComparison.OrdinalIgnoreCase)
        && (m.Tsym.EndsWith("CE", StringComparison.OrdinalIgnoreCase) || m.Tsym.EndsWith("PE", StringComparison.OrdinalIgnoreCase)))
    ?? matches.FirstOrDefault(m => m.Tsym.Contains("NIFTY", StringComparison.OrdinalIgnoreCase));
