using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Reporting;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.State;
using NiftySignal.Trading.Core.Trading;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Adapters;

/// <summary>Run-level settings shared by both adapters. All values are explicit.</summary>
public sealed record RunSettings
{
    /// <summary>Explicit replay horizon (inclusive). Deadlines up to and including it fire even after the last data event; later data is not delivered. Default: the session end of the configuration.</summary>
    public DateTimeOffset? Horizon { get; init; }

    public RetentionPolicy Retention { get; init; } = RetentionPolicy.Default;

    public string RunId { get; init; } = "run-unspecified";

    public string RunMode { get; init; } = "Historical";
}

/// <summary>Counters about how the run was fed. They describe transport and bookkeeping, not economics, and are excluded from the equivalence comparison.</summary>
public sealed record TransportDiagnostics(
    long Pages,
    long Polls,
    long FailedPolls,
    long Heartbeats,
    long MaxBatchRows,
    long GroupsAfterHorizon,
    long ControlEventsDelivered);

public enum UnitKind
{
    DataGroup = 0,
    ControlOnly = 1,
    Advance = 2,
    Finish = 3,
}

/// <summary>One completed unit of work, reported to <see cref="TradingRun.UnitObserver"/>.</summary>
/// <param name="Kind">What was done.</param>
/// <param name="EngineNow">Engine time at the end of the unit.</param>
/// <param name="GroupOrdinal">Translator group ordinal for a data group.</param>
/// <param name="GroupAt">Availability instant of the data group.</param>
/// <param name="MaxSourceId">Highest source id among the group's quotes.</param>
/// <param name="OutputCount">Total engine outputs so far.</param>
/// <param name="Controls">Control / feed-health events delivered in this unit.</param>
public sealed record UnitInfo(UnitKind Kind, DateTimeOffset EngineNow, long? GroupOrdinal, DateTimeOffset? GroupAt, long? MaxSourceId, int OutputCount, IReadOnlyList<EngineEvent> Controls);

/// <summary>Per-variant activity of a run (engineering coverage evidence, not performance).</summary>
public sealed record VariantReport(
    string VariantId,
    int OpportunitiesCreated,
    int EntryIntents,
    int FilledEntries,
    IReadOnlyDictionary<string, int> ExitFillsByReason,
    int ClosedPositions,
    int UnresolvedPositions);

/// <summary>When the adaptive readiness evidence first said Ready, and how the bars were classified.</summary>
public sealed record ReadinessReport(
    int Bars,
    int ValidBars,
    int SignalStartedBars,
    int ReadyBars,
    DateTimeOffset? FirstBarEnd,
    DateTimeOffset? FirstReadyBarEnd,
    long? FirstReadyBarSeq);

public sealed record RunResult(
    IReadOnlyDictionary<string, ChannelDigest> Channels,
    TranslatorCounters Translator,
    EngineLoopStats Loop,
    int Outputs,
    int Fills,
    int Positions,
    int UnresolvedPositions,
    int EngineQuarantines,
    int SeenInLoop,
    TransportDiagnostics Transport,
    IReadOnlyDictionary<string, IReadOnlyList<CapturedLine>> Captured,
    IReadOnlyList<VariantReport> Variants,
    ReadinessReport Readiness);

/// <summary>
/// One trading run: translator + trading engine + loop + recorders, driven by an adapter (contract section A.16.2). The adapter decides how rows arrive;
/// this class decides what happens to them, so both adapters execute identical code after the translator. Control events (feed health, operator
/// commands) are merged into the data stream by availability time, with the engine's same-instant ordering (control and health before data).
/// </summary>
public sealed class TradingRun
{
    private readonly RunSettings _settings;
    private readonly DateTimeOffset _horizon;
    private readonly List<EngineEvent> _controls;
    private int _nextControl;
    private int _outputsRecorded;
    private long _groupsAfterHorizon;
    private long _controlsDelivered;
    private long _pages;
    private long _polls;
    private long _failedPolls;
    private long _heartbeats;
    private long _maxBatch;
    private readonly Dictionary<string, string> _opportunityLines = new(StringComparer.Ordinal);
    private bool _finished;
    private int _barCount;
    private int _validBars;
    private int _signalStartedBars;
    private int _readyBars;
    private DateTimeOffset? _firstBarEnd;
    private DateTimeOffset? _firstReadyEnd;
    private long? _firstReadySeq;

