namespace NiftySignal.MarketSummary;

/// <summary>
/// One summary cell: a raw value and the parts of its normalized ratio (<c>Num / Den * 100</c>, only when <c>Den &gt; 0</c>). Keeping numerator and denominator (never a finished percentage) is what
/// makes pooled aggregation ("ratio of sums") possible. A missing input stays null; nothing here turns "unavailable" into zero.
/// </summary>
public readonly record struct Metric(double? Raw, double? Num = null, double? Den = null)
{
    public static readonly Metric None = default;

    public double? Ratio => Num is { } n && Den is { } d && d > 0d && double.IsFinite(n) && double.IsFinite(d) ? n / d * 100d : null;
}

/// <summary>Display-only scaling. Storage, calculations, thresholds and payloads always keep the raw Black-Scholes units.</summary>
public static class DisplayScale
{
    /// <summary>"Delta ×100" / "Vega ×100": 0.42 → 42, 0.018 → 1.8.</summary>
    public static double? Times100(double? raw) => raw is { } v && double.IsFinite(v) ? v * 100d : null;
}

public enum CoverageStatus { Full, Partial, Insufficient }

/// <summary>Per grid / window coverage, e.g. <c>10/10 – FULL</c>, <c>8/10 – PARTIAL</c>, <c>— – INSUFFICIENT DATA</c>.</summary>
public sealed record Coverage(int Included, int Expected, CoverageStatus Status, string Reason, IReadOnlyList<string> MissingTokens, int? LevelIncluded = null, IReadOnlyDictionary<string, int>? Families = null)
{
    /// <summary><see cref="Included"/> counts contracts with EVERY bar of the window (flow metrics); <see cref="LevelIncluded"/> counts contracts with at least the first and last bar (start-to-end level metrics).</summary>
    public string Text => Status switch
    {
        CoverageStatus.Full => $"{Included}/{Expected} – FULL",
        CoverageStatus.Partial => LevelIncluded is { } l && l != Included ? $"{Included}/{Expected} – PARTIAL (levels {l}/{Expected})" : $"{Included}/{Expected} – PARTIAL",
        _ => "— – INSUFFICIENT DATA",
    };

    /// <summary>Per metric family: how many contracts (CE + PE) actually carry a usable value in this window - e.g. OFI is 0 on v1 rows, Delta/Vega 0 before option-strike-v3. A null COLUMN is not a missing ROW: those contracts still count in <see cref="Included"/>.</summary>
    public int FamilyCount(string family) => Families is not null && Families.TryGetValue(family, out var n) ? n : 0;

    public static Coverage Insufficient(int expected, string reason, IReadOnlyList<string>? missing = null) =>
        new(0, expected, CoverageStatus.Insufficient, reason, missing ?? []);
}

/// <summary>The five fixed windows. <c>Bars == null</c> means Day (session start through the pinned boundary).</summary>
public sealed record SummaryWindow(string Name, int? Bars)
{
    public static readonly IReadOnlyList<SummaryWindow> All =
        [new("5 Bars", 5), new("10 Bars", 10), new("50 Bars", 50), new("100 Bars", 100), new("Day", null)];
}
