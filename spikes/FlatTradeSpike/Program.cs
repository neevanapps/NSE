using FlatTradeSpike;

const string WsUrl = "wss://piconnect.flattrade.in/PiConnectWSAPI/";

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

// Two-step, non-interactive by design: step 1 (no args) prints the authorize URL and
// exits; the human completes the browser login (that's a password/OTP step nobody but
// them should be entering) and this is run again with the pasted-back redirect as an
// argument to do the rest. A blocking Console.ReadLine() doesn't fit a flow where the
// browser step happens out-of-band from whatever's driving this process.
if (args.Length == 0)
{
    Console.WriteLine("Step 1: open this URL in a browser and log in:");
    Console.WriteLine(rest.AuthorizeUrl);
    Console.WriteLine();
    Console.WriteLine("Step 2: run again with the pasted redirect URL (or just the request_code) as the first argument:");
    Console.WriteLine("""  dotnet run --project spikes/FlatTradeSpike -- "<pasted redirect or code>" [durationSeconds]""");
    return 0;
}

var requestCode = ExtractRequestCode(args[0]);
var duration = args.Length > 1 && int.TryParse(args[1], out var seconds)
    ? TimeSpan.FromSeconds(seconds)
    : TimeSpan.FromMinutes(5);

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