    public TradingRun(
        SessionFreeze freeze,
        TranslatorOptions translatorOptions,
        TradingConfig config,
        IReadOnlyDictionary<string, IBaseStrategy> strategies,
        RunSettings settings,
        RecorderOptions? recorderOptions = null,
        IEnumerable<EngineEvent>? controls = null)
    {
        ArgumentNullException.ThrowIfNull(freeze);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(strategies);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Translator = new AdaptiveEventTranslator(freeze, translatorOptions);
        Engine = new TradingEngine(config, freeze.TradeDate, freeze.ToContracts(), strategies, settings.RunId, settings.RunMode, settings.Retention);
        Loop = new EngineLoop(Engine, Engine.Variants.Schedule.DayStartUtc, settings.Retention.LoopSeenRetention);
        Engine.Start(Loop);
        Recorders = new RunRecorders(recorderOptions);
        _horizon = settings.Horizon ?? Engine.Variants.Schedule.SessionEndUtc;
        _controls = (controls ?? Array.Empty<EngineEvent>())
            .OrderBy(e => e, EventOrdering.Instance)
            .ToList();
    }

    public AdaptiveEventTranslator Translator { get; }

    public TradingEngine Engine { get; }

    public EngineLoop Loop { get; }

    public RunRecorders Recorders { get; }

    public DateTimeOffset Horizon => _horizon;

    // ------------------------------------------------------------------ adapter surface

    public RowDisposition Offer(SourceTick row)
    {
        var before = Translator.Quarantines.Count;
        var disposition = Translator.Offer(row);
        if (disposition == RowDisposition.Quarantined && Translator.Quarantines.Count > before)
        {
            var q = Translator.Quarantines[^1];
            if (!q.Transport)
            {
                Recorders[RunRecorders.Names.RowQuarantine].Add($"{q.Token}:{q.Id}", $"{Canonical.Time(q.ReceivedAt)}|{q.Reason}|{q.Token}|{q.Id}|{Canonical.Time(q.ExchangeTimestamp)}");
            }
        }

        return disposition;
    }

    /// <summary>Adds a control event (feed health, operator command) discovered while running, e.g. by the live watchdog. It is delivered in availability order with the data.</summary>
    public void AddControl(EngineEvent control)
    {
        ArgumentNullException.ThrowIfNull(control);
        EnsureOpen();
        if (control.Kind is not (EventKind.Control or EventKind.FeedHealth))
        {
            throw new ArgumentException("Only control and feed-health events can be added.", nameof(control));
        }

        var index = _controls.Count;
        while (index > _nextControl && EventOrdering.Instance.Compare(control, _controls[index - 1]) < 0)
        {
            index--;
        }

        _controls.Insert(index, control);
    }

    public void NotePage(int rows)
    {
        _pages++;
        _maxBatch = Math.Max(_maxBatch, rows);
    }

    public void NotePoll(int rows, bool failed)
    {
        _polls++;
        if (failed)
        {
            _failedPolls++;
        }

        _maxBatch = Math.Max(_maxBatch, rows);
    }

    /// <summary>Releases the groups that are stable and delivers them (and due control events). With <paramref name="heartbeat"/> the loop clock is also advanced to the stable boundary, like a live heartbeat.</summary>
    public void Pump(bool heartbeat)
    {
        EnsureOpen();
        var boundary = Translator.Boundary;
        Deliver(Translator.Release(), boundary);
        if (heartbeat && boundary != DateTimeOffset.MinValue)
        {
            var watermark = boundary < _horizon ? boundary : _horizon;
            if (watermark > Loop.Now)
            {
                Loop.AdvanceTo(watermark);
                _heartbeats++;
            }
        }
    }

