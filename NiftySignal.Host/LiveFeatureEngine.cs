using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftySignal.Domain;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;
using NiftySignal.Pricing;
using NiftySignal.Scoring;

namespace NiftySignal.Host;

readonly record struct InstrumentState(decimal LastPrice, long Volume, long? OpenInterest, MarketDepth? Depth);

/// <summary>
/// Per-strike depth-imbalance accumulator for the Core score's DepthImbalance term (Batch 3 fix,
/// 2026-09-13) -- ported byte-for-byte from NiftySignal.BacktestData/CadencePopulator.cs's own
/// DepthImbalanceAccumulator (same fields, same per-tick formula, same null-if-no-samples
/// semantics), so live's per-strike tick-level average matches the backtest's exactly instead of
/// the original 3s-instant-sample approximation of it (see LiveFeatureEngine's
/// _coreDepthImbalanceByToken field for the full mismatch this replaces).
/// </summary>
sealed class CoreDepthImbalanceAccumulator
{
    double _imbalanceSum;
    int _imbalanceCount;

    public void ApplyTick(MarketDepth depth)
    {
        var bid = depth.TotalBidQty;
        var ask = depth.TotalAskQty;
        if (bid + ask <= 0)
        {
            return;
        }

        _imbalanceSum += (double)(bid - ask) / (bid + ask);
        _imbalanceCount++;
    }

    /// <summary>Average of the per-tick imbalance ratio this cadence -- null if no depth-bearing tick arrived this cadence.</summary>
    public double? CadenceImbalance => _imbalanceCount > 0 ? _imbalanceSum / _imbalanceCount : null;

    public void Reset()
    {
        _imbalanceSum = 0;
        _imbalanceCount = 0;
    }
}

/// <summary>
/// Turns the live tick stream into the six weighted z-scores + composite score (plan
/// sections 5-6), on top of the already-tested <c>WelfordRollingWindow</c> /
/// <c>CompositeScoreCalculator</c> / <c>ImpliedVolatilitySolver</c> building blocks.
///
/// Several formulas here are deliberately scoped-down v1s where the plan names the metric
/// but not an exact formula, in the same spirit as the plan's own "starting point, revisit
/// after real data" framing for score weights and k (section 15):
/// - PriceMomentum: a straight lookback delta on the futures price over the momentum
///   window, not the plan's EMA-crossover-on-1-min-bars (that needs a bars_1min
///   aggregator that doesn't exist yet).
/// - Pcr and OiBuildupNet: nearest-expiry strikes only, not both tracked expiries.
/// - OiBuildupNet's CE-vs-PE sign convention (call buildup/short-covering = bullish; put
///   buildup/short-covering = bearish for the underlying) is the standard NSE option-chain
///   reading, not something the plan spells out numerically.
/// - IvSkew uses the strike nearest +/-200 points from spot as "OTM put/call at a fixed
///   strike offset" (plan 5.3's own wording, offset left unspecified). Priced against spot,
///   not the tracked future -- see <see cref="ComputeIvSkew"/>'s doc comment (2026-09-04 fix).
/// - DepthImbalance's "NTM strikes" (plan 5.3) = the 2 strikes nearest spot, nearest
///   expiry, combined into (bid-ask)/(bid+ask) -- normalized to [-1,+1] and centered at 0
///   (not a raw bid/ask ratio, which can never be negative; fixed 2026-09-04).
///
/// Two more additions from real live data, 2026-09-04:
/// - Within-cadence smoothing: <see cref="Sample"/> takes a point-in-time read of the five
///   "noisy instantaneous" metrics (FuturesBasis, PriceMomentum, Pcr, DepthImbalance,
///   IvSkew) every few seconds; ComputeCadence averages whatever samples landed since the
///   last cadence tick instead of trusting a single instant right at the 15s boundary.
///   OiBuildupNet is deliberately excluded -- it's a genuine interval delta (change since
///   the last cadence), not a point read, so averaging sub-deltas would change what it
///   measures rather than just denoise it; exchange-reported OI also updates far less often
///   than LTP, so sub-sampling it would mostly average in a lot of true zeros.
/// - k (2026-09-08, audit finding F1): briefly self-normalized as the rolling stddev of the
///   raw (pre-tanh) composite itself (2026-09-04), on the reasoning that a fixed k wasn't
///   generalizable across volatility regimes. In practice this fed k's own denominator off
///   the same series it was trying to normalize, which shrank k on quiet stretches and
///   pushed the score toward saturation right when it should have looked calmest -- over
///   60% of a session routinely sat near +/-100 regardless of true conviction. Reverted to
///   <see cref="CompositeScoreCalculator.DefaultK"/>, a single fixed constant -- currently
///   still the audit's own back-of-envelope k~=1.0 starting point, explicitly provisional
///   until a live session's worth of data on Batch 3's corrected raw distributions
///   (F2/F4/F5/F7/F8) exists to size it from properly (see the fix plan's Batch 4 section).
///
/// A 7th, optional component, 2026-09-04: VixChange is India VIX's change over a 30-minute
/// lookback (<see cref="ComputeVixChange"/>), negated -- VIX-vs-Nifty is the standard "fear
/// gauge" inverse relationship (rising VIX = bearish, falling VIX = mildly bullish), so
/// negating keeps the sign convention consistent with the other six (positive raw =
/// bullish). Computed once per cadence tick only, not through <see cref="Sample"/>'s
/// smoothing -- VIX itself ticks far slower than the other metrics' inputs (observed
/// ~20-25s between updates, often with no price change at all), so 3s sub-sampling would
/// mostly average in repeats of the same stale value. Unlike the other six, a missing or
/// not-yet-warm VixChangeZ never blocks the composite score -- see
/// CompositeScoreCalculator's VixComponentName handling and ScoreComponentInputs'
/// VixChangeZ doc comment.
///
/// Not thread-safe on its own -- <see cref="OnTick"/>, <see cref="Sample"/>, and
/// <see cref="ComputeCadence"/> are called from different loops in the same worker and must
/// be externally synchronized.
///
/// PENDING (audit finding F28, 2026-09-08 lead review -- see fix plan Batch 7, the largest item
/// in it): PCR/IvSkew/VixChange (and VolumePcr) are all "level" metrics currently z-scored
/// against a short rolling window (15-30 min, or 2h post-fix) -- a level that stays genuinely
/// extreme all session (e.g. PCR at 1.4 the whole afternoon) reads as "unusual vs the last half
/// hour" and z-scores back toward 0 the moment it stops *changing*, even though it never stopped
/// being extreme. Proposed fix: persist Raw/VsOpen/Vs5DayMedian separately per component, feed
/// the composite from a session-open or multi-day-anchored value instead of the rolling z, and
/// keep the short z as a journal-only diagnostic. Deserves its own design pass once F23/F24/F27's
/// individual sign questions are settled -- see the plan for why order matters here.
///
/// PENDING (audit finding F44, 2026-09-09 external review -- "extract scoring math out of
/// LiveFeatureEngine so replay and live cannot diverge"): checked before deferring, not just
/// assumed -- there is no hidden non-determinism here today (grepped for DateTimeOffset.UtcNow/
/// DateTime.Now inside this file: none), and NiftySignal.ScoreReplay already drives ticks
/// through this exact class' OnTick/Sample/ComputeCadence, not a reimplementation, so live and
/// replay provably cannot diverge numerically right now. The real gap is structural: that
/// guarantee is emergent (both happen to reuse this one class) rather than enforced by the type
/// system, and the ~15 raw-value Compute* methods read instance state (_latest,
/// _previousCadence, the rolling windows) directly rather than taking it as an explicit
/// parameter -- a future consumer could reimplement instead of reuse without anything catching
/// it. Proper fix: give an immutable MarketState-shaped input to each raw-value method and move
/// them out of this class into their own independently-testable module, so "the shared math" is
/// an explicit contract, not a convention. Deliberately not attempted the night this was found --
/// a real, multi-hour, correctness-sensitive refactor of the file that computes every live
/// (paper) trade decision deserves its own careful pass, not one done under time pressure
/// alongside three other changes.
/// </summary>
public sealed class LiveFeatureEngine
{
    // 91-day T-bill proxy (plan 4.1: "static config value, reviewed weekly"). Audit finding F21
    // fixed (2026-09-09): was a hardcoded const duplicated identically in
    // NiftySignal.Dashboard/Services/LiveDataService.cs; both now read the same-shaped
    // "Pricing" config section (PricingOptions, NiftySignal.Domain.Configuration) from their
    // own appsettings.json instead -- see PricingOptions' own doc comment for why this is two
    // config entries kept in sync by hand, not one shared source. Optional and trailing, like
    // _scoreWeightsOptions above, so the ~280 existing tests and NiftySignal.ScoreReplay (which
    // construct this engine directly, not through DI) fall back to the same 0.065 default
    // rather than needing a config source of their own.
    const double DefaultRiskFreeRate = 0.065;
    readonly IOptionsMonitor<PricingOptions>? _pricingOptions;
    double RiskFreeRate => _pricingOptions?.CurrentValue.RiskFreeRate ?? DefaultRiskFreeRate;

    /// <summary>
    /// Strikes each side of ATM whose per-cadence analytics get persisted (2026-09-05) --
    /// so ATM +/- 2, i.e. 5 strikes x 2 sides = ~10 rows a cadence. The full tracked chain is
    /// ATM +/- 10; persisting Greeks for all of it every 15s would be ~600k values a day,
    /// mostly for far strikes that barely trade. Display is unaffected -- the option chain and
    /// OI profile still show the full range.
    /// </summary>
    const int PersistedStrikeBand = 2;

    /// <summary>ATM+/-5 band for the ratio composite's flow metrics (1: notional volume, 2: sized OI flow) -- wider than <see cref="PersistedStrikeBand"/> since these are flow quantities meant to capture broader positioning, not just the immediate ATM neighborhood. Well within the tracked ATM+/-10 universe.</summary>
    const int RatioWideStrikeBand = 5;

    // 3 minutes at the 15s cadence (started at 5 min on 2026-09-07, chosen for a deliberately
    // low-frequency, 1-5-trades-a-day strategy; shortened the same day after watching the
    // first live session -- still meaningfully smoothed versus no averaging at all, just less
    // lag between a real move and the score reflecting it).
    //
    // PENDING (audit finding F53) -- this smooths only the combined raw composite, not each of
    // the 14 components individually the way the ratio composite's five metrics already are
    // (audit finding F51). See docs/REVIEW_FINDINGS.md.
    const int CompositeSmoothingCadences = 12;

    readonly IReadOnlyList<Instrument> _instruments;
    readonly Instrument _spot;
    readonly Instrument _future;

    /// <summary>Null when the day's instrument universe doesn't track VIX -- optional by design, see class doc comment.</summary>
    readonly Instrument? _vix;

    readonly List<Instrument> _nearestExpiryOptions;
    readonly DateOnly _nearestExpiry;

    readonly Dictionary<string, InstrumentState> _latest = [];
    Dictionary<string, InstrumentState>? _previousCadence;
    DateTimeOffset? _lastCadenceAt;

    // Normal cadence spacing is 15s; a gap bigger than this means a feed outage happened
    // between cadences, not ordinary scheduling jitter.
    static readonly TimeSpan MaxCadenceGapForOiBuildup = TimeSpan.FromSeconds(20);

    // Audit finding F50 (2026-09-10, user-caught live) -- see FeatureWindowLengths.
    // OiComparisonWindow's own doc comment for why this needs to be much longer than the 15s
    // cadence, and OiLookbackWindow's for the full mechanism.
    readonly OiLookbackWindow _oiLookback = new(FeatureWindowLengths.OiComparisonWindow);

    readonly WelfordRollingWindow _oiBuildupWindow = new(FeatureWindowLengths.OiBuildupNet);
    readonly WelfordRollingWindow _pcrWindow = new(FeatureWindowLengths.Pcr);
    readonly WelfordRollingWindow _basisWindow = new(FeatureWindowLengths.FuturesBasis);
    readonly WelfordRollingWindow _ivSkewWindow = new(FeatureWindowLengths.IvSkew);
    // FeatureWindowLengths.PriceMomentumZScoreWindow, not .PriceMomentum (audit finding F4,
    // 2026-09-08) -- see that constant's own doc comment for why z-scoring against the same
    // window as the raw lookback collapses variance.
    readonly WelfordRollingWindow _momentumWindow = new(FeatureWindowLengths.PriceMomentumZScoreWindow);
    readonly WelfordRollingWindow _depthImbalanceWindow = new(FeatureWindowLengths.DepthImbalance);
    readonly WelfordRollingWindow _vixWindow = new(FeatureWindowLengths.VixChangeZScoreWindow);
    readonly WelfordRollingWindow _gammaExposureWindow = new(FeatureWindowLengths.GammaExposure);
    readonly WelfordRollingWindow _volumePcrWindow = new(FeatureWindowLengths.VolumePcr);
    readonly WelfordRollingWindow _spreadRatioWindow = new(FeatureWindowLengths.SpreadRatio);

    // Audit finding F55's price-led dynamic-hybrid mode (2026-09-11): the tracked future's own
    // cumulative VWAP, reset once per day (this class's own lifetime already resets per day --
    // see BacktestRunner's one-LiveFeatureEngine-per-trading-day design). _previousFutureVolume
    // is the same "diff cumulative day volume against the prior tick" baseline pattern
    // _previousVolumeByToken already uses, just scoped to the single future token rather than a
    // per-option dictionary.
    long? _previousFutureVolume;
    double _futureCumulativePriceVolume;
    double _futureCumulativeVolume;
    readonly WelfordRollingWindow _futuresVwapDeviationWindow = new(FeatureWindowLengths.FuturesVwapDeviation);
    readonly WelfordRollingWindow _vannaExposureWindow = new(FeatureWindowLengths.VannaExposure);
    readonly WelfordRollingWindow _charmExposureWindow = new(FeatureWindowLengths.CharmExposure);
    readonly WelfordRollingWindow _cvdProxyWindow = new(FeatureWindowLengths.CvdProxy);
    readonly WelfordRollingWindow _straddleRichnessWindow = new(FeatureWindowLengths.StraddleRichness);

    // ---- Core score (2026-09-13, live-wiring plan Batch 2, docs/replication_plan.md) ----
    // A third, independent scoring pipeline (see CoreScoreSnapshot's own doc comment) --
    // deliberately NOT sharing the 14-component composite's or the ratio composite's own
    // Compute* methods, fields, or bands, even where the underlying idea overlaps (plan A3's
    // own rationale: the old methods must stay untouched so their still-computed diagnostic
    // values never silently change shape, and the Core-score methods are free to use the exact
    // band/formula CoreScoreOptionSimulator.cs actually validated).

    /// <summary>7 of the 8 Core-score terms are session-rank-transformed (see RankSigned below); TrendReversion15m is the one exception (already bounded [-1,1] by construction) and has no tracker.</summary>
    readonly SessionRankTracker _coreDepthImbalanceRank = new();
    readonly SessionRankTracker _coreItmSkewRank = new();
    readonly SessionRankTracker _coreFutureCvdRank = new();
    readonly SessionRankTracker _coreNotionalVolumeRatioRank = new();
    readonly SessionRankTracker _coreGammaExposureRank = new();
    readonly SessionRankTracker _coreBasisChangeRank = new();
    readonly SessionRankTracker _coreOiChangeDiffRank = new();

    // TEMPORARY DIAGNOSTIC (2026-09-16) -- see SessionRankTracker.Count's own comment. Public
    // (not internal) so NiftySignal.CoreScoreReplayDiff, a separate assembly, can read it. No
    // production caller.
    public int CoreBasisChangeRankCountDiagnostic => _coreBasisChangeRank.Count;
    public IReadOnlyList<double> CoreBasisChangeRankValuesDiagnostic => _coreBasisChangeRank.Values;

    /// <summary>
    /// DepthImbalance/ItmSkew (Batch 3 fix, 2026-09-13): NOT "noisy instantaneous" 3s-sampled
    /// averages like the 14-component composite's own DepthImbalance/IvSkew -- CoreScoreReplayDiff
    /// caught the original 3s-instant-then-averaged implementation diverging from
    /// CoreScoreOptionSimulator by up to ~0.37 (DepthImbalance) on effectively every cadence, a
    /// genuine structural mismatch, not floating-point noise. Fed per-TICK in OnTick instead,
    /// exactly matching NiftySignal.BacktestData/CadencePopulator.cs's own OptionInstrumentState:
    /// DepthImbalance is averaged across every tick THIS cadence per strike (not sampled every
    /// ~3s), and ItmSkew's IV is solved from the LAST two-sided-quote mid THIS cadence specifically
    /// (null if none arrived, never held over from an earlier cadence) rather than a 5-sample
    /// average of _latest's possibly-stale state. Reset once per cadence in ComputeCadence itself
    /// (after the Core-score combine block reads them), NOT in ResetSamples() -- that runs too
    /// early (before the combine block), which would silently zero these out before they're ever
    /// read.
    /// </summary>
    readonly Dictionary<string, CoreDepthImbalanceAccumulator> _coreDepthImbalanceByToken = [];
    readonly Dictionary<string, decimal?> _coreCadenceMidPriceByToken = [];

    /// <summary>
    /// GammaExposure (Batch 3 fix, 2026-09-13): each strike's LAST successfully-solved Gamma,
    /// frozen and reused on any cadence with no fresh two-sided quote for that strike -- ported
    /// from NiftySignal.BacktestData/CadencePopulator.cs's own forward-fill
    /// (<c>lastGamma = row.Gamma ?? lastGamma</c> in BuildScoreCadences), which the original live
    /// implementation did NOT replicate: it re-solved Gamma fresh every cadence from
    /// <c>_latest</c>'s held-over (possibly many-cadences-stale) mid combined with the CURRENT
    /// (continuously changing) underlying/time-to-expiry, silently drifting the Gamma of any
    /// quiet strike away from what the backtest's frozen value holds -- caught by
    /// CoreScoreReplayDiff as a persistent, large-magnitude GammaExposureRaw mismatch (max
    /// ~51,360 on a metric whose own typical scale is comparable). Deliberately NOT reset
    /// per-cadence or per-day boundary (a new LiveFeatureEngine instance starts with an empty
    /// cache each trading day anyway, matching CadencePopulator's own fresh-per-day forward-fill).
    /// </summary>
    readonly Dictionary<string, double> _coreLastGammaByToken = [];

    /// <summary>
    /// DepthImbalance/ItmSkew (Batch 3 fix, 2026-09-13, second pass): the SAME forward-fill
    /// pattern as <see cref="_coreLastGammaByToken"/>, one layer down -- CoreScoreOptionSimulator's
    /// own BuildScoreCadences forward-fills each strike's raw Depth/Iv value across quiet cadences
    /// too (<c>lastDepth = row.DepthImbalanceFromLastCadence ?? lastDepth</c>/<c>lastIv = row.
    /// ImpliedVolatility ?? lastIv</c>), on TOP of the per-cadence accumulator/mid-price the first
    /// pass already fixed. A strike that ticked before but goes quiet for a cadence or two keeps
    /// contributing its last known Depth ratio / solved IV, not nothing -- caught by
    /// CoreScoreReplayDiff as a small residual null-mismatch (backtest non-null, live null) on
    /// ~1% of cadences, cascading into a larger session-rank (*Signed) mismatch downstream since a
    /// wrongly-excluded cadence permanently diverges the two SessionRankTracker populations from
    /// that point on. Deliberately NOT reset per-cadence (same reasoning as _coreLastGammaByToken).
    /// </summary>
    readonly Dictionary<string, double> _coreLastDepthImbalanceByToken = [];
    readonly Dictionary<string, double> _coreLastIvByToken = [];

