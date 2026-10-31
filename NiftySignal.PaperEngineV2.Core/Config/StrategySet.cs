using NiftySignal.PaperEngineV2.Core.Evaluation;

namespace NiftySignal.PaperEngineV2.Core.Config;

/// <summary>The result of loading a whole configuration set at session start.</summary>
public sealed record StrategySetResult(
    IReadOnlyList<CompiledStrategy> Active, IReadOnlyList<StrategyConfig> DisabledValid, IReadOnlyList<(string Source, IReadOnlyList<ConfigError> Errors)> Rejected, IReadOnlyList<ConfigError> SetErrors)
{
    /// <summary>True when the set as a whole was accepted (no set-level error). Individual invalid strategies are rejected and simply cannot activate.</summary>
    public bool SetAccepted => SetErrors.Count == 0;
}

/// <summary>
/// Set-level rules: at most 30 ENABLED strategies (more rejects the WHOLE set - no partial activation), unique strategy ids, and the immutability rule (a known (id, version) must carry the same hash;
/// a changed hash under the same version is refused and a change needs a new version).
/// </summary>
public static class StrategySet
{
    public const int MaxEnabled = 30;

    public static StrategySetResult Load(IReadOnlyList<(string Source, string Json)> documents, IReadOnlyDictionary<(string StrategyId, int Version), string>? knownHashes = null)
    {
        var parsed = new List<(string Source, StrategyConfig Config)>();
        var rejected = new List<(string, IReadOnlyList<ConfigError>)>();
        var setErrors = new List<ConfigError>();
        foreach (var (source, json) in documents)
        {
            var r = StrategyConfigParser.Parse(json);
            if (r.IsValid) parsed.Add((source, r.Config!)); else rejected.Add((source, r.Errors));
        }

        foreach (var dup in parsed.GroupBy(x => x.Config.Identity.StrategyId, StringComparer.Ordinal).Where(g => g.Count() > 1))
            setErrors.Add(new ConfigError("DuplicateStrategyId", "$", $"strategyId '{dup.Key}' appears in {dup.Count()} files: {string.Join(", ", dup.Select(x => x.Source))}"));

        if (knownHashes is not null)
        {
            foreach (var (source, cfg) in parsed)
            {
                if (knownHashes.TryGetValue((cfg.Identity.StrategyId, cfg.Identity.Version), out var known) && !string.Equals(known, cfg.Identity.Hash, StringComparison.Ordinal))
                    setErrors.Add(new ConfigError("VersionHashConflict", source, $"{cfg.Identity.StrategyId} version {cfg.Identity.Version} was seen with a different configuration hash; a change needs a new version"));
            }
        }

        var enabled = parsed.Where(x => x.Config.Enabled).ToList();
        if (enabled.Count > MaxEnabled)
            setErrors.Add(new ConfigError("TooManyEnabled", "$", $"{enabled.Count} strategies are enabled; at most {MaxEnabled} are allowed and the whole set is rejected"));

        if (setErrors.Count > 0)
            return new StrategySetResult([], parsed.Where(x => !x.Config.Enabled).Select(x => x.Config).ToList(), rejected, setErrors);

        var active = enabled.OrderBy(x => x.Config.Identity.StrategyId, StringComparer.Ordinal).Select(x => CompiledStrategy.Compile(x.Config)).ToList();
        return new StrategySetResult(active, parsed.Where(x => !x.Config.Enabled).Select(x => x.Config).ToList(), rejected, setErrors);
    }
}
