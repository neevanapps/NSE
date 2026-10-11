using System.Globalization;
using System.Text;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Recovery;

/// <summary>
/// The risk figures of a run folded from its durable outputs alone (contract section B.5): trades per variant and family (a trade is a filled entry), realized net P&amp;L,
/// consecutive losses per variant and per family in close order, and the open positions' bought / sold quantities from which the committed capital (open exposure) follows.
/// <para>
/// <b>This is a derived cache, not a balance.</b> The outputs stay the only ledger of record. The fold is deterministic and incremental, so a store keeps one text snapshot per
/// committed step (<c>risk_checkpoints</c>) and recovery recomputes the fold from every output and compares it with the last snapshot: a difference means a lost, altered or
/// misordered record and halts visibly instead of trading on a wrong ledger. The arithmetic mirrors the engine's own position bookkeeping (average entry fill price,
/// proceeds, entry and exit costs), so the figures also equal the live engine's counters; the tests assert that equality.
/// </para>
/// </summary>
public sealed class RiskLedger
{
    public const string Version = "risk-ledger/1";

    private sealed class OpenPosition(VariantKey variant)
    {
        public VariantKey Variant { get; } = variant;

        public long BuyQuantity { get; set; }

        public decimal BuyNotional { get; set; }

        public long SellQuantity { get; set; }

        public decimal SellProceeds { get; set; }

        public decimal Costs { get; set; }

        public decimal AvgEntry => BuyQuantity == 0 ? 0m : BuyNotional / BuyQuantity;

        public decimal NetRealised => SellProceeds - (AvgEntry * SellQuantity) - Costs;

        public long OpenQuantity => BuyQuantity - SellQuantity;
    }

    private sealed class Counters
    {
        public int Trades { get; set; }

        public decimal ClosedNet { get; set; }

        public int LossStreak { get; set; }
    }

    private readonly SortedDictionary<string, OpenPosition> _open = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, Counters> _variants = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, Counters> _families = new(StringComparer.Ordinal);

    /// <summary>Sequence of the last output folded in, -1 before the first.</summary>
    public long LastOutputSeq { get; private set; } = -1;

    public int TradesOf(VariantKey variant) => _variants.TryGetValue(variant.ToString(), out var c) ? c.Trades : 0;

    public int FamilyTradesOf(string strategyId) => _variants.Where(v => v.Key.StartsWith(strategyId + "/", StringComparison.Ordinal)).Sum(v => v.Value.Trades);

    public int GlobalTrades() => _variants.Values.Sum(v => v.Trades);

    public decimal RealizedNetOf(VariantKey variant)
        => (_variants.TryGetValue(variant.ToString(), out var c) ? c.ClosedNet : 0m) + _open.Values.Where(p => p.Variant == variant).Sum(p => p.NetRealised);

    public decimal FamilyRealizedNet(string strategyId)
        => _variants.Where(v => v.Key.StartsWith(strategyId + "/", StringComparison.Ordinal)).Sum(v => v.Value.ClosedNet) + _open.Values.Where(p => p.Variant.StrategyId == strategyId).Sum(p => p.NetRealised);

    public decimal GlobalRealizedNet() => _variants.Values.Sum(v => v.ClosedNet) + _open.Values.Sum(p => p.NetRealised);

    public int ConsecutiveLossesOf(VariantKey variant) => _variants.TryGetValue(variant.ToString(), out var c) ? c.LossStreak : 0;

    public int FamilyConsecutiveLosses(string strategyId) => _families.TryGetValue(strategyId, out var c) ? c.LossStreak : 0;

    /// <summary>The committed capital: average entry fill price x open quantity over every open position.</summary>
    public decimal OpenExposure() => _open.Values.Sum(p => p.AvgEntry * p.OpenQuantity);

    public int OpenPositions => _open.Count;

