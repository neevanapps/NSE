namespace NiftySignal.Trading.Core.Costs;

/// <summary>
/// The unit a brokerage amount is charged on. This is separate from the turnover-based statutory charges (STT, exchange charge, SEBI fee,
/// stamp duty, GST), which are always computed on each fill's own turnover.
/// </summary>
public enum BrokerageUnit
{
    /// <summary>Charged once per order, however many fills the order has. A flat amount is attached to the order's FIRST fill (later fills of the same order carry 0). A percentage is applied to the order's CUMULATIVE turnover (capped per order) and each fill carries the increment, so the order total never exceeds the order-level cap.</summary>
    PerOrder = 0,

    /// <summary>Charged separately on every fill: a flat amount per fill, or a percentage of that fill's turnover (capped per fill). Several fills of one order are charged several times.</summary>
    PerFill = 1,
}

/// <summary>Brokerage assumption. There is deliberately no default amount AND no default unit: the brokerage plan is not known to this project and must be stated in the schedule entry.</summary>
public abstract record BrokerageSpec
{
    public abstract string Kind { get; }

    public required BrokerageUnit Unit { get; init; }
}

/// <summary>A fixed amount per <see cref="BrokerageSpec.Unit"/>.</summary>
public sealed record FlatBrokerage : BrokerageSpec
{
    public required decimal Amount { get; init; }

    public override string Kind => "Flat";
}

/// <summary>A fraction of turnover (of the fill, or of the order's cumulative turnover, per <see cref="BrokerageSpec.Unit"/>), optionally capped per unit.</summary>
public sealed record PercentOfTurnoverBrokerage : BrokerageSpec
{
    public required decimal Rate { get; init; }

    /// <summary>Cap on the brokerage of one unit (one order for PerOrder, one fill for PerFill).</summary>
    public decimal? Cap { get; init; }

    public override string Kind => "PercentOfTurnover";
}

/// <summary>What has already been charged on an order before the current fill: number of fills, cumulative turnover and cumulative brokerage.</summary>
public readonly record struct OrderChargeState(int Fills, decimal Turnover, decimal BrokerageCharged)
{
    public static OrderChargeState None => default;
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

    /// <summary>Names of cost components whose rate is still PROVISIONAL (not read from a primary document, or not supplied). Reports repeat this list and also show net P&amp;L without these components.</summary>
    public IReadOnlyList<string> ProvisionalComponents { get; init; } = Array.Empty<string>();
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

            if (s.Brokerage is FlatBrokerage { Amount: < 0m } || s.Brokerage is PercentOfTurnoverBrokerage { Rate: < 0m or > 1m } || s.Brokerage is PercentOfTurnoverBrokerage { Cap: < 0m })
            {
                errors.Add($"Cost schedule {s.Version}: brokerage must not be negative.");
            }

            if (!Enum.IsDefined(s.Brokerage.Unit))
            {
                errors.Add($"Cost schedule {s.Version}: brokerage unit is not defined.");
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
/// Cost calculation for one trade date. Rounding and aggregation (fixed, documented): STT, exchange charge, SEBI fee and stamp duty are
/// computed on the fill's own premium turnover and rounded to 2 decimals half-away-from-zero; brokerage follows its
/// <see cref="BrokerageUnit"/> (see there) and is rounded the same way; GST is
/// <c>GstRate * (rounded brokerage + rounded exchange charge + rounded SEBI fee)</c> of that fill, rounded the same way; the fill total is the
/// sum of the six rounded components. Costs are computed per fill and never netted across fills before rounding.
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

    /// <summary>The resolved schedule entry (rates, source status, provisional component names).</summary>
    public CostScheduleEntry Entry => _entry;

    public string SourceStatus => _entry.SourceStatus;

    public static bool IsConfigured(CostsConfig config, DateOnly tradeDate)
        => config.Schedules.Any(s => s.EffectiveFrom <= tradeDate && (s.EffectiveTo is null || s.EffectiveTo >= tradeDate));

    public BrokerageUnit BrokerageUnit => _entry.Brokerage.Unit;

    /// <summary>Costs of ONE fill. <paramref name="prior"/> is what the same order already incurred (default: a new order's first fill).</summary>
    public CostBreakdown Compute(TradeSide side, decimal turnover, OrderChargeState prior = default)
    {
        if (turnover < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(turnover));
        }

        var brokerage = BrokerageOf(turnover, prior);
        var stt = side == TradeSide.Sell ? Round(turnover * _entry.SttSellRate) : 0m;
        var exchange = Round(turnover * _entry.ExchangeTransactionRate);
        var sebi = Round(turnover * _entry.SebiTurnoverRate);
        var stamp = side == TradeSide.Buy ? Round(turnover * _entry.StampBuyRate) : 0m;
        var gst = Round(_entry.GstRate * (brokerage + exchange + sebi));
        return new CostBreakdown(brokerage, stt, exchange, sebi, stamp, gst);
    }

    private decimal BrokerageOf(decimal turnover, OrderChargeState prior)
    {
        var spec = _entry.Brokerage;
        switch (spec)
        {
            case FlatBrokerage flat:
                return spec.Unit == BrokerageUnit.PerFill ? Round(flat.Amount) : (prior.Fills == 0 ? Round(flat.Amount) : 0m);
            case PercentOfTurnoverBrokerage pct:
                if (spec.Unit == BrokerageUnit.PerFill)
                {
                    return Round(pct.Cap is { } fillCap ? Math.Min(turnover * pct.Rate, fillCap) : turnover * pct.Rate);
                }

                var cumulative = (prior.Turnover + turnover) * pct.Rate;
                var orderTotal = Round(pct.Cap is { } orderCap ? Math.Min(cumulative, orderCap) : cumulative);
                return Math.Max(0m, orderTotal - prior.BrokerageCharged);
            default:
                throw new NotSupportedException("Unknown brokerage spec.");
        }
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

    public static readonly IReadOnlyList<string> Provisional = new[] { "Brokerage", "SebiFee", "StampDuty", "Gst" };

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
        ProvisionalComponents = Provisional,
    };

    public const string Npt1StatusText =
        "NPT1 FROZEN FEE MODEL (Nifty_Paper_Three_V1_20261012): brokerage 0; STT 0.15% sell; exchange 0.03503% buy+sell; SEBI 0.000001 buy+sell; stamp 0.003% buy; GST 18% of exchange+SEBI. " +
        "Differs from nse-index-options-2026-04-01-provisional ONLY in the exchange rate (0.03503% instead of 0.03553%); that global schedule is unchanged.";

    /// <summary>The fee model the three NPT1 strategies were measured with. A separate, versioned schedule: the global 2026-04-01 schedule is not edited. Brokerage is passed explicitly (the frozen model uses 0).</summary>
    public static CostScheduleEntry Npt1FrozenV1(BrokerageSpec brokerage) => new()
    {
        Version = "npt1-frozen-fees-v1",
        EffectiveFrom = new DateOnly(2026, 4, 1),
        Brokerage = brokerage,
        SttSellRate = 0.0015m,
        ExchangeTransactionRate = 0.0003503m,
        SebiTurnoverRate = 0.000001m,
        StampBuyRate = 0.00003m,
        GstRate = 0.18m,
        SourceStatus = Npt1StatusText,
        ProvisionalComponents = Array.Empty<string>(),
    };
}
