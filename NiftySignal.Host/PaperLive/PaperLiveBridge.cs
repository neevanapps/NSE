using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Live;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Host.PaperLive;

/// <summary>
/// The Host's side of the live PAPER trading path (contract section B.6). The Adaptive Observer calls it at four points of its own live loop — session start, once per
/// availability group (after the group's bars were persisted and their sidecars verified), once per poll (clock heartbeat) and at the session close — and it translates what
/// the Observer FINALIZED into the host-independent <see cref="PaperLiveCoordinator"/>. It does not read ticks, does not build bars, does not touch the Observer's state and never
/// throws into the Observer: a failure here can only stop paper trading, visibly.
/// <para>
/// <b>PAPER ONLY.</b> There is no broker, order, HTTP or ingestion type anywhere in this path. <b>Off by default:</b> <c>Trading:PaperLive:Enabled</c> is false in source; with it
/// off (or with no strategy registered) every method returns immediately.
/// </para>
/// </summary>
public sealed class PaperLiveBridge(IConfiguration configuration, ILogger<PaperLiveBridge> logger) : IDisposable
{
    private readonly PaperLiveOptions _options = configuration.GetSection(PaperLiveOptions.SectionName).Get<PaperLiveOptions>() ?? new PaperLiveOptions();
    private PaperLiveCoordinator? _coordinator;
    private PostgresRunStore? _store;
    private string? _futureToken;
    private IReadOnlySet<string> _optionTokens = new HashSet<string>(StringComparer.Ordinal);
    private string _readinessSpec = string.Empty;

    public bool Enabled => _options.Enabled;

    public PaperLiveStatus Status => _coordinator?.Status
        ?? new PaperLiveStatus(_options.Enabled ? PaperLiveState.Idle : PaperLiveState.Disabled, _options.Enabled ? "waiting for a session" : "Trading:PaperLive:Enabled is false", null, null, null, null, null, 0);

    internal void OnSessionStarted(AdaptiveObserverWorker.LiveState live)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            var tick = _options.OptionTickSize;
            var contracts = tick is { } t && t > 0m
                ? live.Context.Options
                    .OrderBy(o => o.Token, StringComparer.Ordinal)
                    .Select(o => new OptionContract(o.Token, o.OptionType == OptionType.Call ? OptionSide.Call : OptionSide.Put, (decimal)o.Strike, o.ExpiryDate, o.LotSize, t, o.TradingSymbol))
                    .ToList()
                : new List<OptionContract>();
            _futureToken = live.Context.Definition.FutureToken;
            _optionTokens = contracts.Select(c => c.Token).ToHashSet(StringComparer.Ordinal);
            _readinessSpec = BarReadiness.SpecLabel(live.Context.SignalStartUtc, live.Context.Session.ModelVersion);
            EnsureCoordinator();
            _coordinator!.StartSession(new LiveSessionInfo(live.Context.Session.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), live.Context.Session.TradeDate, contracts, _futureToken, _readinessSpec));
        }
        catch (Exception e)
        {
            logger.LogError(e, "Paper-live could not start for this session; the Observer is unaffected.");
        }
    }

    /// <param name="group">The raw cleaned ticks of the availability group, exactly as the Observer processed them.</param>
    /// <param name="bars">The bars the Observer completed in this group, each with whether its persisted projection was verified.</param>
    internal void OnGroup(DateTimeOffset at, IReadOnlyList<(string Token, CleanObserverTick Tick)> group, IReadOnlyList<(AdaptiveCompletedBarPackage Package, bool Verified)> bars, AdaptiveObserverWorker.LiveState live)
    {
        if (_coordinator is null || _coordinator.Status.State != PaperLiveState.Live && _coordinator.Status.State != PaperLiveState.Faulted)
        {
            return;
        }

        try
        {
            var (quotes, liveBars) = Translate(_futureToken!, _optionTokens, group, bars, live.Engine.FutureBars, live.Context.Definition.BaseBarVolume, live.Context.SignalStartUtc, _readinessSpec);
            _coordinator.OnGroup(at, quotes, liveBars);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Paper-live could not process an availability group; the Observer is unaffected.");
        }
    }

    /// <summary>
    /// Pure translation of what the Observer produced for one availability group into the bridge's neutral types: the quotes of the future and of the option universe (the spot index
    /// and anything else is dropped) and the completed bars with their readiness evidence. No state, no clock, no I/O.
    /// </summary>
    internal static (IReadOnlyList<LiveQuote> Quotes, IReadOnlyList<LiveBar> Bars) Translate(
        string futureToken,
        IReadOnlySet<string> optionTokens,
        IReadOnlyList<(string Token, CleanObserverTick Tick)> group,
        IReadOnlyList<(AdaptiveCompletedBarPackage Package, bool Verified)> bars,
        IReadOnlyList<ExactAdaptiveBar> futureBars,
        long baseBarVolume,
        DateTimeOffset signalStartUtc,
        string readinessSpec)
    {
        var quotes = new List<LiveQuote>(group.Count);
        foreach (var (token, tick) in group)
        {
            var isFuture = token == futureToken;
            if (!isFuture && !optionTokens.Contains(token))
            {
                continue;
            }

            quotes.Add(new LiveQuote(token, tick.Id, isFuture, Price(tick.Bid), Price(tick.Ask), Price(tick.Last), tick.BidQty, tick.AskQty));
        }

        var liveBars = bars
            .Select(b => new LiveBar(
                b.Package.FutureBar.BarSeq,
                b.Package.FutureBar.EndAvailableAtUtc,
                BarReadiness.Build(b.Package.FutureBar, futureBars, baseBarVolume, signalStartUtc, readinessSpec),
                b.Verified))
            .ToList();
        return (quotes, liveBars);
    }

    internal void OnPollCompleted(DateTimeOffset stableCutoff)
    {
        try
        {
            _coordinator?.OnHeartbeat(stableCutoff);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Paper-live heartbeat failed; the Observer is unaffected.");
        }
    }

    internal void OnSessionClosing()
    {
        try
        {
            _coordinator?.FinishSession();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Paper-live could not finish the session; the Observer is unaffected.");
        }
    }

    /// <summary>The Observer's prices are doubles; the engine's are decimals. Four decimals keep every price of a 0.05 tick exactly and remove double noise.</summary>
    internal static decimal Price(double value) => double.IsFinite(value) && value > 0d ? decimal.Round((decimal)value, 4) : 0m;

    private void EnsureCoordinator()
    {
        if (_coordinator is not null)
        {
            return;
        }

        var connectionString = configuration.GetConnectionString(_options.ConnectionStringName);
        var strategies = PaperLiveSetup.Strategies(_options);
        Func<IRunStore> factory = () =>
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"ConnectionStrings:{_options.ConnectionStringName} is not set; paper-live cannot persist and therefore does not trade.");
            }

            _store ??= new PostgresRunStore(connectionString);
            return new BlockingRunStoreAdapter(_store, TimeSpan.FromSeconds(30));
        };
        _coordinator = new PaperLiveCoordinator(_options, strategies, factory, logger, $"{Environment.MachineName}:{Environment.ProcessId}");
        if (strategies.Count == 0)
        {
            logger.LogWarning("Paper-live is enabled but no strategy family is registered: it will not trade and nothing is journaled.");
        }
    }

    public void Dispose()
    {
        _coordinator?.Dispose();
        _store?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
