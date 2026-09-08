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
    int _pollInProgress;

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

    // Snapshot copy, not the live list (2026-09-08 live-caught): ApplyPushedTick mutates
    // _positions in place via index-set (list[i] = value), which bumps List<T>'s internal
    // version counter same as Add/Remove does. A caller enumerating the live reference --
    // PositionsPanel's @foreach captures this getter's return value once, then iterates it
    // across an entire render-tree build, well outside this lock -- would throw
    // InvalidOperationException the moment a tick landed on an open position's instrument
    // mid-render. A snapshot is immune: mutating the *live* field afterward can never affect
    // an already-returned, independent copy.
    public IReadOnlyList<PositionRow> Positions { get { lock (_lock) return [.. _positions]; } }

    public IReadOnlyList<ClosedTradeRow> ClosedTrades { get { lock (_lock) return _closedTrades; } }

    public ConnectionStatus Connection { get; private set; } = ConnectionStatus.Disconnected;

    public DateTimeOffset LastTickAt { get; private set; } = DateTimeOffset.MinValue;

    public decimal? SpotLtp { get; private set; }

    public decimal? FutureLtp { get; private set; }

    public decimal? SpotChange { get; private set; }

    public decimal? FutureChange { get; private set; }

    public decimal? VixLtp { get; private set; }

    public decimal? VixChange { get; private set; }

    /// <summary>
    /// The spot level where net Gamma Exposure crosses zero, per the latest cadence (2026-09-08)
    /// -- see ScoreSnapshot.GammaFlipLevel's own doc comment. A price level, not a magnitude, so
    /// it doesn't belong in <see cref="ScoreComponents"/> (which expects a Z-score/weight shape) --
    /// shown instead next to SpotLtp, the same way it's meant to be read.
    /// </summary>
    public double? GammaFlipLevel { get; private set; }

    public LiveDataService(IDbContextFactory<NiftySignalDbContext> dbFactory, FlatTradeAuthClient authClient, ILogger<LiveDataService> logger)
    {
        _dbFactory = dbFactory;
        _authClient = authClient;
        _logger = logger;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    /// <summary>
    /// Guards against overlapping PollAsync runs -- the fire-and-forget timer used to start a
    /// new poll every 5s regardless of whether the previous one had finished, which would have
    /// let concurrent DB-heavy polls pile up on a slow cycle. Now a poll that's still running
    /// when the next tick fires is skipped rather than stacked.
    /// </summary>
    void Poll()
    {
        if (Interlocked.CompareExchange(ref _pollInProgress, 1, 0) != 0)
        {
            _logger.LogWarning("Skipping poll tick -- previous poll is still running");
            return;
        }

        _ = PollAsync();
    }

    /// <summary>
    /// Called by MarketDataHub the instant Host forwards a tick. No DB access here on
    /// purpose -- that's the whole point of push over poll. Unknown tokens (an on-demand
    /// subscription Host hasn't caught up to yet, or a stale role map) are dropped rather
    /// than guessed at; the next 5s poll's role refresh self-heals it.
    /// </summary>
    public void ApplyPushedTick(Tick tick)
    {
        // Independent of _tokenRoles below (2026-09-08): an open position's own instrument
        // isn't necessarily one of the tracked spot/future/vix/quick-quote roles, so this has
        // to be its own check rather than a fifth TokenRole case. Positions.CurrentPremium
        // otherwise only refreshed on the 5s poll or a trade-changed push (RefreshTradesAsync),
        // both far coarser than the live tick stream everything else on this page already gets.
        var matchedPosition = false;
        lock (_lock)
        {
            for (var i = 0; i < _positions.Count; i++)
            {
                if (_positions[i].InstrumentToken == tick.Token)
                {
                    _positions[i] = _positions[i] with { CurrentPremium = tick.LastPrice };
                    matchedPosition = true;
                }
            }
        }

        if (!_tokenRoles.TryGetValue(tick.Token, out var info))
        {
            // Preserves the original "unknown token, nothing changed, nothing to notify" drop --
            // only fire when the position match above actually changed something.
            if (matchedPosition)
            {
                QuoteUpdated?.Invoke();
            }

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
    /// Called by MarketDataHub the instant Host pushes a trade-changed signal (2026-09-07) --
    /// re-reads just positions + closed trades (not the whole 5s poll, which also solves IV
    /// across the option chain) so the Positions/Performance panels update immediately on
    /// entry/partial-book/exit instead of waiting up to 5s for the next timer tick.
    /// </summary>
    public async Task RefreshTradesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var positions = await BuildPositionsAsync(db);
        var closedTrades = await BuildClosedTradesAsync(db);

        lock (_lock)
        {
            _positions = positions;
            _closedTrades = closedTrades;
        }

        Updated?.Invoke();
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
                    _scoreHistory = [.. snapshots.Select(s => new ScoreHistoryPoint(s.ComputedAt, s.CompositeScore ?? 0, s.SpotPrice))];
                }

                ScoreComponents = BuildComponentRows(latest, _firstSnapshotAt ?? latest.ComputedAt, _firstVixSnapshotAt);
                GammaFlipLevel = latest.GammaFlipLevel;
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
        finally
        {
            Interlocked.Exchange(ref _pollInProgress, 0);
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

        var allTokens = instruments.Select(i => i.Token).ToArray();
        var dayOpenTicks = await EarliestTicksAsync(db, allTokens);
        var dayOpenByToken = dayOpenTicks.ToDictionary(t => t.Token, t => t.LastPrice);

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
            ? (FeatureWindowLengths.VixChangeZScoreWindow - (s.ComputedAt - vixStart) is var vixLeft && vixLeft > TimeSpan.Zero ? vixLeft : TimeSpan.Zero)
            : FeatureWindowLengths.VixChangeZScoreWindow;

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
                s.VixChangeZ is not null, FeatureWindowLengths.VixChangeZScoreWindow, vixRemaining),
            // Diagnostic-only (weight 0.0, see ScoreWeights.Default) -- shown so the metric can
            // be watched across real sessions before it's ever given a nonzero weight.
            new("GammaExposure", ScoreWeights.Default.GammaExposure, s.GammaExposureZ ?? 0, (s.GammaExposureZ ?? 0) * ScoreWeights.Default.GammaExposure,
                s.GammaExposureZ is not null, FeatureWindowLengths.GammaExposure, Remaining(FeatureWindowLengths.GammaExposure)),
            // Diagnostic-only (weight 0.0, see ScoreWeights.Default) -- same treatment as GammaExposure.
            new("VolumePcr", ScoreWeights.Default.VolumePcr, s.VolumePcrZ ?? 0, (s.VolumePcrZ ?? 0) * ScoreWeights.Default.VolumePcr,
                s.VolumePcrZ is not null, FeatureWindowLengths.VolumePcr, Remaining(FeatureWindowLengths.VolumePcr)),
            // Diagnostic-only (weight 0.0, see ScoreWeights.Default) -- same treatment as GammaExposure/VolumePcr.
            new("SpreadRatio", ScoreWeights.Default.SpreadRatio, s.SpreadRatioZ ?? 0, (s.SpreadRatioZ ?? 0) * ScoreWeights.Default.SpreadRatio,
                s.SpreadRatioZ is not null, FeatureWindowLengths.SpreadRatio, Remaining(FeatureWindowLengths.SpreadRatio)),
            // Diagnostic-only (weight 0.0, see ScoreWeights.Default) -- same treatment as the three above.
            new("VannaExposure", ScoreWeights.Default.VannaExposure, s.VannaExposureZ ?? 0, (s.VannaExposureZ ?? 0) * ScoreWeights.Default.VannaExposure,
                s.VannaExposureZ is not null, FeatureWindowLengths.VannaExposure, Remaining(FeatureWindowLengths.VannaExposure)),
            new("CharmExposure", ScoreWeights.Default.CharmExposure, s.CharmExposureZ ?? 0, (s.CharmExposureZ ?? 0) * ScoreWeights.Default.CharmExposure,
                s.CharmExposureZ is not null, FeatureWindowLengths.CharmExposure, Remaining(FeatureWindowLengths.CharmExposure)),
            new("CvdProxy", ScoreWeights.Default.CvdProxy, s.CvdProxyZ ?? 0, (s.CvdProxyZ ?? 0) * ScoreWeights.Default.CvdProxy,
                s.CvdProxyZ is not null, FeatureWindowLengths.CvdProxy, Remaining(FeatureWindowLengths.CvdProxy)),
            // Volatility-demand, not directional (see ScoreSnapshot.StraddleRichnessRaw's doc
            // comment) -- still rendered as an ordinary weighted row here since weight is 0.0
            // either way; the distinction only matters once/if it's ever given a real weight.
            new("StraddleRichness", ScoreWeights.Default.StraddleRichness, s.StraddleRichnessZ ?? 0, (s.StraddleRichnessZ ?? 0) * ScoreWeights.Default.StraddleRichness,
                s.StraddleRichnessZ is not null, FeatureWindowLengths.StraddleRichness, Remaining(FeatureWindowLengths.StraddleRichness)),
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
        new("VixChange", ScoreWeights.Default.VixChange, 0, 0, false, FeatureWindowLengths.VixChangeZScoreWindow, FeatureWindowLengths.VixChangeZScoreWindow),
        new("GammaExposure", ScoreWeights.Default.GammaExposure, 0, 0, false, FeatureWindowLengths.GammaExposure, FeatureWindowLengths.GammaExposure),
        new("VolumePcr", ScoreWeights.Default.VolumePcr, 0, 0, false, FeatureWindowLengths.VolumePcr, FeatureWindowLengths.VolumePcr),
        new("SpreadRatio", ScoreWeights.Default.SpreadRatio, 0, 0, false, FeatureWindowLengths.SpreadRatio, FeatureWindowLengths.SpreadRatio),
        new("VannaExposure", ScoreWeights.Default.VannaExposure, 0, 0, false, FeatureWindowLengths.VannaExposure, FeatureWindowLengths.VannaExposure),
        new("CharmExposure", ScoreWeights.Default.CharmExposure, 0, 0, false, FeatureWindowLengths.CharmExposure, FeatureWindowLengths.CharmExposure),
        new("CvdProxy", ScoreWeights.Default.CvdProxy, 0, 0, false, FeatureWindowLengths.CvdProxy, FeatureWindowLengths.CvdProxy),
        new("StraddleRichness", ScoreWeights.Default.StraddleRichness, 0, 0, false, FeatureWindowLengths.StraddleRichness, FeatureWindowLengths.StraddleRichness),
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

        // Both tracked weeklies come back together (2026-09-05); OptionChainPanel filters to
        // the one the viewer selected. Deliberately not filtered here: LiveDataService is a
        // singleton shared by every circuit, so an expiry choice stored on it would change the
        // view for everyone at once. Note this does NOT touch RefreshTokenRolesAsync, which
        // stays nearest-expiry-only -- _quickQuotes is keyed (Strike, OptionType) with no
        // expiry component, and relaxing that filter would reintroduce the CE/PE flip bug
        // caught live on 2026-09-04.
        var tokens = instruments.Select(i => i.Token).ToArray();

        var spotToken = await db.Instruments
            .Where(i => i.AsOfDate == today && i.InstrumentType == InstrumentType.Index)
            .Select(i => i.Token)
            .FirstOrDefaultAsync();

        var latestTicks = await LatestTicksAsync(db, tokens);
        var latestByToken = latestTicks.ToDictionary(t => t.Token);

        // OI itself only updates on the exchange side every ~3 minutes -- comparing
        // against the immediately-prior poll (5s ago) was structurally almost always a
        // no-op, which is why this always read 0.0%. Compare against ~30 minutes ago
        // instead, a window long enough to always span at least one real OI update.
        var oiLookbackCutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        var oi30MinAgoTicks = await LatestTicksAtOrBeforeAsync(db, tokens, oiLookbackCutoff);
        var oi30MinAgoByToken = oi30MinAgoTicks.ToDictionary(t => t.Token, t => t.OpenInterest);

        var dayOpenTicks = await EarliestTicksAsync(db, tokens);
        var dayOpenByToken = dayOpenTicks.ToDictionary(t => t.Token, t => t.LastPrice);

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
        // inflated put IV. Kept only as the anchor for BuildSyntheticForwardByExpiry below
        // (2026-09-07) -- spot itself is no longer fed directly into any IV/Greeks calculation;
        // that first fix was directionally right but incomplete, since spot alone still carries
        // no cost-of-carry adjustment. See BuildSyntheticForwardByExpiry's own doc comment.
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
            ? instruments.Select(i => i.StrikePrice!.Value).Distinct().OrderBy(s => Math.Abs(s - sl)).FirstOrDefault()
            : instruments.Select(i => i.StrikePrice!.Value).Distinct().OrderBy(s => s).Skip(instruments.Count / 4).FirstOrDefault();

        // Time to expiry is per-expiry now that both weeklies are in play -- a single `t` from
        // the nearest expiry would misprice every next-week row.
        var yearsToExpiry = instruments
            .Select(i => i.ExpiryDate!.Value)
            .Distinct()
            .ToDictionary(e => e, e => TimeToExpiry.YearsUntilExpiry(e, DateTimeOffset.UtcNow));

        // Synthetic forward per expiry via put-call parity (2026-09-07) -- replaces spotLtp as
        // the Black-Scholes underlying below. Neither spot nor a tracked future is the right
        // underlying for a weekly option (spot has no cost-of-carry adjustment; the earlier
        // 2026-09-04 fix already ruled out the mismatched monthly future) -- a same-day live
        // check found systematically inconsistent call/put IV at the same strike (put running
        // ~7-8 vol points below call, everywhere, all day) that traced to exactly this: an
        // understated underlying biases a call solve up and a put solve down. See
        // NiftySignal.Pricing.SyntheticForward's doc comment and
        // LiveFeatureEngine.ComputeUnderlyingPrice (same fix, same day, same root cause).
        var forwardByExpiry = spotLtp is { } spotForForward
            ? BuildSyntheticForwardByExpiry(instruments, latestByToken, spotForForward, yearsToExpiry)
            : [];

        // Reference vol for theoretical pricing (2026-09-05), solved per expiry at the ATM
        // strike. Pricing each strike with its *own* implied vol would just reproduce that
        // strike's market price exactly -- true by construction, and useless. Pricing every
        // strike at the ATM strike's vol instead makes the gap meaningful: it's the skew
        // premium in rupee terms, i.e. how much more (or less) the market is paying for this
        // strike than the at-the-money baseline implies. Above theoretical reads as demand
        // bidding premium up; below reads as supply/writing pressure.
        var atmVolByExpiry = BuildAtmReferenceVol(instruments, latestByToken, forwardByExpiry, atmStrike, yearsToExpiry);

        var rows = new List<OptionChainRow>();
        foreach (var instrument in instruments.OrderBy(i => i.ExpiryDate).ThenBy(i => i.StrikePrice))
        {
            if (!latestByToken.TryGetValue(instrument.Token, out var tick))
            {
                continue;
            }

            var expiry = instrument.ExpiryDate!.Value;
            var t = yearsToExpiry[expiry];

            double? iv = null, theoretical = null;
            if (forwardByExpiry.TryGetValue(expiry, out var underlying) && tick.LastPrice > 0)
            {
                iv = ImpliedVolatilitySolver.Solve(instrument.OptionType, (double)tick.LastPrice, (double)underlying, (double)instrument.StrikePrice!.Value, t, RiskFreeRate);

                if (atmVolByExpiry.TryGetValue(expiry, out var atmVol))
                {
                    theoretical = BlackScholes.Calculate(
                        instrument.OptionType, (double)underlying, (double)instrument.StrikePrice!.Value, t, RiskFreeRate, atmVol).Price;
                }
            }

            var depth = tick.Depth;
            // Normalized (bid-ask)/(bid+ask), not a raw bid/ask ratio -- a ratio of two
            // non-negative quantities can never be negative, yet the grid colors this
            // bull/bear and formats it with a sign as if it could swing either way. Same
            // bug, same fix, as LiveFeatureEngine.ComputeDepthImbalance (2026-09-04).
            var depthImbalance = depth is { } d && (d.TotalBidQty + d.TotalAskQty) > 0
                ? (double)(d.TotalBidQty - d.TotalAskQty) / (d.TotalBidQty + d.TotalAskQty)
                : 0.0;

            long? oiChange = null;
            double oiChangePct = 0;
            if (oi30MinAgoByToken.TryGetValue(instrument.Token, out var oi30MinAgo) && oi30MinAgo is { } prevOi && tick.OpenInterest is { } currentOi)
            {
                oiChange = currentOi - prevOi;
                if (prevOi > 0)
                {
                    oiChangePct = (double)oiChange.Value / prevOi * 100.0;
                }
            }

            var change = dayOpenByToken.TryGetValue(instrument.Token, out var open) ? tick.LastPrice - open : (decimal?)null;

            rows.Add(new OptionChainRow(
                TradingSymbol: instrument.TradingSymbol,
                OptionType: instrument.OptionType,
                Strike: instrument.StrikePrice!.Value,
                ExpiryDate: expiry,
                Ltp: tick.LastPrice,
                OpenInterest: tick.OpenInterest ?? 0,
                LotSize: instrument.LotSize,
                OiChangePct: oiChangePct,
                OiChange: oiChange,
                ImpliedVolatility: iv,
                TheoreticalPrice: theoretical,
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
    /// One reference volatility per expiry, solved from the ATM strike -- the most liquid and
    /// therefore most trustworthy point on the chain. Prefers the call, falls back to the put
    /// if the call's solve fails. Expiries with no usable ATM quote are simply absent from the
    /// result, and their rows get no theoretical price rather than one built on a guess.
    /// </summary>
    static Dictionary<DateOnly, double> BuildAtmReferenceVol(
        List<Instrument> instruments,
        Dictionary<string, Tick> latestByToken,
        Dictionary<DateOnly, decimal> forwardByExpiry,
        decimal atmStrike,
        Dictionary<DateOnly, double> yearsToExpiry)
    {
        var result = new Dictionary<DateOnly, double>();

        foreach (var expiry in yearsToExpiry.Keys)
        {
            if (!forwardByExpiry.TryGetValue(expiry, out var underlying))
            {
                continue;
            }

            var atmLegs = instruments
                .Where(i => i.ExpiryDate == expiry && i.StrikePrice == atmStrike)
                .OrderBy(i => i.OptionType == OptionType.Call ? 0 : 1);

            foreach (var leg in atmLegs)
            {
                if (!latestByToken.TryGetValue(leg.Token, out var tick) || tick.LastPrice <= 0)
                {
                    continue;
                }

                var solved = ImpliedVolatilitySolver.Solve(
                    leg.OptionType, (double)tick.LastPrice, (double)underlying, (double)atmStrike, yearsToExpiry[expiry], RiskFreeRate);

                if (solved is { } vol)
                {
                    result[expiry] = vol;
                    break;
                }
            }
        }

        return result;
    }

    const int SyntheticForwardStrikeCount = 5;

    /// <summary>
    /// Synthetic forward via put-call parity, one per expiry (2026-09-07) -- see the call
    /// site's doc comment for why this replaced spotLtp as the Black-Scholes underlying.
    /// Averages (median, via SyntheticForward.Compute) the parity estimate from the
    /// <see cref="SyntheticForwardStrikeCount"/> strikes nearest spot within that expiry, each
    /// with both a call and put quote. Falls back to spotLtp itself for an expiry where no
    /// strike has both legs quoted yet.
    /// </summary>
    static Dictionary<DateOnly, decimal> BuildSyntheticForwardByExpiry(
        List<Instrument> instruments,
        Dictionary<string, Tick> latestByToken,
        decimal spotLtp,
        Dictionary<DateOnly, double> yearsToExpiry)
    {
        var result = new Dictionary<DateOnly, decimal>();

        foreach (var (expiry, t) in yearsToExpiry)
        {
            var expiryInstruments = instruments.Where(i => i.ExpiryDate == expiry).ToList();
            var pairs = new List<(double Strike, double CallMid, double PutMid)>();

            foreach (var strike in expiryInstruments.Select(i => i.StrikePrice!.Value).Distinct().OrderBy(s => Math.Abs(s - spotLtp)).Take(SyntheticForwardStrikeCount))
            {
                var call = expiryInstruments.FirstOrDefault(i => i.OptionType == OptionType.Call && i.StrikePrice == strike);
                var put = expiryInstruments.FirstOrDefault(i => i.OptionType == OptionType.Put && i.StrikePrice == strike);
                if (call is null || put is null
                    || !latestByToken.TryGetValue(call.Token, out var callTick) || !latestByToken.TryGetValue(put.Token, out var putTick)
                    || MidPrice(callTick) is not { } callMid || MidPrice(putTick) is not { } putMid)
                {
                    continue;
                }

                pairs.Add(((double)strike, (double)callMid, (double)putMid));
            }

            result[expiry] = SyntheticForward.Compute(pairs, t, RiskFreeRate) is { } forward ? (decimal)forward : spotLtp;
        }

        return result;
    }

    static decimal? MidPrice(Tick tick)
    {
        if (tick.Depth is { } depth && depth.Bid1Price > 0 && depth.Ask1Price > 0)
        {
            return (depth.Bid1Price + depth.Ask1Price) / 2;
        }

        return tick.LastPrice > 0 ? tick.LastPrice : null;
    }

    /// <summary>
    /// Open paper_trades rows plus what the UI needs but the row itself doesn't store -- the
    /// live mark price, from the same latest-tick lookup BuildOptionChainAsync already uses
    /// for the same purpose. Quantity comes straight from the row itself (2026-09-07: what was
    /// actually traded at entry, not re-derived from the instrument's exchange lot size --
    /// LotsPerTrade means those two numbers are no longer the same thing).
    /// </summary>
    async Task<List<PositionRow>> BuildPositionsAsync(NiftySignalDbContext db)
    {
        var open = await db.PaperTrades.Where(t => t.ExitTime == null).ToListAsync();
        if (open.Count == 0)
        {
            return [];
        }

        var tokens = open.Select(t => t.InstrumentToken).Distinct().ToArray();

        var latestTicks = await LatestTicksAsync(db, tokens);
        var latestPriceByToken = latestTicks.ToDictionary(t => t.Token, t => t.LastPrice);

        return open.Select(t => new PositionRow(
            InstrumentToken: t.InstrumentToken,
            TradingSymbol: t.TradingSymbol,
            Direction: t.Direction,
            EntryPremium: t.EntryPrice,
            // Falls back to entry price (0 unrealized) if no tick has landed yet for this
            // token this session -- same "nothing live yet" tolerance as everywhere else,
            // not a crash or a fabricated number.
            CurrentPremium: latestPriceByToken.TryGetValue(t.InstrumentToken, out var ltp) ? ltp : t.EntryPrice,
            Quantity: t.Quantity,
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

    /// <summary>
    /// "Latest/earliest tick per token" the LINQ way -- <c>GroupBy(t => t.Token).Select(g =>
    /// g.OrderBy(...).First())</c> -- translates to a ROW_NUMBER() window function scanning and
    /// sorting every matching row for every token, not an index seek per token. Measured against
    /// the live DB (3.86M rows): ~11s wall time with a 200MB+ disk sort. Raw SQL was needed too --
    /// Npgsql's EF Core provider flattens even a correlated `Select(x => relatedQuery.
    /// FirstOrDefault())` into the same window-function shape rather than a LATERAL join
    /// (confirmed via ToQueryString), so plain LINQ can't reach the fast plan either way.
    ///
    /// `unnest(tokens) CROSS JOIN LATERAL (... LIMIT 1)` lets Postgres seek the existing
    /// (Token, ExchangeTimestamp) index once per token instead: measured at 1-16ms for the same
    /// 80-token lookup, a ~1000x improvement, because cost now scales with the (small, fixed)
    /// number of distinct tokens rather than with total table size.
    /// </summary>
    static Task<List<Tick>> LatestTicksAsync(NiftySignalDbContext db, string[] tokens) =>
        tokens.Length == 0
            ? Task.FromResult(new List<Tick>())
            : db.Ticks.FromSqlInterpolated($@"
                SELECT lt.* FROM unnest({tokens}) AS tok(token)
                CROSS JOIN LATERAL (
                    SELECT * FROM ticks t WHERE t.""Token"" = tok.token ORDER BY t.""ExchangeTimestamp"" DESC LIMIT 1
                ) lt")
                .AsNoTracking()
                .ToListAsync();

    /// <summary>Same as <see cref="LatestTicksAsync"/> but the earliest tick per token (today's open).</summary>
    static Task<List<Tick>> EarliestTicksAsync(NiftySignalDbContext db, string[] tokens) =>
        tokens.Length == 0
            ? Task.FromResult(new List<Tick>())
            : db.Ticks.FromSqlInterpolated($@"
                SELECT lt.* FROM unnest({tokens}) AS tok(token)
                CROSS JOIN LATERAL (
                    SELECT * FROM ticks t WHERE t.""Token"" = tok.token ORDER BY t.""ExchangeTimestamp"" ASC LIMIT 1
                ) lt")
                .AsNoTracking()
                .ToListAsync();

    /// <summary>Same as <see cref="LatestTicksAsync"/> but the latest tick at or before <paramref name="cutoff"/> per token.</summary>
    static Task<List<Tick>> LatestTicksAtOrBeforeAsync(NiftySignalDbContext db, string[] tokens, DateTimeOffset cutoff) =>
        tokens.Length == 0
            ? Task.FromResult(new List<Tick>())
            : db.Ticks.FromSqlInterpolated($@"
                SELECT lt.* FROM unnest({tokens}) AS tok(token)
                CROSS JOIN LATERAL (
                    SELECT * FROM ticks t WHERE t.""Token"" = tok.token AND t.""ExchangeTimestamp"" <= {cutoff} ORDER BY t.""ExchangeTimestamp"" DESC LIMIT 1
                ) lt")
                .AsNoTracking()
                .ToListAsync();

    // 91-day T-bill proxy -- same starting value as LiveFeatureEngine (plan 4.1).
    const double RiskFreeRate = 0.065;

    public void Dispose() => _timer.Dispose();
}
