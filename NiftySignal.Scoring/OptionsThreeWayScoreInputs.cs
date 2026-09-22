namespace NiftySignal.Scoring;

/// <summary>
/// Explicit, DB-free per-bar inputs to <see cref="OptionsThreeWayScoreCalculator.ComputeScore"/> --
/// 2026-09-20, step 1 of the live/backtest parity plan (`docs/LIVE_PARITY_PLAN.md`): extracted out
/// of `NiftySignal.VolumeBarData/TradeSimulator.cs`'s own `ComputeOptionsThreeWayScore` so the exact
/// same scoring code can eventually be called from both the offline backtest and a future live
/// pipeline. Deliberately primitive-typed (not the EF row types `OptionAtmBarRow`/`OptionDepthBarRow`
/// that this project has no reference to and must never gain one) -- the caller is responsible for
/// projecting whatever storage-shaped row it has into this record.
/// </summary>
/// <param name="TimeOfDayIst">The bar's own end timestamp, already converted to IST time-of-day (same conversion `TradeSimulator.IstTimeOfDay` performs) -- decides which of the 3 legs (Open/Mid/Close) is active.</param>
/// <param name="ClosePrice">This bar's future close price.</param>
/// <param name="PreviousClose">The previous bar's future close price -- null on the first bar of the day. Only the Mid leg (price-signed ΔIV) needs this.</param>
/// <param name="CurrentAtmIv">This bar's own ATM options complex implied volatility (<c>OptionAtmBarRow.AtmIv</c>), null if the day's IV solve failed or no matching row exists for this bar.</param>
/// <param name="WideBookCallBidQtyAvg">Sum, across the wide (adopted ATM+/-2) band's Call-side instruments, of each instrument's own average top-5 resting bid quantity over this bar (<c>OptionDepthBarRow.CallBidQtyAvg</c>). Only the Open leg needs the 4 book fields.</param>
/// <param name="WideBookPutBidQtyAvg">Same as <paramref name="WideBookCallBidQtyAvg"/>, Put side.</param>
/// <param name="WideBookCallAskQtyAvg">Same as <paramref name="WideBookCallBidQtyAvg"/>, Call-side resting ask quantity.</param>
/// <param name="WideBookPutAskQtyAvg">Same as <paramref name="WideBookCallBidQtyAvg"/>, Put-side resting ask quantity.</param>
public readonly record struct OptionsThreeWayScoreInputs(
    TimeSpan TimeOfDayIst,
    decimal ClosePrice,
    decimal? PreviousClose,
    double? CurrentAtmIv,
    double? WideBookCallBidQtyAvg,
    double? WideBookPutBidQtyAvg,
    double? WideBookCallAskQtyAvg,
    double? WideBookPutAskQtyAvg);
