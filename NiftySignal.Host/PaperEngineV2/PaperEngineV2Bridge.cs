using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.MarketSummary;
using NiftySignal.Notifications;
using NiftySignal.PaperEngineV2.Alerts;
using NiftySignal.PaperEngineV2.Frames;
using NiftySignal.PaperEngineV2.Runtime;
using NiftySignal.Persistence;

namespace NiftySignal.Host.PaperEngineV2;

/// <summary>
/// The Host's side of Paper Engine V2 SHADOW (paper only, no broker, no order type). The Adaptive Observer calls it at two points of its own live loop - session start and once per availability group after the
/// group's bars were persisted and their sidecars verified - exactly where the NPT1 bridge is called. Per call it does O(1)-per-tick quote tracking and one bounded, non-blocking channel write per completed bar;
/// it never reads the database, never builds a frame and never throws into the Observer. <b>Off by default:</b> with <c>PaperEngineV2:Enabled</c> false (the source default) every method returns immediately and no
/// connection, task or file is touched. Startup only CHECKS the <c>pev2</c> schema; migrations are applied solely by the owner-run script.
/// </summary>
public sealed class PaperEngineV2Bridge : IHostedService, IAsyncDisposable
{
    readonly V2RuntimeOptions _options;
    readonly IServiceScopeFactory _scopes;
    readonly ILogger<PaperEngineV2Bridge> _log;
    readonly ITelegramTextSender? _telegram;
    readonly TimeProvider _clock;
    V2Runtime? _runtime;
    V2SessionInfo? _session;
    QuoteBook? _book;
    LiveMarkBoard? _marks, _attachedBoard;

    public PaperEngineV2Bridge(IConfiguration configuration, IServiceScopeFactory scopes, ILogger<PaperEngineV2Bridge> log, ITelegramTextSender? telegram = null, TimeProvider? clock = null)
    {
        _options = Bind(configuration); _scopes = scopes; _log = log; _telegram = telegram; _clock = clock ?? TimeProvider.System;
    }

    public bool Enabled => _options.Enabled;

    public V2RuntimeSnapshot? Snapshot => _runtime?.Snapshot();

    public V2RuntimeState State => _runtime?.State ?? (_options.Enabled ? V2RuntimeState.NotStarted : V2RuntimeState.Disabled);

    internal static V2RuntimeOptions Bind(IConfiguration c)
    {
        var s = c.GetSection("PaperEngineV2");
        var name = s["ConnectionStringName"] ?? "TradingPaperLive";
        var alerts = s.GetSection("Alerts");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new V2RuntimeOptions
        {
            Enabled = bool.TryParse(s["Enabled"], out var en) && en,
            ConnectionString = c.GetConnectionString(name),
            StrategyDirectory = s["StrategyDirectory"],
            QueueCapacity = int.TryParse(s["QueueCapacity"], out var cap) ? cap : 0,
            MaxProcessingLag = double.TryParse(s["MaxProcessingLagSeconds"], inv, out var lag) ? TimeSpan.FromSeconds(lag) : TimeSpan.Zero,
            FramesToClearGap = int.TryParse(s["FramesToClearGap"], out var clear) ? clear : 0,
            NoticeCapacity = int.TryParse(s["NoticeCapacity"], out var nc) ? nc : 256,
            LiveMarksEnabled = bool.TryParse(s.GetSection("LiveMarks")["Enabled"], out var lm) && lm,
            LiveMarkInterval = TimeSpan.FromMilliseconds(int.TryParse(s.GetSection("LiveMarks")["IntervalMs"], out var lmi) && lmi is >= 250 and <= 10_000 ? lmi : 1000),
            AlertPollInterval = TimeSpan.FromMilliseconds(int.TryParse(s["AlertPollIntervalMs"], out var ap) && ap > 0 ? ap : 2000),
            TerminalRetryUntil = TimeOnly.TryParse(s["TerminalRetryUntil"], out var t) ? t : default,
            CostScheduleVersion = s["CostScheduleVersion"],
            BrokeragePerOrder = decimal.TryParse(s["BrokeragePerOrder"], inv, out var b) ? b : null,
            Alerts = new AlertOptions(
                Opened: bool.TryParse(alerts["Opened"], out var a1) && a1, Closed: bool.TryParse(alerts["Closed"], out var a2) && a2, OrderRejected: bool.TryParse(alerts["OrderRejected"], out var a3) && a3,
                PartialFill: bool.TryParse(alerts["PartialFill"], out var a4) && a4, ExecutionFailure: bool.TryParse(alerts["ExecutionFailure"], out var a5) && a5),
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        try
        {
            var source = new DbFrameInputSource(
                () => _scopes.CreateScope().ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>(),
                () => _scopes.CreateScope().ServiceProvider.GetRequiredService<NiftySignalDbContext>());
            IAlertTransport? transport = _options.AnyAlertEnabled && _telegram is not null ? new TelegramAlertTransport(_telegram) : null;
            _runtime = new V2Runtime(_options, new InputSourceFrameFactory(source), transport, _clock, _log);
            await _runtime.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Paper Engine V2 could not start; the Observer and NPT1 are unaffected.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runtime is not null) await _runtime.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_runtime is not null) await _runtime.DisposeAsync();
    }

