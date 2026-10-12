using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.State;

namespace NiftySignal.Trading.Adapters;

/// <summary>
/// The single translation layer from recorded source rows to trading-engine events (contract section A.16.1). Both the historical adapter and the
/// live-paper adapter call exactly this class; they differ only in HOW rows arrive (pages vs polls) and where heartbeats come from.
/// <para>
/// Pipeline per row: admission (known token, valid UTC timestamps, no negative values, duplicate / conflict / id-order checks, lateness against the
/// release boundary) → the existing <see cref="AdaptiveIncrementalTickNormalizer"/> (per-token payload de-duplication, availability =
/// max(received, exchange)) → availability window → buffer ordered by (AvailableAt, Id). A refused row changes NO state (not the normaliser, not
/// the adaptive engine, not any quote). The buffer is released as availability groups once the source has proven progress strictly beyond
/// <c>group time + StableOrderingLag</c>; a group is therefore never published while rows of the same availability instant could still arrive in a
/// later batch. Each released group goes through <see cref="AdaptiveObserverEngine.ProcessAvailabilityGroup"/> unchanged and is converted to events
/// in the engine's group order: option quotes by id, futures quotes by id, then the bars the group completed.
/// </para>
/// <para>
/// Time. A row's <c>ReceivedAt</c> and <c>ExchangeTimestamp</c> are the RECORDED values. Availability follows the existing adaptive rule and the
/// choice is kept as provenance on every quote (<see cref="AvailabilityBasis"/>); nothing is repaired, and no wall clock is ever read.
/// </para>
/// </summary>
public sealed class AdaptiveEventTranslator
{
    private const int MaxRetainedQuarantines = 10_000;

    private sealed record Buffered(string Token, CleanObserverTick Tick, SourceTick Source, AvailabilityBasis Basis);

    private sealed class BufferedOrder : IComparer<Buffered>
    {
        public int Compare(Buffered? x, Buffered? y)
        {
            var byTime = x!.Tick.AvailableAt.CompareTo(y!.Tick.AvailableAt);
            return byTime != 0 ? byTime : x.Tick.Id.CompareTo(y.Tick.Id);
        }
    }