    /// <summary>Ends the input: releases everything, delivers the remaining control events up to the horizon, fires every deadline up to and including the horizon, and builds the result.</summary>
    public RunResult Finish()
    {
        EnsureOpen();
        Deliver(Translator.Finish(), DateTimeOffset.MaxValue);
        Loop.Finish(_horizon);
        RecordOutputs();
        RecordFinalChannels();
        NotifyUnit(UnitKind.Finish, null, Array.Empty<EngineEvent>());
        _finished = true;
        return BuildResult();
    }

    // ------------------------------------------------------------------ delivery

    private void Deliver(IReadOnlyList<TranslatedGroup> groups, DateTimeOffset controlBoundary)
    {
        foreach (var group in groups)
        {
            DeliverControlsBefore(group.AvailableAt);
            RecordTranslated(group);
            if (group.AvailableAt > _horizon)
            {
                _groupsAfterHorizon++;
                continue;
            }

            var events = group.Events().ToList();
            while (_nextControl < _controls.Count && _controls[_nextControl].AvailableAt == group.AvailableAt)
            {
                events.Add(_controls[_nextControl++]);
                _controlsDelivered++;
            }

            Process(events, group);
        }

        DeliverControlsBefore(controlBoundary);
    }

    private void DeliverControlsBefore(DateTimeOffset boundary)
    {
        while (_nextControl < _controls.Count && _controls[_nextControl].AvailableAt < boundary && _controls[_nextControl].AvailableAt <= _horizon)
        {
            var at = _controls[_nextControl].AvailableAt;
            var batch = new List<EngineEvent>();
            while (_nextControl < _controls.Count && _controls[_nextControl].AvailableAt == at)
            {
                batch.Add(_controls[_nextControl++]);
                _controlsDelivered++;
            }

            Process(batch, null);
        }
    }

    private void Process(List<EngineEvent> events, TranslatedGroup? group)
    {
        var admitted = Engine.Admission.Admit(Loop, events);
        if (admitted.Count > 0)
        {
            Loop.ProcessGroup(admitted);
        }

        RecordOutputs();
        if (group is not null)
        {
            foreach (var bar in group.Bars)
            {
                var candles = Engine.Variants.Candles.CandlesPublishedFor(bar.Event.BarSeq);
                Recorders[RunRecorders.Names.Candles].Add($"b{bar.Event.BarSeq}", $"{Canonical.Time(group.AvailableAt)}|b{bar.Event.BarSeq}|" + Canonical.Json(candles.ToArray()));
            }
        }

        NotifyUnit(
            group is null ? UnitKind.ControlOnly : UnitKind.DataGroup,
            group,
            events.Where(e => e.Kind is EventKind.Control or EventKind.FeedHealth).ToList());
    }

    // ------------------------------------------------------------------ units (the durable layer's commit boundaries)

    /// <summary>
    /// Called after every complete unit of work: a delivered availability group, a control-only delivery, an effective clock advance and the final
    /// finish. At that moment all recorders, the engine and the translator are consistent, so it is the only place where a cursor, a digest or a state
    /// capture is meaningful (contract section A.18.4).
    /// </summary>
    public Action<UnitInfo>? UnitObserver { get; set; }

    private void NotifyUnit(UnitKind kind, TranslatedGroup? group, IReadOnlyList<EngineEvent> controls)
        => UnitObserver?.Invoke(new UnitInfo(
            kind,
            Loop.Now,
            group?.Ordinal,
            group?.AvailableAt,
            group is null || group.Quotes.Count == 0 ? null : group.Quotes.Max(q => q.Event.SourceId),
            Engine.Outputs.Count,
            controls));

    /// <summary>True when advancing the engine clock to <paramref name="watermark"/> would fire at least one deadline (an "effective" advance that must be journaled).</summary>
    public bool AdvanceWouldFire(DateTimeOffset watermark)
    {
        var next = Loop.PendingDeadlines.FirstOrDefault();
        return next is not null && next.Time < (watermark < _horizon ? watermark : _horizon);
    }

