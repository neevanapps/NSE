using System.Text.Json;

namespace FlatTradeSpike;

/// <summary>
/// Loaded from appsettings.Local.json (gitignored, sits next to this file). Never commit
/// real values -- follow the same convention as NiftySignal.Host.
/// </summary>
public sealed record SpikeConfig(string UserId, string ApiKey, string ApiSecret)
{
    public static SpikeConfig? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SpikeConfig>(json, JsonOpts);
    }

    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}
