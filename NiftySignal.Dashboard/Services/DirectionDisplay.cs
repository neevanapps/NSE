using NiftySignal.MarketSummary;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Dashboard presentation convention (owner decision, 2026-11-04), the same kind as <see cref="MicroDevDisplay"/>: the IV, Delta and Vega DIRECTION cells of TOTAL SUMMARY and OPTION CONTEXT are shown INVERTED,
/// DisplayedDirection = -RawDirection. It is applied exactly once, at the last step before the Direction cell is formatted. The CE and PE source values of IV, Delta and Vega are NEVER inverted; stored values,
/// producers, the Market Summary calculators, the V2 metric registry, the JSON strategy metrics and every trading decision keep the raw direction. Numerator and raw are negated together (so the ratio
/// Num / Den * 100 flips sign too) and the denominator is untouched; zero stays zero (never "-0.00"); missing / non-finite stays missing / neutral.
/// </summary>
public static class DirectionDisplay
{
    public const string IvLabel = "IV Dir (Inv)", DeltaLabel = "Delta Dir (Inv)", VegaLabel = "Vega Dir (Inv)";

    public const string Legend = "IV / Delta / Vega Dir are shown INVERTED (displayed = −raw direction): a display convention, not a change to the metrics or to any trading rule.";

    /// <summary>Inverts a Market Summary direction metric (TOTAL SUMMARY).</summary>
    public static Metric Invert(Metric m) => MicroDevDisplay.Invert(m);

    /// <summary>Inverts ONLY the direction of a fixed-strike CE/PE triple (OPTION CONTEXT): the CE and PE source values stay exactly as stored.</summary>
    public static SideDirection InvertDirectionOnly(SideDirection d) => d with { Direction = MicroDevDisplay.Invert(d.Direction) };
}
