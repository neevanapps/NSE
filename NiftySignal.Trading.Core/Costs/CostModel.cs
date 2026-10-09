namespace NiftySignal.Trading.Core.Costs;

/// <summary>Brokerage assumption. There is deliberately no default: the brokerage plan is not known to this project and must be stated in the schedule entry.</summary>
public abstract record BrokerageSpec
{
    public abstract string Kind { get; }
}

/// <summary>A fixed amount per executed order (fill). Note: partial fills of one order are separate fills here and each is charged.</summary>
public sealed record FlatPerOrderBrokerage : BrokerageSpec
{
    public required decimal Amount { get; init; }

    public override string Kind => "FlatPerOrder";
}

/// <summary>A fraction of the fill's turnover, optionally capped per fill.</summary>
public sealed record PercentOfTurnoverBrokerage : BrokerageSpec
{
    public required decimal Rate { get; init; }

    public decimal? CapPerFill { get; init; }

    public override string Kind => "PercentOfTurnover";
}

/// <summary>
/// One effective-dated, versioned set of cost rates for index options. Every rate is required (no hidden defaults).
/// <see cref="SourceStatus"/> states, per component, whether the rate was read from a primary document or only from a secondary
/// extract, so a provisional label cannot be lost.
/// </summary>
public sealed record CostScheduleEntry
{
    public required string Version { get; init; }

    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>Inclusive; null = open ended.</summary>
    public DateOnly? EffectiveTo { get; init; }

    public required BrokerageSpec Brokerage { get; init; }

    /// <summary>Securities transaction tax on the sale of an option, as a fraction of sale premium value. Sell side only.</summary>
    public required decimal SttSellRate { get; init; }

    /// <summary>Exchange transaction charge (including the IPFT contribution), fraction of premium value, both sides.</summary>
    public required decimal ExchangeTransactionRate { get; init; }

    /// <summary>SEBI turnover fee, fraction of premium value, both sides.</summary>
    public required decimal SebiTurnoverRate { get; init; }

    /// <summary>Stamp duty, fraction of premium value, buy side only.</summary>
    public required decimal StampBuyRate { get; init; }

    /// <summary>GST on (brokerage + exchange transaction charge + SEBI fee). Not applied to STT or stamp duty.</summary>
    public required decimal GstRate { get; init; }

    public required string SourceStatus { get; init; }
}

public sealed record CostsConfig
{
    /// <summary>Effective-dated schedules. Empty = costs not configured; the trading engine refuses to run without a schedule covering the trade date (no silent zero costs).</summary>
    public IReadOnlyList<CostScheduleEntry> Schedules { get; init; } = Array.Empty<CostScheduleEntry>();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var ordered = Schedules.OrderBy(s => s.EffectiveFrom).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            if (string.IsNullOrWhiteSpace(s.Version) || string.IsNullOrWhiteSpace(s.SourceStatus))
            {
                errors.Add("Cost schedule needs a Version and a SourceStatus.");
            }

            if (s.EffectiveTo is { } to && to < s.EffectiveFrom)
            {
                errors.Add($"Cost schedule {s.Version}: EffectiveTo precedes EffectiveFrom.");
            }

            if (new[] { s.SttSellRate, s.ExchangeTransactionRate, s.SebiTurnoverRate, s.StampBuyRate, s.GstRate }.Any(r => r < 0m || r > 1m))
            {
                errors.Add($"Cost schedule {s.Version}: rates must be fractions in [0, 1].");
            }

            if (s.Brokerage is FlatPerOrderBrokerage { Amount: < 0m } || s.Brokerage is PercentOfTurnoverBrokerage { Rate: < 0m or > 1m })
            {
                errors.Add($"Cost schedule {s.Version}: brokerage must not be negative.");
            }

            if (i > 0 && (ordered[i - 1].EffectiveTo is null || ordered[i - 1].EffectiveTo >= s.EffectiveFrom))
            {
                errors.Add($"Cost schedules {ordered[i - 1].Version} and {s.Version} overlap.");
            }
        }

        return errors;
    }
}

public enum TradeSide
{
    Buy = 0,
    Sell = 1,
}

