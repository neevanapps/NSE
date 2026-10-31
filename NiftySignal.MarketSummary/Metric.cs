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
public sealed record Coverage(int Included, int Expected, CoverageStatus Status, string Reason, IReadOnlyList<string> MissingTokens)
{
    public string Text => Status switch
    {
        CoverageStatus.Full => $"{Included}/{Expected} – FULL",
        CoverageStatus.Partial => $"{Included}/{Expected} – PARTIAL",
        _ => "— – INSUFFICIENT DATA",
    };

    public static Coverage Insufficient(int expected, string reason, IReadOnlyList<string>? missing = null) =>
        new(0, expected, CoverageStatus.Insufficient, reason, missing ?? []);
}

/// <summary>The five fixed windows. <c>Bars == null</c> means Day (session start through the pinned boundary).</summary>
public sealed record SummaryWindow(string Name, int? Bars)
{
    public static readonly IReadOnlyList<SummaryWindow> All =
        [new("5 Bars", 5), new("10 Bars", 10), new("50 Bars", 50), new("100 Bars", 100), new("Day", null)];
}
