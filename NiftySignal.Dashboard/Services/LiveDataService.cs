using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Persistence;
using NiftySignal.Pricing;
using NiftySignal.Scoring;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Polls <see cref="NiftySignalDbContext"/> for the dashboard -- successor to the earlier
/// DemoDataService random-walk simulation, kept to the same public shape on purpose (one
/// singleton, one <see cref="Updated"/> event, same view-model records) so panels only had
/// to change their injected service, not their markup.
///
/// Two separate timers, not one: <see cref="Updated"/> (5s) covers the score, data health,
/// and the full option chain table (IV solving + a 30-min OI lookback per row -- genuinely
/// not cheap over ~80 instruments). <see cref="QuoteUpdated"/> (1s) covers only what the
/// Live Quote panel shows -- spot/future LTP and the selected CE/PE legs' LTP/bid/ask --
/// which is cheap enough to poll every second. They used to share one interval; tightening
/// it to 1s for a snappier quote panel silently spun up the whole dashboard (full chain
/// table, chart re-renders) to 1s too, which wasn't the ask -- live-corrected 2026-09-04.
///
/// Positions/ClosedTrades are genuinely empty, not faked -- the rule engine and paper-trade
/// simulator aren't wired into the live pipeline yet (only ingestion + feature/score
/// computation are, as of 2026-09-04), so there really are no trades to show yet.
/// </summary>
public sealed class LiveDataService : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan QuotePollInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    readonly IDbContextFactory<NiftySignalDbContext> _dbFactory;
    readonly FlatTradeAuthClient _authClient;
    readonly ILogger<LiveDataService> _logger;
    readonly Timer _timer;
    readonly Timer _quoteTimer;
    readonly Lock _lock = new();

    double _currentScore;
    List<ScoreHistoryPoint> _scoreHistory = [];
    List<OptionChainRow> _optionChain = [];
    Dictionary<(decimal Strike, OptionType Type), QuickQuote> _quickQuotes = [];
    DateTimeOffset? _firstSnapshotAt;

    public event Action? Updated;

    /// <summary>Fires every ~1s -- subscribe to this (not <see cref="Updated"/>) for anything that needs to feel live tick-by-tick, e.g. the Live Quote panel.</summary>
    public event Action? QuoteUpdated;

    public double CurrentScore { get { lock (_lock) return _currentScore; } }

    public IReadOnlyList<ScoreHistoryPoint> ScoreHistory { get { lock (_lock) return _scoreHistory; } }

    public IReadOnlyList<ScoreComponentRow> ScoreComponents { get; private set; } = [];

    public IReadOnlyList<OptionChainRow> OptionChain { get { lock (_lock) return _optionChain; } }

    public IReadOnlyDictionary<(decimal Strike, OptionType Type), QuickQuote> QuickQuotes { get { lock (_lock) return _quickQuotes; } }

    // No live paper-trade engine wired yet -- genuinely empty, not simulated.
    public IReadOnlyList<PositionRow> Positions => [];

    public IReadOnlyList<ClosedTradeRow> ClosedTrades => [];

    public IReadOnlyList<MetricWarmUpStatus> WarmUpStatuses { get; private set; } = BuildDefaultWarmUpStatuses();

    public ConnectionStatus Connection { get; private set; } = ConnectionStatus.Disconnected;

    public DateTimeOffset LastTickAt { get; private set; } = DateTimeOffset.MinValue;

    public decimal? SpotLtp { get; private set; }

    public decimal? FutureLtp { get; private set; }

    public decimal? SpotChange { get; private set; }

    public decimal? FutureChange { get; private set; }

    public LiveDataService(IDbContextFactory<NiftySignalDbContext> dbFactory, FlatTradeAuthClient authClient, ILogger<LiveDataService> logger)
    {
        _dbFactory = dbFactory;
        _authClient = authClient;
        _logger = logger;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        _quoteTimer = new Timer(_ => QuotePoll(), null, TimeSpan.Zero, QuotePollInterval);
    }

    void Poll() => _ = PollAsync();

    void QuotePoll() => _ = QuotePollAsync();

    /// <summary>
    /// Resolves an arbitrary (strike, side) into a real instrument via a live FlatTrade
    /// call and inserts it (Subscribed=false) for the Host's poll loop to pick up -- the
    /// dashboard's "watch this strike even though it's outside the tracked ATM band" path.
    /// Returns false if already live, the session's stale, or FlatTrade has no such strike.
    /// </summary>
    public async Task<bool> RequestSubscriptionAsync(decimal strike, OptionType side)
    {
        if (QuickQuotes.ContainsKey((strike, side)))
        {
            return true; // already subscribed and showing live data
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.FlatTradeSessions.FindAsync(FlatTradeSession.SingletonId);
        if (session?.Token is null || !session.IsValidAt(DateTimeOffset.UtcNow))
        {
            return false;
        }

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).Date);
        var anchor = await db.Instruments
            .Where(i => i.AsOfDate == today && i.InstrumentType == InstrumentType.Option)
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefaultAsync();
        if (anchor is null)
        {
            return false;
        }

        IReadOnlyList<OptionChainEntry> chain;
        try
        {
            chain = await _authClient.GetOptionChainAsync(session.Token, Exchange.Nfo, anchor.TradingSymbol, strike, strikeCount: 2, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "On-demand strike resolution failed for {Strike} {Side}", strike, side);
            return false;
        }

        var match = chain.FirstOrDefault(c => c.StrikePrice == strike && c.OptionType == side);
        if (match is null)
        {
            return false;
        }

        var exists = await db.Instruments.AnyAsync(i => i.Token == match.Token && i.AsOfDate == today);
        if (!exists)
        {
            db.Instruments.Add(new Instrument
            {
                Token = match.Token,
                Exchange = match.Exchange,
                TradingSymbol = match.TradingSymbol,
                InstrumentType = InstrumentType.Option,
                OptionType = match.OptionType,
                StrikePrice = match.StrikePrice,
                ExpiryDate = anchor.ExpiryDate,
                Underlying = anchor.Underlying,
                LotSize = match.LotSize,
                TickSize = match.TickSize,
                AsOfDate = today,
                Subscribed = false,
            });
            await db.SaveChangesAsync();
        }

        return true;
    }

    async Task PollAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var latestTick = await db.Ticks.OrderByDescending(t => t.ReceivedAt).Select(t => (DateTimeOffset?)t.ReceivedAt).FirstOrDefaultAsync();
            LastTickAt = latestTick ?? DateTimeOffset.MinValue;
            Connection = latestTick is { } lt && DateTimeOffset.UtcNow - lt < StaleAfter
                ? ConnectionStatus.Connected
                : latestTick is null ? ConnectionStatus.Disconnected : ConnectionStatus.Reconnecting;

            var snapshots = await db.ScoreSnapshots.OrderByDescending(s => s.ComputedAt).Take(120).ToListAsync();
            snapshots.Reverse();

            if (snapshots.Count > 0)
            {
                _firstSnapshotAt ??= await db.ScoreSnapshots.OrderBy(s => s.ComputedAt).Select(s => (DateTimeOffset?)s.ComputedAt).FirstOrDefaultAsync();
                var latest = snapshots[^1];

                lock (_lock)
                {
                    _currentScore = latest.CompositeScore ?? _currentScore;
                    _scoreHistory = [.. snapshots.Select(s => new ScoreHistoryPoint(s.ComputedAt, s.CompositeScore ?? 0))];
                }

                ScoreComponents = BuildComponentRows(latest);
                WarmUpStatuses = BuildWarmUpStatuses(latest, _firstSnapshotAt ?? latest.ComputedAt);
            }

            var chain = await BuildOptionChainAsync(db);
            lock (_lock)
            {
                _optionChain = chain;
            }

            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            // A Blazor singleton's background timer must never let an exception escape --
            // that would take down every connected viewer's next poll silently. Log and
            // retry next tick instead (a transient DB hiccup shouldn't kill the dashboard).
            _logger.LogWarning(ex, "LiveDataService poll failed");
        }
    }

    /// <summary>
    /// The Live Quote panel's 1s refresh -- spot/future LTP+change plus LTP/bid/ask+change
    /// for every currently-live nearest-expiry option, deliberately without IV or the OI
    /// lookback (that's what makes this cheap enough to run every second; see the class
    /// doc comment).
    /// </summary>
    async Task QuotePollAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).Date);

            var optionTokens = await db.Instruments
                .Where(i => i.AsOfDate == today && i.InstrumentType == InstrumentType.Option)
                .Select(i => new { i.Token, i.StrikePrice, i.OptionType })
                .ToListAsync();
            var futureToken = await db.Instruments
                .Where(i => i.AsOfDate == today && i.InstrumentType == InstrumentType.Future)
                .Select(i => i.Token)
                .FirstOrDefaultAsync();
            var spotToken = await db.Instruments
                .Where(i => i.AsOfDate == today && i.InstrumentType == InstrumentType.Index)
                .Select(i => i.Token)
                .FirstOrDefaultAsync();

            var allTokens = optionTokens.Select(i => i.Token)
                .Concat(futureToken is null ? [] : [futureToken])
                .Concat(spotToken is null ? [] : [spotToken])
                .ToList();
            if (allTokens.Count == 0)
            {
                return;
            }

            var latestByToken = await db.Ticks
                .Where(t => allTokens.Contains(t.Token))
                .GroupBy(t => t.Token)
                .Select(g => g.OrderByDescending(t => t.ExchangeTimestamp).First())
                .ToDictionaryAsync(t => t.Token);
            var dayOpenByToken = await db.Ticks
                .Where(t => allTokens.Contains(t.Token))
                .GroupBy(t => t.Token)
                .Select(g => g.OrderBy(t => t.ExchangeTimestamp).First())
                .ToDictionaryAsync(t => t.Token, t => t.LastPrice);

            decimal? Change(string token, decimal ltp) => dayOpenByToken.TryGetValue(token, out var open) ? ltp - open : null;

            if (futureToken is not null && latestByToken.TryGetValue(futureToken, out var futureTick))
            {
                FutureLtp = futureTick.LastPrice;
                FutureChange = Change(futureToken, futureTick.LastPrice);
            }

            if (spotToken is not null && latestByToken.TryGetValue(spotToken, out var spotTick))
            {
                SpotLtp = spotTick.LastPrice;
                SpotChange = Change(spotToken, spotTick.LastPrice);
            }

            var quotes = new Dictionary<(decimal Strike, OptionType Type), QuickQuote>();
            foreach (var option in optionTokens)
            {
                if (!latestByToken.TryGetValue(option.Token, out var tick))
                {
                    continue;
                }

                var depth = tick.Depth;
                quotes[(option.StrikePrice!.Value, option.OptionType)] = new QuickQuote(
                    Ltp: tick.LastPrice,
                    Bid: depth?.Bid1Price is > 0 ? depth.Bid1Price : null,
                    Ask: depth?.Ask1Price is > 0 ? depth.Ask1Price : null,
                    Change: Change(option.Token, tick.LastPrice));
            }

            lock (_lock)
            {
                _quickQuotes = quotes;
            }

            QuoteUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiveDataService quote poll failed");
        }
    }

    static List<ScoreComponentRow> BuildComponentRows(ScoreSnapshot s) =>
    [
        new("OiBuildupNet", ScoreWeights.Default.OiBuildupNet, s.OiBuildupNetZ ?? 0, (s.OiBuildupNetZ ?? 0) * ScoreWeights.Default.OiBuildupNet),
        new("Pcr", ScoreWeights.Default.Pcr, s.PcrZ ?? 0, (s.PcrZ ?? 0) * ScoreWeights.Default.Pcr),
        new("FuturesBasis", ScoreWeights.Default.FuturesBasis, s.FuturesBasisZ ?? 0, (s.FuturesBasisZ ?? 0) * ScoreWeights.Default.FuturesBasis),
        new("IvSkew", ScoreWeights.Default.IvSkew, s.IvSkewZ ?? 0, (s.IvSkewZ ?? 0) * ScoreWeights.Default.IvSkew),
        new("PriceMomentum", ScoreWeights.Default.PriceMomentum, s.PriceMomentumZ ?? 0, (s.PriceMomentumZ ?? 0) * ScoreWeights.Default.PriceMomentum),
        new("DepthImbalance", ScoreWeights.Default.DepthImbalance, s.DepthImbalanceZ ?? 0, (s.DepthImbalanceZ ?? 0) * ScoreWeights.Default.DepthImbalance),
    ];

    static List<MetricWarmUpStatus> BuildWarmUpStatuses(ScoreSnapshot latest, DateTimeOffset firstSnapshotAt)
    {
        var elapsed = latest.ComputedAt - firstSnapshotAt;
        TimeSpan Remaining(TimeSpan window) => window - elapsed > TimeSpan.Zero ? window - elapsed : TimeSpan.Zero;

        return
        [
            new("Depth Imbalance", latest.DepthImbalanceZ is not null, FeatureWindowLengths.DepthImbalance, Remaining(FeatureWindowLengths.DepthImbalance)),
            new("Price Momentum", latest.PriceMomentumZ is not null, FeatureWindowLengths.PriceMomentum, Remaining(FeatureWindowLengths.PriceMomentum)),
            new("PCR", latest.PcrZ is not null, FeatureWindowLengths.Pcr, Remaining(FeatureWindowLengths.Pcr)),
            new("OI Buildup Net", latest.OiBuildupNetZ is not null, FeatureWindowLengths.OiBuildupNet, Remaining(FeatureWindowLengths.OiBuildupNet)),
            new("IV Skew", latest.IvSkewZ is not null, FeatureWindowLengths.IvSkew, Remaining(FeatureWindowLengths.IvSkew)),
            new("Futures Basis", latest.FuturesBasisZ is not null, FeatureWindowLengths.FuturesBasis, Remaining(FeatureWindowLengths.FuturesBasis)),
        ];
    }

    static List<MetricWarmUpStatus> BuildDefaultWarmUpStatuses() =>
    [
        new("Depth Imbalance", false, FeatureWindowLengths.DepthImbalance, FeatureWindowLengths.DepthImbalance),
        new("Price Momentum", false, FeatureWindowLengths.PriceMomentum, FeatureWindowLengths.PriceMomentum),
        new("PCR", false, FeatureWindowLengths.Pcr, FeatureWindowLengths.Pcr),
        new("OI Buildup Net", false, FeatureWindowLengths.OiBuildupNet, FeatureWindowLengths.OiBuildupNet),
        new("IV Skew", false, FeatureWindowLengths.IvSkew, FeatureWindowLengths.IvSkew),
        new("Futures Basis", false, FeatureWindowLengths.FuturesBasis, FeatureWindowLengths.FuturesBasis),
    ];

    async Task<List<OptionChainRow>> BuildOptionChainAsync(NiftySignalDbContext db)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).Date);

        var instruments = await db.Instruments
            .Where(i => i.AsOfDate == today && i.InstrumentType == InstrumentType.Option)
            .ToListAsync();
        if (instruments.Count == 0)
        {
            return [];
        }

        var nearestExpiry = instruments.Min(i => i.ExpiryDate);
        var nearest = instruments.Where(i => i.ExpiryDate == nearestExpiry).ToList();
        var tokens = nearest.Select(i => i.Token).ToList();

        var futureToken = await db.Instruments
            .Where(i => i.AsOfDate == today && i.InstrumentType == InstrumentType.Future)
            .Select(i => i.Token)
            .FirstOrDefaultAsync();

        var latestByToken = await db.Ticks
            .Where(t => tokens.Contains(t.Token))
            .GroupBy(t => t.Token)
            .Select(g => g.OrderByDescending(t => t.ExchangeTimestamp).First())
            .ToDictionaryAsync(t => t.Token);

        // OI itself only updates on the exchange side every ~3 minutes -- comparing
        // against the immediately-prior poll (5s ago) was structurally almost always a
        // no-op, which is why this always read 0.0%. Compare against ~30 minutes ago
        // instead, a window long enough to always span at least one real OI update.
        var oiLookbackCutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        var oi30MinAgoByToken = await db.Ticks
            .Where(t => tokens.Contains(t.Token) && t.ExchangeTimestamp <= oiLookbackCutoff)
            .GroupBy(t => t.Token)
            .Select(g => g.OrderByDescending(t => t.ExchangeTimestamp).First())
            .ToDictionaryAsync(t => t.Token, t => t.OpenInterest);

        var dayOpenByToken = await db.Ticks
            .Where(t => tokens.Contains(t.Token))
            .GroupBy(t => t.Token)
            .Select(g => g.OrderBy(t => t.ExchangeTimestamp).First())
            .ToDictionaryAsync(t => t.Token, t => t.LastPrice);

        // Spot/Future LTP+change are the Live Quote panel's job now (QuotePollAsync, 1s) --
        // still fetched here since the ATM calc and IV solve below need futureLtp, just no
        // longer published to the SpotLtp/FutureLtp properties from this (5s) path.
        var futureLtp = futureToken is null
            ? (decimal?)null
            : await db.Ticks.Where(t => t.Token == futureToken).OrderByDescending(t => t.ExchangeTimestamp).Select(t => (decimal?)t.LastPrice).FirstOrDefaultAsync();

        if (latestByToken.Count == 0)
        {
            return [];
        }

        // ATM = the strike whose call+put pair together straddle the futures price most
        // tightly -- approximate with "nearest strike to the futures LTP" if we have one.
        var atmStrike = futureLtp is { } fl
            ? nearest.Select(i => i.StrikePrice!.Value).Distinct().OrderBy(s => Math.Abs(s - fl)).FirstOrDefault()
            : nearest.Select(i => i.StrikePrice!.Value).Distinct().OrderBy(s => s).Skip(nearest.Count / 4).FirstOrDefault();

        var t = TimeToExpiry.YearsUntilExpiry(nearestExpiry!.Value, DateTimeOffset.UtcNow);

        var rows = new List<OptionChainRow>();
        foreach (var instrument in nearest.OrderBy(i => i.StrikePrice))
        {
            if (!latestByToken.TryGetValue(instrument.Token, out var tick))
            {
                continue;
            }

            double? iv = null;
            if (futureLtp is { } underlying && tick.LastPrice > 0)
            {
                iv = ImpliedVolatilitySolver.Solve(instrument.OptionType, (double)tick.LastPrice, (double)underlying, (double)instrument.StrikePrice!.Value, t, RiskFreeRate);
            }

            var depth = tick.Depth;
            var depthImbalance = depth is { TotalAskQty: > 0 } d ? (double)d.TotalBidQty / d.TotalAskQty : 0.0;

            double oiChangePct = 0;
            if (oi30MinAgoByToken.TryGetValue(instrument.Token, out var oi30MinAgo) && oi30MinAgo is > 0 && tick.OpenInterest is { } currentOi)
            {
                oiChangePct = (double)(currentOi - oi30MinAgo.Value) / oi30MinAgo.Value * 100.0;
            }

            var change = dayOpenByToken.TryGetValue(instrument.Token, out var open) ? tick.LastPrice - open : (decimal?)null;

            rows.Add(new OptionChainRow(
                TradingSymbol: instrument.TradingSymbol,
                OptionType: instrument.OptionType,
                Strike: instrument.StrikePrice!.Value,
                Ltp: tick.LastPrice,
                OpenInterest: tick.OpenInterest ?? 0,
                OiChangePct: oiChangePct,
                ImpliedVolatility: iv,
                DepthImbalance: depthImbalance,
                Buildup: OiBuildupClassification.Neutral, // per-row buildup not tracked yet -- see LiveFeatureEngine for the aggregate version
                IsAtm: instrument.StrikePrice == atmStrike,
                Bid: depth?.Bid1Price is > 0 ? depth.Bid1Price : null,
                Ask: depth?.Ask1Price is > 0 ? depth.Ask1Price : null,
                Change: change));
        }

        return rows;
    }

    // 91-day T-bill proxy -- same starting value as LiveFeatureEngine (plan 4.1).
    const double RiskFreeRate = 0.065;

    public void Dispose()
    {
        _timer.Dispose();
        _quoteTimer.Dispose();
    }
}
