using System.Globalization;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Instruments;
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
    public static string AccountOf(Core.Strategies.VariantKey variant) => "independent-virtual-book:" + variant;

    private static IReadOnlyList<VariantIdentity> VariantsOf(DurableRunSpec spec) => VariantsOf(spec.Config, spec.Strategies);

    private static IReadOnlyList<VariantIdentity> VariantsOf(TradingConfig config, IReadOnlyDictionary<string, Core.Strategies.IStrategyFamily> strategies)
        => config.Variants.Values
            .OrderBy(v => v.GetKey())
            .Select(v => new VariantIdentity(
                v.VariantId,
                config.ComputeVariantHash(v.GetKey()).Value,
                v.StrategyId,
                strategies.TryGetValue(v.StrategyId, out var strategy) ? strategy.Version : "unregistered",
                AccountOf(v.GetKey())))
            .ToList();

    /// <summary>Canonical text of the contract universe of a live run (the live counterpart of <see cref="FreezeText"/>): contracts in token order.</summary>
    public static string UniverseText(IReadOnlyList<OptionContract> contracts)
        => Canonical.Json(contracts.OrderBy(c => c.Token, StringComparer.Ordinal).ToArray()) + string.Create(CultureInfo.InvariantCulture, $"|options={contracts.Count}");

    public static CompatibilityIdentity BuildCompatibility(LiveRunSpec spec)
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
            OutputCodec.ContentHash(UniverseText(spec.Contracts)),
            OutputCodec.ContentHash("live-finalized-bars/1;session=" + spec.SourceSessionId),
            spec.ReadinessSpec,
            OutputCodec.ContentHash(Canonical.Json(spec.Settings.Retention)),
            strategies,
            OutputCodec.ContentHash((spec.Durable ?? new DurableOptions()).CommitPolicyText),
            OutputCodec.ContentHash(spec.InputManifest));
    }

    public static RunHeader BuildHeader(LiveRunSpec spec, DateTimeOffset horizon)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return RunHeader.Create(new RunHeader(
            spec.Settings.RunId,
            spec.TradeDate,
            BuildCompatibility(spec),
            spec.Provenance ?? BinaryProvenance.None,
            CanonicalJson.Serialize(spec.Config),
            UniverseText(spec.Contracts),
            spec.InputManifest,
            horizon,
            VariantsOf(spec.Config, spec.Strategies),
            string.Empty));
    }

    /// <summary>The resume rule (contract section A.18.7): a durable header may be continued only by a running configuration with the identical compatibility identity and snapshots; anything else is a visible halt.</summary>
    public static void CheckCompatibility(RunHeader durable, RunHeader running)
    {
        if (!durable.HashIsValid)
        {
            throw new RecoveryHaltException(HaltKind.CorruptLedger, "The durable run header is corrupt (hash mismatch).", new[] { "header hash" });
        }

        var diffs = durable.Compatibility.DifferencesFrom(running.Compatibility).ToList();
        void Check(string name, string a, string b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                diffs.Add($"{name} differs from the durable header");
            }
        }

        Check(nameof(RunHeader.RunId), durable.RunId, running.RunId);
        Check(nameof(RunHeader.TradeDate), durable.TradeDate.ToString("O", CultureInfo.InvariantCulture), running.TradeDate.ToString("O", CultureInfo.InvariantCulture));
        Check(nameof(RunHeader.Horizon), durable.Horizon.UtcTicks.ToString(CultureInfo.InvariantCulture), running.Horizon.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Check(nameof(RunHeader.ConfigJson), durable.ConfigJson, running.ConfigJson);
        Check(nameof(RunHeader.FreezeSnapshot), durable.FreezeSnapshot, running.FreezeSnapshot);
        Check(nameof(RunHeader.SourceManifest), durable.SourceManifest, running.SourceManifest);
        if (diffs.Count > 0)
        {
            throw new RecoveryHaltException(HaltKind.Compatibility, "The durable run is not compatible with this build / configuration; resume refused.", diffs);
        }
    }

    /// <summary>Canonical text of the frozen session: dictionaries are written in key order so equal freezes give equal text.</summary>
    public static string FreezeText(SessionFreeze freeze)
        => Canonical.Json(freeze with { TickSizes = new SortedDictionary<string, decimal>(freeze.TickSizes.ToDictionary(k => k.Key, k => k.Value), StringComparer.Ordinal) })
           + string.Create(CultureInfo.InvariantCulture, $"|options={freeze.Options.Count}");
}