    internal void OnSessionStarted(AdaptiveObserverWorker.LiveState live)
    {
        if (_runtime is not { Accepting: true }) return;
        try
        {
            var ctx = live.Context;
            if (!AdaptiveFixedClockV1.IsFixedClock(ctx.Session.ModelVersion)) { _session = null; _book = null; return; }      // V2 frames are fixed-13,650 only
            var expiry = ctx.WeeklyOptionExpiry;
            var strikes = ctx.Options.Where(o => o.ExpiryDate == expiry).GroupBy(o => o.Strike).OrderBy(g => g.Key)
                .Select(g => new ListedStrike(g.Key, g.FirstOrDefault(o => o.OptionType == OptionType.Call)?.Token, g.FirstOrDefault(o => o.OptionType == OptionType.Put)?.Token)).ToList();
            _session = new V2SessionInfo(ctx.Session.Id, ctx.Session.TradeDate, ctx.Session.ModelVersion, ctx.Session.AtmRuleVersion, expiry, strikes, ctx.Options.ToDictionary(o => o.Token, o => o.LotSize, StringComparer.Ordinal));
            _book = new QuoteBook(ctx.Options.Select(o => o.Token));
            _marks = new LiveMarkBoard(ctx.Options.Select(o => o.Token));
            _runtime?.SetMarkBoard(_marks);
        }
        catch (Exception e)
        {
            _session = null; _book = null;
            _log.LogError(e, "Paper Engine V2 could not register the session; the Observer is unaffected.");
        }
    }

    internal void OnGroup(IReadOnlyList<(string Token, CleanObserverTick Tick)> group, IReadOnlyList<(AdaptiveCompletedBarPackage Package, bool Verified)> bars)
    {
        if (_runtime is not { Accepting: true } || _session is not { } session || _book is not { } book) return;
        try
        {
            foreach (var (token, tick) in group) book.Update(token, tick);
            if (_marks is { } board && !ReferenceEquals(_attachedBoard, board)) { _runtime.SetMarkBoard(board); _attachedBoard = board; }
            foreach (var (token, tick) in group) _runtime.ObserveMarkTick(token, tick);            // read-only live-mark board: O(1) per tick, contained, never on the frame path
            foreach (var (package, verified) in bars)
            {
                var bar = package.FutureBar;
                var input = new BarInput(session.SessionId, session.TradeDate, session.ModelVersion, session.AtmRuleVersion, bar.BarSeq, bar.StartAvailableAtUtc, bar.EndAvailableAtUtc, bar.LastSourceTickId, bar.DurationSeconds,
                    verified, verified ? null : "sidecar integrity unknown");
                var centre = package.OptionBand.Call?.CenterStrike ?? package.OptionBand.Put?.CenterStrike;
                _runtime.TryEnqueue(new V2BarNotice(session, input, centre, book.Snapshot(bar.EndAvailableAtUtc)));
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Paper Engine V2 could not hand over an availability group; the Observer is unaffected.");
        }
    }
}
