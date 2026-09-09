using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;
using NiftySignal.Pricing;
using NiftySignal.Scoring;

namespace NiftySignal.Host;

readonly record struct InstrumentState(decimal LastPrice, long Volume, long? OpenInterest, MarketDepth? Depth);

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
/// </summary>
public sealed class LiveFeatureEngine
{
    // 91-day T-bill proxy (plan 4.1: "static config value, reviewed weekly") -- hardcoded
    // starting point since no config surface exists yet for it.
    // PENDING (audit finding F21, 2026-09-08 lead review -- see fix plan Batch 6): duplicated
    // identically in NiftySignal.Dashboard/Services/LiveDataService.cs. Move both to config
    // (Pricing: { RiskFreeRate: 0.065 }) so a rate change can't land in one copy and not the other.
    const double RiskFreeRate = 0.065;

    /// <summary>
    /// Strikes each side of ATM whose per-cadence analytics get persisted (2026-09-05) --
    /// so ATM +/- 2, i.e. 5 strikes x 2 sides = ~10 rows a cadence. The full tracked chain is
    /// ATM +/- 10; persisting Greeks for all of it every 15s would be ~600k values a day,
    /// mostly for far strikes that barely trade. Display is unaffected -- the option chain and
    /// OI profile still show the full range.
    /// </summary>
    const int PersistedStrikeBand = 2;

    // 3 minutes at the 15s cadence (started at 5 min on 2026-09-07, chosen for a deliberately
    // low-frequency, 1-5-trades-a-day strategy; shortened the same day after watching the
    // first live session -- still meaningfully smoothed versus no averaging at all, just less
    // lag between a real move and the score reflecting it).
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
    readonly WelfordRollingWindow _vannaExposureWindow = new(FeatureWindowLengths.VannaExposure);
    readonly WelfordRollingWindow _charmExposureWindow = new(FeatureWindowLengths.CharmExposure);
    readonly WelfordRollingWindow _cvdProxyWindow = new(FeatureWindowLengths.CvdProxy);
    readonly WelfordRollingWindow _straddleRichnessWindow = new(FeatureWindowLengths.StraddleRichness);

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

