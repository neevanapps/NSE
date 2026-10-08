using NiftySignal.Domain.Enums;

namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// Deterministic in-memory observer. Input ticks MUST already be cleaned/deduplicated and globally
/// ordered by (AvailableAt, Id). This class owns no database or wall-clock dependencies.
/// </summary>
public sealed class AdaptiveObserverEngine
{
    readonly AdaptiveSessionDefinition _session;
    readonly DateOnly _optionExpiry;
    readonly double _riskFreeRate;
    readonly double? _strongThreshold;
    readonly DateTimeOffset _signalStartUtc;
    readonly IReadOnlyList<ObserverOptionInstrument> _options;
    readonly HashSet<string> _optionTokens;
    readonly OptionResidualAnchor? _residualAnchor;

    readonly AdaptiveTradeFlowEnricher _futureEnricher = new();
    readonly ExactAdaptiveFuturesBarBuilder _futureBars;
    readonly AdaptiveInstrumentFlowAccumulator _optionFlow = new();
    readonly Dictionary<string, OptionQuoteSnapshot> _latestOptionQuotes = new(StringComparer.Ordinal);
    readonly Dictionary<string, OptionQuoteSnapshot> _quoteAtDiagnosticBoundary = new(StringComparer.Ordinal);
    bool _processingAvailabilityGroup;
    readonly List<OptionBandSideMetrics?> _callBandHistory = [];
    readonly List<OptionBandSideMetrics?> _putBandHistory = [];
    readonly List<double> _bandDurations = [];
    readonly Dictionary<ResidualVariant, double> _previousResidual = [];
    // Supplemental Level-1 book observation. Separate state: it never influences bar construction or any core output.
    readonly FuturesMicrostructureTracker _microstructure = new();
    // Level-1 book of every universe option, observed only to summarize the bar's center CE/PE (MicroDev/OFI). Never read by core logic.
    readonly Dictionary<string, FuturesMicrostructureTracker> _optionBooks = new(StringComparer.Ordinal);

    BandContext? _currentBand;
    IReadOnlyList<double>? _previousBandStrikes;
    DateTimeOffset? _lastAvailableAt;
    long _lastId;

    sealed record BandContext(
        OptionBandSelection? Selection,
        IReadOnlyDictionary<string, InstrumentFlowSnapshot> StartSnapshots,
        bool BandRolled,
        string? UnavailableReason);

    public AdaptiveObserverEngine(
        AdaptiveSessionDefinition session,
        DateOnly optionExpiry,
        double riskFreeRate,
        double? strongThreshold,
        DateTimeOffset signalStartUtc,
        IReadOnlyList<ObserverOptionInstrument> options,
        OptionResidualAnchor? residualAnchor)
    {
        _session = session;
        _optionExpiry = optionExpiry;
        _riskFreeRate = riskFreeRate;
        _strongThreshold = strongThreshold;
        _signalStartUtc = signalStartUtc;
        _options = options;
        _optionTokens=options.Select(x=>x.Token).ToHashSet(StringComparer.Ordinal);
        _residualAnchor = residualAnchor;
        _futureBars = new ExactAdaptiveFuturesBarBuilder(session);
    }

    public IReadOnlyList<ExactAdaptiveBar> FutureBars => _futureBars.Bars;
    public AdaptivePartialBarStatus PartialBar =>
        new(_futureBars.PartialVolume, _session.BaseBarVolume, _futureBars.PartialBarStartedAtUtc);

    public IReadOnlyList<AdaptiveCompletedBarPackage> Process(string token, CleanObserverTick tick)
    {
        EnsureOrder(tick);
        if (string.Equals(token, _session.FutureToken, StringComparison.Ordinal))
        {
            return ProcessFuture(tick);
        }

        if (_optionTokens.Contains(token))
        {
            _optionFlow.Process(token, tick);
            var boundary=AdaptiveResearchClock.DiagnosticTime(tick.AvailableAt);
            if (_latestOptionQuotes.TryGetValue(token,out var previous) && previous.AvailableAt<=boundary)
                _quoteAtDiagnosticBoundary[token]=previous;
            _latestOptionQuotes[token] = new OptionQuoteSnapshot(
                token, tick.AvailableAt, tick.Last, tick.Bid, tick.Ask, tick.OpenInterest);
            if (!_optionBooks.TryGetValue(token, out var book)) _optionBooks[token] = book = new FuturesMicrostructureTracker();
            book.Observe(tick);
        }

        return Array.Empty<AdaptiveCompletedBarPackage>();
    }

