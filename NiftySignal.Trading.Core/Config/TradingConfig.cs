using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Selection;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Core.Config;

/// <summary>
/// Versioned, immutable engine configuration (contract section 7). Schema 2 (CP2) adds contract selection / entry premium band, candle
/// construction and the variant set to the CP1 session policy. The hash is over the canonical serialisation, so it is independent of
/// property or dictionary order, machine culture, time zone and anything generated at run time. Variants are a dictionary keyed by
/// the canonical variant identity text <c>StrategyId/VariantId</c> (schema 6), so the order in which they are declared never affects the hash and the
/// same VariantId may exist under different StrategyIds.
/// </summary>
public sealed record TradingConfig
{
    public const int CurrentSchemaVersion = 7;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Human-assigned label of this configuration version (part of the hash: a relabel is a new version).</summary>
    public required string ConfigVersion { get; init; }

    public SessionConfig Session { get; init; } = new();

    /// <summary>Entry premium band (default 100..150 inclusive), expiry policy, quote freshness and ranking. Entries only, never exits.</summary>
    public ContractSelectionConfig Selection { get; init; } = new();

    public CandleConfig Candles { get; init; } = new();

    /// <summary>Simulated execution assumptions (placeholders pending calibration). Real-order mode is unavailable.</summary>
    public ExecutionConfig Execution { get; init; } = new();

    /// <summary>Effective-dated cost schedules. Empty = not configured (the trading engine then refuses to run).</summary>
    public CostsConfig Costs { get; init; } = new();

    /// <summary>Named, versioned risk profile. Default ResearchV1 (no trade cap, daily SL observed only). RealOrder is configurable but unstartable.</summary>
    public RiskProfile Risk { get; init; } = RiskProfile.ResearchV1();

    /// <summary>What evidence completes a recovery from a reported feed fault (schema 5; CP5 correction of the CP4 "first option quote" rule).</summary>
    public IntegrityPolicy Integrity { get; init; } = new();

    public IReadOnlyDictionary<string, VariantSpec> Variants { get; init; } = new Dictionary<string, VariantSpec>();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add($"SchemaVersion {SchemaVersion} is not supported (current {CurrentSchemaVersion}).");
        }

        if (string.IsNullOrWhiteSpace(ConfigVersion))
        {
            errors.Add("ConfigVersion is required.");
        }

        errors.AddRange(Session.Validate());
        errors.AddRange(Selection.Validate());
        errors.AddRange(Candles.Validate());
        errors.AddRange(Execution.Validate());
        errors.AddRange(Costs.Validate());
        errors.AddRange(Risk.Validate());
        errors.AddRange(Integrity.Validate());
        if (Risk.Name == RiskProfile.PaperLiveV1Name)
        {
            var barClocked = Variants.Values.Count(v => v.EntryCondition is Entry.NextCompletedBarEntrySpec);
            if (barClocked > 0 && barClocked != Variants.Count)
            {
                errors.Add("Bar-clocked (NPT1) variants run in their own session profile and cannot share a configuration with other variants.");
            }
            else if (barClocked > 0)
            {
                // NPT1: entries fill from 09:30:00; live profile = entries until 15:00:00 (exclusive) with the terminal protocol AT 15:00:00; the historical-parity profile (verification only) ends entries at 11:00:00.
                var live = Session.EntryClose == new TimeOnly(15, 0, 0);
                var parity = Session.EntryClose == new TimeOnly(11, 0, 0);
                if (Session.EntryOpen != new TimeOnly(9, 30, 0) || !(live || parity) || Session.LiquidationStart != new TimeOnly(15, 0, 0))
                {
                    errors.Add("NPT1 variants fix the session rules: entries from 09:30:00 until 15:00:00 (live; 11:00:00 only for the historical-parity profile) and the terminal protocol at 15:00:00 (use SessionConfig.Npt1Live1500() / Npt1HistoricalParity1100()).");
                }
            }
            else if (Session.EntryOpen != new TimeOnly(9, 30, 0) || Session.EntryClose != new TimeOnly(15, 0, 0) || Session.LiquidationStart != new TimeOnly(15, 10, 0))
            {
                errors.Add("PaperLiveV1 fixes the session rules: entries from 09:30:00 until (not including) 15:00:00 and mandatory liquidation from 15:10:00 (use SessionConfig.PaperLiveV1()).");
            }

            foreach (var family in Risk.Families.Keys)
            {
                if (!Variants.Values.Any(v => string.Equals(v.StrategyId, family, StringComparison.Ordinal)))
                {
                    errors.Add($"Family limits are configured for '{family}', which has no variant.");
                }
            }
        }

        foreach (var (key, spec) in Variants)
        {
            if (spec.Limits is not null && Risk.Name != RiskProfile.PaperLiveV1Name)
            {
                errors.Add($"Variant '{key}': Limits are enforced only under the PaperLiveV1 profile.");
            }

            if (!VariantKey.IsValidPart(spec.StrategyId) || !VariantKey.IsValidPart(spec.VariantId))
            {
                errors.Add($"Variant key '{key}': StrategyId '{spec.StrategyId}' and VariantId '{spec.VariantId}' must each be valid identifier parts.");
            }
            else if (!string.Equals(key, spec.GetKey().ToString(), StringComparison.Ordinal))
            {
                errors.Add($"Variant key '{key}' does not match its identity '{spec.GetKey()}' (StrategyId/VariantId).");
            }

            errors.AddRange(spec.Validate(Candles.HistoryDepth));
        }

        return errors;
    }

    public string ToCanonicalJson() => CanonicalJson.Serialize(this);

    public ConfigHash ComputeHash() => CanonicalJson.Hash(this);

    /// <summary>
    /// Hash of everything that determines one variant's behaviour: its spec plus the shared selection, candle and session policy. Two
    /// variants with different specs therefore never share an opportunity or order id namespace.
    /// </summary>
    public ConfigHash ComputeVariantHash(VariantKey key)
    {
        if (!Variants.TryGetValue(key.ToString(), out var spec))
        {
            throw new KeyNotFoundException($"Unknown variant '{key}'.");
        }

        return CanonicalJson.Hash(new { SchemaVersion, Variant = spec, Selection, Candles, Session, Execution, Costs, Risk, Integrity });
    }
}
