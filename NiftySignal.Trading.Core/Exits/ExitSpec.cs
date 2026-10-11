namespace NiftySignal.Trading.Core.Exits;

public enum RemainderSlMode
{
    /// <summary>Default: the per-trade stop keeps its original threshold (average entry fill price x (1 - TradeSlPct)) after TP1.</summary>
    Unchanged = 0,

    /// <summary>After TP1 has completed, the stop for the remainder moves to the average entry fill price (break-even on price, costs excluded).</summary>
    MoveToBreakeven = 1,
}

/// <summary>
/// Exit rules of one variant. Every setting is optional (null = disabled). All thresholds are fractions of the position's AVERAGE ENTRY FILL
/// PRICE (price only, costs excluded) and are compared with the executable BID of the held contract. Triggers fire on quote events and
/// never wait for a completed bar. They are research parameters of a variant, not approved values; none has a default.
/// </summary>
public sealed record ExitSpec
{
    /// <summary>Per-trade stop: trigger when bid &lt;= avgEntry x (1 - TradeSlPct). Separate from the daily SL.</summary>
    public decimal? TradeSlPct { get; init; }

    /// <summary>TP1: trigger once when bid &gt;= avgEntry x (1 + Tp1Pct).</summary>
    public decimal? Tp1Pct { get; init; }

    /// <summary>Share of the open lots TP1 sells (0 &lt; p &lt; 1), rounded DOWN to whole lots at the first trigger.</summary>
    public decimal? Tp1Proportion { get; init; }

    /// <summary>Final target: trigger when bid &gt;= avgEntry x (1 + FinalTpPct); exits everything not already reserved.</summary>
    public decimal? FinalTpPct { get; init; }

    public RemainderSlMode RemainderSl { get; init; } = RemainderSlMode.Unchanged;

    /// <summary>
    /// Maximum holding time of a position, measured from the instant of the ENTRY FILL (the clock is never reset by TP1 or any partial exit). When it elapses the
    /// whole unreserved remainder is sold through the normal exit path (ExitReason.MaxHold). Null = no limit.
    /// </summary>
    public TimeSpan? MaxHold { get; init; }

    public IReadOnlyList<string> Validate(string variantId)
    {
        var errors = new List<string>();
        if (TradeSlPct is <= 0m or >= 1m)
        {
            errors.Add($"Variant '{variantId}': TradeSlPct must be in (0, 1) when set.");
        }

        if (Tp1Pct is <= 0m)
        {
            errors.Add($"Variant '{variantId}': Tp1Pct must be positive when set.");
        }

        if (FinalTpPct is <= 0m)
        {
            errors.Add($"Variant '{variantId}': FinalTpPct must be positive when set.");
        }

        if ((Tp1Pct is null) != (Tp1Proportion is null))
        {
            errors.Add($"Variant '{variantId}': Tp1Pct and Tp1Proportion must be set together.");
        }

        if (Tp1Proportion is <= 0m or >= 1m)
        {
            errors.Add($"Variant '{variantId}': Tp1Proportion must be in (0, 1).");
        }

        if (MaxHold is { } hold && hold <= TimeSpan.Zero)
        {
            errors.Add($"Variant '{variantId}': MaxHold must be positive when set.");
        }

        if (!Enum.IsDefined(RemainderSl))
        {
            errors.Add($"Variant '{variantId}': RemainderSl is not defined.");
        }

        return errors;
    }
}