    /// <summary>
    /// GammaExposure (Batch 3 fix, 2026-09-13, third pass): each strike's last known OpenInterest,
    /// held over across ticks that don't carry OI data (<see cref="Domain.Entities.Tick.OpenInterest"/>
    /// is itself nullable -- not every tick payload includes it). `_latest[token].OpenInterest`
    /// does NOT hold over this way: OnTick replaces the WHOLE InstrumentState on every tick, so an
    /// OI-less tick silently nulls out a strike's previously-known OI until its next OI-bearing
    /// tick. CadencePopulator's own OptionInstrumentState.LatestOpenInterest is a dedicated
    /// property updated only when `tick.OpenInterest is {} oi` (see its own ApplyTick), never
    /// cleared by an OI-less tick -- this cache replicates that specifically for GammaExposure's
    /// OI read, scoped to Core-score use only. Deliberately NOT touching `_latest`'s own
    /// construction or ComputeCoreOiChangeDiffRaw's existing _previousCadence-based OI reads --
    /// that method already matches the backtest exactly on the narrower ATM+/-2 band it uses,
    /// where this gap apparently doesn't surface in practice, and _previousCadence is a
    /// pre-existing, broadly-shared field the old composite also depends on.
    /// </summary>
    readonly Dictionary<string, long> _coreLastOpenInterestByToken = [];

    /// <summary>
    /// NotionalVolumeRatio (Batch 3 fix, fourth pass): each strike's last known mark price
    /// (this cadence's own fresh two-sided-quote mid where one exists, else forward-filled) --
    /// see ComputeCoreNotionalVolumeRatioRaw's own doc comment for why this specific forward-fill
    /// is needed (a strike can trade real volume without also getting a fresh depth quote the
    /// same cadence). Same forward-fill shape as _coreLastGammaByToken/_coreLastIvByToken, just
    /// caching the mark price itself rather than a value derived from it.
    /// </summary>
    readonly Dictionary<string, decimal> _coreLastMarkByToken = [];

    /// <summary>Volume baseline for the Core score's own NotionalVolumeRatio (ATM+/-RatioWideStrikeBand) -- kept as its own dictionary rather than merged with any other volume baseline, same "no accidental cross-method ordering dependency" reasoning _previousVolumeByToken's own doc comment gives (the ratio composite's own equivalent baseline, _previousVolumeByTokenRatio, was removed in Batch 4 along with the rest of the ratio composite).</summary>
    readonly Dictionary<string, long> _previousVolumeByTokenCoreScore = [];

    /// <summary>This cadence's net classified future volume, accumulated per-tick in OnTick (byte-for-byte port of NiftySignal.BacktestData/CadencePopulator.cs's FutureCvdProxyAccumulator.ApplyTick) and consumed/reset once per cadence by ComputeCoreFutureCvdNet5Min -- same instant/reset shape as FutureCvdProxyAccumulator's own CadenceNet/ResetCadence pair.</summary>
    long _coreFutureCvdCadenceNet;
    bool _coreFutureCvdCadenceHasContribution;
    readonly Queue<(DateTimeOffset Timestamp, long Net)> _coreFutureCvdNet5MinWindow = new();
    static readonly TimeSpan CoreFutureCvdNet5MinWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// FutureCvdNet5Min (Batch 3 fix, 2026-09-13, second pass, refined third pass): the timestamp
    /// of this metric's own first-ever cadence CONTRIBUTION (not the first cadence overall), set
    /// once and never touched again -- ported from NiftySignal.BacktestData/CadencePopulator.cs's
    /// own RollingNetSumWindow.IsWarmedUp (<c>latestTimestamp - firstSeenAt >= window</c>).
    /// <see cref="_coreFutureCvdLatestTimestamp"/> is that same window's own <c>_latestTimestamp</c>
    /// -- ALSO updated only on a contributing cadence, not simply read as <c>now</c>. Both fields,
    /// plus the window itself, are updated ONLY when this cadence has a real classifiable tick
    /// (<see cref="_coreFutureCvdCadenceHasContribution"/>) -- CadencePopulator's own comment on
    /// its Add call is explicit: "skip a null cadence, never zero-fill it." An earlier version of
    /// this fix zero-filled every quiet cadence into the window and warmed up 5 minutes after the
    /// very first cadence regardless of contribution -- caught by CoreScoreReplayDiff as a
    /// residual value-level FutureCvdNet5MinRaw mismatch even after the null/non-null warm-up gate
    /// itself was already correct.
    /// </summary>
    DateTimeOffset? _coreFutureCvdFirstSeenAt;
    DateTimeOffset? _coreFutureCvdLatestTimestamp;

    /// <summary>
    /// TrendReversion15m/BasisChange (Batch 3 fix, 2026-09-13): THIS cadence's own future/spot
    /// open-to-close (first tick's price this cadence vs the latest), ported from
    /// NiftySignal.BacktestData/CadencePopulator.cs's own InstrumentPriceState.CadenceOpen/
    /// CadenceClose -- CadenceContext.FutureChangeFromLastCadence/SpotChangeFromLastCadence are
    /// WITHIN-cadence candle bodies despite the "FromLastCadence" name, NOT a cross-cadence
    /// mid-to-mid diff. The original implementation tracked a previous-cadence future MID and
    /// spot LTP instead (a genuinely different, cross-cadence quantity) -- caught by
    /// CoreScoreReplayDiff as a near-100% TrendReversion15m/BasisChange mismatch.
    ///
    /// The future's own open/close is fed with each tick's MID price (see OnTick's own comment on
    /// this -- CadencePopulator's main loop mid-prices the future specifically, `futureState.
    /// ApplyTick(MidPrice(tick) ?? tick.LastPrice)`, to dodge the future's LTP alternating between
    /// two levels a few points apart within seconds); spot's own open/close is fed with raw LTP
    /// (CadencePopulator's spotState.ApplyTick(tick.LastPrice) does NOT mid-price it) -- a first
    /// attempt at this fix used LTP for both and still showed a real, moderate mismatch, caught by
    /// re-running CoreScoreReplayDiff after the "within-cadence not cross-cadence" fix alone.
    ///
    /// Reset once per cadence right after the Core-score combine block reads them (same timing as
    /// _coreDepthImbalanceByToken/_coreCadenceMidPriceByToken -- see their own doc comment for why
    /// ResetSamples() itself is too early).
    /// </summary>
    decimal? _coreFutureCadenceOpen;
    decimal? _coreFutureCadenceClose;
    decimal? _coreSpotCadenceOpen;
    decimal? _coreSpotCadenceClose;
    readonly Queue<(DateTimeOffset Timestamp, double Change)> _coreTrendReversionWindow = new();
    static readonly TimeSpan CoreTrendReversionWindow = TimeSpan.FromMinutes(15);

    /// <summary>Rolling 15-real-minute SUM of (CallOiDelta-PutOiDelta), ATM+/-PersistedStrikeBand -- each cadence's own increment comes from _previousCadence (read BEFORE it's overwritten at the end of ComputeCadence), NOT _oiLookback's ~4-minute comparison ComputeOiBuildupNet uses -- feeding a 4-minute-lookback delta into a 15-minute rolling SUM would overlap successive readings and badly over-count flow (see docs/replication_plan.md A3's own explanation).</summary>
    readonly Queue<(DateTimeOffset Timestamp, long CallDelta, long PutDelta)> _coreOiChangeDiffWindow = new();
    static readonly TimeSpan CoreOiChangeDiffWindow = TimeSpan.FromMinutes(15);

    /// <summary>ATM+/-10 for the Core score's own GammaExposure -- "version A" (each strike's own individually-solved IV), confirmed distinct from the 14-component composite's ComputeGammaExposure ("version B", one shared ATM vol) via docs/SCORE_CANDIDATES.md:697-698. See ComputeCoreGammaExposureRaw.</summary>
    const int CoreGammaBandOffset = 10;

    /// <summary>Real-time trailing moving averages of the already-computed, already-tanh'd CoreScore -- the Crossover strategy's fast/slow reads. MUST be replayed on restart (see SeedCoreScoreHistory) -- unlike the ratio composite's own fast FIFO, an already-open Crossover position depends on these to ever detect its next exit.</summary>
    readonly Queue<(DateTimeOffset Timestamp, double Score)> _coreScoreFastHistory = new();
    readonly Queue<(DateTimeOffset Timestamp, double Score)> _coreScoreSlowHistory = new();
    static readonly TimeSpan CoreScoreFastWindow = TimeSpan.FromMinutes(10);
    static readonly TimeSpan CoreScoreSlowWindow = TimeSpan.FromMinutes(30);

    /// <summary>This cadence's Core-score snapshot, built inside ComputeCadence and exposed here rather than changing ComputeCadence's own return type (which ~280 existing call sites -- tests, NiftySignal.ScoreReplay -- all depend on staying ScoreSnapshot?). Null whenever ComputeCadence itself returns null (no spot/future tick yet) or hasn't been called yet this process lifetime.</summary>
    public CoreScoreSnapshot? LastCoreScoreSnapshot { get; private set; }

    /// <summary>
    /// The ATM strike/quotes/Greeks <see cref="ComputeStraddleRichness"/> saw last cadence, kept
    /// independently of <see cref="_previousCadence"/> for the same reason
    /// <see cref="_previousVolumeByToken"/> is (see BuildStrikeSnapshots' doc comment) -- and not
    /// seeded from persisted history on restart, since only the final raw result is persisted,
    /// not these intermediates. The very first cadence after a restart is null, same as every
    /// other "previous cadence" tracker in this class.
    /// </summary>
    decimal? _previousStraddleStrike;
    DateTimeOffset? _previousStraddleAt;
    double? _previousStraddleMid;
    double? _previousStraddleNetDelta;
    double? _previousStraddleThetaPerDay;
    double? _previousStraddleUnderlying;

    /// <summary>Last <see cref="CompositeSmoothingCadences"/> single-cadence composite raw values, oldest first -- see ComputeCadence's smoothing comment.</summary>
    readonly Queue<double> _compositeRawHistory = new();

    readonly RunningAverage _basisSamples = new();
    readonly RunningAverage _parityGapSamples = new();
    readonly RunningAverage _momentumSamples = new();
    readonly RunningAverage _pcrSamples = new();
    readonly RunningAverage _depthImbalanceSamples = new();
    readonly RunningAverage _ivSkewSamples = new();

    /// <summary>
    /// Per-strike spread samples, fed by <see cref="Sample"/> every ~3s like the five score
    /// metrics above -- but consumed and reset by <see cref="BuildStrikeSnapshots"/> instead of
    /// <see cref="ResetSamples"/>, so the two stay independent of each other's call order (same
    /// reason <see cref="_previousVolumeByToken"/> keeps its own baseline rather than sharing
    /// <c>_previousCadence</c>). Keyed by every tracked option token, not just the currently
    /// persisted band, so a strike that enters the band mid-window still has real history
    /// rather than nothing to average.
    /// </summary>
    readonly Dictionary<string, RunningAverage> _spreadAbsSamplesByToken = [];
    readonly Dictionary<string, RunningAverage> _spreadPctOfMidSamplesByToken = [];

    readonly Queue<(DateTimeOffset At, decimal FuturesPrice)> _momentumLookback = new();
    readonly Queue<(DateTimeOffset At, decimal Vix)> _vixLookback = new();

    /// <summary>Session count bounds for <see cref="ComputeIvRank"/> -- see its own doc comment.</summary>
    const int MinPriorSessionsForIvRank = 5;
    const int MaxPriorSessionsForIvRank = 20;

    /// <summary>
    /// Last <see cref="MaxPriorSessionsForIvRank"/> prior sessions' representative ATM IV (one
    /// value per session, session mean), seeded once at Host startup -- see
    /// <see cref="SeedPriorSessionIvHistory"/>. Not a rolling window: unlike every other field
    /// in this class, this list is replaced wholesale at startup and never mutated intraday.
    /// </summary>
    List<double> _priorSessionAtmIv = [];

    /// <summary>
    /// Today's own ATM IV observations so far, used only as <see cref="ComputeIvRank"/>'s
    /// cold-start fallback while <see cref="_priorSessionAtmIv"/> doesn't yet hold
    /// <see cref="MinPriorSessionsForIvRank"/> sessions. Naturally bounded by the trading
    /// session itself (no time-window eviction needed, unlike every other lookback in this
    /// class) -- restart-safe via <see cref="SeedHistory"/> replaying persisted
    /// <see cref="ScoreSnapshot.AtmIv"/> values back in.
    /// </summary>
    readonly List<double> _todaySessionAtmIv = [];

    /// <summary>
    /// Cumulative day volume per token as of the last <see cref="BuildStrikeSnapshots"/> pass.
    /// Kept separate from <see cref="_previousCadence"/> so strike-snapshot building has no
    /// ordering dependency on ComputeCadence -- either can run first, and neither resets the
    /// other's baseline. Only holds the persisted band's tokens, so it stays tiny.
    /// </summary>
    readonly Dictionary<string, long> _previousVolumeByToken = [];

    /// <summary>
    /// OI per token as of the last <see cref="BuildStrikeSnapshots"/> pass -- same independent-
    /// baseline reasoning as <see cref="_previousVolumeByToken"/>, and not <see cref="_previousCadence"/>
    /// (see BuildStrikeSnapshots' own doc comment for why). Only holds the persisted band's tokens.
    /// </summary>
    readonly Dictionary<string, long> _previousOpenInterestByToken = [];

    /// <summary>
    /// Mark price (bid/ask mid, LTP fallback) per token as of the last
    /// <see cref="BuildStrikeSnapshots"/> pass -- same independent-baseline reasoning as
    /// <see cref="_previousVolumeByToken"/>, needed to classify each strike's own OI buildup.
    /// </summary>
    readonly Dictionary<string, decimal> _previousMarkPriceByToken = [];

    /// <summary>
    /// Cumulative day volume per token, same idea as <see cref="_previousVolumeByToken"/> but
    /// covering the *entire* nearest-expiry chain rather than just the persisted band -- kept
    /// as its own dictionary (2026-09-07) so ComputeVolumePcr has no ordering dependency on
    /// BuildStrikeSnapshots, matching why the two never shared one to begin with.
    /// </summary>
    readonly Dictionary<string, long> _previousVolumeByTokenFullChain = [];

    /// <summary>
    /// Hot-reloaded, already-validated weights (2026-09-09, external review) -- optional and
    /// trailing so the ~280 existing tests and NiftySignal.ScoreReplay, which construct this
    /// engine directly rather than through DI, are unaffected: they fall back to
    /// <see cref="ScoreWeights.Default"/>, which is exactly what they want (a fixed, known
    /// weight set to assert against, not whatever happens to be hot-reloaded on a live VM).
    /// Read fresh on every <see cref="ComputeCadence"/> call via <see cref="Weights"/>, not
    /// cached at construction, so a config change takes effect on the very next cadence.
    /// </summary>
    readonly IValidatedOptions<ScoreWeights>? _scoreWeightsOptions;

    ScoreWeights Weights => _scoreWeightsOptions?.Current ?? ScoreWeights.Default;

    /// <summary>
    /// Logs a ratio-composite metric's own try/catch failures (weekend build, 2026-09-09) --
    /// optional and trailing, zero-churn for the ~60 existing positional test call sites, same
    /// reasoning as <see cref="_scoreWeightsOptions"/>/<see cref="_pricingOptions"/> above. Null
    /// in tests/NiftySignal.ScoreReplay simply means a ratio-metric failure there is silent
    /// (acceptable -- neither exercises that failure path deliberately).
    /// </summary>
    readonly ILogger? _logger;

