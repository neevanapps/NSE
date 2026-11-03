using System.Globalization;

namespace NiftySignal.PaperEngineV2.Core.Metrics;

/// <summary>The five summary windows of the Market Summary (docs/MARKET_SUMMARY_METRICS.md). The index is the position inside the frame's value table.</summary>
public enum SummaryWindowId { W5 = 0, W10 = 1, W50 = 2, W100 = 3, Day = 4 }

public static class SummaryWindows
{
    public const int Count = 5;

    public static string Text(SummaryWindowId w) => w switch { SummaryWindowId.W5 => "5", SummaryWindowId.W10 => "10", SummaryWindowId.W50 => "50", SummaryWindowId.W100 => "100", _ => "Day" };

    public static bool TryParse(string? text, out SummaryWindowId window)
    {
        switch (text)
        {
            case "5": window = SummaryWindowId.W5; return true;
            case "10": window = SummaryWindowId.W10; return true;
            case "50": window = SummaryWindowId.W50; return true;
            case "100": window = SummaryWindowId.W100; return true;
            case "Day": window = SummaryWindowId.Day; return true;
            default: window = default; return false;
        }
    }
}

/// <summary>The physical unit of a metric value. Two metrics are comparable only when their units are equal; thresholds are always expressed in the REGISTRY unit (Delta / Vega are raw, never the x100 display scale).</summary>
public enum MetricUnit { Quantity, Percent, Price, IvPoints, DeltaRaw, VegaRaw, PerSecond, Count, VixPoints, Flag }

/// <summary>What the value of a metric means relative to CE and PE. <see cref="Level"/> = the side's own value (or a futures value); <see cref="CeMinusPe"/> / <see cref="PeMinusCe"/> = the Market Summary direction conventions (OI is PE - CE).</summary>
public enum MetricDirection { Level, CeMinusPe, PeMinusCe, CeMinusPeInverted }

/// <summary>Raw = the value in its own unit; Pct = the pooled ratio expressed in percent (Num / Den x 100) where the Market Summary defines one.</summary>
public enum MetricKind { Raw, Pct }

/// <summary>
/// One registry entry. <see cref="Handle"/> is the dense index frames use for O(1) reads. <see cref="Discrete"/> metrics (flags, counts) are the only ones for which <c>==</c> / <c>!=</c> are accepted;
/// every continuous metric rejects exact equality. <see cref="Availability"/> documents when the value is Unknown (never zero).
/// </summary>
public sealed record MetricSpec(
    int Handle, string Id, string SourceModel, MetricUnit Unit, MetricDirection Direction, MetricKind Kind, bool Discrete, string Description, string Availability)
{
}

/// <summary>
/// The fixed, typed metric registry of source model <c>fixed13650-summary-v1</c>. It is code, not data: a strategy can reference only these ids. The ids follow
/// <c>fut.&lt;family&gt;[:raw|:pct]</c>, <c>opt.&lt;grid&gt;.&lt;family&gt;.&lt;ce|pe|dir&gt;[:raw|:pct]</c>, <c>opt.&lt;grid&gt;.coverage_pct|coverage_full</c> and <c>vix.*</c>.
/// </summary>
public static class MetricRegistry
{
    public const string SourceModel = "fixed13650-summary-v1";

    static readonly Dictionary<string, MetricSpec> ById = new(StringComparer.Ordinal);
    static readonly List<MetricSpec> All = [];

    public static IReadOnlyList<MetricSpec> Specs => All;

    public static int Count => All.Count;

    public static bool TryGet(string? id, out MetricSpec spec)
    {
        if (id is not null && ById.TryGetValue(id, out var found)) { spec = found; return true; }
        spec = null!;
        return false;
    }

    public static MetricSpec Get(string id) => TryGet(id, out var s) ? s : throw new KeyNotFoundException($"Unknown metric '{id}'.");

    static void Add(string id, MetricUnit unit, MetricDirection direction, MetricKind kind, bool discrete, string description, string availability)
    {
        var spec = new MetricSpec(All.Count, id, SourceModel, unit, direction, kind, discrete, description, availability);
        All.Add(spec);
        ById.Add(id, spec);
    }

