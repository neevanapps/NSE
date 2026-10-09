using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Recovery;

/// <summary>
/// Derives the append-only projection rows (fills, positions, orders, audit) from the committed outputs (contract section A.18.5). Projections are
/// derived data: their key is the output's global sequence, so re-deriving them in a re-applied commit yields identical rows, and a recovery can
/// compare them with the durable ones. The ledger of record is the output table; projections never feed back into the engine.
/// </summary>
public static class ProjectionBuilder
{
    public const string Fills = "fills";
    public const string Positions = "positions";
    public const string Orders = "orders";
    public const string Audit = "audit";

    public static IReadOnlyList<ProjectionRow> Build(IEnumerable<OutputRecord> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var rows = new List<ProjectionRow>();
        foreach (var o in outputs)
        {
            var table = TableOf(o.Type);
            if (table is not null)
            {
                rows.Add(ProjectionRow.Create(table, o.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture), o.Payload));
            }
        }

        return rows;
    }

    public static string? TableOf(string outputType)
        => outputType switch
        {
            "FillRecord" => Fills,
            "AuditRecord" => Audit,
            _ when outputType.StartsWith("Position", StringComparison.Ordinal) || outputType.StartsWith("Tp1", StringComparison.Ordinal) || outputType == "DailySlBreached" => Positions,
            _ when outputType.Contains("Intent", StringComparison.Ordinal) || outputType.Contains("Order", StringComparison.Ordinal) || outputType.Contains("Cancel", StringComparison.Ordinal) => Orders,
            _ => null,
        };

    /// <summary>A folded, comparable view of the durable ledger: per projection table the row count and a hash of the ordered rows, plus the fill totals.</summary>
    public sealed record Folded(IReadOnlyDictionary<string, (int Count, string Hash)> Tables, long FilledQuantity, decimal Notional, decimal Costs, int Fills)
    {
        public bool SameAs(Folded other)
            => FilledQuantity == other.FilledQuantity && Notional == other.Notional && Costs == other.Costs && Fills == other.Fills
               && Tables.Count == other.Tables.Count
               && Tables.All(t => other.Tables.TryGetValue(t.Key, out var o) && o == t.Value);
    }

    public static Folded Fold(IEnumerable<OutputRecord> outputs)
    {
        var list = outputs.OrderBy(o => o.Seq).ToList();
        var tables = Build(list)
            .GroupBy(r => r.Table, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.Count(), OutputCodec.ContentHash(string.Join("\n", g.Select(r => r.Key + "|" + r.ContentHash)))), StringComparer.Ordinal);

        long qty = 0;
        decimal notional = 0m;
        decimal costs = 0m;
        var fills = 0;
        foreach (var o in list.Where(o => o.Type == "FillRecord"))
        {
            var fill = (NiftySignal.Trading.Core.Execution.FillRecord)OutputCodec.Decode(o.Payload);
            qty += fill.Quantity;
            notional += fill.Notional;
            costs += fill.Costs.Total;
            fills++;
        }

        return new Folded(tables, qty, notional, costs, fills);
    }
}
