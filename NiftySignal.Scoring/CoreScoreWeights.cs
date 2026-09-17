namespace NiftySignal.Scoring;

/// <summary>
/// The 8-term Core score's weights (2026-09-13 live-wiring plan A4), originally ported directly
/// from `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs`'s own `CoreScoreWeights` record.
///
/// <b>Revised 2026-09-16</b>: `ItmSkew` and `GammaExposure` zeroed out, not just cut, after both
/// were checked at real scale for the first time (a per-term correlation pass this composite had
/// never had, prompted by a live observation that the score read bullish while Nifty fell) and
/// found structurally unreliable, not just weak -- see `docs/SCORE_CANDIDATES.md`'s 2026-09-16
/// revisions for the full evidence:
/// - `ItmSkew` (`PutAvgIv - CallAvgIv`, Itm2Atm1 band) read bearish on **100% of ~1,436 cadences,
///   in all 8 testable day/chain combinations, zero exceptions** -- not "mostly one-sided", never
///   once positive. Traced to the band itself, not an IV-solver defect: it compares ITM calls
///   (strikes below spot) against ITM puts (strikes above spot) -- different points on the strike
///   axis, so it reproduces Nifty's normal downward-sloping skew by construction, independent of
///   any day's actual sentiment.
/// - `GammaExposure` (`Sigma(call: +Gamma*OI, put: -Gamma*OI)`) was genuinely two-sided on only 2
///   of 10 day/chain checks; on the other 8 it was stuck heavily one-sided, and on the 2 days
///   checked against both chains, the stuck direction **flipped depending on which chain computed
///   it** -- same day, same underlying IV surface, different call/put OI distribution. Black-
///   Scholes Gamma is non-negative for both calls and puts regardless of IV, so this sign is
///   driven by chain-specific OI positioning, not a shared IV bug.
///
/// Backtested directly before this change: zeroing both (`CoreScoreOptionSimulator`'s
/// `--drop-itm-skew-and-gamma` mode) improved every one of Hysteresis/Crossover/Combined's 5-day
/// backtest totals (e.g. Hysteresis +75.03 -> +119.60 pts; Crossover 15/30 +248.15 -> +254.50 pts).
/// The remaining 6 weights are rescaled so <see cref="Total"/> is 1.0 -- purely for readability
/// (each weight now reads directly as its share of the composite); <see cref="CoreScoreCalculator"/>
/// renormalizes by whichever weight is actually present each cadence, so a uniform rescale of
/// every weight changes no computed score, only how the numbers themselves read.
/// See `docs/replication_plan.md` for the original per-term rationale, band, and sign for each.
/// </summary>
public sealed record CoreScoreWeights(
    string Version,
    double DepthImbalance,
    double ItmSkew,
    double FutureCvdNet5Min,
    double NotionalVolumeRatio,
    double GammaExposure,
    double TrendReversion15m,
    double BasisChange,
    double OiChangeDiff15m,
    // 2026-09-17, backtest/Phase-1 unification: weight for CoreScoreComponentInputs.ItmSkewChange15m
    // (see its own doc comment). Default 0 -- inert, not yet deployed live.
    double ItmSkewChange15m = 0.0,
    // 2026-09-17, same as above, for CoreScoreComponentInputs.GammaExposureChange5m.
    double GammaExposureChange5m = 0.0)
{
    public static CoreScoreWeights Default { get; } = new(
        Version: "core-score-live-2026-09-16-drop-itmskew-gamma",
        DepthImbalance: 0.316456,
        ItmSkew: 0.0,
        FutureCvdNet5Min: 0.151899,
        NotionalVolumeRatio: 0.177215,
        GammaExposure: 0.0,
        TrendReversion15m: 0.126582,
        BasisChange: 0.101266,
        OiChangeDiff15m: 0.126582);

    /// <summary>Rescaled to sum to 1.0 as of the 2026-09-16 revision (see class doc comment) -- previously deliberately left at 0.915 to match the backtest exactly. <see cref="CoreScoreCalculator"/> renormalizes by whichever weight is actually present each cadence regardless, so this has never been required to sum to 1; it's set to 1.0 now purely so each remaining weight reads as its literal share of the composite.</summary>
    public double Total => DepthImbalance + ItmSkew + FutureCvdNet5Min + NotionalVolumeRatio
        + GammaExposure + TrendReversion15m + BasisChange + OiChangeDiff15m
        + ItmSkewChange15m + GammaExposureChange5m;
}
