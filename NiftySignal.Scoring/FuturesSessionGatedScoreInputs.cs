namespace NiftySignal.Scoring;

/// <summary>
/// Plain-typed, DB-free inputs for <see cref="FuturesSessionGatedScoreCalculator"/> -- same role
/// <see cref="OptionsThreeWayScoreInputs"/> plays for <see cref="OptionsThreeWayScoreCalculator"/>:
/// lets both the offline backtest (<c>NiftySignal.VolumeBarData.TradeSimulator</c>, which projects
/// its own <c>VolumeBarRow</c> into this) and the live pipeline (which projects a freshly-written
/// <c>VolumeBarRow</c> the same way) call the identical formula without either one depending on the
/// other's EF row type.
/// </summary>
/// <param name="TimeOfDayIst">The bar's own EndTimestamp, already converted to IST time-of-day -- the session-gate gate's own switch input.</param>
/// <param name="ClosePrice">This bar's own close.</param>
/// <param name="PreviousClose">The prior bar's own close, null for the day's first bar -- needed for the BarDurationUrgency leg's own price-direction sign.</param>
/// <param name="DurationSeconds">This bar's own wall-clock fill duration -- the BarDurationUrgency leg's own magnitude input (1/DurationSeconds).</param>
/// <param name="DepthImbalance">This bar's own <c>VolumeBarRow.FutureDepthImbalance</c> -- the Open-leg (before the session-gate switch time) driver.</param>
/// <param name="TopOfBookImbalance">This bar's own <c>VolumeBarRow.TopOfBookImbalance</c> -- the Open-window TOB confirmation gate's own input, not part of the traded score itself.</param>
public readonly record struct FuturesSessionGatedScoreInputs(
    TimeSpan TimeOfDayIst,
    decimal ClosePrice,
    decimal? PreviousClose,
    double DurationSeconds,
    double? DepthImbalance,
    double? TopOfBookImbalance);