    /// <summary>
    /// Advances the engine clock to the watermark (capped at the horizon) and fires the deadlines strictly before it, but ONLY when at least one
    /// deadline fires: an advance that fires nothing changes no output and is deliberately not applied, so replay (which cannot know about it) and
    /// the live run keep exactly the same state. Every group before the watermark must already have been released. Returns whether it was applied.
    /// </summary>
    public bool AdvanceClock(DateTimeOffset watermark)
    {
        EnsureOpen();
        if (!AdvanceWouldFire(watermark))
        {
            return false;
        }

        Loop.AdvanceTo(watermark < _horizon ? watermark : _horizon);
        _heartbeats++;
        RecordOutputs();
        NotifyUnit(UnitKind.Advance, null, Array.Empty<EngineEvent>());
        return true;
    }

    /// <summary>The canonical behaviour-relevant state of the whole run (loop, engine, translator). See <see cref="StateWriter"/> for what is included and excluded.</summary>
    public StateWriter CaptureState()
    {
        var writer = new StateWriter();
        Loop.WriteState(writer);
        Engine.WriteState(writer);
        Translator.WriteState(writer);
        return writer;
    }

    // ------------------------------------------------------------------ recording

    private void RecordTranslated(TranslatedGroup group)
    {
        var quotes = Recorders[RunRecorders.Names.Quotes];
        foreach (var q in group.Quotes)
        {
            var e = q.Event;
            var p = q.Provenance;
            quotes.Add(
                $"{e.Kind}:{e.SourceId}",
                FormattableString.Invariant($"{Canonical.Time(e.AvailableAt)}|{e.Kind}|{e.SourceId}|{e.Token}|{e.Bid}|{e.Ask}|{e.Last}|{e.BidQty}|{e.AskQty}|ex={Canonical.Time(p.ExchangeTimestamp)}|rx={Canonical.Time(p.ReceivedAt)}|{p.Basis}"));
        }

        var bars = Recorders[RunRecorders.Names.Bars];
        var readiness = Recorders[RunRecorders.Names.Readiness];
        foreach (var b in group.Bars)
        {
            var info = b.Event.Info!;
            _barCount++;
            _validBars += info.IsValid ? 1 : 0;
            _signalStartedBars += info.SignalStarted ? 1 : 0;
            _firstBarEnd ??= info.EndAvailableAt;
            if (info.Ready)
            {
                _readyBars++;
                if (_firstReadyEnd is null)
                {
                    _firstReadyEnd = info.EndAvailableAt;
                    _firstReadySeq = b.Event.BarSeq;
                }
            }

            bars.Add($"b{b.Event.BarSeq}", $"{Canonical.Time(b.Event.AvailableAt)}|b{b.Event.BarSeq}|{Canonical.Json(info)}|{Canonical.Json(b.Metrics)}");
            readiness.Add($"b{b.Event.BarSeq}", FormattableString.Invariant($"{Canonical.Time(b.Event.AvailableAt)}|b{b.Event.BarSeq}|ready={info.Ready}|valid={info.IsValid}|consecutive={info.ConsecutiveValidBars}/{info.RequiredValidBars}|signalStarted={info.SignalStarted}|{info.ReadinessSpec}"));
        }
    }

    private void RecordOutputs()
    {
        var outputs = Engine.Outputs;
        var channel = Recorders[RunRecorders.Names.Outputs];
        var changed = _outputsRecorded < outputs.Count;
        for (; _outputsRecorded < outputs.Count; _outputsRecorded++)
        {
            var o = outputs[_outputsRecorded];
            channel.Add($"O{_outputsRecorded}:{o.GetType().Name}:{o.VariantId}", $"{Canonical.Time(o.At)}|{Canonical.Typed(o)}");
        }

        if (!changed)
        {
            return;
        }

        var opportunities = Recorders[RunRecorders.Names.Opportunities];
        foreach (var o in Engine.Variants.AllOpportunities)
        {
            var line = $"{o.VariantId}|{o.Id}|{o.State}|{o.Contract?.Token}|attempts={o.Attempts}|selection={o.SelectionAttempts}|cancel={o.CancelReason}|terminal={o.TerminalReason}";
            if (!_opportunityLines.TryGetValue(o.Id, out var previous) || previous != line)
            {
                _opportunityLines[o.Id] = line;
                opportunities.Add($"{o.VariantId}:{o.Id}", $"{Canonical.Time(Loop.Now)}|{line}");
            }
        }
    }