    /// <summary>
    /// A stable availability group is one causal boundary. Research as-of marks include every
    /// option quote at that boundary, even when its source Id follows the closing future Id.
    /// Per-instrument Id order is preserved; no quote with later availability is used.
    /// </summary>
    public IReadOnlyList<AdaptiveCompletedBarPackage> ProcessAvailabilityGroup(
        IReadOnlyList<(string Token,CleanObserverTick Tick)> group)
    {
        if(group.Count==0)return Array.Empty<AdaptiveCompletedBarPackage>();
        var availableAt=group[0].Tick.AvailableAt;
        if(group.Any(x=>x.Tick.AvailableAt!=availableAt))throw new ArgumentException("Expected one stable availability boundary.",nameof(group));
        if(_lastAvailableAt.HasValue && availableAt<=_lastAvailableAt.Value)
            throw new InvalidOperationException("Availability group arrived after its stable boundary was processed; deterministic recovery required.");
        _processingAvailabilityGroup=true;
        try
        {
            foreach(var item in group.Where(x=>x.Token!=_session.FutureToken).OrderBy(x=>x.Tick.Id)) Process(item.Token,item.Tick);
            var result=new List<AdaptiveCompletedBarPackage>();
            foreach(var item in group.Where(x=>x.Token==_session.FutureToken).OrderBy(x=>x.Tick.Id))result.AddRange(Process(item.Token,item.Tick));
            _lastId=group.Max(x=>x.Tick.Id);
            return result;
        }
        finally { _processingAvailabilityGroup=false; }
    }

    IReadOnlyList<AdaptiveCompletedBarPackage> ProcessFuture(CleanObserverTick tick)
    {
        _microstructure.Observe(tick);
        var e = _futureEnricher.Process(tick);
        var hadStarted = _futureBars.PartialBarStartedAtUtc.HasValue;
        var completed = _futureBars.Add(e);

        if (!hadStarted && _futureBars.PartialBarStartedAtUtc.HasValue && _currentBand is null)
        {
            _currentBand = SelectBandContext(_futureBars.PartialBarStartedAtUtc.Value, tick.Last);
        }

        if (completed.Count == 0)
        {
            return Array.Empty<AdaptiveCompletedBarPackage>();
        }

        var packages = new List<AdaptiveCompletedBarPackage>(completed.Count);
        foreach (var bar in completed)
        {
            var finishedBand = _currentBand;
            var optionBand = FinalizeBand(bar);
            var flowStates = AdaptiveFlowEvolutionTracker.Build(_futureBars.Bars);
            if (_strongThreshold.HasValue)
            {
                AdaptiveWeak2Classifier.Apply(flowStates, _strongThreshold.Value);
            }

            var flow = flowStates.Single(x => x.Bar.BarSeq == bar.BarSeq);
            var residuals = BuildResiduals(bar, flow.Rolling);
            var ready = AdaptiveReadinessPolicy.ConsecutiveValidBars(_futureBars.Bars, _session.BaseBarVolume, bar.BarSeq)
                >= AdaptiveReadinessPolicy.RequiredBars;
            var actionable = ready && flow.State == AdaptiveStateKind.Weak2 && bar.EndAvailableAtUtc >= _signalStartUtc;
            packages.Add(new AdaptiveCompletedBarPackage(bar, flow, optionBand, residuals, actionable,
                _microstructure.Complete(bar.StartAvailableAtUtc, bar.EndAvailableAtUtc)));

            // A crossing futures update can close multiple exact bars at the same timestamp.
            // After each close, start the next interval with a fresh causal band/snapshot at
            // that exact timestamp; zero-duration follow-on bars therefore correctly see zero
            // option activity rather than duplicating the preceding interval.
            _currentBand = SelectBandContext(bar.EndAvailableAtUtc, bar.Close);
            // The supplemental needs the NEXT selection's synthetic underlying (the same instant as the bar end) for end-of-bar IV,
            // so it is attached after the core package and the next band context exist. It reads state; it changes none.
            packages[^1] = packages[^1] with { OptionsSupplemental = BuildOptionsSupplemental(bar, finishedBand, optionBand, _currentBand) };
        }

        return packages;
    }