    public void Apply(long seq, EngineOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (seq != LastOutputSeq + 1)
        {
            throw new InvalidOperationException($"Output {seq} does not follow output {LastOutputSeq}: the risk ledger cannot skip or repeat an output.");
        }

        LastOutputSeq = seq;
        switch (output)
        {
            case FillRecord fill:
            {
                if (!_open.TryGetValue(fill.PositionId, out var p))
                {
                    _open[fill.PositionId] = p = new OpenPosition(fill.Variant);
                }

                if (fill.Side == TradeSide.Buy)
                {
                    p.BuyQuantity += fill.Quantity;
                    p.BuyNotional += fill.Notional;
                }
                else
                {
                    p.SellQuantity += fill.Quantity;
                    p.SellProceeds += fill.Notional;
                }

                p.Costs += fill.Costs.Total;
                break;
            }

            case PositionOpened opened:
                CountersOf(_variants, opened.Variant.ToString()).Trades++;
                break;
            case PositionClosed closed:
            {
                if (!_open.Remove(closed.PositionId, out _))
                {
                    throw new InvalidOperationException($"Position '{closed.PositionId}' closes without any fill.");
                }

                var variant = CountersOf(_variants, closed.Variant.ToString());
                var family = CountersOf(_families, closed.Variant.StrategyId);
                variant.ClosedNet += closed.NetPnl;
                if (closed.NetPnl < 0m)
                {
                    variant.LossStreak++;
                    family.LossStreak++;
                }
                else
                {
                    variant.LossStreak = 0;
                    family.LossStreak = 0;
                }

                break;
            }
        }
    }

    public static RiskLedger Fold(IEnumerable<OutputRecord> outputs)
    {
        var ledger = new RiskLedger();
        foreach (var o in outputs.OrderBy(o => o.Seq))
        {
            ledger.Apply(o.Seq, OutputCodec.Decode(o.Payload));
        }

        return ledger;
    }

    /// <summary>The canonical text (and with it the hash) of the ledger: same ledger, same text.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append(Version).Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"seq={LastOutputSeq}\n");
        foreach (var (key, c) in _variants)
        {
            sb.Append(CultureInfo.InvariantCulture, $"v|{key}|{c.Trades}|{DecimalText.Format(c.ClosedNet)}|{c.LossStreak}\n");
        }

        foreach (var (key, c) in _families)
        {
            sb.Append(CultureInfo.InvariantCulture, $"f|{key}|{DecimalText.Format(c.ClosedNet)}|{c.LossStreak}\n");
        }

        foreach (var (id, p) in _open)
        {
            sb.Append(CultureInfo.InvariantCulture, $"p|{id}|{p.Variant}|{p.BuyQuantity}|{DecimalText.Format(p.BuyNotional)}|{p.SellQuantity}|{DecimalText.Format(p.SellProceeds)}|{DecimalText.Format(p.Costs)}\n");
        }

        return sb.ToString();
    }

    public string Hash() => OutputCodec.ContentHash(ToText());

    public static RiskLedger FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || lines[0] != Version || !lines[1].StartsWith("seq=", StringComparison.Ordinal))
        {
            throw new FormatException("Not a risk ledger text of this version.");
        }

        var ledger = new RiskLedger { LastOutputSeq = long.Parse(lines[1][4..], CultureInfo.InvariantCulture) };
        foreach (var line in lines.Skip(2))
        {
            var f = line.Split('|');
            switch (f[0])
            {
                case "v":
                    ledger._variants[f[1]] = new Counters { Trades = int.Parse(f[2], CultureInfo.InvariantCulture), ClosedNet = DecimalText.Parse(f[3]), LossStreak = int.Parse(f[4], CultureInfo.InvariantCulture) };
                    break;
                case "f":
                    ledger._families[f[1]] = new Counters { ClosedNet = DecimalText.Parse(f[2]), LossStreak = int.Parse(f[3], CultureInfo.InvariantCulture) };
                    break;
                case "p":
                {
                    var parts = f[2].Split('/');
                    ledger._open[f[1]] = new OpenPosition(new VariantKey(parts[0], parts[1]))
                    {
                        BuyQuantity = long.Parse(f[3], CultureInfo.InvariantCulture),
                        BuyNotional = DecimalText.Parse(f[4]),
                        SellQuantity = long.Parse(f[5], CultureInfo.InvariantCulture),
                        SellProceeds = DecimalText.Parse(f[6]),
                        Costs = DecimalText.Parse(f[7]),
                    };
                    break;
                }

                default:
                    throw new FormatException($"Unknown risk ledger line '{f[0]}'.");
            }
        }

        return ledger;
    }

    private static Counters CountersOf(SortedDictionary<string, Counters> map, string key)
    {
        if (!map.TryGetValue(key, out var c))
        {
            map[key] = c = new Counters();
        }

        return c;
    }
}