    public LiveFeatureEngine(
        IReadOnlyList<Instrument> instruments,
        IValidatedOptions<ScoreWeights>? scoreWeightsOptions = null,
        IOptionsMonitor<PricingOptions>? pricingOptions = null,
        ILogger? logger = null)
    {
        _instruments = instruments;
        _scoreWeightsOptions = scoreWeightsOptions;
        _pricingOptions = pricingOptions;
        _logger = logger;
        _spot = instruments.First(i => i.InstrumentType == InstrumentType.Index);
        _future = instruments.First(i => i.InstrumentType == InstrumentType.Future);
        _vix = instruments.FirstOrDefault(i => i.InstrumentType == InstrumentType.Vix);
        _nearestExpiry = instruments
            .Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate is not null)
            .Select(i => i.ExpiryDate!.Value)
            .Min();
        _nearestExpiryOptions = [.. instruments.Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate == _nearestExpiry)];

        // Core score DepthImbalance/ItmSkew (Batch 3 fix) -- one accumulator/mid-price slot per
        // nearest-expiry option token, for the whole lifetime of this engine instance (the
        // nearest-expiry universe never changes intraday). Fed per-tick in OnTick, read once per
        // cadence in ComputeCadence's combine block, reset once per cadence right after.
        foreach (var option in _nearestExpiryOptions)
        {
            _coreDepthImbalanceByToken[option.Token] = new CoreDepthImbalanceAccumulator();
            _coreCadenceMidPriceByToken[option.Token] = null;
        }
    }

    public void OnTick(Tick tick)
    {
        _latest[tick.Token] = new InstrumentState(tick.LastPrice, tick.Volume, tick.OpenInterest, tick.Depth);

        // Audit finding F55's price-led dynamic-hybrid mode (2026-09-11): accumulate the future's
        // own VWAP per tick, not per cadence -- same reasoning as everywhere else ticks feed
        // _latest directly rather than waiting for the next cadence, since the whole point of
        // this metric is minimizing lag. Every tick contributes its volume delta at that tick's
        // own price, exactly like a real VWAP -- not just a per-cadence sample of price.
        if (tick.Token == _future.Token)
        {
            var volumeDelta = _previousFutureVolume is { } previousVolume ? Math.Max(0, tick.Volume - previousVolume) : 0;
            _previousFutureVolume = tick.Volume;

            _futureCumulativePriceVolume += (double)tick.LastPrice * volumeDelta;
            _futureCumulativeVolume += volumeDelta;

            // Core score (2026-09-13, replication_plan.md A3, FutureCvdNet5Min) -- byte-for-byte
            // port of NiftySignal.BacktestData/CadencePopulator.cs's FutureCvdProxyAccumulator.
            // ApplyTick, applied to the future token here specifically. Reuses this same tick's
            // volumeDelta (already computed above for VWAP) rather than tracking a second,
            // independent previous-volume baseline -- both consumers need the identical
            // per-tick volume delta of the same token, so sharing it is correct, not a violation
            // of this class's usual "independent baselines" rule (that rule exists for consumers
            // with genuinely different needs, not this one).
            if (tick.Depth is { } depth && depth.Bid1Price > 0 && depth.Ask1Price > 0 && volumeDelta > 0)
            {
                var midpoint = (depth.Bid1Price + depth.Ask1Price) / 2m;
                var signed = tick.LastPrice >= midpoint ? volumeDelta : -volumeDelta;
                _coreFutureCvdCadenceNet += signed;
                _coreFutureCvdCadenceHasContribution = true;
            }

            // TrendReversion15m/BasisChange (Batch 3 fix) -- this cadence's own open/close for the
            // future, fed with the tick's own MID price (not LTP), byte-for-byte matching
            // CadencePopulator's own main loop (`var mid = MidPrice(tick) ?? tick.LastPrice;
            // futureState.ApplyTick(mid);`) -- the future's LTP alternates between two levels a
            // few points apart within seconds (same bug Sample()'s own futureMark already guards
            // against), so using it here (as an earlier version of this fix did) reintroduces
            // exactly that noise into TrendReversion15m/BasisChange. Spot below stays LTP-based --
            // CadencePopulator's own spotState.ApplyTick(tick.LastPrice) does NOT mid-price it.
            var futureTickMid = tick.Depth is { } futureDepthForMid && futureDepthForMid.Bid1Price > 0 && futureDepthForMid.Ask1Price > 0
                ? (futureDepthForMid.Bid1Price + futureDepthForMid.Ask1Price) / 2m
                : tick.LastPrice;
            _coreFutureCadenceOpen ??= futureTickMid;
            _coreFutureCadenceClose = futureTickMid;
        }
        else if (tick.Token == _spot.Token)
        {
            // Same within-cadence LTP open/close as the future above, for BasisChange's spot side.
            _coreSpotCadenceOpen ??= tick.LastPrice;
            _coreSpotCadenceClose = tick.LastPrice;
        }
        else if (_coreDepthImbalanceByToken.TryGetValue(tick.Token, out var coreDepthAcc))
        {
            // Core score DepthImbalance/ItmSkew (Batch 3 fix, 2026-09-13) -- byte-for-byte port of
            // NiftySignal.BacktestData/CadencePopulator.cs's own OptionInstrumentState.ApplyTick
            // gating (same "two-sided quote present" condition, same per-tick timing) for exactly
            // these two terms. DepthImbalance averages every tick's imbalance ratio; ItmSkew's mid
            // is simply overwritten with this tick's own (not accumulated) -- see
            // CoreDepthImbalanceAccumulator's and _coreCadenceMidPriceByToken's own doc comments.
            if (tick.Depth is { } coreDepth && coreDepth.Bid1Price > 0 && coreDepth.Ask1Price > 0)
            {
                coreDepthAcc.ApplyTick(coreDepth);
                _coreCadenceMidPriceByToken[tick.Token] = (coreDepth.Bid1Price + coreDepth.Ask1Price) / 2m;
            }

            // GammaExposure (Batch 3 fix, third pass) -- held over independently of whether THIS
            // tick also carries a two-sided quote; see _coreLastOpenInterestByToken's own doc
            // comment for why _latest's own OpenInterest can't be trusted for this.
            if (tick.OpenInterest is { } oi)
            {
                _coreLastOpenInterestByToken[tick.Token] = oi;
            }
        }
    }

    /// <summary>
    /// Restart-safe warm-up: replays previously-persisted cadence snapshots straight into
    /// the rolling windows, in chronological order, exactly as if this process had been
    /// running continuously. Each WelfordRollingWindow already evicts anything older than
    /// its own window duration as it's fed (<c>Add</c>'s own eviction logic), so passing in
    /// more history than any single window needs is harmless -- callers don't have to
    /// pre-filter to each metric's specific window length. Without this, every service
    /// restart silently reset warm-up progress to zero (live-caught 2026-09-04, after
    /// several redeploys mid-session each cost ~30 minutes of re-warming).
    ///
    /// What this can't restore: <c>_previousCadence</c> (per-instrument OI/price from the
    /// prior tick, needed for OiBuildupNet's own delta calc) and the momentum/VIX lookback
    /// queues' raw price history -- those reset to "no prior observation" on restart
    /// regardless. That costs one cadence tick's OiBuildupNet reading (reported as 0 instead
    /// of the true delta) and one tick where momentum/VixChange fall back to 0 -- a single
    /// slightly-wrong point inside a multi-minute window, not a warm-up reset.
    ///
    /// <see cref="_oiLookback"/> (audit finding F50, 2026-09-10) is not restart-seeded either,
    /// and costs more than "one tick": ComputeOiBuildupNet/ComputeRatioSizedOiFlowRaw both read
    /// through it, so every strike reads as "not enough history yet" (skipped, not a fabricated
    /// 0) for a full <see cref="FeatureWindowLengths.OiComparisonWindow"/> after every restart -- up to a few
    /// minutes of OiBuildupNet returning exactly 0 (contributing nothing, same as any other
    /// cadence with no qualifying strikes) rather than a real reading. Accepted for now, same
    /// spirit as the _previousCadence gap above: raw per-token OI history isn't in
    /// ScoreSnapshot to replay from (only the already-aggregated OiBuildupNetRaw is), and
    /// StrikeSnapshot's own persisted per-strike OI would need new seeding plumbing of its own
    /// to reach this -- worth doing if the restart-frequency cost turns out to matter in
    /// practice, not assumed up front.
    /// </summary>
    public void SeedHistory(IEnumerable<ScoreSnapshot> history)
    {
        foreach (var snapshot in history.OrderBy(s => s.ComputedAt))
        {
            if (snapshot.OiBuildupNetRaw is { } oi)
            {
                _oiBuildupWindow.Add(snapshot.ComputedAt, oi);
            }

            if (snapshot.PcrRaw is { } pcr)
            {
                _pcrWindow.Add(snapshot.ComputedAt, pcr);
            }

            if (snapshot.FuturesBasisRaw is { } basis)
            {
                _basisWindow.Add(snapshot.ComputedAt, basis);
            }

            if (snapshot.IvSkewOneSigmaRaw is { } ivSkew)
            {
                _ivSkewWindow.Add(snapshot.ComputedAt, ivSkew);
            }

            if (snapshot.PriceMomentumRaw is { } momentum)
            {
                _momentumWindow.Add(snapshot.ComputedAt, momentum);
            }

            if (snapshot.DepthImbalanceRaw is { } depthImbalance)
            {
                _depthImbalanceWindow.Add(snapshot.ComputedAt, depthImbalance);
            }

            // Rebuilds the smoothing FIFO from persisted history so a restart doesn't need a
            // fresh 5-minute warm-up before smoothing kicks back in. Null for any row persisted
            // before this field existed -- those are simply skipped, same as every other
            // nullable field's replay above.
            if (snapshot.CompositeScoreRawInstant is { } compositeRawInstant)
            {
                _compositeRawHistory.Enqueue(compositeRawInstant);
                while (_compositeRawHistory.Count > CompositeSmoothingCadences)
                {
                    _compositeRawHistory.Dequeue();
                }
            }

            if (snapshot.VixChangeRaw is { } vixChange)
            {
                _vixWindow.Add(snapshot.ComputedAt, vixChange);
            }

            if (snapshot.GammaExposureRaw is { } gammaExposure)
            {
                _gammaExposureWindow.Add(snapshot.ComputedAt, gammaExposure);
            }

            if (snapshot.VolumePcrRaw is { } volumePcr)
            {
                _volumePcrWindow.Add(snapshot.ComputedAt, volumePcr);
            }

            if (snapshot.SpreadRatioRaw is { } spreadRatio)
            {
                _spreadRatioWindow.Add(snapshot.ComputedAt, spreadRatio);
            }

            if (snapshot.VannaExposureRaw is { } vannaExposure)
            {
                _vannaExposureWindow.Add(snapshot.ComputedAt, vannaExposure);
            }

            // Audit finding F55's price-led dynamic-hybrid mode (2026-09-11): same replay
            // discipline as every other z-scored rolling window here -- not optional, skipping it
            // would reset this 30-minute window's warm-up to zero on every restart, the exact
            // 2026-09-04 bug class this replay method exists to prevent. Only the deviation
            // window is replayed; _futureCumulativePriceVolume/_futureCumulativeVolume (the VWAP
            // itself) are NOT -- a restart mid-session would need the day's full tick history to
            // rebuild the true cumulative VWAP, which SeedHistory's snapshot-only replay can't
            // provide (an accepted gap, same spirit as _previousCadence's own documented restart
            // limitations -- VWAP rebuilds correctly from the next restart-to-restart span of real
            // ticks, just starts from today's post-restart price as an implicit new baseline).
            if (snapshot.FuturesVwapDeviationRaw is { } futuresVwapDeviation)
            {
                _futuresVwapDeviationWindow.Add(snapshot.ComputedAt, futuresVwapDeviation);
            }

            if (snapshot.CharmExposureRaw is { } charmExposure)
            {
                _charmExposureWindow.Add(snapshot.ComputedAt, charmExposure);
            }

            if (snapshot.CvdProxyRaw is { } cvdProxy)
            {
                _cvdProxyWindow.Add(snapshot.ComputedAt, cvdProxy);
            }

            if (snapshot.StraddleRichnessRaw is { } straddleRichness)
            {
                _straddleRichnessWindow.Add(snapshot.ComputedAt, straddleRichness);
            }

            // Today's own IV-rank cold-start fallback (2026-09-09 review amendment to F3) --
            // not IvRankRaw itself, which is already the computed rank, not a raw IV value.
            if (snapshot.AtmIv is { } atmIv)
            {
                _todaySessionAtmIv.Add(atmIv);
            }
        }
    }

    /// <summary>
    /// Restart-safe warm-up for the Core score (2026-09-13, live-wiring plan A1/A5), mirroring
    /// <see cref="SeedHistory"/>'s own chronological-replay pattern for a separate table. Two
    /// distinct restart-safety needs, both handled here:
    ///
    /// 1. The 7 ranked terms' <see cref="SessionRankTracker"/> instances are seeded with
    ///    <c>Math.Abs(rawValue)</c> from each persisted row -- matching exactly what would have
    ///    been added had the process run continuously (<see cref="RankSigned"/> always calls
    ///    <c>rank.Add(Math.Abs(value))</c>, never the signed value itself). <see
    ///    cref="CoreScoreSnapshot.TrendReversion15mRaw"/> is deliberately NOT replayed into any
    ///    tracker -- that term has none (see its own doc comment).
    /// 2. <see cref="CoreScoreSnapshot.CoreScore"/> (the already-tanh'd value, not a raw) is
    ///    replayed into both the fast and slow real-time windows -- REQUIRED, not an accepted gap
    ///    like the ratio composite's own fast FIFO: the Crossover strategy is always-positioned,
    ///    so an already-open position depends on these windows to ever detect its next exit (see
    ///    docs/replication_plan.md A5's own correction for the full reasoning). Note: seeding
    ///    <c>CoreScoreCrossoverRules</c>' own <c>previousDiffSign</c> from this same replayed
    ///    history is a SEPARATE step the trading engine (plan Batch 5) is responsible for, once it
    ///    exists -- this method only rebuilds the two moving-average windows themselves.
    /// </summary>
    public void SeedCoreScoreHistory(IEnumerable<CoreScoreSnapshot> history)
    {
        foreach (var snapshot in history.OrderBy(s => s.ComputedAt))
        {
            if (snapshot.DepthImbalanceRaw is { } depthImbalance)
            {
                _coreDepthImbalanceRank.Add(Math.Abs(depthImbalance));
            }

            if (snapshot.ItmSkewRaw is { } itmSkew)
            {
                _coreItmSkewRank.Add(Math.Abs(itmSkew));
            }

            if (snapshot.FutureCvdNet5MinRaw is { } futureCvd)
            {
                _coreFutureCvdRank.Add(Math.Abs(futureCvd));
            }

            if (snapshot.NotionalVolumeRatioRaw is { } notionalVolumeRatio)
            {
                _coreNotionalVolumeRatioRank.Add(Math.Abs(notionalVolumeRatio));
            }

            if (snapshot.GammaExposureRaw is { } gammaExposure)
            {
                _coreGammaExposureRank.Add(Math.Abs(gammaExposure));
            }

            if (snapshot.BasisChangeRaw is { } basisChange)
            {
                _coreBasisChangeRank.Add(Math.Abs(basisChange));
            }

            if (snapshot.OiChangeDiff15mRaw is { } oiChangeDiff)
            {
                _coreOiChangeDiffRank.Add(Math.Abs(oiChangeDiff));
            }

            if (snapshot.CoreScore is { } coreScore)
            {
                _coreScoreFastHistory.Enqueue((snapshot.ComputedAt, coreScore));
                _coreScoreSlowHistory.Enqueue((snapshot.ComputedAt, coreScore));
            }
        }

        // Trim to each window's own real-time span, same eviction rule ComputeCadence's own
        // combine step uses every cadence -- replaying more history than either window needs is
        // harmless (matches SeedHistory's own reasoning for WelfordRollingWindow), the trim below
        // just keeps memory bounded rather than growing with the whole day's replay.
        if (history.Any())
        {
            var lastTimestamp = history.Max(s => s.ComputedAt);
            while (_coreScoreFastHistory.Count > 0 && lastTimestamp - _coreScoreFastHistory.Peek().Timestamp > CoreScoreFastWindow)
            {
                _coreScoreFastHistory.Dequeue();
            }

            while (_coreScoreSlowHistory.Count > 0 && lastTimestamp - _coreScoreSlowHistory.Peek().Timestamp > CoreScoreSlowWindow)
            {
                _coreScoreSlowHistory.Dequeue();
            }
        }
    }

    /// <summary>The nearer of the two tracked weekly expiries -- what strike selection and entry rules (expiry-day session rules) both key off.</summary>
    public DateOnly NearestExpiry => _nearestExpiry;

    /// <summary>Looks up an instrument's static metadata (tick size, lot size, etc.) for the trade simulator -- searches the full tracked universe, not just the nearest expiry.</summary>
    public Instrument? FindInstrument(string token) => _instruments.FirstOrDefault(i => i.Token == token);

    /// <summary>
    /// For marking an open position and, when exiting, actually filling it -- bid is what
    /// PaperTradeSimulator.FillExit needs (plan 9: exits fill at bid, never LTP, since LTP
    /// flatters results by ignoring the spread). Falls back to LTP only when no depth
    /// snapshot has arrived yet for this instrument.
    /// </summary>
    public bool TryGetLatestQuote(string token, out decimal ltp, out decimal? bid)
    {
        if (_latest.TryGetValue(token, out var state) && state.LastPrice > 0)
        {
            ltp = state.LastPrice;
            bid = state.Depth?.Bid1Price is > 0 ? state.Depth.Bid1Price : null;
            return true;
        }

        ltp = 0;
        bid = null;
        return false;
    }

    const int SyntheticForwardStrikeCount = 5;

    /// <summary>
    /// The Black-Scholes underlying for every IV/Greeks calculation in this class -- see
    /// <see cref="NiftySignal.Pricing.SyntheticForward"/>'s doc comment for why this replaced
    /// raw spot (2026-09-07). Averages the put-call-parity estimate from the
    /// <see cref="SyntheticForwardStrikeCount"/> strikes nearest spot, each with both a call
    /// and put quote -- falls back to spot itself when no strike has both legs quoted yet
    /// (e.g. very early in the session), rather than blocking every downstream calculation.
    /// PENDING (external review, 2026-09-12): this spot-fallback reintroduces the too-low-S bias
    /// on the exact cadences it triggers on, unlike CadencePopulator's equivalent
    /// (ComputeSyntheticUnderlyingForChain), which nulls out instead of falling back -- "never
    /// fabricate, always null when uncertain" is this codebase's own stated convention elsewhere.
    /// Not fixed yet: doing so properly means changing this method's return type to nullable and
    /// reviewing all 7 call sites' null-propagation (basis, GEX, IV/Delta/Gamma/Theta/Vega, and
    /// others) -- a real refactor of live scoring code, not a one-line change, and everything it
    /// currently feeds (GammaExposure/VannaExposure/CharmExposure) is zero-weighted in the live
    /// composite today, so the practical exposure is low. Revisit deliberately, not as part of an
    /// unrelated cleanup pass.
    /// </summary>
    decimal ComputeUnderlyingPrice(decimal spotPrice, double t)
    {
        var pairs = new List<(double Strike, double CallMid, double PutMid)>();

        foreach (var strike in _nearestExpiryOptions.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => Math.Abs(s - spotPrice)).Take(SyntheticForwardStrikeCount))
        {
            var call = _nearestExpiryOptions.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strike);
            var put = _nearestExpiryOptions.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strike);
            if (call is null || put is null
                || !_latest.TryGetValue(call.Token, out var callState) || !_latest.TryGetValue(put.Token, out var putState)
                || MidPrice(callState) is not { } callMid || MidPrice(putState) is not { } putMid)
            {
                continue;
            }

            pairs.Add(((double)strike, (double)callMid, (double)putMid));
        }

        return NiftySignal.Pricing.SyntheticForward.Compute(pairs, t, RiskFreeRate) is { } forward ? (decimal)forward : spotPrice;
    }

    /// <summary>
    /// Live option-chain snapshot for strike selection, nearest-expiry + one side only (the
    /// side the entry direction calls for). Mid/bid/ask come from the top-of-book depth
    /// snapshot (null bid/ask if none arrived yet -- StrikeSelector already treats that as
    /// a filter failure, not a crash). Delta/IV are priced against the synthetic forward, not
    /// raw spot or the tracked future -- see <see cref="ComputeUnderlyingPrice"/>.
    /// </summary>
    public List<Execution.StrikeCandidate> BuildStrikeCandidates(OptionType side, DateTimeOffset now)
    {
        var candidates = new List<Execution.StrikeCandidate>();
        if (!_latest.TryGetValue(_spot.Token, out var spot) || spot.LastPrice <= 0)
        {
            return candidates;
        }

        var t = TimeToExpiry.YearsUntilExpiry(_nearestExpiry, now);
        var underlying = ComputeUnderlyingPrice(spot.LastPrice, t);

        foreach (var instrument in _nearestExpiryOptions.Where(o => o.OptionType == side))
        {
            if (!_latest.TryGetValue(instrument.Token, out var state) || state.LastPrice <= 0)
            {
                continue;
            }

            var depth = state.Depth;
            var bid = depth?.Bid1Price is > 0 ? depth.Bid1Price : (decimal?)null;
            var ask = depth?.Ask1Price is > 0 ? depth.Ask1Price : (decimal?)null;
            var mid = bid is { } b && ask is { } a ? (b + a) / 2 : state.LastPrice;

            double? iv = null, delta = null;
            if (state.LastPrice > 0)
            {
                iv = ImpliedVolatilitySolver.Solve(side, (double)mid, (double)underlying, (double)instrument.StrikePrice!.Value, t, RiskFreeRate);
                if (iv is { } ivValue)
                {
                    delta = BlackScholes.Calculate(side, (double)underlying, (double)instrument.StrikePrice.Value, t, RiskFreeRate, ivValue).Greeks.Delta;
                }
            }

            candidates.Add(new Execution.StrikeCandidate(
                Token: instrument.Token,
                TradingSymbol: instrument.TradingSymbol,
                OptionType: side,
                Mid: mid,
                BidPrice: bid,
                AskPrice: ask,
                OpenInterest: state.OpenInterest ?? 0,
                // Audit finding F19 fixed (2026-09-09): was hardcoded 0, making
                // StrikeSelector.SelectBestCandidate's .ThenByDescending(c => c.Volume) tie-break
                // a no-op (always 0 vs 0). Cumulative day volume-so-far, not a per-cadence delta
                // -- already sitting on `state` with no new tracking needed, and a perfectly
                // legitimate liquidity signal in its own right (this tie-break is about ranking
                // by liquidity, per this class' own doc comment -- volume-to-date answers that
                // as well as a delta would, without the timing complexity of a delta computed
                // after ComputeCadence has already rolled its own "previous volume" baseline
                // forward for the next cadence).
                Volume: state.Volume,
                Delta: delta,
                ImpliedVolatility: iv));
        }

        return candidates;
    }

    /// <summary>
    /// Point-in-time read of the five "noisy instantaneous" metrics, folded into a running
    /// average that <see cref="ComputeCadence"/> consumes and resets. Meant to be called
    /// every few seconds between cadence ticks (see class doc comment) -- a no-op if spot or
    /// future haven't ticked yet, same guard as ComputeCadence itself.
    /// </summary>
    public void Sample(DateTimeOffset now)
    {
        if (!_latest.TryGetValue(_spot.Token, out var spot) || !_latest.TryGetValue(_future.Token, out var future)
            || spot.LastPrice <= 0 || future.LastPrice <= 0)
        {
            return;
        }

        // Mid (bid+ask)/2 rather than raw LTP -- the future's last-traded print was observed
        // alternating between two levels ~3-4 points apart within seconds (live-caught
        // 2026-09-07), which read as a real move to both Basis and Momentum even though
        // nothing was actually trending. Same MidPrice fallback-to-LTP helper options already
        // use, so this degrades to the old behavior if depth isn't available.
        var futureMark = MidPrice(future) ?? future.LastPrice;

        // Basis measured against the real monthly future's own mid, not a synthetic forward
        // (2026-09-09 external review, reverting audit finding F11's original fix): the tracked
        // future genuinely is a *monthly* contract, distinct from the weeklies being traded, so
        // this stays honestly named -- FuturesBasisRaw is futureMid minus spotMid, nothing more.
        // The original F11 fix (2026-09-08) swapped in ComputeUnderlyingPrice (the put-call-
        // parity synthetic forward) on the theory that basis should track the option's own
        // expiry -- but parity S isn't a real traded future and doesn't carry a monthly
        // contract's actual cost-of-carry; that swap produced a value that mostly sits near zero
        // and occasionally spikes on a stale wing-strike quote, not a basis reading. See
        // ParityGapRaw below for that same synthetic-forward-vs-spot value, kept as its own,
        // honestly-named, weight-0 diagnostic instead of overloading FuturesBasisRaw with it.
        // PENDING (audit finding F25, 2026-09-08 lead review -- see fix plan Batch 7): this
        // component may simply have near-zero forward correlation intraday for weeklies (current
        // weight 0.1425, tied for second-largest) even when correctly computed -- validate via
        // forward correlation before touching the weight, don't assume it should drop to 0.
        _basisSamples.Add((double)(futureMark - spot.LastPrice));

        // Parity-gap diagnostic (2026-09-09 review, new alongside the F11 revert above):
        // synthetic forward S via put-call parity (same ComputeUnderlyingPrice already used by
        // GEX/Vanna/Charm/theoretical pricing) minus spot mid. A quote-quality signal -- stale
        // or wide wing-strike quotes show up here -- typically small and near zero on clean
        // data. Deliberately never fed into the composite: no ParityGapZ, no rolling window,
        // persisted purely for inspection (see ScoreSnapshot.ParityGapRaw).
        var basisT = TimeToExpiry.YearsUntilExpiry(_nearestExpiry, now);
        var syntheticForward = ComputeUnderlyingPrice(spot.LastPrice, basisT);
        _parityGapSamples.Add((double)(syntheticForward - spot.LastPrice));

        // Momentum deliberately keeps using the raw tracked future, not the synthetic forward --
        // it measures a *relative* short-window change, where the monthly contract's own slow
        // time-decay drift washes out over a few minutes; F11 is specifically about Basis's
        // absolute-level bias, not Momentum.
        _momentumSamples.Add(ComputeMomentum(now, futureMark));
        _pcrSamples.Add(ComputePcr(spot.LastPrice));
        _depthImbalanceSamples.Add(ComputeDepthImbalance(spot.LastPrice));
        _ivSkewSamples.Add(ComputeIvSkew(spot.LastPrice, now));
        SampleSpreads();

        // Core score DepthImbalance/ItmSkew (Batch 3 fix, 2026-09-13): no longer sampled here --
        // fed per-tick in OnTick instead (see _coreDepthImbalanceByToken's own doc comment for
        // why the old 3s-instant-sample approach was a genuine mismatch from the backtest, not
        // just a coarser approximation of it).
    }

    /// <summary>
    /// One spread reading per tracked option token, folded into that token's running average --
    /// a single wide print right at the cadence boundary would otherwise look identical to a
    /// persistently wide, illiquid market. Every option in the nearest expiry is sampled, not
    /// just the currently persisted ATM band, since the band itself is only decided later in
    /// <see cref="BuildStrikeSnapshots"/> once the exact cadence-time spot price is known.
    /// </summary>
    void SampleSpreads()
    {
        foreach (var option in _nearestExpiryOptions)
        {
            if (!_latest.TryGetValue(option.Token, out var state))
            {
                continue;
            }

            var depth = state.Depth;
            var bid = depth?.Bid1Price is > 0 ? depth.Bid1Price : (decimal?)null;
            var ask = depth?.Ask1Price is > 0 ? depth.Ask1Price : (decimal?)null;
            if (bid is not { } b || ask is not { } a)
            {
                continue;
            }

            var spreadAbs = a - b;
            if (!_spreadAbsSamplesByToken.TryGetValue(option.Token, out var absSamples))
            {
                absSamples = new RunningAverage();
                _spreadAbsSamplesByToken[option.Token] = absSamples;
            }
            absSamples.Add((double)spreadAbs);

            var mid = (a + b) / 2;
            if (mid > 0)
            {
                if (!_spreadPctOfMidSamplesByToken.TryGetValue(option.Token, out var pctSamples))
                {
                    pctSamples = new RunningAverage();
                    _spreadPctOfMidSamplesByToken[option.Token] = pctSamples;
                }
                pctSamples.Add((double)(spreadAbs / mid * 100m));
            }
        }
    }

    /// <summary>
    /// Put-spread/call-spread ratio (2026-09-07, diagnostic-only -- see ScoreWeights.Default's
    /// SpreadRatio weight), across the *entire* nearest-expiry chain -- reuses
    /// <see cref="_spreadPctOfMidSamplesByToken"/>, which <see cref="SampleSpreads"/> already
    /// fills for every tracked option token every ~3s, not just the persisted band. Averages
    /// each side's per-token spread-percent-of-mid, then divides -- ratio, not difference: a
    /// same-day check found the ratio construction correlated more strongly and more
    /// consistently across horizons (+0.10/+0.15/+0.32 at 1/5/15min) than a plain put-minus-call
    /// difference. Must run before <see cref="BuildStrikeSnapshots"/> clears these dictionaries
    /// for the next cadence -- true today because MarketDataIngestionWorker always calls
    /// ComputeCadence first, but this method has no way to enforce that itself.
    /// </summary>
    double? ComputeSpreadRatio()
    {
        double callTotal = 0, putTotal = 0;
        int callCount = 0, putCount = 0;

        foreach (var option in _nearestExpiryOptions)
        {
            if (!_spreadPctOfMidSamplesByToken.TryGetValue(option.Token, out var samples) || samples.Average is not { } avg)
            {
                continue;
            }

            if (option.OptionType == OptionType.Call)
            {
                callTotal += avg;
                callCount++;
            }
            else if (option.OptionType == OptionType.Put)
            {
                putTotal += avg;
                putCount++;
            }
        }

        if (callCount == 0 || putCount == 0)
        {
            return null;
        }

        var callMean = callTotal / callCount;
        var putMean = putTotal / putCount;
        return callMean > 0 ? putMean / callMean : null;
    }

    /// <summary>Null until the spot and future have at least one tick each.</summary>
    public ScoreSnapshot? ComputeCadence(DateTimeOffset now)
    {
        if (!_latest.TryGetValue(_spot.Token, out var spot) || !_latest.TryGetValue(_future.Token, out var future)
            || spot.LastPrice <= 0 || future.LastPrice <= 0)
        {
            LastCoreScoreSnapshot = null;
            return null;
        }

        // One final sample right up to the cadence boundary, then consume the average of
        // everything collected since the last cadence tick. Basis/momentum are guaranteed
        // at least this one sample (spot/future are already confirmed present above, and
        // Sample() uses the same _latest data synchronously) -- Average is never null for
        // those two. Pcr/DepthImbalance/IvSkew can still legitimately be null (e.g. no depth
        // snapshot has arrived yet), same as before this change.
        Sample(now);

        var basisRaw = _basisSamples.Average!.Value;
        // Audit finding F46 (2026-09-10): z-score computed against the window BEFORE this
        // observation is added to it, not after -- the current value must never contaminate
        // the mean/stddev it's then compared against. Same capture-before-Add discipline at
        // every _xWindow site in this method from here on.
        var futuresBasisZ = _basisWindow.ComputeZScore(basisRaw);
        _basisWindow.Add(now, basisRaw);

        // Same non-null guarantee as basisRaw above -- computed unconditionally alongside it
        // in Sample(). No window/Z: weight-0 diagnostic, see ScoreSnapshot.ParityGapRaw.
        var parityGapRaw = _parityGapSamples.Average!.Value;

        var momentumRaw = _momentumSamples.Average!.Value;
        var priceMomentumZ = _momentumWindow.ComputeZScore(momentumRaw);
        _momentumWindow.Add(now, momentumRaw);

        var pcrRaw = _pcrSamples.Average;
        double? pcrZ = null;
        if (pcrRaw is { } pcr)
        {
            pcrZ = _pcrWindow.ComputeZScore(pcr);
            _pcrWindow.Add(now, pcr);
        }

        // Not smoothed like the other five -- see class doc comment. Compares state at this
        // cadence tick to state at the last one. Null (not a fabricated delta) when the "last
        // cadence" is stale by more than one normal interval -- see ComputeOiBuildupNet.
        var oiBuildupRaw = ComputeOiBuildupNet(now, spot.LastPrice);
        double? oiBuildupNetZ = null;
        if (oiBuildupRaw is { } oiBuildup)
        {
            oiBuildupNetZ = _oiBuildupWindow.ComputeZScore(oiBuildup);
            _oiBuildupWindow.Add(now, oiBuildup);
        }

        var depthImbalanceRaw = _depthImbalanceSamples.Average;
        double? depthImbalanceZ = null;
        if (depthImbalanceRaw is { } di)
        {
            depthImbalanceZ = _depthImbalanceWindow.ComputeZScore(di);
            _depthImbalanceWindow.Add(now, di);
        }

        var ivSkewRaw = _ivSkewSamples.Average;
        double? ivSkewZ = null;
        if (ivSkewRaw is { } skew)
        {
            ivSkewZ = _ivSkewWindow.ComputeZScore(skew);
            _ivSkewWindow.Add(now, skew);
        }

        // Core score DepthImbalance/ItmSkew (Batch 3 fix, 2026-09-13): no longer captured here --
        // _coreDepthImbalanceByToken/_coreCadenceMidPriceByToken are untouched by ResetSamples()
        // below (that call is too early; see those fields' own doc comments), so the Core-score
        // combine block reads them directly, then resets them itself once it's done.

        // Reads _spreadPctOfMidSamplesByToken, which BuildStrikeSnapshots clears later this
        // same cadence -- must run before that, which ResetSamples() below doesn't touch
        // anyway (see SampleSpreads' own doc comment on why it's independent of ResetSamples).
        var spreadRatioRaw = ComputeSpreadRatio();
        double? spreadRatioZ = null;
        if (spreadRatioRaw is { } sr)
        {
            spreadRatioZ = _spreadRatioWindow.ComputeZScore(sr);
            _spreadRatioWindow.Add(now, sr);
        }

        ResetSamples();

        // Not smoothed like the other five -- see class doc comment.
        var vixChangeRaw = ComputeVixChange(now);
        double? vixChangeZ = null;
        if (vixChangeRaw is { } vc)
        {
            vixChangeZ = _vixWindow.ComputeZScore(vc);
            _vixWindow.Add(now, vc);
        }

        // Not smoothed either -- same reasoning as VixChange (a full-chain aggregate, not a
        // point-in-time price/quote read that benefits from within-cadence averaging).
        double? gammaExposureRaw = null, vannaExposureRaw = null, charmExposureRaw = null, gammaFlipLevel = null, straddleRichnessRaw = null, ivRankRaw = null, atmIvRaw = null;
        if (_nearestExpiryOptions.Count > 0)
        {
            var gexStrikesByDistance = _nearestExpiryOptions.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => Math.Abs(s - spot.LastPrice)).ToList();
            var gexAtmStrike = gexStrikesByDistance[0];
            var gexT = TimeToExpiry.YearsUntilExpiry(_nearestExpiry, now);
            var gexUnderlying = ComputeUnderlyingPrice(spot.LastPrice, gexT);
            var gexAtmVol = SolveAtmReferenceVol(gexAtmStrike, gexUnderlying, gexT);
            gammaExposureRaw = ComputeGammaExposure(gexUnderlying, gexAtmVol, gexT);

            // Same reference vol/underlying/t as GEX above -- one ATM solve serving all three
            // dealer-hedging Greeks, not three separate (and possibly inconsistent) solves.
            vannaExposureRaw = ComputeVannaExposure(gexUnderlying, gexAtmVol, gexT);
            charmExposureRaw = ComputeCharmExposure(gexUnderlying, gexAtmVol, gexT);
            gammaFlipLevel = ComputeGammaFlipLevel(gexAtmVol, gexT);
            straddleRichnessRaw = ComputeStraddleRichness(gexUnderlying, gexAtmVol, gexT, now);
            atmIvRaw = gexAtmVol;
            ivRankRaw = ComputeIvRank(gexAtmVol, now);
        }

        double? gammaExposureZ = null;
        if (gammaExposureRaw is { } gex)
        {
            gammaExposureZ = _gammaExposureWindow.ComputeZScore(gex);
            _gammaExposureWindow.Add(now, gex);
        }

        double? vannaExposureZ = null;
        if (vannaExposureRaw is { } vanna)
        {
            vannaExposureZ = _vannaExposureWindow.ComputeZScore(vanna);
            _vannaExposureWindow.Add(now, vanna);
        }

        double? charmExposureZ = null;
        if (charmExposureRaw is { } charm)
        {
            charmExposureZ = _charmExposureWindow.ComputeZScore(charm);
            _charmExposureWindow.Add(now, charm);
        }

        // Not smoothed either -- see ComputeVolumePcrAndCvdProxy's own doc comment.
        var (volumePcrRaw, cvdProxyRaw) = ComputeVolumePcrAndCvdProxy();
        double? volumePcrZ = null;
        if (volumePcrRaw is { } vpcr)
        {
            volumePcrZ = _volumePcrWindow.ComputeZScore(vpcr);
            _volumePcrWindow.Add(now, vpcr);
        }

        double? cvdProxyZ = null;
        if (cvdProxyRaw is { } cvd)
        {
            cvdProxyZ = _cvdProxyWindow.ComputeZScore(cvd);
            _cvdProxyWindow.Add(now, cvd);
        }

        double? straddleRichnessZ = null;
        if (straddleRichnessRaw is { } richness)
        {
            straddleRichnessZ = _straddleRichnessWindow.ComputeZScore(richness);
            _straddleRichnessWindow.Add(now, richness);
        }

        var inputs = new ScoreComponentInputs(
            OiBuildupNetZ: oiBuildupNetZ,
            PcrZ: pcrZ,
            FuturesBasisZ: futuresBasisZ,
            IvSkewZ: ivSkewZ,
            PriceMomentumZ: priceMomentumZ,
            DepthImbalanceZ: depthImbalanceZ,
            VixChangeZ: vixChangeZ,
            GammaExposureZ: gammaExposureZ,
            VolumePcrZ: volumePcrZ,
            SpreadRatioZ: spreadRatioZ,
            VannaExposureZ: vannaExposureZ,
            CharmExposureZ: charmExposureZ,
            CvdProxyZ: cvdProxyZ,
            StraddleRichnessZ: straddleRichnessZ);

        // Multi-cadence smoothing (2026-09-07): a composite recomputed from scratch every 15s
        // with no memory of its own recent behavior was too noisy to sustain past the entry
        // rules' hold-above-threshold window, even when the underlying direction was genuinely
        // right (live-caught: a real, sustained ~120-point down move never produced a trade
        // because single-cadence spikes kept resetting the sustain timer). Average the last
        // few cadences' raw values -- a plain FIFO count, not a strict time window, so a
        // cadence delayed by a reconnect just means "average of the last few genuine readings"
        // rather than a gap in the window. Skipped (not backfilled with a fabricated value)
        // whenever this cadence's own raw is null -- e.g. the one cadence right after a
        // reconnect where OiBuildupNet has no reliable baseline (see ComputeOiBuildupNet) --
        // Calculate's own warm-up check (from `inputs`, not this) already suppresses the score
        // for that cadence regardless, so there is nothing meaningful to add.
        // Captured once, not read twice via the Weights property -- both calls below must see
        // the exact same weight set even if a hot reload lands mid-cadence on another thread.
        var weights = Weights;
        var compositeRawInstant = CompositeScoreCalculator.ComputeRaw(inputs, weights);
        double? compositeRawSmoothed = null;
        if (compositeRawInstant is { } instantRaw)
        {
            _compositeRawHistory.Enqueue(instantRaw);
            while (_compositeRawHistory.Count > CompositeSmoothingCadences)
            {
                _compositeRawHistory.Dequeue();
            }

            compositeRawSmoothed = _compositeRawHistory.Average();
        }

        // Fixed k (audit finding F1, 2026-09-08) -- see class doc comment for why the dynamic,
        // self-normalizing version this replaced was actually driving the saturation it was
        // meant to prevent.
        var composite = CompositeScoreCalculator.Calculate(
            inputs, weights, now, CompositeScoreCalculator.DefaultK, compositeRawSmoothed);

        // Audit finding F55's price-led dynamic-hybrid mode (2026-09-11): own try/catch, same
        // "one bad thing must not take down everything else" discipline as audit finding F34 --
        // a bug here must never prevent _previousCadence/_lastCadenceAt below from updating.
        double? futuresVwap = null, futuresVwapDeviationRaw = null, futuresVwapDeviationZ = null;
        try
        {
            if (_latest.TryGetValue(_future.Token, out var futureState) && _futureCumulativeVolume > 0)
            {
                var vwap = _futureCumulativePriceVolume / _futureCumulativeVolume;
                var deviation = (double)futureState.LastPrice - vwap;

                // Compute the z-score BEFORE adding this cadence's own deviation to the window --
                // the exact same self-inclusion-safe ordering audit finding F46 already fixed for
                // every other z-scored component, applied here so this new metric doesn't
                // reintroduce that bug class.
                futuresVwapDeviationZ = _futuresVwapDeviationWindow.ComputeZScore(deviation);
                _futuresVwapDeviationWindow.Add(now, deviation);
                futuresVwap = vwap;
                futuresVwapDeviationRaw = deviation;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Futures VWAP deviation combine step failed");
        }

        // ==================== Core score (2026-09-13, live-wiring plan Batch 2) ====================
        // Own try/catch, same F34 discipline as every other combine block above -- a bug here must
        // never prevent _previousCadence/_lastCadenceAt below from updating. Must run BEFORE
        // _previousCadence is overwritten below -- ComputeCoreOiChangeDiffRaw reads it.
        double? coreDepthImbalanceRaw = null, coreItmSkewRaw = null, coreFutureCvdRaw = null,
            coreNotionalVolumeRatioRaw = null, coreGammaExposureRaw = null, coreTrendReversion15mRaw = null,
            coreBasisChangeRaw = null, coreOiChangeDiff15mRaw = null;
        double? coreDepthImbalanceSigned = null, coreItmSkewSigned = null, coreFutureCvdSigned = null,
            coreNotionalVolumeRatioSigned = null, coreGammaExposureSigned = null, coreTrendReversion15mSigned = null,
            coreBasisChangeSigned = null, coreOiChangeDiff15mSigned = null;
        double? coreScoreRawInstant = null, coreScoreRaw = null, coreScore = null, coreScoreFast = null, coreScoreSlow = null;
        var coreIsWarmedUp = false;
        string? coreWeightSetVersion = null;
        try
        {
            var coreStrikeOffsets = ComputeStrikeOffsets(spot.LastPrice);
            coreDepthImbalanceRaw = ComputeCoreDepthImbalanceRaw(coreStrikeOffsets);
            coreItmSkewRaw = ComputeCoreItmSkewRaw(coreStrikeOffsets, spot.LastPrice, now);

            coreFutureCvdRaw = ComputeCoreFutureCvdNet5Min(now);

            coreNotionalVolumeRatioRaw = ComputeCoreNotionalVolumeRatioRaw(coreStrikeOffsets);

            var coreT = TimeToExpiry.YearsUntilExpiry(_nearestExpiry, now);
            var coreUnderlying = ComputeUnderlyingPrice(spot.LastPrice, coreT);
            coreGammaExposureRaw = ComputeCoreGammaExposureRaw(coreStrikeOffsets, coreUnderlying, coreT);

            var (trendReversion, basisChange) = ComputeCoreTrendAndBasis(now);
            coreTrendReversion15mRaw = trendReversion;
            coreBasisChangeRaw = basisChange;

            coreOiChangeDiff15mRaw = ComputeCoreOiChangeDiffRaw(now, coreStrikeOffsets);

            coreDepthImbalanceSigned = RankSigned(coreDepthImbalanceRaw, _coreDepthImbalanceRank);
            coreItmSkewSigned = RankSigned(coreItmSkewRaw, _coreItmSkewRank);
            coreFutureCvdSigned = RankSigned(coreFutureCvdRaw, _coreFutureCvdRank);
            coreNotionalVolumeRatioSigned = RankSigned(coreNotionalVolumeRatioRaw, _coreNotionalVolumeRatioRank);
            coreGammaExposureSigned = RankSigned(coreGammaExposureRaw, _coreGammaExposureRank);
            // Negate-then-rank, not rank-then-negate -- matches CoreScoreOptionSimulator.cs:370 exactly.
            coreBasisChangeSigned = RankSigned(coreBasisChangeRaw is { } b ? -b : null, _coreBasisChangeRank);
            coreOiChangeDiff15mSigned = RankSigned(coreOiChangeDiff15mRaw, _coreOiChangeDiffRank);
            // NOT ranked -- already bounded [-1,1] by construction. See ComputeCoreTrendAndBasis.
            coreTrendReversion15mSigned = coreTrendReversion15mRaw;

            var coreInputs = new CoreScoreComponentInputs(
                DepthImbalance: coreDepthImbalanceSigned,
                ItmSkew: coreItmSkewSigned,
                FutureCvdNet5Min: coreFutureCvdSigned,
                NotionalVolumeRatio: coreNotionalVolumeRatioSigned,
                GammaExposure: coreGammaExposureSigned,
                TrendReversion15m: coreTrendReversion15mSigned,
                BasisChange: coreBasisChangeSigned,
                OiChangeDiff15m: coreOiChangeDiff15mSigned);

            coreScoreRawInstant = CoreScoreCalculator.ComputeRaw(coreInputs, CoreScoreWeights.Default);
            coreScoreRaw = coreScoreRawInstant;

            var coreResult = CoreScoreCalculator.Calculate(coreInputs, CoreScoreWeights.Default, now, CoreScoreCalculator.DefaultK, coreScoreRaw);
            coreScore = coreResult.Score;
            coreIsWarmedUp = coreResult.IsWarmedUp;
            if (coreResult.IsWarmedUp)
            {
                coreWeightSetVersion = coreResult.WeightSetVersion;
            }

            // Crossover's fast/slow smoothing (plan A5) -- plain post-tanh moving averages of the
            // already-computed CoreScore, NOT routed back through CoreScoreCalculator (see
            // CoreScoreCalculator's own rawOverride doc comment for why).
            if (coreScore is { } scoreValue)
            {
                _coreScoreFastHistory.Enqueue((now, scoreValue));
                _coreScoreSlowHistory.Enqueue((now, scoreValue));
            }

            while (_coreScoreFastHistory.Count > 0 && now - _coreScoreFastHistory.Peek().Timestamp > CoreScoreFastWindow)
            {
                _coreScoreFastHistory.Dequeue();
            }

            while (_coreScoreSlowHistory.Count > 0 && now - _coreScoreSlowHistory.Peek().Timestamp > CoreScoreSlowWindow)
            {
                _coreScoreSlowHistory.Dequeue();
            }

            coreScoreFast = _coreScoreFastHistory.Count > 0 ? _coreScoreFastHistory.Average(w => w.Score) : null;
            coreScoreSlow = _coreScoreSlowHistory.Count > 0 ? _coreScoreSlowHistory.Average(w => w.Score) : null;

            LastCoreScoreSnapshot = new CoreScoreSnapshot
            {
                ComputedAt = now,
                DepthImbalanceRaw = coreDepthImbalanceRaw,
                ItmSkewRaw = coreItmSkewRaw,
                FutureCvdNet5MinRaw = coreFutureCvdRaw,
                NotionalVolumeRatioRaw = coreNotionalVolumeRatioRaw,
                GammaExposureRaw = coreGammaExposureRaw,
                TrendReversion15mRaw = coreTrendReversion15mRaw,
                BasisChangeRaw = coreBasisChangeRaw,
                OiChangeDiff15mRaw = coreOiChangeDiff15mRaw,
                DepthImbalanceSigned = coreDepthImbalanceSigned,
                ItmSkewSigned = coreItmSkewSigned,
                FutureCvdNet5MinSigned = coreFutureCvdSigned,
                NotionalVolumeRatioSigned = coreNotionalVolumeRatioSigned,
                GammaExposureSigned = coreGammaExposureSigned,
                TrendReversion15mSigned = coreTrendReversion15mSigned,
                BasisChangeSigned = coreBasisChangeSigned,
                OiChangeDiff15mSigned = coreOiChangeDiff15mSigned,
                CoreScoreRawInstant = coreScoreRawInstant,
                CoreScoreRaw = coreScoreRaw,
                CoreScore = coreScore,
                IsWarmedUp = coreIsWarmedUp,
                WeightSetVersion = coreWeightSetVersion,
                CoreScoreFast = coreScoreFast,
                CoreScoreSlow = coreScoreSlow,
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Core score combine step failed");
            LastCoreScoreSnapshot = null;
        }

        // Reset for the NEXT cadence, unconditionally (outside the try/catch above) -- same
        // "reset even if the combine step threw" requirement ResetSamples() itself satisfies by
        // being called standalone, not inside a try. Run only after the combine block has had its
        // chance to read this cadence's data (see _coreDepthImbalanceByToken's own doc comment for
        // why this can't live inside ResetSamples(), which runs too early).
        foreach (var accumulator in _coreDepthImbalanceByToken.Values)
        {
            accumulator.Reset();
        }

        foreach (var token in _coreCadenceMidPriceByToken.Keys.ToList())
        {
            _coreCadenceMidPriceByToken[token] = null;
        }

        _coreFutureCadenceOpen = null;
        _coreFutureCadenceClose = null;
        _coreSpotCadenceOpen = null;
        _coreSpotCadenceClose = null;
        // ================== end Core score ==================

        // Audit finding F50 (2026-09-10): records this cadence's OI for the lookback window
        // ComputeOiBuildupNet/ComputeRatioSizedOiFlowRaw both read from above -- after every
        // read this cadence, same before/after ordering _previousCadence itself follows, so
        // both methods see state strictly before this cadence's own recording.
        foreach (var opt in _nearestExpiryOptions)
        {
            if (_latest.TryGetValue(opt.Token, out var optState))
            {
                _oiLookback.Record(opt.Token, now, optState.OpenInterest ?? 0);
            }
        }

        _previousCadence = new Dictionary<string, InstrumentState>(_latest);
        _lastCadenceAt = now;

        return new ScoreSnapshot
        {
            ComputedAt = now,
            OiBuildupNetRaw = oiBuildupRaw,
            PcrRaw = pcrRaw,
            FuturesBasisRaw = basisRaw,
            ParityGapRaw = parityGapRaw,
            IvSkewOneSigmaRaw = ivSkewRaw,
            PriceMomentumRaw = momentumRaw,
            DepthImbalanceRaw = depthImbalanceRaw,
            VixChangeRaw = vixChangeRaw,
            GammaExposureRaw = gammaExposureRaw,
            VolumePcrRaw = volumePcrRaw,
            SpreadRatioRaw = spreadRatioRaw,
            VannaExposureRaw = vannaExposureRaw,
            CharmExposureRaw = charmExposureRaw,
            CvdProxyRaw = cvdProxyRaw,
            StraddleRichnessRaw = straddleRichnessRaw,
            GammaFlipLevel = gammaFlipLevel,
            IvRankRaw = ivRankRaw,
            AtmIv = atmIvRaw,
            IvRankSessionCount = IvRankSessionCount,
            OiBuildupNetZ = inputs.OiBuildupNetZ,
            PcrZ = inputs.PcrZ,
            FuturesBasisZ = inputs.FuturesBasisZ,
            IvSkewZ = inputs.IvSkewZ,
            PriceMomentumZ = inputs.PriceMomentumZ,
            DepthImbalanceZ = inputs.DepthImbalanceZ,
            VixChangeZ = inputs.VixChangeZ,
            GammaExposureZ = inputs.GammaExposureZ,
            VolumePcrZ = inputs.VolumePcrZ,
            SpreadRatioZ = inputs.SpreadRatioZ,
            VannaExposureZ = inputs.VannaExposureZ,
            CharmExposureZ = inputs.CharmExposureZ,
            CvdProxyZ = inputs.CvdProxyZ,
            StraddleRichnessZ = inputs.StraddleRichnessZ,
            CompositeScoreRaw = compositeRawSmoothed,
            CompositeScoreRawInstant = compositeRawInstant,
            CompositeScore = composite.Score,
            SpotPrice = (double)spot.LastPrice,
            IsWarmedUp = composite.IsWarmedUp,
            WeightSetVersion = composite.WeightSetVersion,
            // Ratio* properties deliberately omitted (Batch 4, 2026-09-13) -- the ratio composite
            // is retired; these columns stay in the schema (permanently null/default from here on)
            // rather than a destructive migration dropping them. See docs/replication_plan.md's
            // "ratio composite retired entirely" decision.
            FuturesVwap = futuresVwap,
            FuturesVwapDeviationRaw = futuresVwapDeviationRaw,
            FuturesVwapDeviationZ = futuresVwapDeviationZ,
        };
    }

    /// <summary>
    /// Per-strike analytics for the persisted band (2026-09-05) -- volume traded this cadence,
    /// top-of-book spread, IV and the full Greeks set. Pure accumulation for later analysis;
    /// nothing downstream consumes these yet.
    ///
    /// Independent of <see cref="ComputeCadence"/> in both directions: it keeps its own volume
    /// baseline (<see cref="_previousVolumeByToken"/>) rather than sharing
    /// <c>_previousCadence</c>, so the worker can call the two in either order without one
    /// silently zeroing the other's deltas.
    ///
    /// Empty (not null) whenever spot hasn't ticked yet -- same "no data, no output" tolerance
    /// as everything else here.
    /// </summary>
    public List<StrikeSnapshot> BuildStrikeSnapshots(DateTimeOffset now)
    {
        var snapshots = new List<StrikeSnapshot>();
        if (!_latest.TryGetValue(_spot.Token, out var spot) || spot.LastPrice <= 0)
        {
            return snapshots;
        }

        // Nearest (2*band + 1) distinct strikes to spot. Taking by absolute distance rather
        // than computing an ATM strike and stepping outward means an off-centre spot (sitting
        // between two strikes) still yields the genuinely closest set, which is what "near the
        // money" should mean.
        var strikesByDistance = _nearestExpiryOptions
            .Select(o => o.StrikePrice!.Value)
            .Distinct()
            .OrderBy(s => Math.Abs(s - spot.LastPrice))
            .ToList();
        var atmStrike = strikesByDistance[0];
        var bandStrikes = strikesByDistance.Take((PersistedStrikeBand * 2) + 1).ToHashSet();

        var t = TimeToExpiry.YearsUntilExpiry(_nearestExpiry, now);
        var underlying = ComputeUnderlyingPrice(spot.LastPrice, t);

        // One reference vol per cadence, solved at the ATM strike -- the most liquid, most
        // trustworthy point on the chain. Pricing every strike at the ATM strike's vol (rather
        // than each strike's own solved IV, which would just reproduce its own price and be
        // circular) makes the gap meaningful: the skew premium in rupees. Same methodology as
        // the dashboard's live theoretical-price display (LiveDataService.BuildAtmReferenceVol),
        // ported here so it gets persisted for analysis rather than only existing on screen.
        var atmReferenceVol = SolveAtmReferenceVol(atmStrike, underlying, t);

        foreach (var option in _nearestExpiryOptions.Where(o => bandStrikes.Contains(o.StrikePrice!.Value)))
        {
            if (!_latest.TryGetValue(option.Token, out var state))
            {
                continue;
            }

            long? volumeDelta = null;
            if (_previousVolumeByToken.TryGetValue(option.Token, out var previousVolume))
            {
                // Guard against a feed reset (cumulative counter going backwards) producing a
                // negative "traded volume", which would be nonsense rather than merely wrong.
                volumeDelta = Math.Max(0, state.Volume - previousVolume);
            }

            _previousVolumeByToken[option.Token] = state.Volume;

            long? oiDelta = null;
            if (state.OpenInterest is { } currentOi)
            {
                if (_previousOpenInterestByToken.TryGetValue(option.Token, out var previousOi))
                {
                    oiDelta = currentOi - previousOi;
                }

                _previousOpenInterestByToken[option.Token] = currentOi;
            }

            // Bid/Ask themselves stay instantaneous -- "what could I trade at right now" is
            // inherently a point-in-time fact. Spread is different: it's averaged over the
            // cadence (fed by SampleSpreads every ~3s, same treatment as the five score
            // metrics), because it's used to judge how *consistently* liquid a strike is, and a
            // single wide print at the cadence boundary shouldn't read the same as a
            // persistently wide market.
            var depth = state.Depth;
            var bid = depth?.Bid1Price is > 0 ? depth.Bid1Price : (decimal?)null;
            var ask = depth?.Ask1Price is > 0 ? depth.Ask1Price : (decimal?)null;

            decimal? spreadAbs = _spreadAbsSamplesByToken.TryGetValue(option.Token, out var spreadAbsSamples) && spreadAbsSamples.Average is { } avgSpreadAbs
                ? (decimal)avgSpreadAbs
                : null;
            decimal? spreadPctOfMid = _spreadPctOfMidSamplesByToken.TryGetValue(option.Token, out var spreadPctSamples) && spreadPctSamples.Average is { } avgSpreadPct
                ? (decimal)avgSpreadPct
                : null;

            // Priced against the synthetic forward, not raw spot or the tracked (monthly)
            // future -- see ComputeUnderlyingPrice's doc comment for why. Greeks are only
            // meaningful if the IV solve succeeded; a failed solve leaves all six null rather
            // than seeding an arbitrary volatility.
            var mark = MidPrice(state);

            decimal? markPriceDelta = null;
            if (mark is { } currentMark)
            {
                if (_previousMarkPriceByToken.TryGetValue(option.Token, out var previousMark))
                {
                    markPriceDelta = currentMark - previousMark;
                }

                _previousMarkPriceByToken[option.Token] = currentMark;
            }

            // Reuses OiBuildupNet's own classifier (plan section 5.2) rather than a second copy
            // of the quadrant table -- null, not Neutral, when either delta has no prior cadence
            // to compare against yet.
            var oiBuildup = markPriceDelta is { } priceDeltaForClassification && oiDelta is { } oiDeltaForClassification
                ? MapOiBuildupQuadrant(OiBuildupClassifier.Classify(priceDeltaForClassification, oiDeltaForClassification))
                : (Domain.Enums.OiBuildupQuadrant?)null;

            double? iv = null, delta = null, gamma = null, thetaPerDay = null, vega = null, rho = null;
            if (mark is { } markPrice && markPrice > 0)
            {
                iv = ImpliedVolatilitySolver.Solve(
                    option.OptionType, (double)markPrice, (double)underlying, (double)option.StrikePrice!.Value, t, RiskFreeRate);

                if (iv is { } ivValue)
                {
                    var greeks = BlackScholes.Calculate(
                        option.OptionType, (double)underlying, (double)option.StrikePrice!.Value, t, RiskFreeRate, ivValue).Greeks;
                    delta = greeks.Delta;
                    gamma = greeks.Gamma;
                    thetaPerDay = greeks.ThetaPerDay;
                    vega = greeks.Vega;
                    rho = greeks.Rho;
                }
            }

            // Independent of this strike's own mark: an illiquid wing with no live quote right
            // now should still show what it *should* cost against the reference vol, not just
            // strikes that happen to have a two-sided quote this cadence.
            double? theoreticalPrice = atmReferenceVol is { } refVol
                ? BlackScholes.Calculate(option.OptionType, (double)underlying, (double)option.StrikePrice!.Value, t, RiskFreeRate, refVol).Price
                : null;

            double? priceVsTheoretical = mark is { } markForDiff && theoreticalPrice is { } theo
                ? (double)markForDiff - theo
                : null;

            snapshots.Add(new StrikeSnapshot
            {
                ComputedAt = now,
                Token = option.Token,
                StrikePrice = option.StrikePrice!.Value,
                OptionType = option.OptionType,
                ExpiryDate = _nearestExpiry,
                VolumeDelta = volumeDelta,
                OpenInterest = state.OpenInterest,
                OpenInterestDelta = oiDelta,
                MarkPriceDelta = markPriceDelta,
                OiBuildup = oiBuildup,
                BidPrice = bid,
                AskPrice = ask,
                SpreadAbs = spreadAbs,
                SpreadPctOfMid = spreadPctOfMid,
                ImpliedVolatility = iv,
                Delta = delta,
                Gamma = gamma,
                ThetaPerDay = thetaPerDay,
                Vega = vega,
                Rho = rho,
                MarkPrice = mark,
                TheoreticalPrice = theoreticalPrice,
                PriceVsTheoretical = priceVsTheoretical,
            });
        }

        // Independent of ResetSamples() -- resets every tracked token's spread accumulator
        // (not just this cadence's persisted band), so a strike that enters the band later
        // starts averaging fresh rather than inheriting stale samples from before it mattered.
        _spreadAbsSamplesByToken.Clear();
        _spreadPctOfMidSamplesByToken.Clear();

        return snapshots;
    }

    /// <summary>
    /// Features' classification and Domain's <see cref="Domain.Enums.OiBuildupQuadrant"/> are
    /// structurally identical but can't be the same type (see that enum's own doc comment for
    /// why) -- this is the single place they're kept in lockstep.
    /// </summary>
    static Domain.Enums.OiBuildupQuadrant MapOiBuildupQuadrant(Features.OiBuildupClassification classification) => classification switch
    {
        Features.OiBuildupClassification.LongBuildup => Domain.Enums.OiBuildupQuadrant.LongBuildup,
        Features.OiBuildupClassification.ShortBuildup => Domain.Enums.OiBuildupQuadrant.ShortBuildup,
        Features.OiBuildupClassification.LongUnwinding => Domain.Enums.OiBuildupQuadrant.LongUnwinding,
        Features.OiBuildupClassification.ShortCovering => Domain.Enums.OiBuildupQuadrant.ShortCovering,
        _ => Domain.Enums.OiBuildupQuadrant.Neutral,
    };

    void ResetSamples()
    {
        _basisSamples.Reset();
        _parityGapSamples.Reset();
        _momentumSamples.Reset();
        _pcrSamples.Reset();
        _depthImbalanceSamples.Reset();
        _ivSkewSamples.Reset();
        // _coreDepthImbalanceByToken/_coreCadenceMidPriceByToken deliberately NOT reset here --
        // this runs too early (before the Core-score combine block reads them); see their own
        // doc comment. Reset happens in ComputeCadence itself, right after that block.
    }

    double ComputeMomentum(DateTimeOffset now, decimal futuresPrice)
    {
        _momentumLookback.Enqueue((now, futuresPrice));
        while (_momentumLookback.Count > 1 && now - _momentumLookback.Peek().At > FeatureWindowLengths.PriceMomentum)
        {
            _momentumLookback.Dequeue();
        }

        // Audit finding F26 resolved (2026-09-09, external review, folded into F4): this raw
        // value's backward r=+0.48 / forward r=-0.08 (documented on ScoreWeights.Default) argued
        // for going the rest of the way to weight 0 rather than the 2026-09-07 partial cut
        // (0.1425 -> 0.07) -- see ScoreWeights.Default.PriceMomentum's own doc comment for the
        // 0.07 -> 0.0 change. Still computed and persisted (PriceMomentumRaw/Z) even at weight 0
        // -- a required composite component and a visible diagnostic series, just with no say
        // in the score.
        return (double)(futuresPrice - _momentumLookback.Peek().FuturesPrice);
    }

    /// <summary>
    /// Null when the day's universe doesn't track VIX, or it hasn't ticked yet -- same
    /// optional-by-design tolerance as Pcr/DepthImbalance/IvSkew's own null cases.
    ///
    /// PENDING (audit finding F27, 2026-09-08 lead review -- see fix plan Batch 7): this reads a
    /// 30-minute lookback (then z-scored against a 2h window); the lead proposes a session-open
    /// baseline instead (-(VIX - VIX_open)), same class of fix as F23's PCR baseline. Weight is
    /// small (0.05) so lower risk than F23/F24, but same validation method applies before changing it.
    /// </summary>
    double? ComputeVixChange(DateTimeOffset now)
    {
        if (_vix is null || !_latest.TryGetValue(_vix.Token, out var vix) || vix.LastPrice <= 0)
        {
            return null;
        }

        _vixLookback.Enqueue((now, vix.LastPrice));
        while (_vixLookback.Count > 1 && now - _vixLookback.Peek().At > FeatureWindowLengths.VixChange)
        {
            _vixLookback.Dequeue();
        }

        // Negated -- see class doc comment: VIX-vs-Nifty is the standard inverse "fear
        // gauge" relationship, so a rising VIX must contribute negatively (bearish) to stay
        // consistent with the other six components' "positive raw = bullish" convention.
        return -(double)(vix.LastPrice - _vixLookback.Peek().Vix);
    }

    /// <summary>
    /// Restricted to the ATM +/- PersistedStrikeBand strikes, not the full chain (2026-09-08,
    /// audit finding F5) -- the full chain was dominated by enormous open interest sitting on
    /// far strikes that has nothing to do with today's positioning, and refreshes at OI-update
    /// frequency (not the 15s cadence clock), so summing dozens of strikes together mostly just
    /// added stale noise rather than signal. Same band selection as BuildStrikeSnapshots.
    ///
    /// PENDING (audit finding F23, 2026-09-08 lead review -- see fix plan Batch 7): the raw value
    /// here feeds a 30-minute rolling z-score with a *positive* composite weight (0.19) -- i.e.
    /// "PCR above its own recent mean" currently reads bullish, the opposite of the classic
    /// contrarian reading (high PCR = bearish). Needs a scatter/correlation check against forward
    /// returns before changing sign or switching to a session-open baseline -- see the plan for
    /// the exact validation method. Do not flip this on intuition alone.
    /// </summary>
    double? ComputePcr(decimal spotPrice)
    {
        var bandStrikes = _nearestExpiryOptions
            .Select(o => o.StrikePrice!.Value)
            .Distinct()
            .OrderBy(s => Math.Abs(s - spotPrice))
            .Take((PersistedStrikeBand * 2) + 1)
            .ToHashSet();

        long callOi = 0, putOi = 0;
        foreach (var opt in _nearestExpiryOptions.Where(o => bandStrikes.Contains(o.StrikePrice!.Value)))
        {
            if (!_latest.TryGetValue(opt.Token, out var state) || state.OpenInterest is not { } oi)
            {
                continue;
            }

            if (opt.OptionType == OptionType.Call)
            {
                callOi += oi;
            }
            else if (opt.OptionType == OptionType.Put)
            {
                putOi += oi;
            }
        }

        return callOi > 0 ? (double)putOi / callOi : null;
    }

    /// <summary>
    /// Null (not a fabricated delta) when "the last cadence" is stale by more than one normal
    /// interval -- e.g. right after a feed reconnect (live-caught 2026-09-07). This compares
    /// OI now against OI at whatever <see cref="_previousCadence"/> happens to hold, which is
    /// normally ~15s old; after an outage it could be 60-90+ seconds old, so the delta would
    /// really be "several cadences' worth of change," not one -- and z-scoring that against a
    /// window calibrated for normal 15s deltas reads as a spurious extreme move. Skipping the
    /// one cadence right after a gap is cheaper and more honest than trying to guess a
    /// time-normalized correction.
    /// </summary>
    // Three fixes together (audit findings F16/F17/F18, 2026-09-08 lead review), on the single
    // largest-weighted component (0.2775):
    //   F16 -- returns null (not a fabricated 0) when there's no trustworthy previous cadence,
    //   including right after a Host restart (frequent, per this project's own deploy cadence) --
    //   the old `return 0` fed a fake reading into the downstream Welford window indistinguishable
    //   from a real "no buildup" cadence. Matches the gap branch just below, which already
    //   correctly returns null for the same underlying reason.
    //   F17 -- classifies from spot's own price change, not the option's -- the option leg's
    //   LastPrice is driven by its own delta/gamma/theta/vega, not necessarily monotonic with the
    //   underlying's actual direction (a flat-spot IV pop can move both a call's and a put's price
    //   up simultaneously with no real spot move behind either).
    //   F18 -- restricted to the ATM +/- PersistedStrikeBand strikes, not the full chain, same
    //   band ComputePcr already uses (F5) -- a deep-OTM strike's OI change previously voted as
    //   loud as an ATM one.
    double? ComputeOiBuildupNet(DateTimeOffset now, decimal spotPrice)
    {
        if (_previousCadence is null || !_previousCadence.TryGetValue(_spot.Token, out var prevSpot))
        {
            return null;
        }

        if (_lastCadenceAt is { } lastAt && now - lastAt > MaxCadenceGapForOiBuildup)
        {
            return null;
        }

        // PENDING (audit finding F50, 2026-09-10, follow-up not yet done): still the previous
        // 15s cadence's spot, not ~OiComparisonWindow ago -- oiChange below now spans the OI
        // lookback window (up to a few minutes), so pairing it with a 15s-old price direction
        // can occasionally disagree with the direction spot actually moved over that same
        // longer span. Left as-is for this fix (spot ticks continuously, so a 15s-old direction
        // is usually still representative, unlike OI's genuinely lumpy refresh) rather than
        // widening scope into a second lookback window without being asked -- worth doing if
        // classification accuracy turns out to matter more than expected.
        var spotPriceChange = spotPrice - prevSpot.LastPrice;

        var bandStrikes = _nearestExpiryOptions
            .Select(o => o.StrikePrice!.Value)
            .Distinct()
            .OrderBy(s => Math.Abs(s - spotPrice))
            .Take((PersistedStrikeBand * 2) + 1)
            .ToHashSet();

        double net = 0;
        foreach (var opt in _nearestExpiryOptions.Where(o => bandStrikes.Contains(o.StrikePrice!.Value)))
        {
            if (!_latest.TryGetValue(opt.Token, out var curr))
            {
                continue;
            }

            // Audit finding F50 (2026-09-10): OI compared against ~OiComparisonWindow ago, not
            // the previous 15s cadence -- see OiLookbackWindow's own doc comment. Null (skip
            // this strike, not a fabricated 0) until this token has enough history for a
            // trustworthy answer, same "don't guess" rule the method-level gap check above
            // already applies to spotPriceChange.
            if (_oiLookback.Lookback(opt.Token, now) is not { } prevOi)
            {
                continue;
            }

            var oiChange = (curr.OpenInterest ?? 0) - prevOi;
            var classification = OiBuildupClassifier.Classify(spotPriceChange, oiChange);

            // Standard NSE option-chain reading: call buildup/short-covering is bullish for
            // the underlying, put buildup/short-covering is bearish -- see class doc comment.
            // The sign switch below is multiplied by |oiChange| (audit finding F7, 2026-09-08),
            // not just summed as a flat +/-1 -- a 200-contract strike voted exactly as loud as
            // a 500,000-contract one under the old formula, on a component that carries the
            // largest weight in the whole composite. Raw OI-change magnitude, not further
            // normalized: this only ever feeds a z-score downstream, same as every other raw
            // value here, so the absolute scale (now much larger than the old +/-1..6 range)
            // is immaterial -- only its variation against its own rolling window is used.
            var sign = (opt.OptionType, classification) switch
            {
                (OptionType.Call, OiBuildupClassification.LongBuildup) => 1,
                (OptionType.Call, OiBuildupClassification.ShortCovering) => 1,
                (OptionType.Call, OiBuildupClassification.ShortBuildup) => -1,
                (OptionType.Call, OiBuildupClassification.LongUnwinding) => -1,
                (OptionType.Put, OiBuildupClassification.ShortBuildup) => 1,
                (OptionType.Put, OiBuildupClassification.LongUnwinding) => 1,
                (OptionType.Put, OiBuildupClassification.LongBuildup) => -1,
                (OptionType.Put, OiBuildupClassification.ShortCovering) => -1,
                _ => 0,
            };
            net += sign * Math.Abs(oiChange);
        }

        return net;
    }

    // DEFERRED (audit finding F30, 2026-09-08 lead review -- see fix plan Batch 6/7, "Deferred"
    // section): this reads two option strikes' own depth, which is market-maker inventory, not
    // Nifty order flow. Feasible alternative checked: the future instrument already flows through
    // the same _latest dictionary and InstrumentState.Depth structure options use (_latest[_future.Token].Depth),
    // so computing (bid-ask)/(bid+ask) on the future's own book instead needs no new ingestion
    // work. Real behavior change to a currently-required, 0.1275-weighted component though --
    // belongs in its own batch with before/after validation, not bundled into a mechanical fix.
    double? ComputeDepthImbalance(decimal spotPrice)
    {
        var ntmStrikes = _nearestExpiryOptions
            .Select(o => o.StrikePrice!.Value)
            .Distinct()
            .OrderBy(s => Math.Abs(s - spotPrice))
            .Take(2)
            .ToHashSet();

        long callBid = 0, callAsk = 0, putBid = 0, putAsk = 0;
        foreach (var opt in _nearestExpiryOptions.Where(o => ntmStrikes.Contains(o.StrikePrice!.Value)))
        {
            if (!_latest.TryGetValue(opt.Token, out var state) || state.Depth is not { } depth)
            {
                continue;
            }

            if (opt.OptionType == OptionType.Call)
            {
                callBid += depth.TotalBidQty;
                callAsk += depth.TotalAskQty;
            }
            else if (opt.OptionType == OptionType.Put)
            {
                putBid += depth.TotalBidQty;
                putAsk += depth.TotalAskQty;
            }
        }

        var callDepth = callBid + callAsk;
        var putDepth = putBid + putAsk;
        if (callDepth == 0 && putDepth == 0)
        {
            return null;
        }

        // Each side normalized independently, then differenced -- not summed together before
        // normalizing (audit finding F2, 2026-09-08). A call book bid-heavy is bullish (buying
        // calls); a put book bid-heavy is bearish (buying puts). Summing both sides' raw
        // quantities together treated them as the same signal, so a book that was simply busy
        // on both sides -- no real directional pressure at all -- read as strongly bullish. A
        // side with no depth this cadence contributes 0 (no evidence either way) rather than
        // making the whole cadence null just because one side happened to be quiet. Range is
        // now [-2,+2], not [-1,+1] -- immaterial, since this only ever feeds a z-score.
        var callImbalance = callDepth > 0 ? (double)(callBid - callAsk) / callDepth : 0.0;
        var putImbalance = putDepth > 0 ? (double)(putBid - putAsk) / putDepth : 0.0;
        return callImbalance - putImbalance;
    }

    /// <summary>
    /// Underlying for the IV solve is spot, not the tracked future (2026-09-04 fix,
    /// live-caught: "all put IVs read >10, call IVs around 5"). NSE only lists monthly
    /// futures; the tracked future is the current-month contract, which doesn't match a
    /// weekly option's own (much nearer) expiry. Using it overstated the underlying by its
    /// full month-vs-week cost-of-carry gap (~127 points, observed 2026-09-04) -- enough to
    /// price 23400 CE below its own intrinsic value (an impossible/arbitrage price), which
    /// is what was driving the solver to a degenerate call IV and, symmetrically, an
    /// inflated put IV. Same fix applied to BuildStrikeCandidates' Delta/IV.
    /// </summary>
    double? ComputeIvSkew(decimal spotPrice, DateTimeOffset now)
    {
        var t = TimeToExpiry.YearsUntilExpiry(_nearestExpiry, now);
        var underlying = ComputeUnderlyingPrice(spotPrice, t);

        // Risk-normalized offset, not a fixed point count (audit finding F8, 2026-09-08): a
        // constant 200-point offset means something completely different on a calm day (deep
        // OTM, near-worthless wings) than a volatile one (close to ATM, real premium) -- the
        // metric quietly changed what it was measuring depending on conditions. Anchored to the
        // expiry's own expected move (spot x sigma x sqrt(t)) via the same ATM-solved reference
        // vol GEX/Vanna/Charm/IvRank already use, so the same *relative* strikes (roughly one
        // expected move away) get compared regardless of regime.
        var atmStrike = _nearestExpiryOptions.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => Math.Abs(s - spotPrice)).FirstOrDefault();
        if (atmStrike == 0 || SolveAtmReferenceVol(atmStrike, underlying, t) is not { } atmVol)
        {
            return null;
        }

        var expectedMove = (decimal)((double)spotPrice * atmVol * Math.Sqrt(t));
        var targetCallStrike = spotPrice + expectedMove;
        var targetPutStrike = spotPrice - expectedMove;

        var callOpt = _nearestExpiryOptions
            .Where(o => o.OptionType == OptionType.Call)
            .MinBy(o => Math.Abs(o.StrikePrice!.Value - targetCallStrike));
        var putOpt = _nearestExpiryOptions
            .Where(o => o.OptionType == OptionType.Put)
            .MinBy(o => Math.Abs(o.StrikePrice!.Value - targetPutStrike));

        if (callOpt is null || putOpt is null
            || !_latest.TryGetValue(callOpt.Token, out var callState) || !_latest.TryGetValue(putOpt.Token, out var putState))
        {
            return null;
        }

        var callMid = MidPrice(callState);
        var putMid = MidPrice(putState);
        if (callMid is not { } cm || putMid is not { } pm)
        {
            return null;
        }

        var callIv = ImpliedVolatilitySolver.Solve(OptionType.Call, (double)cm, (double)underlying, (double)callOpt.StrikePrice!.Value, t, RiskFreeRate);
        var putIv = ImpliedVolatilitySolver.Solve(OptionType.Put, (double)pm, (double)underlying, (double)putOpt.StrikePrice!.Value, t, RiskFreeRate);

        // PENDING (audit finding F24, 2026-09-08 lead review -- see fix plan Batch 7): two open
        // questions on this component (weight 0.1425). First, F8's expected-move-based target
        // strikes (above) are a real improvement over the old fixed +/-200pt offset, but still an
        // approximation of the industry-standard 25-delta risk reversal (strikes where |delta| is
        // actually ~0.25, not a price-distance heuristic). Second, and independently: putIv -
        // callIv carries a positive composite weight, meaning rising put skew (more fear premium)
        // currently reads bullish -- the opposite of the standard reading. Needs the same
        // scatter/correlation validation as F23 before changing either.
        return callIv is null || putIv is null ? null : putIv - callIv;
    }

    /// <summary>
    /// Solves IV at the ATM strike to use as every other strike's theoretical-pricing input in
    /// <see cref="BuildStrikeSnapshots"/>. Prefers the call leg, falls back to the put if the
    /// call has no usable quote or its solve fails -- same preference as the dashboard's own
    /// BuildAtmReferenceVol. Null (not a guess) when neither leg can be solved this cadence.
    /// <paramref name="underlyingPrice"/> should be <see cref="ComputeUnderlyingPrice"/>'s
    /// result, not raw spot -- see that method's doc comment.
    /// </summary>
    double? SolveAtmReferenceVol(decimal atmStrike, decimal underlyingPrice, double t)
    {
        // Audit finding F33 fixed (2026-09-09): the fallback/solve step itself is now shared
        // with LiveDataService's own ATM-vol solve (AtmReferenceVolSolver, NiftySignal.Pricing)
        // -- only this method's own iteration and mid-price mark choice stay local, since
        // preferring mid over LTP for live scoring stability is a deliberate, pre-existing
        // difference from the dashboard's LTP-based read, not something that needed sharing.
        var atmLegs = _nearestExpiryOptions
            .Where(o => o.StrikePrice == atmStrike)
            .OrderBy(o => o.OptionType == OptionType.Call ? 0 : 1)
            .Select(o => _latest.TryGetValue(o.Token, out var state) && MidPrice(state) is { } mark
                ? new AtmReferenceVolSolver.Leg(o.OptionType, mark)
                : (AtmReferenceVolSolver.Leg?)null)
            .Where(leg => leg is not null)
            .Select(leg => leg!.Value);

        return AtmReferenceVolSolver.Solve(atmLegs, atmStrike, underlyingPrice, t, RiskFreeRate);
    }

    /// <summary>
    /// Net gamma exposure across the *entire* nearest-expiry chain (2026-09-07, diagnostic-only
    /// -- see ScoreWeights.Default's GammaExposure weight), not just the ATM+/-2 strikes
    /// strike_snapshots persists (see BuildStrikeSnapshots' PersistedStrikeBand) -- that band is
    /// far too narrow to mean anything as an aggregate positioning read, and evaluating it
    /// against one day's data (2026-09-07) came back inconclusive for exactly that reason.
    ///
    /// Deliberately reuses the single ATM-solved reference vol (same one BuildStrikeSnapshots
    /// uses for TheoreticalPrice) for every strike's Gamma, rather than solving each strike's
    /// own IV -- two reasons: (1) it's cheap, a closed-form BlackScholes.Calculate call per
    /// strike with zero additional Newton-Raphson solves; (2) it doesn't depend on every strike
    /// having a live two-sided quote this cadence (illiquid wings often don't), only on knowing
    /// its OpenInterest, which arrives on touchline updates alone. A strike's own IV would be
    /// more accurate per-strike, but GEX is inherently an aggregate approximation, not a pricing
    /// exercise, and this keeps every OI-bearing strike counted rather than only the ones with a
    /// fresh two-sided market.
    ///
    /// Sign convention matches OiBuildupNet's call-vs-put reasoning: net call gamma-OI counted
    /// positive, net put gamma-OI counted negative -- an exploratory choice, not a claim about
    /// which side of a given position dealers are actually on (this system has no visibility
    /// into dealer positioning, only aggregate OI). Null when the ATM vol can't be solved this
    /// cadence (same "don't fabricate a value" rule as everywhere else) or no strike has both a
    /// strike price and OI yet.
    ///
    /// DEFERRED (audit finding F29, 2026-09-08 lead review -- see fix plan Batch 6/7, "Deferred"
    /// section): this (and GammaFlipLevel) is a call-put gamma *tilt*, not dealer GEX -- true
    /// dealer GEX needs the same short-dealer sign convention on both legs, not the OiBuildupNet-
    /// style call-positive/put-negative split above. Naming is misleading (GammaFlipLevel reads
    /// as "the pin," but it's this tilt's own zero-cross, not dealer positioning) but zero live
    /// impact today since weight is already 0 (diagnostic-only). Would need a migration (rename
    /// or new columns: CallPutGammaTilt/TiltCrossLevel here, new DealerGexRaw/DealerGexFlip
    /// alongside) plus a genuinely new calculation -- not urgent, but worth doing before this
    /// component is ever given a nonzero weight. Also folded in here (2026-09-08 third-party
    /// review, same low-priority/diagnostic-only status): this, Vanna, and Charm all sum raw
    /// per-contract Greek x OI with no notional scaling (the conventional Gamma x OI x Spot^2 x
    /// 0.01-style scaling most public GEX write-ups use) -- internally consistent for z-scoring
    /// against their own history (all that's used today) but not comparable to any external
    /// reference level. Scale to a proper notional unit whenever any of the three is validated.
    /// </summary>
    double? ComputeGammaExposure(decimal spotPrice, double? atmReferenceVol, double t)
    {
        if (atmReferenceVol is not { } vol)
        {
            return null;
        }

        double net = 0;
        var any = false;
        foreach (var option in _nearestExpiryOptions)
        {
            if (!_latest.TryGetValue(option.Token, out var state) || state.OpenInterest is not { } oi || oi <= 0)
            {
                continue;
            }

            var gamma = BlackScholes.Calculate(
                option.OptionType, (double)spotPrice, (double)option.StrikePrice!.Value, t, RiskFreeRate, vol).Greeks.Gamma;
            net += option.OptionType == OptionType.Call ? gamma * oi : -(gamma * oi);
            any = true;
        }

        return any ? net : null;
    }

    /// <summary>
    /// Net Vanna (d Delta/d sigma) exposure, OI-weighted across the full nearest-expiry chain --
    /// same shape and same call/put dealer-positioning sign convention as
    /// <see cref="ComputeGammaExposure"/> (whose own doc comment explains the convention), reused
    /// here because Vanna, like Gamma, is identical for a call and put at the same strike (see
    /// <see cref="OptionGreeks"/>' doc comment) -- the sign difference is the modeling choice
    /// already made for GEX, not a property of the Greek itself.
    /// </summary>
    double? ComputeVannaExposure(decimal spotPrice, double? atmReferenceVol, double t)
    {
        if (atmReferenceVol is not { } vol)
        {
            return null;
        }

        double net = 0;
        var any = false;
        foreach (var option in _nearestExpiryOptions)
        {
            if (!_latest.TryGetValue(option.Token, out var state) || state.OpenInterest is not { } oi || oi <= 0)
            {
                continue;
            }

            var vanna = BlackScholes.Calculate(
                option.OptionType, (double)spotPrice, (double)option.StrikePrice!.Value, t, RiskFreeRate, vol).Greeks.Vanna;
            net += option.OptionType == OptionType.Call ? vanna * oi : -(vanna * oi);
            any = true;
        }

        return any ? net : null;
    }

    /// <summary>
    /// Net Charm (d Delta/d t, per day) exposure, OI-weighted across the full nearest-expiry
    /// chain -- same shape/convention as <see cref="ComputeVannaExposure"/>. Particularly
    /// relevant on expiry day, when OTM deltas decay fastest toward zero in the final hours.
    /// </summary>
    double? ComputeCharmExposure(decimal spotPrice, double? atmReferenceVol, double t)
    {
        if (atmReferenceVol is not { } vol)
        {
            return null;
        }

        double net = 0;
        var any = false;
        foreach (var option in _nearestExpiryOptions)
        {
            if (!_latest.TryGetValue(option.Token, out var state) || state.OpenInterest is not { } oi || oi <= 0)
            {
                continue;
            }

            var charm = BlackScholes.Calculate(
                option.OptionType, (double)spotPrice, (double)option.StrikePrice!.Value, t, RiskFreeRate, vol).Greeks.CharmPerDay;
            net += option.OptionType == OptionType.Call ? charm * oi : -(charm * oi);
            any = true;
        }

        return any ? net : null;
    }

    /// <summary>
    /// The spot level where net Gamma Exposure crosses zero (2026-09-08, diagnostic-only) --
    /// evaluates <see cref="ComputeGammaExposure"/>'s own aggregation with each tracked strike
    /// in turn standing in as a hypothetical spot price (same reference vol shared across the
    /// whole sweep, not re-solved per candidate -- IV doesn't change with a hypothetical spot,
    /// only the resulting Greeks do), then linearly interpolates the zero crossing between the
    /// two adjacent strikes where the sign flips. A grid search over the currently tracked
    /// strikes, not a continuous root-find -- deliberately scoped down given the currently
    /// tracked chain is the only spot range this system has live quotes/OI for anyway. Null when
    /// no sign flip exists across that range (e.g. net GEX is one-sided all the way through).
    /// </summary>
    double? ComputeGammaFlipLevel(double? atmReferenceVol, double t)
    {
        if (atmReferenceVol is not { } vol)
        {
            return null;
        }

        var strikes = _nearestExpiryOptions.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => s).ToList();
        if (strikes.Count < 2)
        {
            return null;
        }

        double? previousStrike = null;
        double? previousNet = null;
        foreach (var strike in strikes)
        {
            var candidateSpot = (double)strike;
            double net = 0;
            var any = false;
            foreach (var option in _nearestExpiryOptions)
            {
                if (!_latest.TryGetValue(option.Token, out var state) || state.OpenInterest is not { } oi || oi <= 0)
                {
                    continue;
                }

                var gamma = BlackScholes.Calculate(
                    option.OptionType, candidateSpot, (double)option.StrikePrice!.Value, t, RiskFreeRate, vol).Greeks.Gamma;
                net += option.OptionType == OptionType.Call ? gamma * oi : -(gamma * oi);
                any = true;
            }

            if (!any)
            {
                continue;
            }

            if (previousNet is { } prevNet && previousStrike is { } prevStrike && prevNet != 0 && net != 0 && Math.Sign(prevNet) != Math.Sign(net))
            {
                var fraction = prevNet / (prevNet - net);
                return prevStrike + (fraction * (candidateSpot - prevStrike));
            }

            previousStrike = candidateSpot;
            previousNet = net;
        }

        return null;
    }

    /// <summary>
    /// ATM straddle's actual price change this cadence minus what its Delta and Theta from the
    /// *previous* cadence predicted, given the realized underlying move and elapsed time --
    /// isolates the vega/skew-driven residual from the delta and theta effects that would move
    /// the straddle's price regardless of any change in volatility demand (2026-09-08,
    /// diagnostic-only -- see ScoreWeights.Default's StraddleRichness weight and
    /// ScoreSnapshot.StraddleRichnessRaw's own doc comment for the sign convention).
    ///
    /// Deliberately compares the same strike cadence-to-cadence, not "whichever strike is ATM
    /// right now" -- if the ATM strike itself rolled since the previous cadence, the two
    /// straddles aren't the same instrument and comparing their prices would be meaningless, so
    /// this returns null on a roll cadence rather than fabricate a comparison (same "null, not
    /// wrong" discipline as <see cref="ComputeOiBuildupNet"/>'s stale-gap case).
    ///
    /// DEFERRED (audit finding F31, 2026-09-08 lead review -- see fix plan Batch 6/7, "Deferred"
    /// section): already correctly excluded from the directional sum (weight 0) -- it's a vol-
    /// demand residual, not a direction. Lead's new idea: use a sharply falling richness (a vol
    /// dump) as a block on new long-premium entries, not add it to the score. A genuinely new
    /// feature, not a fix; worth a dedicated look once there's enough live history to know what
    /// "sharply falling" should mean numerically.
    /// </summary>
    double? ComputeStraddleRichness(decimal underlying, double? atmReferenceVol, double t, DateTimeOffset now)
    {
        if (atmReferenceVol is not { } vol || _nearestExpiryOptions.Count == 0)
        {
            return null;
        }

        var atmStrike = _nearestExpiryOptions.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => Math.Abs(s - underlying)).First();
        var call = _nearestExpiryOptions.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == atmStrike);
        var put = _nearestExpiryOptions.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == atmStrike);
        if (call is null || put is null
            || !_latest.TryGetValue(call.Token, out var callState) || !_latest.TryGetValue(put.Token, out var putState))
        {
            return null;
        }

        if (MidPrice(callState) is not { } callMid || MidPrice(putState) is not { } putMid)
        {
            return null;
        }

        var underlyingAsDouble = (double)underlying;
        var callGreeks = BlackScholes.Calculate(OptionType.Call, underlyingAsDouble, (double)atmStrike, t, RiskFreeRate, vol).Greeks;
        var putGreeks = BlackScholes.Calculate(OptionType.Put, underlyingAsDouble, (double)atmStrike, t, RiskFreeRate, vol).Greeks;

        var straddleMid = (double)(callMid + putMid);
        var netDelta = callGreeks.Delta + putGreeks.Delta;
        var thetaPerDay = callGreeks.ThetaPerDay + putGreeks.ThetaPerDay;

        double? richness = null;
        if (_previousStraddleStrike == atmStrike
            && _previousStraddleAt is { } prevAt && _previousStraddleMid is { } prevMid
            && _previousStraddleNetDelta is { } prevDelta && _previousStraddleThetaPerDay is { } prevTheta
            && _previousStraddleUnderlying is { } prevUnderlying)
        {
            var elapsedDays = (now - prevAt).TotalSeconds / 86400.0;
            var expectedChange = (prevDelta * (underlyingAsDouble - prevUnderlying)) + (prevTheta * elapsedDays);
            var actualChange = straddleMid - prevMid;
            richness = actualChange - expectedChange;
        }

        _previousStraddleStrike = atmStrike;
        _previousStraddleAt = now;
        _previousStraddleMid = straddleMid;
        _previousStraddleNetDelta = netDelta;
        _previousStraddleThetaPerDay = thetaPerDay;
        _previousStraddleUnderlying = underlyingAsDouble;

        return richness;
    }

    /// <summary>
    /// Where today's ATM IV sits against a reference distribution, 0-100 (2026-09-08, audit
    /// finding F3; amended 2026-09-09 external review). Originally ranked against a same-day
    /// rolling window (<c>FeatureWindowLengths.IvRank</c>, 2 hours) -- exactly the same "fades
    /// toward neutral the moment a genuinely extreme level stops *changing*" bug this finding
    /// exists to fix, just wearing an IV-rank badge instead of a z-score: a genuinely high-vol
    /// day would still read as "normal" for its own first couple of hours. Now ranks against
    /// the last <see cref="MaxPriorSessionsForIvRank"/> prior sessions' representative ATM IV
    /// (<see cref="_priorSessionAtmIv"/>, seeded once at Host startup) once at least
    /// <see cref="MinPriorSessionsForIvRank"/> of them exist; below that, falls back to ranking
    /// against today's own observations so far (<see cref="_todaySessionAtmIv"/> -- the
    /// original min-max-within-window math, just against an unbounded same-day list instead of
    /// a 2-hour rolling one). <see cref="IvRankSessionCount"/> tells which mode produced the
    /// value, so <see cref="Rules.EntryRuleEvaluator"/>'s MaxIvRankForEntry gate can refuse to
    /// act on a same-day-only rank that isn't trustworthy yet. Null until the ranked-against
    /// distribution holds at least two observations with a real (non-degenerate) spread -- a
    /// single point, or a perfectly flat distribution, can't produce a meaningful rank, and
    /// fabricating a "neutral 50" would be exactly the kind of made-up value this codebase
    /// avoids everywhere else. Clamped to [0,100]: a mature-mode rank is measured against a
    /// *fixed* prior-session distribution that doesn't include today, so a session more extreme
    /// than anything in the last 20 would otherwise read outside that range.
    /// </summary>
    double? ComputeIvRank(double? currentVol, DateTimeOffset now)
    {
        if (currentVol is not { } vol)
        {
            return null;
        }

        if (_priorSessionAtmIv.Count < MinPriorSessionsForIvRank)
        {
            _todaySessionAtmIv.Add(vol);
            return RankWithinDistribution(_todaySessionAtmIv, vol);
        }

        return RankWithinDistribution(_priorSessionAtmIv, vol);
    }

    static double? RankWithinDistribution(IReadOnlyList<double> distribution, double value)
    {
        if (distribution.Count < 2)
        {
            return null;
        }

        var min = distribution.Min();
        var max = distribution.Max();
        if (max - min < 1e-9)
        {
            return null;
        }

        return Math.Clamp((value - min) / (max - min) * 100.0, 0.0, 100.0);
    }

    /// <summary>How many prior sessions <see cref="ComputeIvRank"/> is currently ranking against -- see its own doc comment and <see cref="ScoreSnapshot.IvRankSessionCount"/>.</summary>
    public int IvRankSessionCount => _priorSessionAtmIv.Count;

    /// <summary>
    /// Seeds the prior-session ATM IV distribution <see cref="ComputeIvRank"/> ranks against
    /// once mature (2026-09-09 external review amendment to audit finding F3). Called once at
    /// Host startup with each of the last <see cref="MaxPriorSessionsForIvRank"/> prior
    /// sessions' representative ATM IV (session mean) -- see
    /// MarketDataIngestionWorker's prior-session seed query. Deliberately not restart-seedable
    /// from today's own <see cref="SeedHistory"/> replay the way the rolling windows are: this
    /// is cross-session state, not same-day state, and gets set exactly once per process
    /// lifetime rather than replayed every restart.
    /// </summary>
    public void SeedPriorSessionIvHistory(IReadOnlyList<double> sessionMeans)
    {
        _priorSessionAtmIv = [.. sessionMeans.TakeLast(MaxPriorSessionsForIvRank)];
    }

    /// <summary>
    /// Notional (traded-volume x mark price) put/call ratio, and a quote-rule aggressor-volume
    /// proxy, both across the *entire* nearest-expiry chain -- one pass, one shared
    /// <see cref="_previousVolumeByTokenFullChain"/> baseline, rather than two separate full-chain
    /// volume-delta trackers (2026-09-08: CvdProxy added onto VolumePcr's existing loop instead of
    /// duplicating it).
    ///
    /// VolumePcr (2026-09-07, diagnostic-only -- see ScoreWeights.Default's VolumePcr weight):
    /// notional rather than raw contract count -- a same-day check found a notional-weighted
    /// version of the existing OI-based Pcr correlated far more strongly with forward price moves
    /// than the count-weighted one; see ScoreWeights.Default's own doc comment for the full
    /// reasoning, including where count and notional weighting disagreed for volume specifically.
    ///
    /// CvdProxy (2026-09-08, diagnostic-only -- see ScoreWeights.Default's CvdProxy weight): the
    /// feed has no per-trade tape (touchline LTP/cumulative volume/5-level depth only), so there
    /// is no true aggressor-tagged Cumulative Volume Delta available -- this is a quote-rule
    /// proxy instead. Each cadence's traded volume on a strike is classified buy- or sell-leaning
    /// by whether LTP sat at/above or below the contemporaneous bid/ask midpoint (needs an actual
    /// two-sided quote, unlike VolumePcr's mark price which falls back to LTP when one-sided).
    /// Signed per option type the same way OiBuildupNetRaw already is: buy-leaning call volume
    /// and sell-leaning put volume (writing) both read bullish; the reverse reads bearish.
    ///
    /// Both are genuine interval deltas like OiBuildupNet/GammaExposure (volume traded *this
    /// cadence*), not point-in-time reads -- computed once per cadence, not smoothed through
    /// <see cref="Sample"/>.
    /// </summary>
    (double? VolumePcr, double? CvdProxy) ComputeVolumePcrAndCvdProxy()
    {
        double callNotional = 0, putNotional = 0, cvdNet = 0;
        var anyCvd = false;

        foreach (var option in _nearestExpiryOptions)
        {
            if (!_latest.TryGetValue(option.Token, out var state))
            {
                continue;
            }

            var volumeDelta = _previousVolumeByTokenFullChain.TryGetValue(option.Token, out var previousVolume)
                ? Math.Max(0, state.Volume - previousVolume)
                : 0;
            _previousVolumeByTokenFullChain[option.Token] = state.Volume;

            if (volumeDelta <= 0)
            {
                continue;
            }

            if (MidPrice(state) is { } mark and > 0)
            {
                var notional = (double)volumeDelta * (double)mark;
                if (option.OptionType == OptionType.Call)
                {
                    callNotional += notional;
                }
                else if (option.OptionType == OptionType.Put)
                {
                    putNotional += notional;
                }
            }

            if (state.Depth is { } depth && depth.Bid1Price > 0 && depth.Ask1Price > 0)
            {
                var midpoint = (depth.Bid1Price + depth.Ask1Price) / 2;
                var buyLeaning = state.LastPrice >= midpoint;
                var signedVolume = buyLeaning ? (double)volumeDelta : -(double)volumeDelta;
                cvdNet += option.OptionType == OptionType.Call ? signedVolume : -signedVolume;
                anyCvd = true;
            }
        }

        double? volumePcr = callNotional > 0 ? putNotional / callNotional : null;
        double? cvdProxy = anyCvd ? cvdNet : null;
        return (volumePcr, cvdProxy);
    }

    // ==================== Core score (2026-09-13, live-wiring plan Batch 2) ====================
    // See docs/replication_plan.md's A3 for the full per-term reuse/adapt/build-fresh rationale.

    // Itm2Atm1: calls {-2,-1,0}, puts {0,+1,+2} by signed strike offset -- ported unchanged from
    // NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs's own InItm2Atm1, matches
    // docs/CHILD_TABLE_SCHEMA.md exactly.
    static bool InCoreItm2Atm1(OptionType type, int offset) =>
        type == OptionType.Call ? offset is >= -2 and <= 0 : offset is >= 0 and <= 2;

    /// <summary>
    /// Signed strike offsets from ATM (0=ATM, negative=below spot, positive=above) -- matching
    /// StrikeCadenceSnapshot.StrikeOffsetFromAtm's own convention exactly. Needed because the
    /// Itm2Atm1 band is defined by signed offset, unlike every existing live band (PersistedStrikeBand,
    /// RatioWideStrikeBand), which select "nearest N strikes to spot" without distinguishing
    /// which side of spot they're on.
    /// </summary>
    Dictionary<decimal, int> ComputeStrikeOffsets(decimal spotPrice)
    {
        var distinctStrikes = _nearestExpiryOptions.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => s).ToList();
        if (distinctStrikes.Count == 0)
        {
            return [];
        }

        var atmIndex = 0;
        var atmDistance = decimal.MaxValue;
        for (var i = 0; i < distinctStrikes.Count; i++)
        {
            var distance = Math.Abs(distinctStrikes[i] - spotPrice);
            if (distance < atmDistance)
            {
                atmDistance = distance;
                atmIndex = i;
            }
        }

        var offsets = new Dictionary<decimal, int>();
        for (var i = 0; i < distinctStrikes.Count; i++)
        {
            offsets[distinctStrikes[i]] = i - atmIndex;
        }

        return offsets;
    }

    /// <summary>
    /// Cadence-level DepthImbalance, Itm2Atm1 band (Batch 3 fix, 2026-09-13) -- each strike's own
    /// tick-level-averaged imbalance ratio this cadence (<see cref="_coreDepthImbalanceByToken"/>,
    /// fed per-tick in OnTick), THEN averaged per side (call avg minus put avg), NOT the
    /// 14-component composite's ComputeDepthImbalance construction (sum raw quantities across
    /// strikes first, normalize once). Matches CoreScoreOptionSimulator.cs:283-288/
    /// CadencePopulator.cs's DepthImbalanceAccumulator exactly -- the original 3s-instant-sample
    /// version of this method did NOT (see _coreDepthImbalanceByToken's own doc comment).
    /// </summary>
    double? ComputeCoreDepthImbalanceRaw(Dictionary<decimal, int> strikeOffsets)
    {
        var callImbalances = new List<double>();
        var putImbalances = new List<double>();

        foreach (var option in _nearestExpiryOptions)
        {
            if (!strikeOffsets.TryGetValue(option.StrikePrice!.Value, out var offset) || !InCoreItm2Atm1(option.OptionType, offset))
            {
                continue;
            }

            // Forward-fill (Batch 3 fix, second pass) -- see _coreLastDepthImbalanceByToken's own
            // doc comment: a strike with no fresh tick THIS cadence still contributes its last
            // known imbalance ratio, matching BuildScoreCadences' own lastDepth forward-fill.
            if (_coreDepthImbalanceByToken.TryGetValue(option.Token, out var accumulator) && accumulator.CadenceImbalance is { } freshImbalance)
            {
                _coreLastDepthImbalanceByToken[option.Token] = freshImbalance;
            }

            if (!_coreLastDepthImbalanceByToken.TryGetValue(option.Token, out var imbalance))
            {
                continue;
            }

            (option.OptionType == OptionType.Call ? callImbalances : putImbalances).Add(imbalance);
        }

        return callImbalances.Count > 0 && putImbalances.Count > 0
            ? callImbalances.Average() - putImbalances.Average()
            : (double?)null;
    }

    /// <summary>
    /// Cadence-level ItmSkew, Itm2Atm1 band (Batch 3 fix, 2026-09-13) -- plain arithmetic mean of
    /// every call strike's IV in the band, separately the plain mean of every put strike's IV,
    /// then putAvg-callAvg, each strike's IV solved from its own LAST two-sided-quote mid THIS
    /// cadence specifically (<see cref="_coreCadenceMidPriceByToken"/>, fed per-tick in OnTick;
    /// null -- excluded, not stale -- if no such tick arrived this cadence). Matches
    /// CoreScoreOptionSimulator.cs:290-299/CadencePopulator's own CadenceMarkPrice semantics
    /// exactly -- the original 3s-instant-sample-then-averaged version of this method read
    /// _latest instead, which holds whatever tick last arrived regardless of how many cadences
    /// ago, a genuine mismatch from the backtest's "this cadence only" semantics. NOT a
    /// two-single-strike difference (unlike ComputeIvSkew's dynamic ~1-sigma OTM pair) and NOT
    /// OI-weighted. Uses the same synthetic-forward underlying (ComputeUnderlyingPrice) every
    /// other live IV/Greeks solve in this class already uses. Nulled outright on a 0-DTE (expiry)
    /// day -- IV is known to distort severely on expiry day (docs/SCORE_CANDIDATES.md's
    /// "expiry-day regime exclusion" finding). Deliberately duplicates the 0-DTE check as a
    /// literal condition here rather than sharing a helper with LiveTradingEngine's own
    /// isExpiryDay -- matches this codebase's stated convention (see
    /// EntryRuleEvaluator.MinIvRankSessionsForGate's own doc comment).
    /// </summary>
    double? ComputeCoreItmSkewRaw(Dictionary<decimal, int> strikeOffsets, decimal spotPrice, DateTimeOffset now)
    {
        if (_nearestExpiry == DateOnly.FromDateTime(now.ToIst().DateTime))
        {
            return null;
        }

        var t = TimeToExpiry.YearsUntilExpiry(_nearestExpiry, now);
        var underlying = ComputeUnderlyingPrice(spotPrice, t);

        var callIvs = new List<double>();
        var putIvs = new List<double>();

        foreach (var option in _nearestExpiryOptions)
        {
            if (!strikeOffsets.TryGetValue(option.StrikePrice!.Value, out var offset) || !InCoreItm2Atm1(option.OptionType, offset))
            {
                continue;
            }

            // Forward-fill (Batch 3 fix, second pass) -- see _coreLastIvByToken's own doc
            // comment: a strike with no fresh two-sided quote THIS cadence still contributes its
            // last successfully-solved IV, matching BuildScoreCadences' own lastIv forward-fill.
            if (_coreCadenceMidPriceByToken.TryGetValue(option.Token, out var mid) && mid is { } midValue && midValue > 0)
            {
                var freshIv = ImpliedVolatilitySolver.Solve(option.OptionType, (double)midValue, (double)underlying, (double)option.StrikePrice!.Value, t, RiskFreeRate);
                if (freshIv is { } freshIvValue)
                {
                    _coreLastIvByToken[option.Token] = freshIvValue;
                }
            }

            if (!_coreLastIvByToken.TryGetValue(option.Token, out var ivValue))
            {
                continue;
            }

            (option.OptionType == OptionType.Call ? callIvs : putIvs).Add(ivValue);
        }

        return callIvs.Count > 0 && putIvs.Count > 0 ? putIvs.Average() - callIvs.Average() : (double?)null;
    }

    /// <summary>
    /// Rolling 5-real-minute SUM of this cadence's classified future volume (from OnTick's own
    /// FutureCvdProxyAccumulator port) -- mirrors NiftySignal.BacktestData/CadencePopulator.cs's
    /// RollingNetSumWindow. A cadence with no contributing tick records a genuine 0 (not skipped),
    /// same "a real zero is a real zero" reasoning FutureCvdProxyAccumulator.CadenceNet's own
    /// null-vs-zero split documents for a single cadence, extended here to the rolling sum. Resets
    /// the per-cadence accumulator for the next cadence, same instant/reset shape as
    /// FutureCvdProxyAccumulator.CadenceNet/ResetCadence.
    /// </summary>
    double? ComputeCoreFutureCvdNet5Min(DateTimeOffset now)
    {
        // "Skip a null cadence, never zero-fill it" (CadencePopulator's own comment on
        // cvdNet5Min.Add's conditional call) -- the window, its own eviction, and both warm-up
        // timestamps are all updated ONLY on a cadence with a real classifiable tick, matching
        // RollingNetSumWindow.Add/EvictOlderThan's exact coupling (eviction runs INSIDE Add, so it
        // never runs on a quiet cadence either). See _coreFutureCvdFirstSeenAt's own doc comment.
        if (_coreFutureCvdCadenceHasContribution)
        {
            _coreFutureCvdFirstSeenAt ??= now;
            _coreFutureCvdLatestTimestamp = now;

            while (_coreFutureCvdNet5MinWindow.Count > 0 && now - _coreFutureCvdNet5MinWindow.Peek().Timestamp > CoreFutureCvdNet5MinWindow)
            {
                _coreFutureCvdNet5MinWindow.Dequeue();
            }

            _coreFutureCvdNet5MinWindow.Enqueue((now, _coreFutureCvdCadenceNet));
        }

        _coreFutureCvdCadenceNet = 0;
        _coreFutureCvdCadenceHasContribution = false;

        if (_coreFutureCvdFirstSeenAt is not { } firstSeenAt || _coreFutureCvdLatestTimestamp is not { } latestTimestamp
            || latestTimestamp - firstSeenAt < CoreFutureCvdNet5MinWindow)
        {
            return null;
        }

        return _coreFutureCvdNet5MinWindow.Sum(w => (double)w.Net);
    }

    /// <summary>
    /// Call notional / put notional, ATM+/-RatioWideStrikeBand, log-ratio inverted (put/call, not
    /// call/put) -- matches CoreScoreOptionSimulator.cs:304-311 exactly (tested NEGATIVE
    /// correlation with price, inverted so a positive log-ratio consistently means bullish like
    /// the other terms). Structurally identical to ComputeRatioNotionalVolumeRaw (same ATM+/-5
    /// band, same notional-by-side construction) but deliberately kept as its own method with its
    /// own independent volume baseline (_previousVolumeByTokenCoreScore) rather than sharing or
    /// renaming that one while the ratio composite it serves is still active -- the "move, don't
    /// duplicate" consolidation from docs/replication_plan.md A3 happens in Batch 4, once the
    /// ratio composite (and its own ComputeRatioNotionalVolumeRaw call) is retired, not before.
    ///
    /// Band membership (Batch 3 fix) uses the SAME signed index offset (<paramref
    /// name="strikeOffsets"/>) DepthImbalance/ItmSkew/GammaExposure already use -- NOT "nearest N
    /// strikes by raw price distance from spot" (an earlier version of this method), same
    /// index-vs-price-distance mismatch GammaExposure's own fix already found. Mark price is this
    /// cadence's own fresh two-sided-quote mid where one exists, else the LAST known mark,
    /// forward-filled via <see cref="_coreLastMarkByToken"/> -- matches
    /// CoreScoreOptionSimulator.cs's own <c>lastMark = row.MarkPrice ?? lastMark</c> forward-fill
    /// exactly (a strike can have real VolumeDeltaRaw this cadence without ALSO having a fresh
    /// two-sided depth quote this same cadence, if depth updates lag LTP/volume updates in the raw
    /// feed) -- NOT `_latest`'s own MidPrice, which reads whatever depth object happens to be
    /// sitting there regardless of which cadence it actually arrived in.
    /// </summary>
    double? ComputeCoreNotionalVolumeRatioRaw(Dictionary<decimal, int> strikeOffsets)
    {
        double callNotional = 0, putNotional = 0;

        foreach (var option in _nearestExpiryOptions)
        {
            // Volume baseline and mark forward-fill are updated for EVERY tracked option,
            // regardless of band membership THIS cadence -- matching CadencePopulator's own
            // OptionInstrumentState, which tracks every strike's per-cadence volume delta
            // unconditionally (see its own "history is never gapped by drifting in or out of the
            // persisted band" doc comment on BuildStrikeRows). Scoping this update to only the
            // currently-in-band strikes (an earlier version of this method) let a strike's
            // baseline go stale while ATM drifted it out of the +/-5 band, so its volumeDelta on
            // re-entry silently summed several cadences' worth of volume at once instead of one --
            // caught by CoreScoreReplayDiff as a persistent, large NotionalVolumeRatioRaw
            // mismatch that neither the band-selection nor the mark-forward-fill fix touched.
            if (!_latest.TryGetValue(option.Token, out var state))
            {
                continue;
            }

            var volumeDelta = _previousVolumeByTokenCoreScore.TryGetValue(option.Token, out var previousVolume)
                ? Math.Max(0, state.Volume - previousVolume)
                : 0;
            _previousVolumeByTokenCoreScore[option.Token] = state.Volume;

            if (_coreCadenceMidPriceByToken.GetValueOrDefault(option.Token) is { } freshMark && freshMark > 0)
            {
                _coreLastMarkByToken[option.Token] = freshMark;
            }

            if (!strikeOffsets.TryGetValue(option.StrikePrice!.Value, out var offset) || Math.Abs(offset) > RatioWideStrikeBand)
            {
                continue;
            }

            if (volumeDelta <= 0 || !_coreLastMarkByToken.TryGetValue(option.Token, out var mark) || mark <= 0)
            {
                continue;
            }

            var notional = (double)volumeDelta * (double)mark;
            if (option.OptionType == OptionType.Call)
            {
                callNotional += notional;
            }
            else if (option.OptionType == OptionType.Put)
            {
                putNotional += notional;
            }
        }

        return callNotional > 0 && putNotional > 0 ? Math.Log(putNotional / callNotional) : null;
    }

    /// <summary>
    /// Net gamma exposure, ATM+/-CoreGammaBandOffset, "version A" -- each strike's OWN
    /// individually-solved IV feeds its own Gamma, NOT the 14-component composite's
    /// ComputeGammaExposure ("version B": one shared ATM reference vol for every strike).
    /// Confirmed distinct formulas via docs/SCORE_CANDIDATES.md:697-698 -- the backtest was
    /// validated on version A specifically; reusing ComputeGammaExposure with just a band filter
    /// would silently trade version B, a formula never actually backtested. Matches
    /// CoreScoreOptionSimulator.cs:313-317's sign convention (+call, -put) exactly.
    ///
    /// Gamma itself is solved fresh only when a strike has a real two-sided quote THIS cadence
    /// (<see cref="_coreCadenceMidPriceByToken"/>); otherwise the LAST successfully-solved value
    /// is reused, frozen, via <see cref="_coreLastGammaByToken"/> (Batch 3 fix -- see that field's
    /// own doc comment for why re-solving from a stale mid against the current, still-decaying
    /// underlying/time-to-expiry was a genuine mismatch from the backtest's own forward-fill, not
    /// a harmless approximation of it).
    ///
    /// Band membership (Batch 3 fix, fourth pass) uses the SAME signed INDEX offset
    /// (<paramref name="strikeOffsets"/>, from <see cref="ComputeStrikeOffsets"/>) DepthImbalance/
    /// ItmSkew already use, matching CoreScoreOptionSimulator.cs:346's own
    /// <c>Math.Abs(r.Offset) &lt;= GammaBandOffset</c> exactly -- NOT "nearest 21 strikes by raw
    /// price distance from spot" (an earlier version of this method), which coincides with the
    /// index-based band for the narrow Itm2Atm1 strikes DepthImbalance/ItmSkew use (always
    /// liquid, rarely gapped) but can silently diverge from it at the wide +/-10 band's own edges
    /// whenever the tracked strike chain has ANY gap (a far, illiquid strike simply never
    /// resolved as an Instrument) -- caught by CoreScoreReplayDiff as a persistent, moderate
    /// GammaExposureRaw mismatch (backtest and live including a slightly different strike set at
    /// the band's edge) that neither the Gamma-freeze fix nor the OI-holdover fix touched.
    /// </summary>
    double? ComputeCoreGammaExposureRaw(Dictionary<decimal, int> strikeOffsets, decimal underlying, double t)
    {
        double net = 0;
        var any = false;

        foreach (var option in _nearestExpiryOptions)
        {
            if (!strikeOffsets.TryGetValue(option.StrikePrice!.Value, out var offset) || Math.Abs(offset) > CoreGammaBandOffset)
            {
                continue;
            }

            if (!_coreLastOpenInterestByToken.TryGetValue(option.Token, out var oi) || oi <= 0)
            {
                continue;
            }

            if (_coreCadenceMidPriceByToken.GetValueOrDefault(option.Token) is { } freshMid && freshMid > 0)
            {
                var iv = ImpliedVolatilitySolver.Solve(option.OptionType, (double)freshMid, (double)underlying, (double)option.StrikePrice!.Value, t, RiskFreeRate);
                if (iv is { } ivValue)
                {
                    _coreLastGammaByToken[option.Token] = BlackScholes.Calculate(option.OptionType, (double)underlying, (double)option.StrikePrice!.Value, t, RiskFreeRate, ivValue).Greeks.Gamma;
                }
            }

            if (!_coreLastGammaByToken.TryGetValue(option.Token, out var gamma))
            {
                continue;
            }

            net += option.OptionType == OptionType.Call ? gamma * oi : -(gamma * oi);
            any = true;
        }

        return any ? net : null;
    }

    /// <summary>
    /// TrendReversion15m (signed net future-price change / path length over a trailing real 15
    /// minutes, negated) and BasisChange (futureChange - spotChange per cadence) share the same
    /// per-cadence future/spot price trackers -- computed together since BasisChange needs
    /// TrendReversion15m's own futureChange as one of its two terms. Matches
    /// CoreScoreOptionSimulator.cs:319-346 exactly, including TrendReversion15m's negation ("a
    /// clean recent trend reads as a REVERSION signal, not continuation").
    ///
    /// Batch 3 fix (2026-09-13): futureChange/spotChange are THIS cadence's own LTP open-to-close
    /// (<see cref="_coreFutureCadenceOpen"/>/<see cref="_coreFutureCadenceClose"/> and their spot
    /// counterparts), matching CadenceContext.FutureChangeFromLastCadence/
    /// SpotChangeFromLastCadence's actual definition (a within-cadence candle body, despite the
    /// "FromLastCadence" name) exactly. The original implementation tracked a cross-cadence
    /// previous-mid-to-current-mid diff instead -- a genuinely different quantity that
    /// CoreScoreReplayDiff caught as a near-100% mismatch on both terms. Null whenever this
    /// specific cadence had no future tick (or, for BasisChange, no spot tick either) -- NOT held
    /// over from an earlier cadence, matching InstrumentPriceState's own per-cadence reset.
    /// </summary>
    (double? TrendReversion15m, double? BasisChange) ComputeCoreTrendAndBasis(DateTimeOffset now)
    {
        double? trendReversion = null;

        double? futureChange = _coreFutureCadenceClose is { } futureClose && _coreFutureCadenceOpen is { } futureOpen
            ? (double)(futureClose - futureOpen)
            : null;
        double? spotChange = _coreSpotCadenceClose is { } spotClose && _coreSpotCadenceOpen is { } spotOpen
            ? (double)(spotClose - spotOpen)
            : null;

        // Enqueue conditionally (only a real futureChange this cadence contributes), but eviction
        // AND the net/pathLength read run UNCONDITIONALLY every cadence -- matching
        // CoreScoreOptionSimulator.cs:341-353 exactly. A quiet cadence for the future (no fresh
        // tick) must still read whatever the window already holds from earlier cadences, not skip
        // the read entirely -- an earlier version of this fix wrapped the whole eviction+read step
        // inside the Enqueue's own `if`, silently going null on every quiet cadence even with
        // plenty of still-fresh history in the window, caught by CoreScoreReplayDiff as a
        // null-mismatch (backtest real value, live null) clustered on quiet-future cadences.
        if (futureChange is { } change)
        {
            _coreTrendReversionWindow.Enqueue((now, change));
        }

        while (_coreTrendReversionWindow.Count > 0 && now - _coreTrendReversionWindow.Peek().Timestamp > CoreTrendReversionWindow)
        {
            _coreTrendReversionWindow.Dequeue();
        }

        if (_coreTrendReversionWindow.Count > 0)
        {
            var net = _coreTrendReversionWindow.Sum(w => w.Change);
            var pathLength = _coreTrendReversionWindow.Sum(w => Math.Abs(w.Change));
            if (pathLength > 0)
            {
                trendReversion = -1.0 * (net / pathLength);
            }
        }

        // 2026-09-17 fix: recombine from the ORIGINAL DECIMAL opens/closes in decimal arithmetic,
        // ONE cast to double at the end -- matching CoreScoreOptionSimulator.cs's own
        // `(double)(fChg - sChg)` order-of-operations exactly, where fChg/sChg are themselves
        // already-decimal per-cadence changes (CadenceContext.FutureChangeFromLastCadence/
        // SpotChangeFromLastCadence). The previous version combined `futureChange`/`spotChange`
        // AFTER each had already been independently cast to double above -- two independent
        // decimal-to-binary roundings then a double subtraction, instead of one decimal subtraction
        // then a single rounding. CoreScoreReplayDiff found the resulting BasisChangeRaw noise too
        // small to trip its own 1e-6 epsilon (0 raw mismatches reported) -- but SessionRankTracker's
        // Rank() compares each new value against every prior value added this session via a strict
        // `<=`, and BasisChange's own distribution (derived from tick-granular decimal prices) is
        // dense with near-exact ties; once one historical entry lands a few ULPs off from where
        // backtest's tracker put it, every later Rank() call that falls near that entry can disagree
        // -- which is how a difference too small to see in BasisChangeRaw cascaded into
        // BasisChangeSigned mismatching on 54% of all cadences (4016 of 7385, see the 2026-09-17
        // CoreScoreReplayDiff run). `futureChange`/`spotChange` above are left untouched -- they
        // still feed TrendReversion15m, which already matched exactly.
        double? basisChange = _coreFutureCadenceClose is { } fc && _coreFutureCadenceOpen is { } fo
            && _coreSpotCadenceClose is { } sc && _coreSpotCadenceOpen is { } so
            ? (double)((fc - fo) - (sc - so))
            : null;

        return (trendReversion, basisChange);
    }

    /// <summary>
    /// Rolling 15-real-minute SUM of (CallOiDelta-PutOiDelta), ATM+/-PersistedStrikeBand -- each
    /// cadence's own increment is a NAIVE previous-cadence-to-this-cadence OI diff read from
    /// _previousCadence (must be called BEFORE ComputeCadence overwrites it at the end), NOT
    /// _oiLookback's ~4-minute comparison ComputeOiBuildupNet uses specifically to dodge OI's slow
    /// real-world refresh rate -- feeding that 4-minute-lookback delta into a 15-minute rolling SUM
    /// here would overlap successive readings and badly over-count flow. Matches
    /// CoreScoreOptionSimulator.cs:348-361's band and naive-diff construction exactly. Null only
    /// on the very first cadence (no _previousCadence at all yet) -- after that, always a real
    /// number (0 contributions from a strike with no comparable previous OI, never skipped/null),
    /// same as the backtest's own unconditional Sum().
    ///
    /// 2026-09-17 fix: band membership now uses the SAME signed INDEX offset
    /// (<paramref name="strikeOffsets"/>, from <see cref="ComputeStrikeOffsets"/>) every other
    /// Core-score strike band already uses, matching CoreScoreOptionSimulator.cs's own
    /// <c>Math.Abs(r.Offset) &lt;= OiDiffBandOffset</c> exactly. The previous version selected
    /// "nearest (PersistedStrikeBand*2)+1 strikes by raw price distance from spot" -- the same
    /// "earlier version" pattern already found and fixed for GammaExposureRaw (see that method's
    /// own doc comment): coincides with the index-based band whenever the tracked strike chain has
    /// no gaps near the money, but is a structurally different selection whenever it does. Every
    /// sibling Core-score band method already carries a doc comment cross-referencing the backtest
    /// line it matches; this one never got migrated during that pass.
    /// </summary>
    double? ComputeCoreOiChangeDiffRaw(DateTimeOffset now, Dictionary<decimal, int> strikeOffsets)
    {
        if (_previousCadence is null)
        {
            return null;
        }

        long callDelta = 0, putDelta = 0;

        foreach (var option in _nearestExpiryOptions)
        {
            if (!strikeOffsets.TryGetValue(option.StrikePrice!.Value, out var offset) || Math.Abs(offset) > PersistedStrikeBand)
            {
                continue;
            }

            if (!_latest.TryGetValue(option.Token, out var curr) || curr.OpenInterest is not { } currOi)
            {
                continue;
            }

            if (!_previousCadence.TryGetValue(option.Token, out var prev) || prev.OpenInterest is not { } prevOi)
            {
                continue;
            }

            var delta = currOi - prevOi;
            if (option.OptionType == OptionType.Call)
            {
                callDelta += delta;
            }
            else if (option.OptionType == OptionType.Put)
            {
                putDelta += delta;
            }
        }

        _coreOiChangeDiffWindow.Enqueue((now, callDelta, putDelta));
        while (_coreOiChangeDiffWindow.Count > 0 && now - _coreOiChangeDiffWindow.Peek().Timestamp > CoreOiChangeDiffWindow)
        {
            _coreOiChangeDiffWindow.Dequeue();
        }

        return _coreOiChangeDiffWindow.Sum(w => (double)(w.CallDelta - w.PutDelta));
    }

    /// <summary>
    /// Ranks |raw| against this metric's own session-so-far distribution of magnitudes,
    /// reattaches raw's sign -- ported unchanged from CoreScoreOptionSimulator.RankSigned. Reads
    /// the rank BEFORE adding (self-inclusion-safe, no same-bar leakage) -- the one correctness
    /// rule this whole project has been strictest about.
    /// </summary>
    static double? RankSigned(double? raw, SessionRankTracker rank)
    {
        if (raw is not { } value || value == 0)
        {
            return raw is 0 ? 0.0 : null;
        }

        var signed = Math.Sign(value) * (rank.Rank(Math.Abs(value)) / 100.0);
        rank.Add(Math.Abs(value));
        return signed;
    }

    // ================== end Core score ==================

    // Plan 4.2: mid of bid/ask when the depth snapshot is available, LTP fallback otherwise.
    static decimal? MidPrice(InstrumentState state)
    {
        if (state.Depth is { } depth && depth.Bid1Price > 0 && depth.Ask1Price > 0)
        {
            return (depth.Bid1Price + depth.Ask1Price) / 2;
        }

        return state.LastPrice > 0 ? state.LastPrice : null;
    }

    /// <summary>Accumulates a mean across null-tolerant samples (2026-09-04), reset each cadence tick -- <see cref="Add"/> silently skips null values instead of counting them.</summary>
    sealed class RunningAverage
    {
        double _sum;
        int _count;

        public void Add(double? value)
        {
            if (value is { } v)
            {
                _sum += v;
                _count++;
            }
        }

        public double? Average => _count > 0 ? _sum / _count : null;

        public void Reset()
        {
            _sum = 0;
            _count = 0;
        }
    }
}
