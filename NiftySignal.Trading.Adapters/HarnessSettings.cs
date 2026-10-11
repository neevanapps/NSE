using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Adapters;

/// <summary>
/// Labelled TEST inputs for the values that are still unresolved (contract section A.16.8): the user's brokerage plan, the liquidation-start time and
/// the watchdog thresholds. They exist so that engineering validation can run end to end. They are NOT approved production defaults, they are printed
/// with every harness report, and the configuration version carries the label so that the hash cannot be mistaken for a production configuration.
/// Everything else in the configuration keeps the confirmed trading contract (entry window, premium band, 650 units, ResearchV1 risk profile, ...).
/// </summary>
public static class HarnessSettings
{
    public const string Label = "HARNESS-TEST-ONLY";

    public const string BrokerageProvenance = "TEST-ONLY: flat Rs 20 per ORDER; the user's brokerage plan is unknown and unresolved";

    public const string LiquidationProvenance = "TEST-ONLY: liquidation start 15:05:00; the real value is an unresolved decision (D-14)";

    public static readonly TimeOnly LiquidationStart = new(15, 5, 0);

    public const decimal BrokeragePerOrder = 20m;

    public static IReadOnlyList<string> Provenance { get; } = new[]
    {
        "Brokerage: " + BrokerageProvenance,
        "Liquidation start: " + LiquidationProvenance,
        "Watchdog thresholds: TEST-ONLY harness values (WatchdogOptions.TestOnly); not approved",
        "Execution placeholders (latency 1 s, quote age 2 s, entry lifetime 5 s, spreads, fill fraction, retry 1 s): contract defaults D-8, calibration pending",
        "StableOrderingLag 2 s: the existing AdaptiveObserverWorker constant, not a new tuning",
    };

    /// <summary>The harness trading configuration: the confirmed contract defaults plus the labelled test brokerage and liquidation start.</summary>
    public static TradingConfig Config(string name, IEnumerable<VariantSpec> variants)
    {
        ArgumentNullException.ThrowIfNull(variants);
        return new TradingConfig
        {
            ConfigVersion = $"{Label}:{name}",
            Costs = new CostsConfig { Schedules = new[] { NseIndexOptionsCostSchedules.From20260401(new FlatBrokerage { Amount = BrokeragePerOrder, Unit = BrokerageUnit.PerOrder }) } },
            Session = new SessionConfig { LiquidationStart = LiquidationStart },
            Variants = variants.ToDictionary(v => v.VariantId),
        };
    }
}
