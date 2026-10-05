using NiftySignal.Domain;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;

namespace NiftySignal.Host;

public sealed record AdaptiveFuturesObservationSnapshot(
    DateOnly TradeDate,
    long OpeningVolume,
    double ExpectedDayVolume,
    long BaseBarVolume,
    long Rolling10BarVolume,
    int CompletedBars,
    int? RollingStartBar,
    int? RollingEndBar,
    long? RollingStrictDelta,
    double? RollingStrictDeltaRatio,
    int? RollingPriceDirection,
    int? RollingDeltaDirection,
    string? DominanceEvolution,
    decimal? LatestClose,
    double? LatestBarDurationSeconds);

/// <summary>
/// Observation-only live mirror of the NiftyResearcher exact-volume market-state clock.
/// It never places trades, changes existing score inputs, or writes to the existing
/// live volume-bar tables.
///
/// 09:15-09:30 IST futures ticks are buffered. At 09:30 the validated V1 linear model
/// freezes ExpectedDayVolume and the day's lot-rounded exact base-bar threshold. The buffered
/// ticks are then replayed through ExactResearchVolumeBarBuilder, so the live clock starts at
/// 09:15 without using any information that was unavailable at 09:30.
/// </summary>
public sealed class AdaptiveFuturesObservationEngine(
    ILogger<AdaptiveFuturesObservationEngine> logger)
{
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly ForecastCutoff = new(9, 30);

    readonly List<Tick> _buffer = [];
    readonly Queue<ExactResearchVolumeBar> _rolling = new();

    DateOnly _tradeDate;
    string? _futureToken;
    int _lotSize;
    DateTimeOffset _marketOpenUtc;
    DateTimeOffset _cutoffUtc;
    ExactResearchVolumeBarBuilder? _builder;
    AdaptiveVolumeForecast? _forecast;
    long? _previousRollingDelta;
    int? _previousRollingDeltaDirection;

    public AdaptiveFuturesObservationSnapshot? Current { get; private set; }
    public string? FutureToken => _futureToken;
    public bool ForecastFrozen => _forecast is not null;

    public void StartSession(DateOnly tradeDate, Instrument future)
    {
        if (future.InstrumentType != InstrumentType.Future || future.Underlying != "NIFTY")
            throw new ArgumentException("Observer requires the NIFTY future.", nameof(future));

        _tradeDate = tradeDate;
        _futureToken = future.Token;
        _lotSize = future.LotSize;
        _marketOpenUtc = ToUtc(tradeDate, MarketOpen);
        _cutoffUtc = ToUtc(tradeDate, ForecastCutoff);
        _buffer.Clear();
        _rolling.Clear();
        _builder = null;
        _forecast = null;
        _previousRollingDelta = null;
        _previousRollingDeltaDirection = null;
        Current = null;

        logger.LogInformation(
            "Adaptive futures observer initialized for {TradeDate}: future={Symbol} token={Token} lotSize={LotSize}; forecast freezes at 09:30 IST",
            tradeDate, future.TradingSymbol, future.Token, future.LotSize);
    }

    public void OnTick(Tick tick)
    {
        if (_futureToken is null || tick.Token != _futureToken) return;

        var availableAt = AvailableAt(tick);
        if (availableAt < _marketOpenUtc) return;

        if (_forecast is null)
        {
            _buffer.Add(tick);
            if (availableAt >= _cutoffUtc)
            {
                FreezeAndReplay();
            }
            return;
        }

        ApplyToBuilder(tick);
    }

    public void EndSession()
    {
        if (_forecast is null || _builder is null) return;

        var actualObserved = _builder.MaxCumVolume - _builder.FirstTickCumVolume;
        var errorPct = actualObserved > 0
            ? 100d * (_forecast.ExpectedDayVolume - actualObserved) / actualObserved
            : 0d;

        logger.LogInformation(
            "Adaptive futures observer EOD {TradeDate}: expectedDayVolume={Expected:0} actualObservedVolume={Actual} error={Error:+0.0;-0.0;0.0}% baseBar={BaseBar} completedBars={Bars}",
            _tradeDate, _forecast.ExpectedDayVolume, actualObserved, errorPct, _forecast.BaseBarVolume, _builder.Bars.Count);
    }

    void FreezeAndReplay()
    {
        var openingTicks = _buffer
            .Where(t => AvailableAt(t) < _cutoffUtc)
            .OrderBy(AvailableAt)
            .ThenBy(t => t.Id)
            .ToArray();

        var openingVolume = ObservedTradeVolume(openingTicks);
        _forecast = AdaptiveOpeningVolumeModel.Forecast(openingVolume, _lotSize);
        _builder = new ExactResearchVolumeBarBuilder(_forecast.BaseBarVolume);

        logger.LogInformation(
            "Adaptive futures observer 09:30 freeze {TradeDate}: openingVolume={OpeningVolume} expectedDayVolume={Expected:0} rawBaseBar={Raw:0.0} baseBar={BaseBar} rolling10Volume={RollingVolume}",
            _tradeDate,
            _forecast.OpeningVolume,
            _forecast.ExpectedDayVolume,
            _forecast.RawBaseBarVolume,
            _forecast.BaseBarVolume,
            _forecast.Rolling10BarVolume);

        foreach (var tick in _buffer.OrderBy(AvailableAt).ThenBy(t => t.Id))
        {
            ApplyToBuilder(tick);
        }

        _buffer.Clear();
    }

    void ApplyToBuilder(Tick tick)
    {
        foreach (var bar in _builder!.ApplyTick(tick))
        {
            HandleCompletedBar(bar);
        }
    }

    void HandleCompletedBar(ExactResearchVolumeBar bar)
    {
        _rolling.Enqueue(bar);
        while (_rolling.Count > 10) _rolling.Dequeue();

        int? rollingStart = null;
        int? rollingEnd = null;
        long? rollingDelta = null;
        double? rollingDeltaRatio = null;
        int? priceDirection = null;
        int? deltaDirection = null;
        string? evolution = null;

        if (_rolling.Count == 10)
        {
            var bars = _rolling.ToArray();
            rollingStart = bars[0].BarIndex;
            rollingEnd = bars[^1].BarIndex;
            rollingDelta = bars.Sum(x => x.StrictDelta);
            var rollingVolume = bars.Sum(x => x.Volume);
            rollingDeltaRatio = rollingVolume > 0 ? (double)rollingDelta.Value / rollingVolume : 0d;
            priceDirection = Math.Sign(bars[^1].ClosePrice - bars[0].OpenPrice);
            deltaDirection = Math.Sign(rollingDelta.Value);
            evolution = ClassifyEvolution(_previousRollingDelta, _previousRollingDeltaDirection, rollingDelta.Value, deltaDirection.Value);
            _previousRollingDelta = rollingDelta;
            _previousRollingDeltaDirection = deltaDirection;
        }

        Current = new AdaptiveFuturesObservationSnapshot(
            _tradeDate,
            _forecast!.OpeningVolume,
            _forecast.ExpectedDayVolume,
            _forecast.BaseBarVolume,
            _forecast.Rolling10BarVolume,
            bar.BarIndex,
            rollingStart,
            rollingEnd,
            rollingDelta,
            rollingDeltaRatio,
            priceDirection,
            deltaDirection,
            evolution,
            bar.ClosePrice,
            bar.Duration.TotalSeconds);

        logger.LogInformation(
            "Adaptive futures bar {BarIndex}: threshold={Threshold} close={Close} durationSec={Duration:0.###} strictDelta={BarDelta} rolling10Delta={RollingDelta} rolling10Ratio={RollingRatio} priceDir={PriceDirection} deltaDir={DeltaDirection} evolution={Evolution}",
            bar.BarIndex,
            _forecast.BaseBarVolume,
            bar.ClosePrice,
            bar.Duration.TotalSeconds,
            bar.StrictDelta,
            rollingDelta,
            rollingDeltaRatio,
            priceDirection,
            deltaDirection,
            evolution);
    }

    static string? ClassifyEvolution(long? previousDelta, int? previousDirection, long currentDelta, int currentDirection)
    {
        if (!previousDelta.HasValue || !previousDirection.HasValue) return null;

        if (previousDirection == 0 && currentDirection > 0) return "BuyerDominanceStarted";
        if (previousDirection == 0 && currentDirection < 0) return "SellerDominanceStarted";
        if (previousDirection != 0 && currentDirection == 0) return "DominanceNeutralized";
        if (previousDirection > 0 && currentDirection < 0) return "FlipToSeller";
        if (previousDirection < 0 && currentDirection > 0) return "FlipToBuyer";

        if (currentDirection != 0 && currentDirection == previousDirection)
        {
            var priorAbs = Math.Abs(previousDelta.Value);
            var currentAbs = Math.Abs(currentDelta);
            if (currentAbs > priorAbs) return "Strengthening";
            if (currentAbs < priorAbs) return "Weakening";
        }

        return "Unchanged";
    }

    static long ObservedTradeVolume(IReadOnlyList<Tick> ticks)
    {
        if (ticks.Count == 0) return 0;

        var maxCum = ticks[0].Volume;
        long traded = 0;
        for (var i = 1; i < ticks.Count; i++)
        {
            if (ticks[i].Volume > maxCum)
            {
                traded += ticks[i].Volume - maxCum;
                maxCum = ticks[i].Volume;
            }
        }

        return traded;
    }

    static DateTimeOffset AvailableAt(Tick tick) =>
        tick.ReceivedAt > tick.ExchangeTimestamp ? tick.ReceivedAt : tick.ExchangeTimestamp;

    static DateTimeOffset ToUtc(DateOnly day, TimeOnly time) =>
        new DateTimeOffset(day.ToDateTime(time), IstTime.Offset).ToUniversalTime();
}