    /// <summary>The lines of the two channels that are only complete at the end (audit trail and daily summaries), in channel order. Used by the recorders and by the durable final record.</summary>
    public (IReadOnlyList<string> Audit, IReadOnlyList<string> Summaries) FinalChannelLines()
    {
        var audit = Engine.AuditTrailEntries()
            .Select(a =>
                // RunId and RunMode are labels of the run, not behaviour: they are the one documented exclusion in the audit comparison.
                $"{Canonical.Time(a.At)}|{a.AccountId}|{a.VariantId}|{a.OpportunityId}|{a.OrderId}|{a.PositionId}|{a.ConfigHash}|{a.VariantHash}|{a.Category}|{a.Reason}|{a.Event}|{a.Detail}")
            .ToList();
        var summaries = Engine.DailySummaries(_horizon).Select(s => Canonical.Json(s with { RunId = string.Empty })).ToList();
        return (audit, summaries);
    }

    private void RecordFinalChannels()
    {
        var audit = Recorders[RunRecorders.Names.Audit];
        foreach (var a in Engine.AuditTrailEntries())
        {
            // RunId and RunMode are labels of the run, not behaviour: they are the one documented exclusion in the audit comparison.
            audit.Add(
                $"A{a.Seq}",
                $"{Canonical.Time(a.At)}|{a.AccountId}|{a.VariantId}|{a.OpportunityId}|{a.OrderId}|{a.PositionId}|{a.ConfigHash}|{a.VariantHash}|{a.Category}|{a.Reason}|{a.Event}|{a.Detail}");
        }

        var summaries = Recorders[RunRecorders.Names.Summaries];
        foreach (var s in Engine.DailySummaries(_horizon))
        {
            summaries.Add(s.VariantId, Canonical.Json(s with { RunId = string.Empty }));
        }
    }

    private RunResult BuildResult()
    {
        var fills = Engine.Outputs.OfType<FillRecord>().Count();
        var positions = Engine.Execution.AllPositions.ToList();
        var captured = RunRecordersCaptured();
        return new RunResult(
            Recorders.Digests(),
            Translator.Counters,
            Loop.Stats,
            Engine.Outputs.Count,
            fills,
            positions.Count,
            positions.Count(p => p.State == PositionState.Unresolved),
            Engine.Admission.QuarantinedCount,
            Loop.SeenCount,
            new TransportDiagnostics(_pages, _polls, _failedPolls, _heartbeats, _maxBatch, _groupsAfterHorizon, _controlsDelivered),
            captured,
            VariantReports(),
            new ReadinessReport(_barCount, _validBars, _signalStartedBars, _readyBars, _firstBarEnd, _firstReadyEnd, _firstReadySeq));
    }

    private List<VariantReport> VariantReports()
    {
        var list = new List<VariantReport>();
        foreach (var v in Engine.Variants.Variants.OrderBy(x => x.VariantId, StringComparer.Ordinal))
        {
            var positions = Engine.Execution.PositionsOf(v.VariantId);
            var fills = Engine.Outputs.OfType<FillRecord>().Where(f => f.VariantId == v.VariantId).ToList();
            var exits = fills.Where(f => f.Side == NiftySignal.Trading.Core.Costs.TradeSide.Sell)
                .GroupBy(f => f.Reason?.ToString() ?? "Unspecified")
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            list.Add(new VariantReport(
                v.VariantId,
                Engine.Variants.AllOpportunities.Count(o => o.VariantId == v.VariantId),
                Engine.Outputs.OfType<EntryIntent>().Count(i => i.VariantId == v.VariantId),
                positions.Count,
                exits,
                positions.Count(p => p.State == PositionState.Closed),
                positions.Count(p => p.State == PositionState.Unresolved)));
        }

        return list;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<CapturedLine>> RunRecordersCaptured()
        => RunRecorders.Names.All.ToDictionary(n => n, n => Recorders[n].Captured, StringComparer.Ordinal);

    private void EnsureOpen()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The run has finished.");
        }
    }
}
