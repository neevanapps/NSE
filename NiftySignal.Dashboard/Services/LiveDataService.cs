using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Persistence;
using NiftySignal.Pricing;
using NiftySignal.Scoring;

namespace NiftySignal.Dashboard.Services;

enum TokenRole { Spot, Future, Vix, Option }

/// <summary>Which slot a token feeds when a tick is pushed -- resolved from the DB every 5s (<see cref="LiveDataService.PollAsync"/>), consulted on every push.</summary>
sealed record TokenInfo(TokenRole Role, decimal Strike, OptionType OptionType);

/// <summary>
/// Backs the dashboard -- successor to the earlier DemoDataService random-walk simulation,
/// kept to the same public shape on purpose (one singleton, one <see cref="Updated"/>
/// event, same view-model records) so panels only had to change their injected service,
/// not their markup.
///
/// Two update paths, not one poll loop:
/// - <see cref="Updated"/> (5s poll) covers the score, data health, and the full option
///   chain table (IV solving + a 30-min OI lookback per row -- genuinely not cheap over
///   ~80 instruments), plus refreshes the token-role/day-open lookups the push path needs.
/// - <see cref="QuoteUpdated"/> (push, via <see cref="ApplyPushedTick"/>) covers what the
///   Live Quote panel shows -- spot/future LTP and the selected CE/PE legs' LTP/bid/ask.
///   Host is a SignalR client of Dashboard's own MarketDataHub (2026-09-04) and forwards
///   every tick the instant it receives one; this class just mutates in-memory state on
///   receipt, no DB round-trip on the hot path. Before this, both paths shared one poll
///   timer, then two separate poll timers (1s/5s) -- push removes the polling floor
///   entirely for the one thing that actually needed sub-second freshness.
///
/// Positions/ClosedTrades (2026-09-04) read the same paper_trades table LiveTradingEngine
/// (Host) writes to -- open positions' CurrentPremium/lot size come from the latest tick and
/// today's instrument master, same "read fresh from the DB each cycle" philosophy
/// LiveTradingEngine itself already uses for open-position state, rather than caching
/// anything that could drift out of sync with what Host actually persisted.
/// </summary>
public sealed class LiveDataService : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    readonly IDbContextFactory<NiftySignalDbContext> _dbFactory;
    readonly FlatTradeAuthClient _authClient;
    readonly ILogger<LiveDataService> _logger;
    readonly Timer _timer;
    readonly Lock _lock = new();

    double _currentScore;
    List<ScoreHistoryPoint> _scoreHistory = [];
    List<OptionChainRow> _optionChain = [];
    List<PositionRow> _positions = [];
    List<ClosedTradeRow> _closedTrades = [];
    Dictionary<(decimal Strike, OptionType Type), QuickQuote> _quickQuotes = [];
    Dictionary<string, TokenInfo> _tokenRoles = [];
    Dictionary<string, decimal> _dayOpenByToken = [];
    DateTimeOffset? _firstSnapshotAt;

    /// <summary>
    /// VIX's own first-observed timestamp (2026-09-04), separate from <see cref="_firstSnapshotAt"/>
    /// -- VIX tracking can start well after the session's other six metrics already have
    /// hours of history (as it did today), so its warm-up countdown needs its own clock
    /// rather than reusing the whole day's start time.
    /// </summary>
    DateTimeOffset? _firstVixSnapshotAt;

    public event Action? Updated;

    /// <summary>Fires on every pushed tick that resolves to a known token -- subscribe to this (not <see cref="Updated"/>) for anything that needs to feel live tick-by-tick, e.g. the Live Quote panel.</summary>
    public event Action? QuoteUpdated;

    public double CurrentScore { get { lock (_lock) return _currentScore; } }

    public IReadOnlyList<ScoreHistoryPoint> ScoreHistory { get { lock (_lock) return _scoreHistory; } }

    public IReadOnlyList<ScoreComponentRow> ScoreComponents { get; private set; } = BuildDefaultComponentRows();

    public IReadOnlyList<OptionChainRow> OptionChain { get { lock (_lock) return _optionChain; } }

    public IReadOnlyDictionary<(decimal Strike, OptionType Type), QuickQuote> QuickQuotes { get { lock (_lock) return _quickQuotes; } }

    public IReadOnlyList<PositionRow> Positions { get { lock (_lock) return _positions; } }

    public IReadOnlyList<ClosedTradeRow> ClosedTrades { get { lock (_lock) return _closedTrades; } }

    public ConnectionStatus Connection { get; private set; } = ConnectionStatus.Disconnected;

    public DateTimeOffset LastTickAt { get; private set; } = DateTimeOffset.MinValue;

    public decimal? SpotLtp { get; private set; }

    public decimal? FutureLtp { get; private set; }

    public decimal? SpotChange { get; private set; }

    public decimal? FutureChange { get; private set; }

    public decimal? VixLtp { get; private set; }

    public decimal? VixChange { get; private set; }

    public LiveDataService(IDbContextFactory<NiftySignalDbContext> dbFactory, FlatTradeAuthClient authClient, ILogger<LiveDataService> logger)
    {
        _dbFactory = dbFactory;
        _authClient = authClient;
        _logger = logger;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    void Poll() => _ = PollAsync();

    /// <summary>
    /// Called by MarketDataHub the instant Host forwards a tick. No DB access here on
    /// purpose -- that's the whole point of push over poll. Unknown tokens (an on-demand
    /// subscription Host hasn't caught up to yet, or a stale role map) are dropped rather
    /// than guessed at; the next 5s poll's role refresh self-heals it.
    /// </summary>
    public void ApplyPushedTick(Tick tick)
    {
        if (!_tokenRoles.TryGetValue(tick.Token, out var info))
        {
            return;
        }

        var change = _dayOpenByToken.TryGetValue(tick.Token, out var open) ? tick.LastPrice - open : (decimal?)null;

        switch (info.Role)
        {
            case TokenRole.Spot:
                SpotLtp = tick.LastPrice;
                SpotChange = change;
                break;

            case TokenRole.Future:
                FutureLtp = tick.LastPrice;
                FutureChange = change;
                break;

            case TokenRole.Vix:
                VixLtp = tick.LastPrice;
                VixChange = change;
                break;

            case TokenRole.Option:
                var depth = tick.Depth;
                lock (_lock)
                {
                    _quickQuotes[(info.Strike, info.OptionType)] = new QuickQuote(
                        Ltp: tick.LastPrice,
                        Bid: depth?.Bid1Price is > 0 ? depth.Bid1Price : null,
                        Ask: depth?.Ask1Price is > 0 ? depth.Ask1Price : null,
                        Change: change);
                }
                break;
        }

        QuoteUpdated?.Invoke();
    }

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
                _firstVixSnapshotAt ??= await db.ScoreSnapshots
                    .Where(s => s.VixChangeRaw != null)
                    .OrderBy(s => s.ComputedAt)
                    .Select(s => (DateTimeOffset?)s.ComputedAt)
                    .FirstOrDefaultAsync();
                var latest = snapshots[^1];

                lock (_lock)
                {
                    _currentScore = latest.CompositeScore ?? _currentScore;
                    _scoreHistory = [.. snapshots.Select(s => new ScoreHistoryPoint(s.ComputedAt, s.CompositeScore ?? 0))];
                }

                ScoreComponents = BuildComponentRows(latest, _firstSnapshotAt ?? latest.ComputedAt, _firstVixSnapshotAt);
            }

            var chain = await BuildOptionChainAsync(db);
            lock (_lock)
            {
                _optionChain = chain;
            }

            var positions = await BuildPositionsAsync(db);
            var closedTrades = await BuildClosedTradesAsync(db);
            lock (_lock)
            {
                _positions = positions;
                _closedTrades = closedTrades;
            }

            await RefreshTokenRolesAsync(db);

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
    /// What <see cref="ApplyPushedTick"/> needs to make sense of a bare token: which slot
    /// it feeds (spot/future/a specific strike+side) and today's opening price for that
    /// token (for the "(+x.xx)" change display). Refreshed every 5s alongside the rest of
    /// <see cref="PollAsync"/> -- a newly on-demand-subscribed instrument shows up here
    /// within one poll cycle of Host actually subscribing to it.
    /// </summary>
    async Task RefreshTokenRolesAsync(NiftySignalDbContext db)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).Date);

        var instruments = await db.Instruments
            .Where(i => i.AsOfDate == today && (i.InstrumentType == InstrumentType.Option || i.InstrumentType == InstrumentType.Future || i.InstrumentType == InstrumentType.Index || i.InstrumentType == InstrumentType.Vix))
            .ToListAsync();

        // Nearest-expiry options only -- same filter BuildOptionChainAsync already applies.
        // The tracked universe holds both weekly expiries, and they share identical
        // strikes (e.g. 23400 CE exists as both a near-week and a next-week token). Without
        // this filter, both tokens map into _quickQuotes under the same (Strike, OptionType)
        // key -- no Expiry component -- and the Live Quote panel flips unpredictably between
        // whichever expiry's tick arrived last (live-caught 2026-09-04).
        var nearestExpiry = instruments
            .Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate is not null)
            .Select(i => i.ExpiryDate!.Value)
            .DefaultIfEmpty()
            .Min();

        var roles = new Dictionary<string, TokenInfo>();
        foreach (var instrument in instruments)
        {
            if (instrument.InstrumentType == InstrumentType.Option && instrument.ExpiryDate != nearestExpiry)
            {
                continue;
            }

            roles[instrument.Token] = instrument.InstrumentType switch
            {
                InstrumentType.Index => new TokenInfo(TokenRole.Spot, 0, OptionType.None),
                InstrumentType.Future => new TokenInfo(TokenRole.Future, 0, OptionType.None),
                InstrumentType.Vix => new TokenInfo(TokenRole.Vix, 0, OptionType.None),
                _ => new TokenInfo(TokenRole.Option, instrument.StrikePrice!.Value, instrument.OptionType),
            };
        }

        var allTokens = instruments.Select(i => i.Token).ToList();
        var dayOpenByToken = new Dictionary<string, decimal>();
        if (allTokens.Count > 0)
        {
            dayOpenByToken = await db.Ticks
                .Where(t => allTokens.Contains(t.Token))
                .GroupBy(t => t.Token)
                .Select(g => g.OrderBy(t => t.ExchangeTimestamp).First())
                .ToDictionaryAsync(t => t.Token, t => t.LastPrice);
        }

        _tokenRoles = roles;
        _dayOpenByToken = dayOpenByToken;
    }

    /// <summary>
    /// Weight/z/contribution plus each component's own warm-up status (2026-09-04) --
    /// folded together from the former separate ScoreComponents/WarmUpStatuses so ScorePanel
    /// (which absorbed the standalone Data Health panel) reads one list keyed one way,
    /// instead of correlating two lists with differently-formatted names.
    /// </summary>
    static List<ScoreComponentRow> BuildComponentRows(ScoreSnapshot s, DateTimeOffset firstSnapshotAt, DateTimeOffset? firstVixSnapshotAt)
    {
        var elapsed = s.ComputedAt - firstSnapshotAt;
        TimeSpan Remaining(TimeSpan window) => window - elapsed > TimeSpan.Zero ? window - elapsed : TimeSpan.Zero;

        // VIX gets its own clock -- it can start tracking well after the session's other
        // six already have hours of history (as it did today), so reusing firstSnapshotAt
        // would under-count its remaining warm-up time. Null (never seen yet) reads as
        // "the full window remains", not "already warmed up".
        var vixRemaining = firstVixSnapshotAt is { } vixStart
            ? (FeatureWindowLengths.VixChange - (s.ComputedAt - vixStart) is var vixLeft && vixLeft > TimeSpan.Zero ? vixLeft : TimeSpan.Zero)
            : FeatureWindowLengths.VixChange;

        return
        [
            new("OiBuildupNet", ScoreWeights.Default.OiBuildupNet, s.OiBuildupNetZ ?? 0, (s.OiBuildupNetZ ?? 0) * ScoreWeights.Default.OiBuildupNet,
                s.OiBuildupNetZ is not null, FeatureWindowLengths.OiBuildupNet, Remaining(FeatureWindowLengths.OiBuildupNet)),
            new("Pcr", ScoreWeights.Default.Pcr, s.PcrZ ?? 0, (s.PcrZ ?? 0) * ScoreWeights.Default.Pcr,
                s.PcrZ is not null, FeatureWindowLengths.Pcr, Remaining(FeatureWindowLengths.Pcr)),
            new("FuturesBasis", ScoreWeights.Default.FuturesBasis, s.FuturesBasisZ ?? 0, (s.FuturesBasisZ ?? 0) * ScoreWeights.Default.FuturesBasis,
                s.FuturesBasisZ is not null, FeatureWindowLengths.FuturesBasis, Remaining(FeatureWindowLengths.FuturesBasis)),
            new("IvSkew", ScoreWeights.Default.IvSkew, s.IvSkewZ ?? 0, (s.IvSkewZ ?? 0) * ScoreWeights.Default.IvSkew,
                s.IvSkewZ is not null, FeatureWindowLengths.IvSkew, Remaining(FeatureWindowLengths.IvSkew)),
            new("PriceMomentum", ScoreWeights.Default.PriceMomentum, s.PriceMomentumZ ?? 0, (s.PriceMomentumZ ?? 0) * ScoreWeights.Default.PriceMomentum,
                s.PriceMomentumZ is not null, FeatureWindowLengths.PriceMomentum, Remaining(FeatureWindowLengths.PriceMomentum)),
            new("DepthImbalance", ScoreWeights.Default.DepthImbalance, s.DepthImbalanceZ ?? 0, (s.DepthImbalanceZ ?? 0) * ScoreWeights.Default.DepthImbalance,
                s.DepthImbalanceZ is not null, FeatureWindowLengths.DepthImbalance, Remaining(FeatureWindowLengths.DepthImbalance)),
            // Optional (see CompositeScoreCalculator) -- can legitimately stay "not warmed
            // up" indefinitely on a day VIX isn't tracked, unlike the other six.
            new("VixChange", ScoreWeights.Default.VixChange, s.VixChangeZ ?? 0, (s.VixChangeZ ?? 0) * ScoreWeights.Default.VixChange,
                s.VixChangeZ is not null, FeatureWindowLengths.VixChange, vixRemaining),
        ];
    }

    static List<ScoreComponentRow> BuildDefaultComponentRows() =>
    [
        new("OiBuildupNet", ScoreWeights.Default.OiBuildupNet, 0, 0, false, FeatureWindowLengths.OiBuildupNet, FeatureWindowLengths.OiBuildupNet),
        new("Pcr", ScoreWeights.Default.Pcr, 0, 0, false, FeatureWindowLengths.Pcr, FeatureWindowLengths.Pcr),
        new("FuturesBasis", ScoreWeights.Default.FuturesBasis, 0, 0, false, FeatureWindowLengths.FuturesBasis, FeatureWindowLengths.FuturesBasis),
        new("IvSkew", ScoreWeights.Default.IvSkew, 0, 0, false, FeatureWindowLengths.IvSkew, FeatureWindowLengths.IvSkew),
        new("PriceMomentum", ScoreWeights.Default.PriceMomentum, 0, 0, false, FeatureWindowLengths.PriceMomentum, FeatureWindowLengths.PriceMomentum),
        new("DepthImbalance", ScoreWeights.Default.DepthImbalance, 0, 0, false, FeatureWindowLengths.DepthImbalance, FeatureWindowLengths.DepthImbalance),
        new("VixChange", ScoreWeights.Default.VixChange, 0, 0, false, FeatureWindowLengths.VixChange, FeatureWindowLengths.VixChange),
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

        var spotToken = await db.Instruments
            .Where(i => i.AsOfDate == today && i.InstrumentType == InstrumentType.Index)
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

        // Spot/Future LTP+change are ApplyPushedTick's job now (push, not poll) -- still
        // fetched here since the ATM calc and IV solve below need spotLtp, just no longer
        // published to the SpotLtp/FutureLtp properties from this (5s) path.
        //
        // Spot, not the tracked future (2026-09-04 fix, live-caught: "all put IVs read >10,
        // call IVs around 5"). NSE only lists monthly futures; the tracked future is the
        // current-month contract, which doesn't match this weekly option chain's own (much
        // nearer) expiry. Using it overstated the underlying by its full month-vs-week
        // cost-of-carry gap (~127 points, observed 2026-09-04) -- enough to price deep-ITM
        // calls below their own intrinsic value (an impossible/arbitrage price), which is
        // what was driving the solver to a degenerate call IV and, symmetrically, an
        // inflated put IV. Same root cause and fix as LiveFeatureEngine's IV/Delta calcs.
        var spotLtp = spotToken is null
            ? (decimal?)null
            : await db.Ticks.Where(t => t.Token == spotToken).OrderByDescending(t => t.ExchangeTimestamp).Select(t => (decimal?)t.LastPrice).FirstOrDefaultAsync();

        if (latestByToken.Count == 0)
        {
            return [];
        }

        // ATM = the strike whose call+put pair together straddle the underlying most
        // tightly -- approximate with "nearest strike to spot" if we have one.
        var atmStrike = spotLtp is { } sl
            ? nearest.Select(i => i.StrikePrice!.Value).Distinct().OrderBy(s => Math.Abs(s - sl)).FirstOrDefault()
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
            if (spotLtp is { } underlying && tick.LastPrice > 0)
            {
                iv = ImpliedVolatilitySolver.Solve(instrument.OptionType, (double)tick.LastPrice, (double)underlying, (double)instrument.StrikePrice!.Value, t, RiskFreeRate);
            }

            var depth = tick.Depth;
            // Normalized (bid-ask)/(bid+ask), not a raw bid/ask ratio -- a ratio of two
            // non-negative quantities can never be negative, yet the grid colors this
            // bull/bear and formats it with a sign as if it could swing either way. Same
            // bug, same fix, as LiveFeatureEngine.ComputeDepthImbalance (2026-09-04).
            var depthImbalance = depth is { } d && (d.TotalBidQty + d.TotalAskQty) > 0
                ? (double)(d.TotalBidQty - d.TotalAskQty) / (d.TotalBidQty + d.TotalAskQty)
                : 0.0;

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

    /// <summary>
    /// Open paper_trades rows plus what the UI needs but the row itself doesn't store --
    /// PaperTrade has no Quantity column (LiveTradingEngine always trades the same
    /// Instrument-defined lot size, so it doesn't need to persist a per-trade copy) and no
    /// live mark price, so both come from the same today's-instruments/latest-tick lookups
    /// BuildOptionChainAsync already uses for the same purpose.
    /// </summary>
    async Task<List<PositionRow>> BuildPositionsAsync(NiftySignalDbContext db)
    {
        var open = await db.PaperTrades.Where(t => t.ExitTime == null).ToListAsync();
        if (open.Count == 0)
        {
            return [];
        }

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).Date);
        var tokens = open.Select(t => t.InstrumentToken).Distinct().ToList();

        var lotSizeByToken = await db.Instruments
            .Where(i => i.AsOfDate == today && tokens.Contains(i.Token))
            .ToDictionaryAsync(i => i.Token, i => i.LotSize);

        var latestPriceByToken = await db.Ticks
            .Where(t => tokens.Contains(t.Token))
            .GroupBy(t => t.Token)
            .Select(g => g.OrderByDescending(t => t.ExchangeTimestamp).First())
            .ToDictionaryAsync(t => t.Token, t => t.LastPrice);

        return open.Select(t => new PositionRow(
            TradingSymbol: t.TradingSymbol,
            Direction: t.Direction,
            EntryPremium: t.EntryPrice,
            // Falls back to entry price (0 unrealized) if no tick has landed yet for this
            // token this session -- same "nothing live yet" tolerance as everywhere else,
            // not a crash or a fabricated number.
            CurrentPremium: latestPriceByToken.TryGetValue(t.InstrumentToken, out var ltp) ? ltp : t.EntryPrice,
            Quantity: lotSizeByToken.TryGetValue(t.InstrumentToken, out var lotSize) ? lotSize : 0,
            EntryTime: t.EntryTime,
            HasPartiallyBooked: t.HasPartiallyBooked))
            .ToList();
    }

    /// <summary>Closed trades in the last 24h, matching PerformancePanel's own "(last 24h)" title.</summary>
    async Task<List<ClosedTradeRow>> BuildClosedTradesAsync(NiftySignalDbContext db)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var closed = await db.PaperTrades
            .Where(t => t.ExitTime != null && t.ExitTime >= cutoff)
            .OrderByDescending(t => t.ExitTime)
            .ToListAsync();

        return closed.Select(t => new ClosedTradeRow(
            TradingSymbol: t.TradingSymbol,
            Direction: t.Direction,
            EntryTime: t.EntryTime,
            ExitTime: t.ExitTime!.Value,
            NetPnl: t.NetPnl ?? 0,
            ExitReason: t.ExitReason))
            .ToList();
    }

    // 91-day T-bill proxy -- same starting value as LiveFeatureEngine (plan 4.1).
    const double RiskFreeRate = 0.065;

    public void Dispose() => _timer.Dispose();
}
