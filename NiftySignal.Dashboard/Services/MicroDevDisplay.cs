using NiftySignal.MarketSummary;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Dashboard presentation convention (owner decision, 2026-11-02): the MicroDev cells are shown INVERTED, Displayed MicroDev = -Raw MicroDev, so a positive displayed value is green and a negative one red.
/// This is a UI interpretation only. It is applied exactly once, at the last step before a MicroDev cell is formatted; stored values, producers, aggregates, the metric registry, thresholds and every trading
/// decision keep the raw MicroDev. Numerator and raw are negated together (the ratio Num / Den * 100 therefore flips sign as well) and the denominator is untouched. Zero stays zero (never "-0.00"); a missing
/// value stays missing.
/// </summary>
public static class MicroDevDisplay
{
    public const string Label = "MicroDev (Inv)";

    public const string Legend = "MicroDev is shown INVERTED (displayed = −raw MicroDev): a display convention, not a change to the metric or to any trading rule.";

    public static double? Invert(double? v) => v is { } x && double.IsFinite(x) ? (x == 0d ? 0d : -x) : v;

    public static RatioPart Invert(RatioPart p) => p with { Raw = Invert(p.Raw), Num = Invert(p.Num) };

    public static Metric Invert(Metric m) => m with { Raw = Invert(m.Raw), Num = Invert(m.Num) };

    public static SideDirection Invert(SideDirection d) => new(Invert(d.Ce), Invert(d.Pe), Invert(d.Direction));
}