    OptionsSupplementalBar BuildOptionsSupplemental(ExactAdaptiveBar bar, BandContext? start, OptionBandBarResult band, BandContext? next) =>
        OptionsSupplementalCalculator.Build(new OptionsSupplementalInput
        {
            Selection = start?.Selection,
            UnavailableReason = start is null ? "Band context was not initialized." : start.UnavailableReason,
            Start = bar.StartAvailableAtUtc,
            End = bar.EndAvailableAtUtc,
            DurationSeconds = bar.DurationSeconds,
            RiskFreeRate = _riskFreeRate,
            StartUnderlying = start?.Selection?.SyntheticUnderlying,
            EndUnderlying = next?.Selection?.SyntheticUnderlying,
            StartSnapshot = token => start is not null && start.StartSnapshots.TryGetValue(token, out var s) ? s : null,
            EndSnapshot = _optionFlow.Snapshot,
            CallBand = band.Call,
            PutBand = band.Put,
            CallHistory = _callBandHistory,
            PutHistory = _putBandHistory,
            RollingWindowBars = _session.RollingWindowBars,
            Universe = _options,
            CenterBook = token => _optionBooks.TryGetValue(token, out var tracker)
                ? tracker.Complete(bar.StartAvailableAtUtc, bar.EndAvailableAtUtc) : null,
        });

    OptionBandBarResult FinalizeBand(ExactAdaptiveBar bar)
    {
        if (_currentBand is null)
        {
            _callBandHistory.Add(null);
            _putBandHistory.Add(null);
            _bandDurations.Add(bar.DurationSeconds);
            return new OptionBandBarResult(null, false, null, null, null, null, "Band context was not initialized.");
        }

        var ctx = _currentBand;
        if (ctx.Selection is null)
        {
            _callBandHistory.Add(null);
            _putBandHistory.Add(null);
            _bandDurations.Add(bar.DurationSeconds);
            return new OptionBandBarResult(null, ctx.BandRolled, null, null, null, null, ctx.UnavailableReason);
        }

        var tokens = ctx.Selection.Calls.Concat(ctx.Selection.Puts).Select(x => x.Token).Distinct().ToArray();
        var end = tokens.ToDictionary(x => x, _optionFlow.Snapshot, StringComparer.Ordinal);

        var call = AdaptiveOptionBandCalculator.BuildSideMetrics(
            ctx.Selection, OptionType.Call, bar.EndAvailableAtUtc, ctx.StartSnapshots, end);
        var put = AdaptiveOptionBandCalculator.BuildSideMetrics(
            ctx.Selection, OptionType.Put, bar.EndAvailableAtUtc, ctx.StartSnapshots, end);

        _callBandHistory.Add(call);
        _putBandHistory.Add(put);
        _bandDurations.Add(bar.DurationSeconds);

        var callRolling = OptionBandRollingCalculator.BuildLatest(_callBandHistory, _bandDurations, _session.RollingWindowBars);
        var putRolling = OptionBandRollingCalculator.BuildLatest(_putBandHistory, _bandDurations, _session.RollingWindowBars);

        return new OptionBandBarResult(ctx.Selection, ctx.BandRolled, call, put, callRolling, putRolling, null);
    }

