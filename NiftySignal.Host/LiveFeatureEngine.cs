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
/// - Dynamic k: composite scoring used a hardcoded k, calibrated by eyeballing one trending
///   session's data -- directionally unbiased (tanh is odd, so no k value favors bulls or
///   bears) but not generalizable across volatility regimes. k is now the rolling stddev of
///   the raw (pre-tanh) composite itself, via the same WelfordRollingWindow machinery
///   already used to z-score the six inputs -- it shrinks on quiet days and grows on
///   volatile ones instead of needing a human to re-tune it from a snapshot of one session.
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
/// </summary>
public sealed class LiveFeatureEngine
{
    // 91-day T-bill proxy (plan 4.1: "static config value, reviewed weekly") -- hardcoded
    // starting point since no config surface exists yet for it.
    const double RiskFreeRate = 0.065;
    const decimal IvSkewStrikeOffset = 200m;

    /// <summary>
    /// Strikes each side of ATM whose per-cadence analytics get persisted (2026-09-05) --
    /// so ATM +/- 2, i.e. 5 strikes x 2 sides = ~10 rows a cadence. The full tracked chain is
    /// ATM +/- 10; persisting Greeks for all of it every 15s would be ~600k values a day,
    /// mostly for far strikes that barely trade. Display is unaffected -- the option chain and
    /// OI profile still show the full range.
    /// </summary>
    const int PersistedStrikeBand = 2;

    // Matches the longest of the six per-metric windows (Pcr/OiBuildupNet/FuturesBasis) so
    // the dynamic-k window doesn't push composite warm-up out any further than the existing
    // six already require.
    static readonly TimeSpan CompositeRawWindowLength = TimeSpan.FromMinutes(30);

    readonly IReadOnlyList<Instrument> _instruments;
    readonly Instrument _spot;
    readonly Instrument _future;

    /// <summary>Null when the day's instrument universe doesn't track VIX -- optional by design, see class doc comment.</summary>
    readonly Instrument? _vix;

    readonly List<Instrument> _nearestExpiryOptions;
    readonly DateOnly _nearestExpiry;

    readonly Dictionary<string, InstrumentState> _latest = [];
    Dictionary<string, InstrumentState>? _previousCadence;

    readonly WelfordRollingWindow _oiBuildupWindow = new(FeatureWindowLengths.OiBuildupNet);
    readonly WelfordRollingWindow _pcrWindow = new(FeatureWindowLengths.Pcr);
    readonly WelfordRollingWindow _basisWindow = new(FeatureWindowLengths.FuturesBasis);
    readonly WelfordRollingWindow _ivSkewWindow = new(FeatureWindowLengths.IvSkew);
    readonly WelfordRollingWindow _momentumWindow = new(FeatureWindowLengths.PriceMomentum);
    readonly WelfordRollingWindow _depthImbalanceWindow = new(FeatureWindowLengths.DepthImbalance);
    readonly WelfordRollingWindow _compositeRawWindow = new(CompositeRawWindowLength);
    readonly WelfordRollingWindow _vixWindow = new(FeatureWindowLengths.VixChange);

    readonly RunningAverage _basisSamples = new();
    readonly RunningAverage _momentumSamples = new();
    readonly RunningAverage _pcrSamples = new();
    readonly RunningAverage _depthImbalanceSamples = new();
    readonly RunningAverage _ivSkewSamples = new();

    readonly Queue<(DateTimeOffset At, decimal FuturesPrice)> _momentumLookback = new();
    readonly Queue<(DateTimeOffset At, decimal Vix)> _vixLookback = new();

    /// <summary>
    /// Cumulative day volume per token as of the last <see cref="BuildStrikeSnapshots"/> pass.
    /// Kept separate from <see cref="_previousCadence"/> so strike-snapshot building has no
    /// ordering dependency on ComputeCadence -- either can run first, and neither resets the
    /// other's baseline. Only holds the persisted band's tokens, so it stays tiny.
    /// </summary>
    readonly Dictionary<string, long> _previousVolumeByToken = [];

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

            if (snapshot.IvSkewRaw is { } ivSkew)
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

            if (snapshot.CompositeScoreRaw is { } compositeRaw)
            {
                _compositeRawWindow.Add(snapshot.ComputedAt, compositeRaw);
            }