    public LiveFeatureEngine(IReadOnlyList<Instrument> instruments)
    {
        _instruments = instruments;
        _spot = instruments.First(i => i.InstrumentType == InstrumentType.Index);
        _future = instruments.First(i => i.InstrumentType == InstrumentType.Future);
        _vix = instruments.FirstOrDefault(i => i.InstrumentType == InstrumentType.Vix);
        _nearestExpiry = instruments
            .Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate is not null)
            .Select(i => i.ExpiryDate!.Value)
            .Min();
        _nearestExpiryOptions = [.. instruments.Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate == _nearestExpiry)];
    }

    public void OnTick(Tick tick)
    {
        _latest[tick.Token] = new InstrumentState(tick.LastPrice, tick.Volume, tick.OpenInterest, tick.Depth);
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
                // PENDING (audit finding F19, 2026-09-08 lead review -- see fix plan Batch 6):
                // StrikeSelector.SelectBestCandidate's .ThenByDescending(c => c.Volume) tie-break
                // therefore always compares 0 to 0 -- a no-op presented as a real rank step. Wire
                // real per-instrument volume delta here, or delete that dead ThenByDescending.
                Volume: 0, // not tracked per-instrument yet -- see class doc comment's other scoped-down v1s
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
        _basisWindow.Add(now, basisRaw);

        // Same non-null guarantee as basisRaw above -- computed unconditionally alongside it
        // in Sample(). No window/Z: weight-0 diagnostic, see ScoreSnapshot.ParityGapRaw.
        var parityGapRaw = _parityGapSamples.Average!.Value;

        var momentumRaw = _momentumSamples.Average!.Value;
        _momentumWindow.Add(now, momentumRaw);

        var pcrRaw = _pcrSamples.Average;
        if (pcrRaw is { } pcr)
        {
            _pcrWindow.Add(now, pcr);
        }

        // Not smoothed like the other five -- see class doc comment. Compares state at this
        // cadence tick to state at the last one. Null (not a fabricated delta) when the "last
        // cadence" is stale by more than one normal interval -- see ComputeOiBuildupNet.
        var oiBuildupRaw = ComputeOiBuildupNet(now, spot.LastPrice);
        if (oiBuildupRaw is { } oiBuildup)
        {
            _oiBuildupWindow.Add(now, oiBuildup);
        }

        var depthImbalanceRaw = _depthImbalanceSamples.Average;
        if (depthImbalanceRaw is { } di)
        {
            _depthImbalanceWindow.Add(now, di);
        }

        var ivSkewRaw = _ivSkewSamples.Average;
        if (ivSkewRaw is { } skew)
        {
            _ivSkewWindow.Add(now, skew);
        }

        // Reads _spreadPctOfMidSamplesByToken, which BuildStrikeSnapshots clears later this
        // same cadence -- must run before that, which ResetSamples() below doesn't touch
        // anyway (see SampleSpreads' own doc comment on why it's independent of ResetSamples).
        var spreadRatioRaw = ComputeSpreadRatio();
        if (spreadRatioRaw is { } sr)
        {
            _spreadRatioWindow.Add(now, sr);
        }

        ResetSamples();

        // Not smoothed like the other five -- see class doc comment.
        var vixChangeRaw = ComputeVixChange(now);
        if (vixChangeRaw is { } vc)
        {
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

        if (gammaExposureRaw is { } gex)
        {
            _gammaExposureWindow.Add(now, gex);
        }

        if (vannaExposureRaw is { } vanna)
        {
            _vannaExposureWindow.Add(now, vanna);
        }

        if (charmExposureRaw is { } charm)
        {
            _charmExposureWindow.Add(now, charm);
        }

        // Not smoothed either -- see ComputeVolumePcrAndCvdProxy's own doc comment.
        var (volumePcrRaw, cvdProxyRaw) = ComputeVolumePcrAndCvdProxy();
        if (volumePcrRaw is { } vpcr)
        {
            _volumePcrWindow.Add(now, vpcr);
        }

        if (cvdProxyRaw is { } cvd)
        {
            _cvdProxyWindow.Add(now, cvd);
        }

        if (straddleRichnessRaw is { } richness)
        {
            _straddleRichnessWindow.Add(now, richness);
        }

        var inputs = new ScoreComponentInputs(
            OiBuildupNetZ: oiBuildupRaw is { } oi ? _oiBuildupWindow.ComputeZScore(oi) : null,
            PcrZ: pcrRaw is { } p ? _pcrWindow.ComputeZScore(p) : null,
            FuturesBasisZ: _basisWindow.ComputeZScore(basisRaw),
            IvSkewZ: ivSkewRaw is { } iv ? _ivSkewWindow.ComputeZScore(iv) : null,
            PriceMomentumZ: _momentumWindow.ComputeZScore(momentumRaw),
            DepthImbalanceZ: depthImbalanceRaw is { } d ? _depthImbalanceWindow.ComputeZScore(d) : null,
            VixChangeZ: vixChangeRaw is { } vcr ? _vixWindow.ComputeZScore(vcr) : null,
            GammaExposureZ: gammaExposureRaw is { } gexr ? _gammaExposureWindow.ComputeZScore(gexr) : null,
            VolumePcrZ: volumePcrRaw is { } vpcrr ? _volumePcrWindow.ComputeZScore(vpcrr) : null,
            SpreadRatioZ: spreadRatioRaw is { } srr ? _spreadRatioWindow.ComputeZScore(srr) : null,
            VannaExposureZ: vannaExposureRaw is { } vannar ? _vannaExposureWindow.ComputeZScore(vannar) : null,
            CharmExposureZ: charmExposureRaw is { } charmr ? _charmExposureWindow.ComputeZScore(charmr) : null,
            CvdProxyZ: cvdProxyRaw is { } cvdr ? _cvdProxyWindow.ComputeZScore(cvdr) : null,
            StraddleRichnessZ: straddleRichnessRaw is { } richr ? _straddleRichnessWindow.ComputeZScore(richr) : null);

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
        var compositeRawInstant = CompositeScoreCalculator.ComputeRaw(inputs, ScoreWeights.Default);
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
            inputs, ScoreWeights.Default, now, CompositeScoreCalculator.DefaultK, compositeRawSmoothed);

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
            if (!_latest.TryGetValue(opt.Token, out var curr) || !_previousCadence.TryGetValue(opt.Token, out var prev))
            {
                continue;
            }

            var oiChange = (curr.OpenInterest ?? 0) - (prev.OpenInterest ?? 0);
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
        var atmLegs = _nearestExpiryOptions
            .Where(o => o.StrikePrice == atmStrike)
            .OrderBy(o => o.OptionType == OptionType.Call ? 0 : 1);

        foreach (var leg in atmLegs)
        {
            if (!_latest.TryGetValue(leg.Token, out var state))
            {
                continue;
            }

            var mark = MidPrice(state);
            if (mark is not { } markPrice || markPrice <= 0)
            {
                continue;
            }

            var solved = ImpliedVolatilitySolver.Solve(leg.OptionType, (double)markPrice, (double)underlyingPrice, (double)atmStrike, t, RiskFreeRate);
            if (solved is { } vol)
            {
                return vol;
            }
        }

        return null;
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