    private readonly SessionFreeze _freeze;
    private readonly TranslatorOptions _options;
    private readonly AdaptiveObserverEngine _engine;
    private readonly AdaptiveIncrementalTickNormalizer _normalizer = new();
    private readonly HashSet<string> _optionTokens;
    private readonly SortedSet<Buffered> _buffer = new(new BufferedOrder());
    private readonly Dictionary<long, (string Fingerprint, DateTimeOffset Received)> _seen = new();
    private readonly Queue<(long Id, DateTimeOffset Received)> _seenOrder = new();
    private readonly Dictionary<string, ResidualReadingPoint> _previousResidual = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ResidualBases? Bases, string Basket)> _baskets;
    private readonly List<RowQuarantine> _quarantines = new();
    private readonly Dictionary<string, long> _quarantineCounts = new(StringComparer.Ordinal);
    private readonly string _readinessSpec;
    private readonly Dictionary<string, string> _lastReleasedRow = new(StringComparer.Ordinal);
    private long _highestReleasedId = long.MinValue;
    private long _highestId = long.MinValue;
    private DateTimeOffset _highWater = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReleasedGroup = DateTimeOffset.MinValue;
    private bool _finished;
    private long _groupOrdinal;
    private long _input;
    private long _translated;
    private long _redelivered;
    private long _payloadDuplicates;
    private long _outsideWindow;
    private long _quarantined;
    private long _clamped;
    private long _payloadDuplicatesFuture;
    private long _clampSkewTicks;
    private TimeSpan _maxClampSkew;
    private long _exchangeEqualsReceived;
    private long _bars;
    private int _maxBuffered;

    public AdaptiveEventTranslator(SessionFreeze freeze, TranslatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(freeze);
        _freeze = freeze;
        _options = options ?? new TranslatorOptions();
        var errors = _options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid translator options: " + string.Join(" ", errors), nameof(options));
        }

        _engine = new AdaptiveObserverEngine(
            freeze.Definition,
            freeze.WeeklyOptionExpiry,
            freeze.RiskFreeRate,
            freeze.StrongThreshold,
            freeze.SignalStartUtc,
            freeze.Options,
            freeze.ResidualAnchor);
        _optionTokens = freeze.Options.Select(o => o.Token).ToHashSet(StringComparer.Ordinal);
        _readinessSpec = _options.ReadinessSpec(freeze);
        _baskets = new Dictionary<string, (ResidualBases?, string)>(StringComparer.Ordinal)
        {
            [nameof(ResidualVariant.Atm)] = BasketOf(ResidualVariant.Atm),
            [nameof(ResidualVariant.AtmPlusMinus2)] = BasketOf(ResidualVariant.AtmPlusMinus2),
        };
    }

    public SessionFreeze Freeze => _freeze;

    public TranslatorOptions Options => _options;

    public string ReadinessSpec => _readinessSpec;

    /// <summary>Completed futures bars so far (read-only view of the adaptive engine; for tests and diagnostics).</summary>
    public IReadOnlyList<ExactAdaptiveBar> FutureBars => _engine.FutureBars;

    /// <summary>
    /// Exclusive stable boundary: every group with availability strictly before it is complete and may be released. It only moves forward. Derived
    /// from the recorded <c>ReceivedAt</c> of admitted rows and from source progress proofs (<see cref="AdvanceSource"/>), never from a wall clock.
    /// </summary>
    public DateTimeOffset Boundary
        => _finished ? DateTimeOffset.MaxValue
            : _highWater == DateTimeOffset.MinValue ? DateTimeOffset.MinValue
            : _highWater - _options.StableOrderingLag;

    public IReadOnlyList<RowQuarantine> Quarantines => _quarantines;

    public TranslatorCounters Counters => new(
        _input,
        _translated,
        _redelivered,
        _payloadDuplicates,
        _outsideWindow,
        _quarantined,
        new SortedDictionary<string, long>(_quarantineCounts, StringComparer.Ordinal),
        _clamped,
        _exchangeEqualsReceived,
        _groupOrdinal,
        _bars,
        _maxBuffered,
        _seen.Count,
        _payloadDuplicatesFuture,
        _payloadDuplicates - _payloadDuplicatesFuture,
        _maxClampSkew,
        _clamped == 0 ? 0d : _clampSkewTicks / (double)_clamped / TimeSpan.TicksPerMillisecond);

    /// <summary>
    /// Source progress proof: "every row with ReceivedAt before <paramref name="through"/> has been offered". Raises the stable boundary to
    /// <c>through - StableOrderingLag</c>. A caller must NOT claim progress while a batch was cut short (more rows pending): that is how poll batches
    /// are prevented from splitting an availability group.
    /// </summary>
    public void AdvanceSource(DateTimeOffset through)
    {
        if (through > _highWater)
        {
            _highWater = through;
        }
    }

    public RowDisposition Offer(SourceTick row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_finished)
        {
            throw new InvalidOperationException("The translator has finished; no further rows are accepted.");
        }

        _input++;
        if (row.Token != _freeze.FutureToken && !_optionTokens.Contains(row.Token))
        {
            return Refuse(row, RowRejection.UnknownToken, "Token is neither the session future nor a frozen option.");
        }

        if (row.ExchangeTimestamp.Year < 2000 || row.ReceivedAt.Year < 2000)
        {
            return Refuse(row, RowRejection.MissingTimestamp, "A timestamp is missing (default value); it is not repaired.");
        }

        if (row.ExchangeTimestamp.Offset != TimeSpan.Zero || row.ReceivedAt.Offset != TimeSpan.Zero)
        {
            return Refuse(row, RowRejection.NonUtcTimestamp, "Timestamps must be UTC; no conversion is attempted.");
        }

        if (row.Last < 0m || row.Bid < 0m || row.Ask < 0m || row.BidQty < 0 || row.AskQty < 0 || row.Volume < 0 || row.OpenInterest < 0)
        {
            return Refuse(row, RowRejection.NegativeValue, "A price, size, volume or open interest is negative.");
        }

        var fingerprint = row.Fingerprint();
        if (_seen.TryGetValue(row.Id, out var known))
        {
            if (known.Fingerprint == fingerprint)
            {
                _redelivered++;
                return RowDisposition.Redelivered;
            }

            return Refuse(row, RowRejection.ConflictingDuplicate, $"Id {row.Id} was already accepted with different content.");
        }

        if (row.Id <= _highestId)
        {
            return Refuse(row, RowRejection.IdOutOfOrder, $"Id {row.Id} is not above the highest accepted id {_highestId} and is not a remembered row.");
        }

        var basis = row.ReceivedAt < row.ExchangeTimestamp ? AvailabilityBasis.ExchangeTimestampClamp : AvailabilityBasis.ReceivedAt;
        var provisional = basis == AvailabilityBasis.ExchangeTimestampClamp ? row.ExchangeTimestamp : row.ReceivedAt;
        if (provisional < Boundary || provisional <= _lastReleasedGroup)
        {
            return Refuse(row, RowRejection.LateAvailability, $"Availability {provisional:O} is before the released boundary {Boundary:O}; it is never merged retroactively.");
        }

        // Admitted: from here the row is part of the source history (even if the normaliser or the window later drops it).
        _highestId = row.Id;
        _seen[row.Id] = (fingerprint, row.ReceivedAt);
        _seenOrder.Enqueue((row.Id, row.ReceivedAt));
        if (row.ReceivedAt > _highWater)
        {
            _highWater = row.ReceivedAt;
        }

        PruneSeen();
        if (row.ExchangeTimestamp == row.ReceivedAt)
        {
            _exchangeEqualsReceived++;
        }

        var clean = _normalizer.Process(row.Token, row.ToRaw());
        if (clean is not { } tick)
        {
            _payloadDuplicates++;
            if (row.Token == _freeze.FutureToken)
            {
                _payloadDuplicatesFuture++;
            }

            return RowDisposition.PayloadDuplicate;
        }

        var from = _options.AcceptFromUtc ?? _freeze.ObservationStartUtc;
        if (tick.AvailableAt < from || (_options.AcceptThroughUtc is { } through && tick.AvailableAt > through))
        {
            _outsideWindow++;
            return RowDisposition.OutsideWindow;
        }

        if (basis == AvailabilityBasis.ExchangeTimestampClamp)
        {
            _clamped++;
            var skew = row.ExchangeTimestamp - row.ReceivedAt;
            _clampSkewTicks += skew.Ticks;
            if (skew > _maxClampSkew)
            {
                _maxClampSkew = skew;
            }
        }

        _buffer.Add(new Buffered(row.Token, tick, row, basis));
        _maxBuffered = Math.Max(_maxBuffered, _buffer.Count);
        _translated++;
        return RowDisposition.Accepted;
    }

    /// <summary>Releases every group strictly before the stable boundary.</summary>
    public IReadOnlyList<TranslatedGroup> Release() => ReleaseBefore(Boundary);

    /// <summary>End of input: releases everything that is buffered (the source will deliver no more rows) and closes the translator.</summary>
    public IReadOnlyList<TranslatedGroup> Finish()
    {
        var groups = ReleaseBefore(DateTimeOffset.MaxValue);
        _finished = true;
        return groups;
    }

    private IReadOnlyList<TranslatedGroup> ReleaseBefore(DateTimeOffset boundary)
    {
        var groups = new List<TranslatedGroup>();
        while (_buffer.Count > 0 && _buffer.Min!.Tick.AvailableAt < boundary)
        {
            var at = _buffer.Min.Tick.AvailableAt;
            var rows = new List<Buffered>();
            while (_buffer.Count > 0 && _buffer.Min!.Tick.AvailableAt == at)
            {
                var next = _buffer.Min;
                _buffer.Remove(next);
                rows.Add(next);
            }

            groups.Add(Translate(at, rows));
            _lastReleasedGroup = at;
        }

        return groups;
    }

    private TranslatedGroup Translate(DateTimeOffset at, IReadOnlyList<Buffered> rows)
    {
        var packages = _engine.ProcessAvailabilityGroup(rows.Select(r => (r.Token, r.Tick)).ToArray());

        var quotes = new List<TranslatedQuote>(rows.Count);
        foreach (var r in rows.Where(r => r.Token != _freeze.FutureToken).OrderBy(r => r.Tick.Id))
        {
            quotes.Add(ToQuote(at, EventKind.OptionQuote, r));
        }

        foreach (var r in rows.Where(r => r.Token == _freeze.FutureToken).OrderBy(r => r.Tick.Id))
        {
            quotes.Add(ToQuote(at, EventKind.FutureQuote, r));
        }

        var bars = new List<TranslatedBar>(packages.Count);
        foreach (var package in packages)
        {
            if (package.FutureBar.EndAvailableAtUtc != at)
            {
                throw new InvalidOperationException($"Bar {package.FutureBar.BarSeq} ends at {package.FutureBar.EndAvailableAtUtc:O} but its group is at {at:O}; the ordering contract is broken.");
            }

            bars.Add(ToBar(package));
        }

        _bars += bars.Count;
        foreach (var r in rows)
        {
            _lastReleasedRow[r.Token] = r.Source.Fingerprint();
            _highestReleasedId = Math.Max(_highestReleasedId, r.Source.Id);
        }

        return new TranslatedGroup(at, _groupOrdinal++, quotes, bars);
    }

    /// <summary>
    /// Writes the translator's behaviour-relevant state AS OF THE RELEASED GROUPS: the last released row per token (the released-order mirror of the
    /// normaliser's "previous payload"), the highest released id, the last released group, the group ordinal, the residual readings kept for adjacent
    /// deltas, every completed bar and the partial bar. Rows that were offered but not yet released (the buffer), the stable-boundary high water, the
    /// remembered-id window and all counters depend on how the input was paged or polled, not on behaviour, and are excluded on purpose.
    /// <para>
    /// Limit: the adaptive engine's own accumulators (option flow books, band histories, microstructure trackers) are private to the parity-frozen
    /// observer and are not read here; they are covered through the completed bars, the readings above and, above all, by the continuation
    /// comparison after a resume (hidden-state divergence must surface in later bar metrics).
    /// </para>
    /// </summary>
    public void WriteState(StateWriter w)
    {
        using var scope = w.Scope("translator");
        w.Line("finished", _finished);
        w.Line("lastReleasedGroup", _lastReleasedGroup == DateTimeOffset.MinValue ? null : _lastReleasedGroup.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.Line("groupOrdinal", _groupOrdinal);
        w.Line("highestReleasedId", _highestReleasedId == long.MinValue ? null : _highestReleasedId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var token in _lastReleasedRow.Keys.Order(StringComparer.Ordinal))
        {
            w.Line($"lastRow[{token}]", _lastReleasedRow[token]);
        }

        foreach (var name in _previousResidual.Keys.Order(StringComparer.Ordinal))
        {
            var p = _previousResidual[name];
            w.Line($"previousResidual[{name}]", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.BarSeq}|{p.IsAvailable}|{p.CeResidual:R}|{p.PeResidual:R}|{p.CeResidualPct:R}|{p.PeResidualPct:R}|{p.DirectionalResidualPct:R}"));
        }

        foreach (var b in _engine.FutureBars)
        {
            w.Line(
                $"bar[{b.BarSeq}]",
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b.IsComplete}|{b.StartAvailableAtUtc.UtcTicks}|{b.EndAvailableAtUtc.UtcTicks}|{b.Open:R}|{b.High:R}|{b.Low:R}|{b.Close:R}|{b.Volume}|{b.TradeUpdates}|{b.FirstSourceTickId}|{b.LastSourceTickId}|{b.StrictBuyVolume}|{b.StrictSellVolume}|{b.StrictUnknownVolume}|{b.EnrichedBuyVolume}|{b.EnrichedSellVolume}|{b.EnrichedUnknownVolume}"));
        }

        var partial = _engine.PartialBar;
        w.Line("partialBar", $"{partial.AccumulatedVolume}|{partial.TargetVolume}|{partial.StartedAtUtc?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "~"}");
    }

    private static TranslatedQuote ToQuote(DateTimeOffset at, EventKind kind, Buffered r)
    {
        var s = r.Source;
        var bid = s.Bid > 0m ? s.Bid : (decimal?)null;
        var ask = s.Ask > 0m ? s.Ask : (decimal?)null;
        var quote = new QuoteEvent(at, kind, s.Id, s.Token, bid, ask, s.Last > 0m ? s.Last : null, bid is null ? null : s.BidQty, ask is null ? null : s.AskQty);
        return new TranslatedQuote(quote, new QuoteProvenance(s.Token, s.Id, s.ExchangeTimestamp, s.ReceivedAt, r.Basis));
    }

    private TranslatedBar ToBar(AdaptiveCompletedBarPackage package)
    {
        var bar = package.FutureBar;
        var info = BarReadiness.Build(bar, _engine.FutureBars, _freeze.Definition.BaseBarVolume, _freeze.SignalStartUtc, _readinessSpec);
        var evt = new BarCompletedEvent(bar.EndAvailableAtUtc, bar.BarSeq, _freeze.FutureToken, info);
        return new TranslatedBar(evt, MetricsOf(package), package);
    }

    // ------------------------------------------------------------------ derived metrics (captured at the bar's own boundary)

    private static readonly IReadOnlyList<string> Gaps = new[]
    {
        "RelativeOptionRollingStrictRatio: no definition exists in the adaptive sources; the per-side Call and Put rolling strict ratios are carried as they are and are NOT combined into a relative metric.",
        "FuturesBasis (spot): not translated in CP5; the session freeze carries no spot instrument.",
    };

    private BarMetrics MetricsOf(AdaptiveCompletedBarPackage package)
    {
        var bar = package.FutureBar;
        var rolling = package.FlowState.Rolling;
        var residuals = new List<ResidualMetrics>(2);
        if (package.Residuals.Count == 0)
        {
            foreach (var name in new[] { nameof(ResidualVariant.Atm), nameof(ResidualVariant.AtmPlusMinus2) })
            {
                residuals.Add(Unavailable(name, "Before the signal start: the adaptive engine computes no residual for this bar."));
            }
        }
        else
        {
            foreach (var r in package.Residuals.OrderBy(x => (int)x.Variant))
            {
                residuals.Add(ResidualOf(bar.BarSeq, r));
            }
        }

        return new BarMetrics(
            bar.BarSeq,
            _freeze.FutureToken,
            bar.Open,
            bar.High,
            bar.Low,
            bar.Close,
            bar.Volume,
            bar.StrictDeltaRatioTotal,
            rolling?.ReturnBps,
            rolling?.StrictDeltaRatioTotal,
            package.FlowState.State.ToString(),
            residuals,
            SideOf("Call", package.OptionBand.CallRolling),
            SideOf("Put", package.OptionBand.PutRolling),
            Gaps);
    }

    private static BandSideMetrics SideOf(string side, OptionBandRollingMetrics? rolling)
        => rolling is null
            ? new BandSideMetrics(side, false, null, null, null)
            : new BandSideMetrics(side, true, rolling.ContractStrictDeltaRatioTotal, rolling.NotionalStrictDeltaRatioTotal, rolling.WindowBars);

    private ResidualMetrics Unavailable(string variant, string reason)
        => new(variant, false, reason, null, null, null, null, null, null, null, null, _baskets[variant].Basket);

    /// <summary>
    /// Straddle residual % = 100 x (CeResidual + PeResidual) / (CeBase + PeBase), the existing <see cref="AdaptiveResidualProjection"/> formula, evaluated for
    /// THIS bar from this bar's reading and (for the adjacent delta only) the immediately previous bar's reading of the same variant. The bases are the
    /// frozen 09:30 anchor prices of the variant's basket. Nothing later than the bar's boundary is used.
    /// </summary>
    private ResidualMetrics ResidualOf(int barSeq, ResidualBarResult r)
    {
        var name = r.Variant.ToString();
        var (bases, basket) = _baskets[name];
        if (r.Reading is not { } reading)
        {
            _previousResidual[name] = new ResidualReadingPoint(barSeq, false, 0d, 0d, 0d, 0d, 0d);
            return Unavailable(name, r.UnavailableReason ?? "Residual reading unavailable.");
        }

        var point = new ResidualReadingPoint(barSeq, true, reading.CEResidual, reading.PEResidual, reading.CEResidualPct, reading.PEResidualPct, reading.DirectionalResidualPct);
        double? straddle = null;
        double? adjacent = null;
        if (bases is not null)
        {
            var series = _previousResidual.TryGetValue(name, out var previous) ? new[] { previous, point } : new[] { point };
            var derived = AdaptiveResidualProjection.Derive(series, bases).Single(x => x.BarSeq == barSeq);
            straddle = derived.StraddleResidualPct;
            adjacent = derived.AdjacentDirectionalResidualDelta;
        }

        _previousResidual[name] = point;
        return new ResidualMetrics(
            name,
            true,
            null,
            reading.CEResidualPct,
            reading.PEResidualPct,
            reading.DirectionalResidualPct,
            reading.CommonResidualPct,
            straddle,
            adjacent,
            bases?.CeBase,
            bases?.PeBase,
            basket);
    }

    private (ResidualBases? Bases, string Basket) BasketOf(ResidualVariant variant)
    {
        var anchor = _freeze.ResidualAnchor;
        if (anchor is null)
        {
            return (null, $"{variant}|anchor=unavailable");
        }

        var calls = variant == ResidualVariant.Atm ? anchor.Calls.Where(c => c.IsCenterStrike).ToList() : anchor.Calls.ToList();
        var puts = variant == ResidualVariant.Atm ? anchor.Puts.Where(c => c.IsCenterStrike).ToList() : anchor.Puts.ToList();
        var basket = FormattableString.Invariant(
            $"{variant}|anchor={anchor.AnchorTimeUtc:O}|expiry={anchor.ExpiryDate:yyyy-MM-dd}|center={anchor.CenterStrike}|CE=[{string.Join(",", calls.Select(c => c.Instrument.Token).Order(StringComparer.Ordinal))}]|PE=[{string.Join(",", puts.Select(c => c.Instrument.Token).Order(StringComparer.Ordinal))}]");
        if (calls.Count == 0 || puts.Count == 0)
        {
            return (null, basket);
        }

        var bases = new ResidualBases(calls.Sum(c => c.Price0930), puts.Sum(c => c.Price0930));
        return (bases, basket);
    }

    // ------------------------------------------------------------------ bookkeeping

    private RowDisposition Refuse(SourceTick row, RowRejection reason, string detail)
    {
        _quarantined++;
        var key = reason.ToString();
        _quarantineCounts[key] = _quarantineCounts.GetValueOrDefault(key) + 1;
        if (_quarantines.Count < MaxRetainedQuarantines)
        {
            _quarantines.Add(new RowQuarantine(reason, row.Token, row.Id, row.ReceivedAt, row.ExchangeTimestamp, detail));
        }

        return RowDisposition.Quarantined;
    }

    private void PruneSeen()
    {
        var cutoff = _highWater - _options.SeenIdRetention;
        while (_seenOrder.Count > 0 && _seenOrder.Peek().Received < cutoff)
        {
            _seen.Remove(_seenOrder.Dequeue().Id);
        }
    }
}