    BandContext SelectBandContext(DateTimeOffset at, double futurePrice)
    {
        var selection = AdaptiveOptionBandCalculator.Select(
            at,
            _optionExpiry,
            futurePrice,
            _options,
            _latestOptionQuotes,
            _riskFreeRate);

        if (selection is null)
        {
            return new BandContext(null, new Dictionary<string, InstrumentFlowSnapshot>(), false,
                "Insufficient fresh CE/PE quotes to derive weekly synthetic ATM±2.");
        }

        var rolled = _previousBandStrikes is not null && !_previousBandStrikes.SequenceEqual(selection.Strikes);
        _previousBandStrikes = selection.Strikes.ToArray();

        var tokens = selection.Calls.Concat(selection.Puts).Select(x => x.Token).Distinct().ToArray();
        var snapshots = tokens.ToDictionary(x => x, _optionFlow.Snapshot, StringComparer.Ordinal);
        return new BandContext(selection, snapshots, rolled, null);
    }

    IReadOnlyList<ResidualBarResult> BuildResiduals(ExactAdaptiveBar bar, AdaptiveRollingState? rolling)
    {
        if (bar.EndAvailableAtUtc < _signalStartUtc)
        {
            return Array.Empty<ResidualBarResult>();
        }

        var futuresDirection = rolling?.PriceDirection ?? 0;
        if (_residualAnchor is null)
        {
            return new[]
            {
                UnavailableResidual(ResidualVariant.Atm, futuresDirection, "09:30 residual anchor unavailable."),
                UnavailableResidual(ResidualVariant.AtmPlusMinus2, futuresDirection, "09:30 residual anchor unavailable."),
            };
        }

        var diagnosticTime=AdaptiveResearchClock.DiagnosticTime(bar.EndAvailableAtUtc);
        var diagnosticQuotes=_latestOptionQuotes.ToDictionary(x=>x.Key,x=>x.Value.AvailableAt<=diagnosticTime
            ? x.Value : _quoteAtDiagnosticBoundary.GetValueOrDefault(x.Key),StringComparer.Ordinal)
            .Where(x=>x.Value is not null && x.Value.AvailableAt<=diagnosticTime)
            .ToDictionary(x=>x.Key,x=>x.Value!,StringComparer.Ordinal);
        var readings = OptionResidualModel.Evaluate(
            _residualAnchor,
            diagnosticTime,
            bar.Close,
            diagnosticQuotes,
            _riskFreeRate)
            .ToDictionary(x => x.Variant);

        var result = new List<ResidualBarResult>(2);
        foreach (var variant in new[] { ResidualVariant.Atm, ResidualVariant.AtmPlusMinus2 })
        {
            if (!readings.TryGetValue(variant, out var reading))
            {
                result.Add(UnavailableResidual(
                    variant, futuresDirection, "One or more fixed diagnostic option quotes are missing or stale."));
                continue;
            }

            double? delta = _previousResidual.TryGetValue(variant, out var previous)
                ? reading.DirectionalResidualPct - previous
                : null;
            _previousResidual[variant] = reading.DirectionalResidualPct;

            var residualDirection = Sign(reading.DirectionalResidualPct);
            var relationship = residualDirection == 0 || futuresDirection == 0
                ? "NEUTRAL"
                : residualDirection == futuresDirection ? "ALIGN" : "OPPOSE";

            result.Add(new ResidualBarResult(
                variant, reading, delta, residualDirection, futuresDirection, relationship, null));
        }

        return result;
    }

    static ResidualBarResult UnavailableResidual(
        ResidualVariant variant,
        int futuresDirection,
        string reason) =>
        new(variant, null, null, 0, futuresDirection, "NEUTRAL", reason);

    void EnsureOrder(CleanObserverTick tick)
    {
        if (_lastAvailableAt.HasValue)
        {
            var cmp = tick.AvailableAt.CompareTo(_lastAvailableAt.Value);
            if (cmp < 0 || (!_processingAvailabilityGroup && cmp == 0 && tick.Id < _lastId))
            {
                throw new InvalidOperationException(
                    $"AdaptiveObserverEngine requires globally ordered input. Previous=({_lastAvailableAt:O},{_lastId}), current=({tick.AvailableAt:O},{tick.Id}).");
            }
        }

        _lastAvailableAt = tick.AvailableAt;
        _lastId = tick.Id;
    }

    static int Sign(double x) => x > 0d ? 1 : x < 0d ? -1 : 0;
}
