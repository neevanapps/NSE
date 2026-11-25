using NiftySignal.MarketSummary;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Dashboard presentation convention (owner decision, 2026-11-02): the standalone MicroDev cells (Futures, CE, PE) are shown INVERTED, Displayed MicroDev = -Raw MicroDev, so a positive displayed value is green and a negative one red.
/// This is a UI interpretation only. It is applied exactly once, at the last step before a MicroDev cell is formatted; stored values, producers, aggregates, the metric registry, thresholds and every trading
/// decision keep the raw MicroDev. Numerator and raw are negated together (the ratio Num / Den * 100 therefore flips sign as well) and the denominator is untouched. Zero stays zero (never "-0.00"); a missing
/// value stays missing. EXCEPTION (owner decision, 2026-11-25): MicroDev / MicroDev5 DIRECTION cells are NOT inverted; they show the raw CE − PE.
/// </summary>
public static class MicroDevDisplay
{
    public const string Label = "MicroDev (Inv)";

    public const string Legend = "Futures / CE / PE MicroDev is shown INVERTED (displayed = −raw MicroDev); every MicroDev Dir is the RAW CE − PE: a display convention, not a change to the metric or to any trading rule.";

    public static double? Invert(double? v) => v is { } x && double.IsFinite(x) ? (x == 0d ? 0d : -x) : v;

    public static RatioPart Invert(RatioPart p) => p with { Raw = Invert(p.Raw), Num = Invert(p.Num) };

    public static Metric Invert(Metric m) => m with { Raw = Invert(m.Raw), Num = Invert(m.Num) };

    /// <summary>Inverts the whole row (CE, PE and Direction). Only for rows that have no derived-direction semantics; MicroDev DIRECTIONS must use <see cref="InvertSides"/> (owner correction 2026-11-25).</summary>
    public static SideDirection Invert(SideDirection d) => new(Invert(d.Ce), Invert(d.Pe), Invert(d.Direction));

    /// <summary>
    /// MicroDev direction rows: the standalone CE and PE cells keep the display inversion, the DIRECTION is the RAW CE − PE (never negated). The inversion of MicroDev Direction was reverted by the owner on 2026-11-25;
    /// the direction is derived from the raw CE / PE values, not from the displayed inverted ones.
    /// </summary>
    public static SideDirection InvertSides(SideDirection d) => d with { Ce = Invert(d.Ce), Pe = Invert(d.Pe) };
}
