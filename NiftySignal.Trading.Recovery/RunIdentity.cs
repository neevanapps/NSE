using System.Globalization;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Recovery;

/// <summary>
/// Builds the run header and its explicit compatibility identity from a spec (contract section A.18.7). Everything that can change what the engine
/// does for identical inputs is hashed here; the binary provenance is kept separately and never compared.
/// </summary>
public static class RunIdentity
{
    public static CompatibilityIdentity BuildCompatibility(DurableRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var strategies = string.Join(
            ";",
            spec.Strategies.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value.StrategyId}@{s.Value.Version}"));
        return new CompatibilityIdentity(
            OutputCodec.Version,
            NiftySignal.Trading.Core.State.StateWriter.Version,
            CompatibilityIdentity.CurrentEngine,
            spec.Config.SchemaVersion,
            spec.Config.ComputeHash().Value,
            OutputCodec.ContentHash(FreezeText(spec.Freeze)),
            OutputCodec.ContentHash(Canonical.Json(spec.Translator)),
            spec.Translator.ReadinessSpec(spec.Freeze),
            OutputCodec.ContentHash(Canonical.Json(spec.Settings.Retention)),
            strategies,
            OutputCodec.ContentHash((spec.Durable ?? new DurableOptions()).CommitPolicyText),
            OutputCodec.ContentHash(spec.SourceManifest));
    }

    public static RunHeader BuildHeader(DurableRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var horizon = spec.Settings.Horizon ?? throw new ArgumentException("A durable run needs an explicit horizon.", nameof(spec));
        return RunHeader.Create(new RunHeader(
            spec.Settings.RunId,
            spec.Freeze.TradeDate,
            BuildCompatibility(spec),
            spec.Provenance ?? BinaryProvenance.None,
            CanonicalJson.Serialize(spec.Config),
            FreezeText(spec.Freeze),
            spec.SourceManifest,
            horizon,
            VariantsOf(spec),
            string.Empty));
    }

    /// <summary>The ownership account of a variant: one independent virtual book per variant (the engine's own label).</summary>
    public static string AccountOf(string variantId) => "independent-virtual-book:" + variantId;

    private static IReadOnlyList<VariantIdentity> VariantsOf(DurableRunSpec spec)
        => spec.Config.Variants
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => new VariantIdentity(
                v.Key,
                spec.Config.ComputeVariantHash(v.Key).Value,
                v.Value.StrategyId,
                spec.Strategies.TryGetValue(v.Value.StrategyId, out var strategy) ? strategy.Version : "unregistered",
                AccountOf(v.Key)))
            .ToList();

    /// <summary>Canonical text of the frozen session: dictionaries are written in key order so equal freezes give equal text.</summary>
    public static string FreezeText(SessionFreeze freeze)
        => Canonical.Json(freeze with { TickSizes = new SortedDictionary<string, decimal>(freeze.TickSizes.ToDictionary(k => k.Key, k => k.Value), StringComparer.Ordinal) })
           + string.Create(CultureInfo.InvariantCulture, $"|options={freeze.Options.Count}");
}