/// <summary>Per-fill cost components in rupees, each already rounded to paise; <see cref="Total"/> is the sum of the rounded components.</summary>
public sealed record CostBreakdown(decimal Brokerage, decimal Stt, decimal ExchangeCharge, decimal SebiFee, decimal StampDuty, decimal Gst)
{
    public decimal Total => Brokerage + Stt + ExchangeCharge + SebiFee + StampDuty + Gst;

    public static CostBreakdown Zero { get; } = new(0m, 0m, 0m, 0m, 0m, 0m);
}

/// <summary>
/// Cost calculation for one trade date. Rounding and aggregation (fixed, documented): brokerage, STT, exchange charge, SEBI fee and
/// stamp duty are each computed on the fill's premium turnover and rounded to 2 decimals half-away-from-zero; GST is
/// <c>GstRate * (rounded brokerage + rounded exchange charge + rounded SEBI fee)</c>, rounded the same way; the fill total is the sum of
/// the six rounded components. Costs are computed per fill and never netted across fills before rounding.
/// </summary>
public sealed class CostModel
{
    private readonly CostScheduleEntry _entry;

    public CostModel(CostsConfig config, DateOnly tradeDate)
    {
        ArgumentNullException.ThrowIfNull(config);
        _entry = config.Schedules.FirstOrDefault(s => s.EffectiveFrom <= tradeDate && (s.EffectiveTo is null || s.EffectiveTo >= tradeDate))
            ?? throw new InvalidOperationException($"No cost schedule is effective on {tradeDate:yyyy-MM-dd}. Costs are never assumed to be zero.");
    }

    public string ScheduleVersion => _entry.Version;

    public string SourceStatus => _entry.SourceStatus;

    public static bool IsConfigured(CostsConfig config, DateOnly tradeDate)
        => config.Schedules.Any(s => s.EffectiveFrom <= tradeDate && (s.EffectiveTo is null || s.EffectiveTo >= tradeDate));

    public CostBreakdown Compute(TradeSide side, decimal turnover)
    {
        if (turnover < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(turnover));
        }

        var brokerage = Round(_entry.Brokerage switch
        {
            FlatPerOrderBrokerage flat => flat.Amount,
            PercentOfTurnoverBrokerage pct => pct.CapPerFill is { } cap ? Math.Min(turnover * pct.Rate, cap) : turnover * pct.Rate,
            _ => throw new NotSupportedException("Unknown brokerage spec."),
        });
        var stt = side == TradeSide.Sell ? Round(turnover * _entry.SttSellRate) : 0m;
        var exchange = Round(turnover * _entry.ExchangeTransactionRate);
        var sebi = Round(turnover * _entry.SebiTurnoverRate);
        var stamp = side == TradeSide.Buy ? Round(turnover * _entry.StampBuyRate) : 0m;
        var gst = Round(_entry.GstRate * (brokerage + exchange + sebi));
        return new CostBreakdown(brokerage, stt, exchange, sebi, stamp, gst);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Cost rates for NSE index options effective for the 18 development sessions (2026-09-04 .. 2026-10-01). Brokerage is NOT supplied:
/// the caller must state it. Component status (see <see cref="StatusText"/>): exchange charge and STT were read from the NSE circulars
/// themselves; SEBI fee, stamp duty and the GST base are taken from NSE page extracts / secondary sources and remain PROVISIONAL.
/// </summary>
public static class NseIndexOptionsCostSchedules
{
    public const string StatusText =
        "PROVISIONAL: STT 0.15% sell (NSE/FATAX/73524, 2026-03-31, w.e.f. 2026-04-01, primary read); exchange charge+IPFT Rs 3,553/crore (NSE/FA/73061, 2026-02-27, w.e.f. 2026-03-01, primary read); " +
        "SEBI fee Rs 10/crore, stamp duty 0.003% buyer, GST 18% on brokerage+exchange+SEBI fee: NSE page extracts / secondary sources, primary text not read; brokerage not supplied";

    public static CostScheduleEntry From20260401(BrokerageSpec brokerage) => new()
    {
        Version = "nse-index-options-2026-04-01-provisional",
        EffectiveFrom = new DateOnly(2026, 4, 1),
        Brokerage = brokerage,
        SttSellRate = 0.0015m,
        ExchangeTransactionRate = 0.0003553m,
        SebiTurnoverRate = 0.000001m,
        StampBuyRate = 0.00003m,
        GstRate = 0.18m,
        SourceStatus = StatusText,
    };
}