            if (snapshot.VixChangeRaw is { } vixChange)
            {
                _vixWindow.Add(snapshot.ComputedAt, vixChange);
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

    /// <summary>
    /// Live option-chain snapshot for strike selection, nearest-expiry + one side only (the
    /// side the entry direction calls for). Mid/bid/ask come from the top-of-book depth
    /// snapshot (null bid/ask if none arrived yet -- StrikeSelector already treats that as
    /// a filter failure, not a crash). Delta/IV use spot as the underlying, not the tracked
    /// future -- see <see cref="ComputeIvSkew"/>'s doc comment for why (2026-09-04 fix: NSE
    /// only lists monthly futures, which don't match a weekly option's own expiry).
    /// </summary>
    public List<Execution.StrikeCandidate> BuildStrikeCandidates(OptionType side, DateTimeOffset now)
    {
        var candidates = new List<Execution.StrikeCandidate>();
        if (!_latest.TryGetValue(_spot.Token, out var spot) || spot.LastPrice <= 0)
        {
            return candidates;
        }

        var t = TimeToExpiry.YearsUntilExpiry(_nearestExpiry, now);

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
                iv = ImpliedVolatilitySolver.Solve(side, (double)mid, (double)spot.LastPrice, (double)instrument.StrikePrice!.Value, t, RiskFreeRate);
                if (iv is { } ivValue)
                {
                    delta = BlackScholes.Calculate(side, (double)spot.LastPrice, (double)instrument.StrikePrice.Value, t, RiskFreeRate, ivValue).Greeks.Delta;
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

        _basisSamples.Add((double)(future.LastPrice - spot.LastPrice));
        _momentumSamples.Add(ComputeMomentum(now, future.LastPrice));
        _pcrSamples.Add(ComputePcr());
        _depthImbalanceSamples.Add(ComputeDepthImbalance(spot.LastPrice));
        _ivSkewSamples.Add(ComputeIvSkew(spot.LastPrice, now));
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

        var momentumRaw = _momentumSamples.Average!.Value;
        _momentumWindow.Add(now, momentumRaw);

        var pcrRaw = _pcrSamples.Average;
        if (pcrRaw is { } pcr)
        {
            _pcrWindow.Add(now, pcr);
        }

        // Not smoothed like the other five -- see class doc comment. Compares state at this
        // cadence tick to state at the last one (unchanged from before this change).
        var oiBuildupRaw = ComputeOiBuildupNet();
        _oiBuildupWindow.Add(now, oiBuildupRaw);

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

        ResetSamples();

        // Not smoothed like the other five -- see class doc comment.
        var vixChangeRaw = ComputeVixChange(now);
        if (vixChangeRaw is { } vc)
        {
            _vixWindow.Add(now, vc);
        }

        var inputs = new ScoreComponentInputs(
            OiBuildupNetZ: _oiBuildupWindow.ComputeZScore(oiBuildupRaw),
            PcrZ: pcrRaw is { } p ? _pcrWindow.ComputeZScore(p) : null,
            FuturesBasisZ: _basisWindow.ComputeZScore(basisRaw),
            IvSkewZ: ivSkewRaw is { } iv ? _ivSkewWindow.ComputeZScore(iv) : null,
            PriceMomentumZ: _momentumWindow.ComputeZScore(momentumRaw),
            DepthImbalanceZ: depthImbalanceRaw is { } d ? _depthImbalanceWindow.ComputeZScore(d) : null,
            VixChangeZ: vixChangeRaw is { } vcr ? _vixWindow.ComputeZScore(vcr) : null);

        // Dynamic k -- see class doc comment. Falls back to CompositeScoreCalculator.DefaultK
        // until the composite-raw window itself has 30 real minutes of history, same
        // "unreliable until warmed up" rule WelfordRollingWindow already applies to StdDev.
        var compositeRaw = CompositeScoreCalculator.ComputeRaw(inputs, ScoreWeights.Default);
        var k = CompositeScoreCalculator.DefaultK;
        if (compositeRaw is { } raw)
        {
            _compositeRawWindow.Add(now, raw);
            if (_compositeRawWindow.IsWarmedUp && _compositeRawWindow.StdDev >= 1e-12)
            {
                k = _compositeRawWindow.StdDev;
            }
        }

        var composite = CompositeScoreCalculator.Calculate(inputs, ScoreWeights.Default, now, k);

        _previousCadence = new Dictionary<string, InstrumentState>(_latest);

        return new ScoreSnapshot
        {
            ComputedAt = now,
            OiBuildupNetRaw = oiBuildupRaw,
            PcrRaw = pcrRaw,
            FuturesBasisRaw = basisRaw,
            IvSkewRaw = ivSkewRaw,
            PriceMomentumRaw = momentumRaw,
            DepthImbalanceRaw = depthImbalanceRaw,
            VixChangeRaw = vixChangeRaw,
            OiBuildupNetZ = inputs.OiBuildupNetZ,
            PcrZ = inputs.PcrZ,
            FuturesBasisZ = inputs.FuturesBasisZ,
            IvSkewZ = inputs.IvSkewZ,
            PriceMomentumZ = inputs.PriceMomentumZ,
            DepthImbalanceZ = inputs.DepthImbalanceZ,
            VixChangeZ = inputs.VixChangeZ,
            CompositeScoreRaw = compositeRaw,
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
        var bandStrikes = _nearestExpiryOptions
            .Select(o => o.StrikePrice!.Value)
            .Distinct()
            .OrderBy(s => Math.Abs(s - spot.LastPrice))
            .Take((PersistedStrikeBand * 2) + 1)
            .ToHashSet();

        var t = TimeToExpiry.YearsUntilExpiry(_nearestExpiry, now);

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

            var depth = state.Depth;
            var bid = depth?.Bid1Price is > 0 ? depth.Bid1Price : (decimal?)null;
            var ask = depth?.Ask1Price is > 0 ? depth.Ask1Price : (decimal?)null;

            decimal? spreadAbs = null, spreadPctOfMid = null;
            if (bid is { } b && ask is { } a)
            {
                spreadAbs = a - b;
                var mid = (a + b) / 2;
                spreadPctOfMid = mid > 0 ? spreadAbs / mid * 100m : null;
            }

            // Priced against spot, not the tracked (monthly) future -- see ComputeIvSkew's doc
            // comment for why. Greeks are only meaningful if the IV solve succeeded; a failed
            // solve leaves all six null rather than seeding an arbitrary volatility.
            var mark = MidPrice(state);
            double? iv = null, delta = null, gamma = null, thetaPerDay = null, vega = null, rho = null;
            if (mark is { } markPrice && markPrice > 0)
            {
                iv = ImpliedVolatilitySolver.Solve(
                    option.OptionType, (double)markPrice, (double)spot.LastPrice, (double)option.StrikePrice!.Value, t, RiskFreeRate);

                if (iv is { } ivValue)
                {
                    var greeks = BlackScholes.Calculate(
                        option.OptionType, (double)spot.LastPrice, (double)option.StrikePrice!.Value, t, RiskFreeRate, ivValue).Greeks;
                    delta = greeks.Delta;
                    gamma = greeks.Gamma;
                    thetaPerDay = greeks.ThetaPerDay;
                    vega = greeks.Vega;
                    rho = greeks.Rho;
                }
            }

            snapshots.Add(new StrikeSnapshot
            {
                ComputedAt = now,
                Token = option.Token,
                StrikePrice = option.StrikePrice!.Value,
                OptionType = option.OptionType,
                ExpiryDate = _nearestExpiry,
                VolumeDelta = volumeDelta,
                OpenInterest = state.OpenInterest,
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
            });
        }

        return snapshots;
    }

    void ResetSamples()
    {
        _basisSamples.Reset();
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

        return (double)(futuresPrice - _momentumLookback.Peek().FuturesPrice);
    }

    /// <summary>
    /// Null when the day's universe doesn't track VIX, or it hasn't ticked yet -- same
    /// optional-by-design tolerance as Pcr/DepthImbalance/IvSkew's own null cases.
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

    double? ComputePcr()
    {
        long callOi = 0, putOi = 0;
        foreach (var opt in _nearestExpiryOptions)
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

    double ComputeOiBuildupNet()
    {
        if (_previousCadence is null)
        {
            return 0;
        }

        double net = 0;
        foreach (var opt in _nearestExpiryOptions)
        {
            if (!_latest.TryGetValue(opt.Token, out var curr) || !_previousCadence.TryGetValue(opt.Token, out var prev))
            {
                continue;
            }

            var priceChange = curr.LastPrice - prev.LastPrice;
            var oiChange = (curr.OpenInterest ?? 0) - (prev.OpenInterest ?? 0);
            var classification = OiBuildupClassifier.Classify(priceChange, oiChange);

            // Standard NSE option-chain reading: call buildup/short-covering is bullish for
            // the underlying, put buildup/short-covering is bearish -- see class doc comment.
            net += (opt.OptionType, classification) switch
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
        }

        return net;
    }

    double? ComputeDepthImbalance(decimal spotPrice)
    {
        var ntmStrikes = _nearestExpiryOptions
            .Select(o => o.StrikePrice!.Value)
            .Distinct()
            .OrderBy(s => Math.Abs(s - spotPrice))
            .Take(2)
            .ToHashSet();

        long totalBid = 0, totalAsk = 0;
        foreach (var opt in _nearestExpiryOptions.Where(o => ntmStrikes.Contains(o.StrikePrice!.Value)))
        {
            if (_latest.TryGetValue(opt.Token, out var state) && state.Depth is { } depth)
            {
                totalBid += depth.TotalBidQty;
                totalAsk += depth.TotalAskQty;
            }
        }

        var totalDepth = totalBid + totalAsk;
        // Normalized imbalance, not a raw bid/ask ratio: (bid-ask)/(bid+ask) ranges [-1,+1]
        // and is centered at 0 (balanced). A plain ratio is a real bug fixed here
        // (2026-09-04, live-caught) -- it can never be negative by construction (two
        // non-negative quantities divided), yet both this feature's z-score and the
        // option-chain grid display it as a signed bull/bear value. The window's z-score
        // was still statistically valid against the old formula, just not measuring
        // "which side has more pressure" the way "imbalance" implies.
        return totalDepth > 0 ? (double)(totalBid - totalAsk) / totalDepth : null;
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
        var targetCallStrike = spotPrice + IvSkewStrikeOffset;
        var targetPutStrike = spotPrice - IvSkewStrikeOffset;

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

        var t = TimeToExpiry.YearsUntilExpiry(_nearestExpiry, now);
        var callIv = ImpliedVolatilitySolver.Solve(OptionType.Call, (double)cm, (double)spotPrice, (double)callOpt.StrikePrice!.Value, t, RiskFreeRate);
        var putIv = ImpliedVolatilitySolver.Solve(OptionType.Put, (double)pm, (double)spotPrice, (double)putOpt.StrikePrice!.Value, t, RiskFreeRate);

        return callIv is null || putIv is null ? null : putIv - callIv;
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