    static MetricRegistry()
    {
        const string flowAvail = "Unknown when the window has insufficient completed bars, no included contract has every bar of the window, the value family has no evidence (e.g. OFI on v1 rows) or a denominator is zero.";
        const string levelAvail = "Unknown when the window's first or last bar row is missing, the start value is not positive (IV, price) or the Greek is unavailable.";

        // ---- futures (single series) ----
        foreach (var (family, rawUnit, pct, avail, desc) in new (string, MetricUnit, bool, string, string)[]
        {
            ("price_change", MetricUnit.Price, true, levelAvail, "Futures close(last) - open(first) of the window; pct = change / first open x 100."),
            ("volume", MetricUnit.Quantity, false, flowAvail, "Futures traded volume over the window."),
            ("strict", MetricUnit.Quantity, true, flowAvail, "Futures strict delta; pct = strict delta / volume x 100."),
            ("enriched", MetricUnit.Quantity, true, flowAvail, "Futures enriched delta; pct = delta / classified volume x 100."),
            ("oi_change", MetricUnit.Quantity, true, levelAvail, "Futures open-interest change; pct over the first OI."),
            ("microdev", MetricUnit.Price, true, flowAvail, "Usable-seconds-weighted MicroDev; pct = time-weighted TOB x 100."),
            ("ofi", MetricUnit.Quantity, true, flowAvail, "Net order-flow imbalance; pct = net / gross x 100."),
            ("activity", MetricUnit.PerSecond, false, flowAvail, "Traded quantity per second over valid elapsed time."),
        })
        {
            Add($"fut.{family}:raw", rawUnit, MetricDirection.Level, MetricKind.Raw, false, desc, avail);
            if (pct) Add($"fut.{family}:pct", MetricUnit.Percent, MetricDirection.Level, MetricKind.Pct, false, desc, avail);
        }

        Add("fut.coverage_pct", MetricUnit.Percent, MetricDirection.Level, MetricKind.Raw, false, "Share of the window's bars with a stored futures bar.", "Unknown when the window has insufficient completed bars.");
        Add("fut.coverage_full", MetricUnit.Flag, MetricDirection.Level, MetricKind.Raw, true, "1 when every bar of the window is stored (FULL), else 0.", "Unknown when the window has insufficient completed bars.");

        // ---- option grids ----
        foreach (var grid in new[] { "atm", "itm", "otm", "total" })
        {
            foreach (var (family, rawUnit, hasPct, levelBased) in new (string, MetricUnit, bool, bool)[]
            {
                ("price", MetricUnit.Price, true, true), ("roll", MetricUnit.Price, true, true), ("strict", MetricUnit.Quantity, true, false), ("enriched", MetricUnit.Quantity, true, false),
                ("oi", MetricUnit.Quantity, true, true), ("microdev", MetricUnit.Price, true, false), ("ofi", MetricUnit.Quantity, true, false), ("iv", MetricUnit.IvPoints, true, true),
                ("delta", MetricUnit.DeltaRaw, true, true), ("vega", MetricUnit.VegaRaw, true, true), ("activity", MetricUnit.PerSecond, false, false),
            })
            {
                foreach (var side in new[] { "ce", "pe", "dir" })
                {
                    var dir = side switch { "dir" => family == "oi" ? MetricDirection.PeMinusCe : MetricDirection.CeMinusPe, _ => MetricDirection.Level };
                    var avail = levelBased ? levelAvail : flowAvail;
                    var desc = $"Market Summary {grid.ToUpperInvariant()} grid, {family}, {side.ToUpperInvariant()} (aggregation per docs/MARKET_SUMMARY_METRICS.md).";
                    Add($"opt.{grid}.{family}.{side}:raw", rawUnit, dir, MetricKind.Raw, false, desc, avail);
                    // percent form exists where the Market Summary defines a pooled ratio; activity has one only for the direction.
                    if (hasPct || (family == "activity" && side == "dir")) Add($"opt.{grid}.{family}.{side}:pct", MetricUnit.Percent, dir, MetricKind.Pct, false, desc, avail);
                }
            }

            Add($"opt.{grid}.coverage_pct", MetricUnit.Percent, MetricDirection.Level, MetricKind.Raw, false, $"Included / expected contracts of the {grid.ToUpperInvariant()} grid x 100 (flow family).", "Unknown when the window has insufficient completed bars.");
            Add($"opt.{grid}.coverage_full", MetricUnit.Flag, MetricDirection.Level, MetricKind.Raw, true, $"1 when every expected contract of the {grid.ToUpperInvariant()} grid is included (FULL), else 0.", "Unknown when the window has insufficient completed bars.");
        }

        // ---- India VIX ----
        const string vixAvail = "Unknown (never zero) when the VIX observation at the boundary is stale (> the freshness limit) or missing; change needs both the window start and end points.";
        Add("vix.level:raw", MetricUnit.VixPoints, MetricDirection.Level, MetricKind.Raw, false, "India VIX at the window end (latest valid observation at or before the boundary).", vixAvail);
        Add("vix.change:raw", MetricUnit.VixPoints, MetricDirection.Level, MetricKind.Raw, false, "India VIX end - start over the window.", vixAvail);
        Add("vix.change:pct", MetricUnit.Percent, MetricDirection.Level, MetricKind.Pct, false, "India VIX change / start x 100.", vixAvail);
        Add("vix.fresh", MetricUnit.Flag, MetricDirection.Level, MetricKind.Raw, true, "1 when the VIX observation at the decision boundary is within the freshness limit, else 0.", "Never Unknown once the frame exists: a missing/stale VIX is 0.");

        // ---- appended after the original registry: existing handles (and therefore existing frames / config hashes) never move ----
        // The Dashboard's "MicroDev Dir (Inv)" convention (displayed = -raw) as an explicit, separate metric. The sign flip happens exactly once, in MarketStateFrameBuilder; the raw
        // opt.total.microdev.dir metric is unchanged and no other metric is inverted.
        const string invDesc = "Market Summary TOTAL grid MicroDev direction in the Dashboard's INVERTED convention (value = -raw CE-PE MicroDev; the ratio flips sign with it; zero stays zero). Derived once in the frame builder from opt.total.microdev.dir.";
        const string invAvail = "Unknown exactly when opt.total.microdev.dir is Unknown.";
        Add("opt.total.microdev_inv.dir:raw", MetricUnit.Price, MetricDirection.CeMinusPeInverted, MetricKind.Raw, false, invDesc, invAvail);
        Add("opt.total.microdev_inv.dir:pct", MetricUnit.Percent, MetricDirection.CeMinusPeInverted, MetricKind.Pct, false, invDesc, invAvail);
    }

    /// <summary>Whether two metrics may be compared with each other: same unit and the same direction convention (a CE value is not comparable to a CE-PE difference, and an OI direction (PE-CE) is not comparable to a flow direction (CE-PE)).</summary>
    public static bool AreComparable(MetricSpec a, MetricSpec b, out string reason)
    {
        if (a.Unit != b.Unit) { reason = string.Create(CultureInfo.InvariantCulture, $"units differ ({a.Unit} vs {b.Unit})"); return false; }
        if (a.Direction != b.Direction) { reason = string.Create(CultureInfo.InvariantCulture, $"direction conventions differ ({a.Direction} vs {b.Direction})"); return false; }
        reason = string.Empty;
        return true;
    }
}
