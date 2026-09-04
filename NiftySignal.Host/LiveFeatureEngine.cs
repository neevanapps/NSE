using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;
using NiftySignal.Pricing;
using NiftySignal.Scoring;

namespace NiftySignal.Host;

readonly record struct InstrumentState(decimal LastPrice, long? OpenInterest, MarketDepth? Depth);

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
///   strike offset" (plan 5.3's own wording, offset left unspecified).
/// - DepthImbalance's "NTM strikes" (plan 5.3) = the 2 strikes nearest spot, nearest expiry.
///
/// Not thread-safe on its own -- <see cref="OnTick"/> and <see cref="ComputeCadence"/> are
/// called from different loops in the same worker and must be externally synchronized.
/// </summary>
public sealed class LiveFeatureEngine
{
    // 91-day T-bill proxy (plan 4.1: "static config value, reviewed weekly") -- hardcoded
    // starting point since no config surface exists yet for it.
    const double RiskFreeRate = 0.065;
    const decimal IvSkewStrikeOffset = 200m;

    readonly IReadOnlyList<Instrument> _instruments;
    readonly Instrument _spot;
    readonly Instrument _future;
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

    readonly Queue<(DateTimeOffset At, decimal FuturesPrice)> _momentumLookback = new();

    public LiveFeatureEngine(IReadOnlyList<Instrument> instruments)
    {
        _instruments = instruments;
        _spot = instruments.First(i => i.InstrumentType == InstrumentType.Index);
        _future = instruments.First(i => i.InstrumentType == InstrumentType.Future);
        _nearestExpiry = instruments
            .Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate is not null)
            .Select(i => i.ExpiryDate!.Value)
            .Min();
        _nearestExpiryOptions = [.. instruments.Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate == _nearestExpiry)];
    }

    public void OnTick(Tick tick)
    {
        _latest[tick.Token] = new InstrumentState(tick.LastPrice, tick.OpenInterest, tick.Depth);
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
    /// prior tick, needed for OiBuildupNet's own delta calc) and the momentum lookback
    /// queue's raw futures-price history -- those reset to "no prior observation" on
    /// restart regardless. That costs one cadence tick's OiBuildupNet reading (reported as
    /// 0 instead of the true delta) and one tick where momentum falls back to 0 -- a single
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
    /// a filter failure, not a crash). Delta/IV reuse the same futures-as-underlying
    /// Black-Scholes convention as <see cref="ComputeIvSkew"/>.
    /// </summary>
    public List<Execution.StrikeCandidate> BuildStrikeCandidates(OptionType side, DateTimeOffset now)
    {
        var candidates = new List<Execution.StrikeCandidate>();
        if (!_latest.TryGetValue(_future.Token, out var future) || future.LastPrice <= 0)
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
                iv = ImpliedVolatilitySolver.Solve(side, (double)mid, (double)future.LastPrice, (double)instrument.StrikePrice!.Value, t, RiskFreeRate);
                if (iv is { } ivValue)
                {
                    delta = BlackScholes.Calculate(side, (double)future.LastPrice, (double)instrument.StrikePrice.Value, t, RiskFreeRate, ivValue).Greeks.Delta;
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

    /// <summary>Null until the spot and future have at least one tick each.</summary>
    public ScoreSnapshot? ComputeCadence(DateTimeOffset now)
    {
        if (!_latest.TryGetValue(_spot.Token, out var spot) || !_latest.TryGetValue(_future.Token, out var future)
            || spot.LastPrice <= 0 || future.LastPrice <= 0)
        {
            return null;
        }

        var basisRaw = (double)(future.LastPrice - spot.LastPrice);
        _basisWindow.Add(now, basisRaw);

        var momentumRaw = ComputeMomentum(now, future.LastPrice);
        _momentumWindow.Add(now, momentumRaw);

        var pcrRaw = ComputePcr();
        if (pcrRaw is { } pcr)
        {
            _pcrWindow.Add(now, pcr);
        }

        var oiBuildupRaw = ComputeOiBuildupNet();
        _oiBuildupWindow.Add(now, oiBuildupRaw);

        var depthImbalanceRaw = ComputeDepthImbalance(spot.LastPrice);
        if (depthImbalanceRaw is { } di)
        {
            _depthImbalanceWindow.Add(now, di);
        }

        var ivSkewRaw = ComputeIvSkew(spot.LastPrice, future.LastPrice, now);
        if (ivSkewRaw is { } skew)
        {
            _ivSkewWindow.Add(now, skew);
        }

        var inputs = new ScoreComponentInputs(
            OiBuildupNetZ: _oiBuildupWindow.ComputeZScore(oiBuildupRaw),
            PcrZ: pcrRaw is { } p ? _pcrWindow.ComputeZScore(p) : null,
            FuturesBasisZ: _basisWindow.ComputeZScore(basisRaw),
            IvSkewZ: ivSkewRaw is { } iv ? _ivSkewWindow.ComputeZScore(iv) : null,
            PriceMomentumZ: _momentumWindow.ComputeZScore(momentumRaw),
            DepthImbalanceZ: depthImbalanceRaw is { } d ? _depthImbalanceWindow.ComputeZScore(d) : null);

        var composite = CompositeScoreCalculator.Calculate(inputs, ScoreWeights.Default, now);

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
            OiBuildupNetZ = inputs.OiBuildupNetZ,
            PcrZ = inputs.PcrZ,
            FuturesBasisZ = inputs.FuturesBasisZ,
            IvSkewZ = inputs.IvSkewZ,
            PriceMomentumZ = inputs.PriceMomentumZ,
            DepthImbalanceZ = inputs.DepthImbalanceZ,
            CompositeScore = composite.Score,
            IsWarmedUp = composite.IsWarmedUp,
            WeightSetVersion = composite.WeightSetVersion,
        };
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

        return totalAsk > 0 ? (double)totalBid / totalAsk : null;
    }

    double? ComputeIvSkew(decimal spotPrice, decimal futuresPrice, DateTimeOffset now)
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
        var callIv = ImpliedVolatilitySolver.Solve(OptionType.Call, (double)cm, (double)futuresPrice, (double)callOpt.StrikePrice!.Value, t, RiskFreeRate);
        var putIv = ImpliedVolatilitySolver.Solve(OptionType.Put, (double)pm, (double)futuresPrice, (double)putOpt.StrikePrice!.Value, t, RiskFreeRate);

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
}
