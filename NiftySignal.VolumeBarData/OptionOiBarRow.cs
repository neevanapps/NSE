namespace NiftySignal.VolumeBarData;

/// <summary>
/// The ATM±1 options complex's own open-interest FLOW during one future volume-bar's time window
/// -- 2026-09-18, Phase 1 metric 3. Both sub-metrics below are already flow-shaped, persisted
/// directly (no further differencing needed in <c>TradeSimulator</c>, same as
/// <see cref="OptionBandFlowBarRow"/>) -- an earlier version of this row persisted OI x price as a
/// LEVEL for TradeSimulator to difference, which was wrong: differencing a re-priced level
/// conflates real OI change with the option's own (far noisier) mid-price movement between bars
/// (Δ(OI·Price) = OI·ΔPrice + Price·ΔOI + ...), and produced 100+ trades/day of pure price-noise
/// churn on calibration. Both fields here instead compute directly from the DISCRETE per-strike
/// ΔOI at the moment it's observed, weighted by that moment's price -- genuine OI flow, not a
/// price echo.
///
/// Band definition matches <see cref="OptionBandFlowBarRow"/> exactly: ATM = strike nearest the
/// FUTURE's own close price at this bar, band = ATM ± 1 strike (3 strikes, both sides).
/// </summary>
public sealed class OptionOiBarRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    public required int BarIndex { get; set; }

    public required long BarVolumeThreshold { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    /// <summary>Sum of (ΔOI x mid price at the time of that change) across the band's Call-side instruments, for OI changes observed at this bar's boundary vs. the previous one. 0 (not null) when nothing changed or there's no previous state yet (the day's first bar) -- a real "no flow" reading, same convention as <see cref="OptionBandFlowBarRow"/>'s notional volume fields.</summary>
    public decimal CallOiChangeNotional { get; set; }

    public decimal PutOiChangeNotional { get; set; }

    /// <summary>
    /// Sign-weighted vote across the band's 6 instruments (3 strikes x 2 sides):
    /// <c>Sum(sign x |ΔOI| x midPrice)</c>, where sign comes from
    /// <see cref="NiftySignal.Features.OiBuildupClassifier.Classify"/> applied to THIS OPTION's
    /// own bar-over-bar mid-price change and OI change (the option's own price, not the future's --
    /// matches the original time-cadence <c>OiBuildupNet</c> convention exactly, reused here on a
    /// narrower ATM±1 band and the volume-bar clock instead of ATM±2 on the 15s cadence). Put-side
    /// contributions get their sign flipped before summing (a "LongBuildup" read on a put contract
    /// itself is bearish for the underlying -- same flip <c>OiBuildupNet</c> already documented).
    /// Null only on a bar with no usable previous-bar state to diff against (the day's first bar,
    /// or a strike with no prior OI/mid reading yet) -- 0.0 is a real "these cancelled out" or
    /// "every leg was Neutral" reading, not missing data.
    /// </summary>
    public double? BuildupNet { get; set; }
}
