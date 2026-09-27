using System.Globalization;
using System.Text;
using NiftySignal.Features;

namespace NiftySignal.VolumeBarData;

public static class VolumeBar6500Revalidation
{
    public const long ThresholdContracts = 6500;

    public static readonly DateOnly[] PrimaryResearchDates =
    [
        new(2026, 9, 8),
        new(2026, 9, 9),
        new(2026, 9, 10),
        new(2026, 9, 11),
        new(2026, 9, 15),
        new(2026, 9, 16),
        new(2026, 9, 17),
        new(2026, 9, 18),
        new(2026, 9, 21),
    ];

    public sealed record Bar(
        int BarIndex,
        DateTimeOffset StartTimestamp,
        DateTimeOffset EndTimestamp,
        DateTimeOffset StartReceivedAt,
        DateTimeOffset AvailableAt,
        long FirstUpdateId,
        long LastUpdateId,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        long ObservedVolume,
        long? OvershootVolume,
        long? OpenInterestAtClose,
        int FeedUpdateCount,
        int DepthUpdateCount,
        long? FutureCvdProxyNet,
        double? DepthImbalance,
        double? OrderFlowImbalance,
        double? TopOfBookImbalance,
        bool IsFinalPartialBar)
    {
        public double ExchangeDurationSeconds => (EndTimestamp - StartTimestamp).TotalSeconds;
        public double ReceiptDurationSeconds => (AvailableAt - StartReceivedAt).TotalSeconds;

        public double? TobDepthDivergence =>
            DepthImbalance is { } depth && TopOfBookImbalance is { } tob ? depth - tob : null;

        public double? BarDurationUrgency
        {
            get
            {
                if (ExchangeDurationSeconds <= 0) { return null; }
                var direction = Math.Sign(Close - Open);
                return direction == 0 ? null : direction / ExchangeDurationSeconds;
            }
        }
    }

    public sealed record Observation(
        DateOnly TradingDate,
        int Dte,
        int BarIndex,
        DateTimeOffset Timestamp,
        DateTimeOffset AvailableAt,
        decimal FuturesClose,
        decimal SignalBarOpen,
        decimal SignalBarHigh,
        decimal SignalBarLow,
        double SignalBarChangePoints,
        double SignalBarReturn,
        double SignalBarRangePoints,
        long ObservedVolume,
        long? OvershootVolume,
        int FeedUpdateCount,
        int DepthUpdateCount,
        double ExchangeDurationSeconds,
        double ReceiptDurationSeconds,
        double? DepthImbalance,
        double? TopOfBookImbalance,
        double? TobDepthDivergence,
        double? OrderFlowImbalance,
        long? FutureCvdProxyNet,
        double? BarDurationUrgency,
        double? Forward1Points,
        double? Forward2Points,
        double? Forward4Points,
        long? Forward1ObservedVolume,
        long? Forward2ObservedVolume,
        long? Forward4ObservedVolume,
        double? MaxUp1Points,
        double? MaxUp2Points,
        double? MaxUp4Points,
        double? MaxDown1Points,
        double? MaxDown2Points,
        double? MaxDown4Points);

    public sealed record OvershootSummary(
        int FullBarCount,
        long MedianOvershoot,
        long P90Overshoot,
        long P95Overshoot,
        long P99Overshoot,
        long MaxOvershoot,
        long MaxObservedVolume,
        int BarsAtLeast7500,
        int BarsAtLeast10000,
        int BarsAtLeast13000);

    public sealed record ParityReport(
        int ComparedBarCount,
        int MismatchCount,
        string? FirstMismatch);

    public sealed record SessionAudit(
        DateOnly TradingDate,
        string FutureToken,
        int Dte,
        int TotalFeedUpdates,
        DateTimeOffset? FirstExchangeTimestamp,
        DateTimeOffset? LastExchangeTimestamp,
        DateTimeOffset? FirstReceivedAt,
        DateTimeOffset? LastReceivedAt,
        int ExchangeOrderingViolations,
        int IdOrderingViolations,
        int ReceivedAtBackwards,
        int NegativeVolumeDeltas,
        int FullBarCount,
        int PartialBarCount,
        int LegacyParityMismatchCount,
        long MedianOvershoot,
        long P90Overshoot,
        long P95Overshoot,
        long P99Overshoot,
        long MaxOvershoot,
        long MaxObservedVolume,
        int BarsAtLeast7500,
        int BarsAtLeast10000,
        int BarsAtLeast13000);

    public static IReadOnlyList<Bar> BuildBars(IReadOnlyList<OptionTickV2> ticks)
    {
        var builder = new WholeFeedUpdateNoCarryBuilder();
        var bars = new List<Bar>();

        foreach (var tick in ticks)
        {
            if (builder.Apply(tick) is { } completed)
            {
                bars.Add(completed);
            }
        }

        if (builder.FlushPartial() is { } partial)
        {
            bars.Add(partial);
        }

        return bars;
    }

    public static ParityReport CompareWithExistingVolumeBarBuilder(
        IReadOnlyList<OptionTickV2> ticks,
        IReadOnlyList<Bar> candidateBars)
    {
        var referenceBuilder = new VolumeBarBuilder(ThresholdContracts);
        var referenceBars = new List<VolumeBar>();
        DateTimeOffset? lastTimestamp = null;

        foreach (var tick in ticks)
        {
            lastTimestamp = tick.ExchangeTimestamp;
            var completed = referenceBuilder.ApplyTick(
                tick.ExchangeTimestamp,
                tick.LastPrice,
                tick.Volume,
                tick.Depth,
                tick.OpenInterest);

            if (completed is not null)
            {
                referenceBars.Add(completed);
            }
        }

        if (lastTimestamp is { } last && referenceBuilder.FlushPartial(last) is { } partial)
        {
            referenceBars.Add(partial);
        }

        var mismatches = 0;
        string? firstMismatch = null;

        if (referenceBars.Count != candidateBars.Count)
        {
            mismatches++;
            firstMismatch = $"bar-count mismatch: existing={referenceBars.Count}, audited={candidateBars.Count}";
        }

        var comparable = Math.Min(referenceBars.Count, candidateBars.Count);
        for (var i = 0; i < comparable; i++)
        {
            var mismatch = FirstBarMismatch(referenceBars[i], candidateBars[i]);
            if (mismatch is null) { continue; }

            mismatches++;
            firstMismatch ??= $"bar {i}: {mismatch}";
        }

        return new ParityReport(comparable, mismatches, firstMismatch);
    }

    public static IReadOnlyList<Observation> BuildObservations(
        DateOnly tradingDate,
        int dte,
        IReadOnlyList<Bar> bars)
    {
        var full = bars.Where(b => !b.IsFinalPartialBar).ToList();
        var rows = new List<Observation>(full.Count);

        for (var i = 0; i < full.Count; i++)
        {
            var bar = full[i];
            rows.Add(new Observation(
                tradingDate,
                dte,
                bar.BarIndex,
                bar.EndTimestamp,
                bar.AvailableAt,
                bar.Close,
                bar.Open,
                bar.High,
                bar.Low,
                (double)(bar.Close - bar.Open),
                bar.Open != 0 ? (double)((bar.Close / bar.Open) - 1m) : 0.0,
                (double)(bar.High - bar.Low),
                bar.ObservedVolume,
                bar.OvershootVolume,
                bar.FeedUpdateCount,
                bar.DepthUpdateCount,
                bar.ExchangeDurationSeconds,
                bar.ReceiptDurationSeconds,
                bar.DepthImbalance,
                bar.TopOfBookImbalance,
                bar.TobDepthDivergence,
                bar.OrderFlowImbalance,
                bar.FutureCvdProxyNet,
                bar.BarDurationUrgency,
                Forward(full, i, 1),
                Forward(full, i, 2),
                Forward(full, i, 4),
                ForwardObservedVolume(full, i, 1),
                ForwardObservedVolume(full, i, 2),
                ForwardObservedVolume(full, i, 4),
                MaxUp(full, i, 1),
                MaxUp(full, i, 2),
                MaxUp(full, i, 4),
                MaxDown(full, i, 1),
                MaxDown(full, i, 2),
                MaxDown(full, i, 4)));
        }

        return rows;
    }

    public static OvershootSummary SummarizeOvershoot(IReadOnlyList<Bar> bars)
    {
        var full = bars.Where(b => !b.IsFinalPartialBar).ToList();
        if (full.Count == 0)
        {
            return new OvershootSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var overshoots = full
            .Select(b => b.OvershootVolume ?? throw new InvalidOperationException("Full bar missing overshoot."))
            .OrderBy(x => x)
            .ToArray();

        return new OvershootSummary(
            full.Count,
            NearestRank(overshoots, 0.50),
            NearestRank(overshoots, 0.90),
            NearestRank(overshoots, 0.95),
            NearestRank(overshoots, 0.99),
            overshoots[^1],
            full.Max(b => b.ObservedVolume),
            full.Count(b => b.ObservedVolume >= 7_500),
            full.Count(b => b.ObservedVolume >= 10_000),
            full.Count(b => b.ObservedVolume >= 13_000));
    }

    public static SessionAudit AuditSession(
        DateOnly tradingDate,
        string futureToken,
        int dte,
        IReadOnlyList<OptionTickV2> ticks,
        IReadOnlyList<Bar> bars,
        ParityReport parity)
    {
        var exchangeViolations = 0;
        var idViolations = 0;
        var receivedBackwards = 0;
        var negativeVolumeDeltas = 0;

        for (var i = 1; i < ticks.Count; i++)
        {
            var previous = ticks[i - 1];
            var current = ticks[i];

            if (current.ExchangeTimestamp < previous.ExchangeTimestamp) { exchangeViolations++; }
            if (current.Id <= previous.Id) { idViolations++; }
            if (current.ReceivedAt < previous.ReceivedAt) { receivedBackwards++; }
            if (current.Volume < previous.Volume) { negativeVolumeDeltas++; }
        }

        var overshoot = SummarizeOvershoot(bars);

        return new SessionAudit(
            tradingDate,
            futureToken,
            dte,
            ticks.Count,
            ticks.Count == 0 ? null : ticks[0].ExchangeTimestamp,
            ticks.Count == 0 ? null : ticks[^1].ExchangeTimestamp,
            ticks.Count == 0 ? null : ticks[0].ReceivedAt,
            ticks.Count == 0 ? null : ticks[^1].ReceivedAt,
            exchangeViolations,
            idViolations,
            receivedBackwards,
            negativeVolumeDeltas,
            bars.Count(b => !b.IsFinalPartialBar),
            bars.Count(b => b.IsFinalPartialBar),
            parity.MismatchCount,
            overshoot.MedianOvershoot,
            overshoot.P90Overshoot,
            overshoot.P95Overshoot,
            overshoot.P99Overshoot,
            overshoot.MaxOvershoot,
            overshoot.MaxObservedVolume,
            overshoot.BarsAtLeast7500,
            overshoot.BarsAtLeast10000,
            overshoot.BarsAtLeast13000);
    }

    static string? FirstBarMismatch(VolumeBar expected, Bar actual)
    {
        if (expected.StartTimestamp != actual.StartTimestamp)
            return $"StartTimestamp existing={expected.StartTimestamp:O}, audited={actual.StartTimestamp:O}";
        if (expected.EndTimestamp != actual.EndTimestamp)
            return $"EndTimestamp existing={expected.EndTimestamp:O}, audited={actual.EndTimestamp:O}";
        if (expected.OpenPrice != actual.Open)
            return $"Open existing={expected.OpenPrice}, audited={actual.Open}";
        if (expected.HighPrice != actual.High)
            return $"High existing={expected.HighPrice}, audited={actual.High}";
        if (expected.LowPrice != actual.Low)
            return $"Low existing={expected.LowPrice}, audited={actual.Low}";
        if (expected.ClosePrice != actual.Close)
            return $"Close existing={expected.ClosePrice}, audited={actual.Close}";
        if (expected.Volume != actual.ObservedVolume)
            return $"Volume existing={expected.Volume}, audited={actual.ObservedVolume}";
        if (expected.OpenInterestAtClose != actual.OpenInterestAtClose)
            return $"OI existing={expected.OpenInterestAtClose}, audited={actual.OpenInterestAtClose}";
        if (expected.TickCount != actual.FeedUpdateCount)
            return $"FeedUpdateCount existing={expected.TickCount}, audited={actual.FeedUpdateCount}";
        if (expected.FutureCvdNet != actual.FutureCvdProxyNet)
            return $"CVD proxy existing={expected.FutureCvdNet}, audited={actual.FutureCvdProxyNet}";
        if (!Same(expected.DepthImbalance, actual.DepthImbalance))
            return $"DepthImbalance existing={expected.DepthImbalance}, audited={actual.DepthImbalance}";
        if (!Same(expected.OrderFlowImbalance, actual.OrderFlowImbalance))
            return $"OFI existing={expected.OrderFlowImbalance}, audited={actual.OrderFlowImbalance}";
        if (!Same(expected.TopOfBookImbalance, actual.TopOfBookImbalance))
            return $"TOB existing={expected.TopOfBookImbalance}, audited={actual.TopOfBookImbalance}";

        return null;
    }

    static bool Same(double? left, double? right)
    {
        if (left is null || right is null) { return left == right; }
        return Math.Abs(left.Value - right.Value) <= 1e-12;
    }

    static long NearestRank(IReadOnlyList<long> sorted, double percentile)
    {
        if (sorted.Count == 0) { return 0; }
        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        var index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    static double? Forward(IReadOnlyList<Bar> bars, int index, int horizon)
    {
        var target = index + horizon;
        return target < bars.Count ? (double)(bars[target].Close - bars[index].Close) : null;
    }

    static long? ForwardObservedVolume(IReadOnlyList<Bar> bars, int index, int horizon)
    {
        if (index + horizon >= bars.Count) { return null; }

        long total = 0;
        for (var i = index + 1; i <= index + horizon; i++)
        {
            total += bars[i].ObservedVolume;
        }

        return total;
    }

    static double? MaxUp(IReadOnlyList<Bar> bars, int index, int horizon)
    {
        if (index + horizon >= bars.Count) { return null; }
        var anchor = bars[index].Close;
        var maxHigh = bars.Skip(index + 1).Take(horizon).Max(b => b.High);
        return Math.Max(0.0, (double)(maxHigh - anchor));
    }

    static double? MaxDown(IReadOnlyList<Bar> bars, int index, int horizon)
    {
        if (index + horizon >= bars.Count) { return null; }
        var anchor = bars[index].Close;
        var minLow = bars.Skip(index + 1).Take(horizon).Min(b => b.Low);
        return Math.Max(0.0, (double)(anchor - minLow));
    }

    sealed class WholeFeedUpdateNoCarryBuilder
    {
        readonly FutureCvdProxyAccumulator _cvd = new();
        readonly DepthImbalanceAccumulator _depth = new();
        readonly OrderFlowImbalanceAccumulator _ofi = new();
        readonly TopOfBookImbalanceAccumulator _tob = new();

        long? _previousCumulativeVolume;
        long? _lastId;
        DateTimeOffset? _lastExchangeTimestamp;

        int _barIndex;
        long _barVolume;

        bool _hasOpenBar;
        DateTimeOffset _startTimestamp;
        DateTimeOffset _endTimestamp;
        DateTimeOffset _startReceivedAt;
        DateTimeOffset _availableAt;
        long _firstUpdateId;
        long _lastUpdateId;
        decimal _open;
        decimal _high;
        decimal _low;
        decimal _close;
        long? _lastOpenInterest;
        int _feedUpdateCount;
        int _depthUpdateCount;

        public Bar? Apply(OptionTickV2 tick)
        {
            if (_lastId is { } lastId && tick.Id <= lastId)
                throw new InvalidDataException($"Feed update Id must strictly increase: {lastId} -> {tick.Id}.");

            if (_lastExchangeTimestamp is { } lastTs && tick.ExchangeTimestamp < lastTs)
                throw new InvalidDataException(
                    $"ExchangeTimestamp must not go backwards: {lastTs:O} -> {tick.ExchangeTimestamp:O}.");

            var delta = _previousCumulativeVolume is { } previousVolume
                ? tick.Volume - previousVolume
                : 0;

            if (delta < 0)
                throw new InvalidDataException(
                    $"Negative cumulative-volume delta at update {tick.Id}: {_previousCumulativeVolume} -> {tick.Volume}.");

            _previousCumulativeVolume = tick.Volume;
            _lastId = tick.Id;
            _lastExchangeTimestamp = tick.ExchangeTimestamp;

            StartBarIfNeeded(tick);

            _endTimestamp = tick.ExchangeTimestamp;
            _availableAt = tick.ReceivedAt > _availableAt ? tick.ReceivedAt : _availableAt;
            _lastUpdateId = tick.Id;
            _high = Math.Max(_high, tick.LastPrice);
            _low = Math.Min(_low, tick.LastPrice);
            _close = tick.LastPrice;
            _lastOpenInterest = tick.OpenInterest ?? _lastOpenInterest;
            _feedUpdateCount++;
            _barVolume += delta;

            if (tick.Depth is { } depth)
            {
                _depth.ApplyTick(depth);
                _ofi.ApplyTick(depth);
                _tob.ApplyTick(depth);
                _depthUpdateCount++;

                if (delta > 0)
                    _cvd.ApplyTick(tick.LastPrice, depth, delta);
            }

            return _barVolume >= ThresholdContracts
                ? CompleteBar(isFinalPartialBar: false)
                : null;
        }

        public Bar? FlushPartial() =>
            _hasOpenBar ? CompleteBar(isFinalPartialBar: true) : null;

        void StartBarIfNeeded(OptionTickV2 tick)
        {
            if (_hasOpenBar) { return; }

            _hasOpenBar = true;
            _startTimestamp = tick.ExchangeTimestamp;
            _endTimestamp = tick.ExchangeTimestamp;
            _startReceivedAt = tick.ReceivedAt;
            _availableAt = tick.ReceivedAt;
            _firstUpdateId = tick.Id;
            _lastUpdateId = tick.Id;
            _open = tick.LastPrice;
            _high = tick.LastPrice;
            _low = tick.LastPrice;
            _close = tick.LastPrice;
            _barVolume = 0;
            _feedUpdateCount = 0;
            _depthUpdateCount = 0;
        }

        Bar CompleteBar(bool isFinalPartialBar)
        {
            long? overshoot = null;
            if (!isFinalPartialBar)
            {
                overshoot = _barVolume - ThresholdContracts;
                if (overshoot.Value < 0)
                    throw new InvalidOperationException(
                        $"Completed bar {_barIndex} below threshold: {_barVolume}.");
            }

            var bar = new Bar(
                _barIndex++,
                _startTimestamp,
                _endTimestamp,
                _startReceivedAt,
                _availableAt,
                _firstUpdateId,
                _lastUpdateId,
                _open,
                _high,
                _low,
                _close,
                _barVolume,
                overshoot,
                _lastOpenInterest,
                _feedUpdateCount,
                _depthUpdateCount,
                _cvd.CadenceNet,
                _depth.CadenceImbalance,
                _ofi.CadenceNet,
                _tob.CadenceImbalance,
                isFinalPartialBar);

            _hasOpenBar = false;
            _barVolume = 0;
            _feedUpdateCount = 0;
            _depthUpdateCount = 0;
            _cvd.ResetCadence();
            _depth.Reset();
            _ofi.ResetCadence();
            _tob.Reset();

            return bar;
        }
    }
}

public static class VolumeBar6500RevalidationRunner
{
    public static async Task<int> RunAsync(string inputRoot, string outputDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(inputRoot))
        {
            Console.Error.WriteLine($"Input directory not found: {inputRoot}");
            return 2;
        }

        Directory.CreateDirectory(outputDirectory);

        var allBars = new List<(DateOnly Date, int Dte, VolumeBar6500Revalidation.Bar Bar)>();
        var allObservations = new List<VolumeBar6500Revalidation.Observation>();
        var audits = new List<VolumeBar6500Revalidation.SessionAudit>();

        foreach (var date in VolumeBar6500Revalidation.PrimaryResearchDates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dayDir = Path.Combine(inputRoot, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!Directory.Exists(dayDir))
                throw new DirectoryNotFoundException($"Missing predeclared primary session: {dayDir}");

            var manifest = await OptionTickReaderV2.LoadManifestAsync(dayDir);
            var future = manifest
                .Where(m => string.Equals(m.InstrumentType, "Future", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(m.Underlying, "NIFTY", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.ExpiryDate)
                .FirstOrDefault()
                ?? throw new InvalidDataException($"{date:yyyy-MM-dd}: no NIFTY Future in instruments.json.");

            var nearestOptionExpiry = manifest
                .Where(m => string.Equals(m.InstrumentType, "Option", StringComparison.OrdinalIgnoreCase)
                    && m.ExpiryDate is { } expiry && expiry >= date)
                .Select(m => m.ExpiryDate!.Value)
                .OrderBy(expiry => expiry)
                .FirstOrDefault();

            if (nearestOptionExpiry == default)
                throw new InvalidDataException($"{date:yyyy-MM-dd}: no nearest-expiry option chain in instruments.json.");

            var dte = nearestOptionExpiry.DayNumber - date.DayNumber;
            var futurePath = Path.Combine(dayDir, $"{future.Token}.ndjson");
            if (!File.Exists(futurePath))
                throw new FileNotFoundException($"{date:yyyy-MM-dd}: Future feed-update file missing.", futurePath);

            var ticks = OptionTickReaderV2.LoadFile(futurePath);

            var orderingViolations = OptionTickReaderV2.ValidateOrdering(ticks);
            if (orderingViolations.Count > 0)
                throw new InvalidDataException(
                    $"{date:yyyy-MM-dd}: deterministic feed-update ordering failed: {orderingViolations[0]}");

            var volumeReport = OptionTickReaderV2.ValidateCumulativeVolume(ticks);
            if (volumeReport.NegativeDeltaCount > 0)
                throw new InvalidDataException(
                    $"{date:yyyy-MM-dd}: {volumeReport.NegativeDeltaCount} negative cumulative-volume delta(s).");

            var bars = VolumeBar6500Revalidation.BuildBars(ticks);
            var parity = VolumeBar6500Revalidation.CompareWithExistingVolumeBarBuilder(ticks, bars);
            if (parity.MismatchCount > 0)
                throw new InvalidDataException(
                    $"{date:yyyy-MM-dd}: audited builder does not match existing VolumeBarBuilder(6500): " +
                    $"{parity.FirstMismatch}");

            var audit = VolumeBar6500Revalidation.AuditSession(date, future.Token, dte, ticks, bars, parity);
            var observations = VolumeBar6500Revalidation.BuildObservations(date, dte, bars);

            audits.Add(audit);
            allObservations.AddRange(observations);
            allBars.AddRange(bars.Select(b => (date, dte, b)));

            Console.WriteLine(
                $"{date:yyyy-MM-dd}: updates={ticks.Count:N0}, fullBars={audit.FullBarCount:N0}, " +
                $"partialBars={audit.PartialBarCount}, parity=PASS, medianOvershoot={audit.MedianOvershoot:N0}, " +
                $"p95Overshoot={audit.P95Overshoot:N0}, maxOvershoot={audit.MaxOvershoot:N0}, " +
                $"observations={observations.Count:N0}");
        }

        var aggregate = VolumeBar6500Revalidation.SummarizeOvershoot(allBars.Select(x => x.Bar).ToList());
        Console.WriteLine(
            $"Aggregate overshoot: bars={aggregate.FullBarCount:N0}, median={aggregate.MedianOvershoot:N0}, " +
            $"p90={aggregate.P90Overshoot:N0}, p95={aggregate.P95Overshoot:N0}, " +
            $"p99={aggregate.P99Overshoot:N0}, max={aggregate.MaxOvershoot:N0}; " +
            $">=7500={aggregate.BarsAtLeast7500:N0}, >=10000={aggregate.BarsAtLeast10000:N0}, " +
            $">=13000={aggregate.BarsAtLeast13000:N0}");

        await WriteAuditCsvAsync(Path.Combine(outputDirectory, "session-audit.csv"), audits);
        await WriteBarsCsvAsync(Path.Combine(outputDirectory, "bars-6500.csv"), allBars);
        await WriteObservationsCsvAsync(Path.Combine(outputDirectory, "observations-6500.csv"), allObservations);
        await VolumeBar6500MetricAnalysis.WriteReportsAsync(allObservations, outputDirectory);
        await VolumeBar6500DurationIncrementalAnalysis.WriteReportsAsync(allObservations, outputDirectory);

        Console.WriteLine($"6500 raw-feed-update revalidation + metric analyses complete -> {outputDirectory}");
        return 0;
    }

    static async Task WriteAuditCsvAsync(
        string path,
        IReadOnlyList<VolumeBar6500Revalidation.SessionAudit> rows)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync(
            "TradingDate,FutureToken,Dte,TotalFeedUpdates,FirstExchangeTimestamp,LastExchangeTimestamp," +
            "FirstReceivedAt,LastReceivedAt,ExchangeOrderingViolations,IdOrderingViolations," +
            "ReceivedAtBackwards,NegativeVolumeDeltas,FullBarCount,PartialBarCount,LegacyParityMismatchCount," +
            "MedianOvershoot,P90Overshoot,P95Overshoot,P99Overshoot,MaxOvershoot,MaxObservedVolume," +
            "BarsAtLeast7500,BarsAtLeast10000,BarsAtLeast13000");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.FutureToken,
                r.Dte,
                r.TotalFeedUpdates,
                Iso(r.FirstExchangeTimestamp),
                Iso(r.LastExchangeTimestamp),
                Iso(r.FirstReceivedAt),
                Iso(r.LastReceivedAt),
                r.ExchangeOrderingViolations,
                r.IdOrderingViolations,
                r.ReceivedAtBackwards,
                r.NegativeVolumeDeltas,
                r.FullBarCount,
                r.PartialBarCount,
                r.LegacyParityMismatchCount,
                r.MedianOvershoot,
                r.P90Overshoot,
                r.P95Overshoot,
                r.P99Overshoot,
                r.MaxOvershoot,
                r.MaxObservedVolume,
                r.BarsAtLeast7500,
                r.BarsAtLeast10000,
                r.BarsAtLeast13000));
        }
    }

    static async Task WriteBarsCsvAsync(
        string path,
        IReadOnlyList<(DateOnly Date, int Dte, VolumeBar6500Revalidation.Bar Bar)> rows)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync(
            "TradingDate,Dte,BarIndex,StartTimestamp,EndTimestamp,StartReceivedAt,AvailableAt," +
            "FirstUpdateId,LastUpdateId,Open,High,Low,Close,ObservedVolume,OvershootVolume," +
            "OpenInterestAtClose,FeedUpdateCount,DepthUpdateCount,FutureCvdProxyNet,DepthImbalance," +
            "OrderFlowImbalance,TopOfBookImbalance,TobDepthDivergence,ExchangeDurationSeconds," +
            "ReceiptDurationSeconds,BarDurationUrgency,IsFinalPartialBar");

        foreach (var row in rows)
        {
            var b = row.Bar;
            await writer.WriteLineAsync(Csv(
                row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.Dte,
                b.BarIndex,
                b.StartTimestamp.ToString("O", CultureInfo.InvariantCulture),
                b.EndTimestamp.ToString("O", CultureInfo.InvariantCulture),
                b.StartReceivedAt.ToString("O", CultureInfo.InvariantCulture),
                b.AvailableAt.ToString("O", CultureInfo.InvariantCulture),
                b.FirstUpdateId,
                b.LastUpdateId,
                b.Open,
                b.High,
                b.Low,
                b.Close,
                b.ObservedVolume,
                b.OvershootVolume,
                b.OpenInterestAtClose,
                b.FeedUpdateCount,
                b.DepthUpdateCount,
                b.FutureCvdProxyNet,
                b.DepthImbalance,
                b.OrderFlowImbalance,
                b.TopOfBookImbalance,
                b.TobDepthDivergence,
                b.ExchangeDurationSeconds,
                b.ReceiptDurationSeconds,
                b.BarDurationUrgency,
                b.IsFinalPartialBar));
        }
    }

    static async Task WriteObservationsCsvAsync(
        string path,
        IReadOnlyList<VolumeBar6500Revalidation.Observation> rows)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync(
            "TradingDate,Dte,BarIndex,Timestamp,AvailableAt,FuturesClose,SignalBarOpen,SignalBarHigh,SignalBarLow," +
            "SignalBarChangePoints,SignalBarReturn,SignalBarRangePoints,ObservedVolume,OvershootVolume," +
            "FeedUpdateCount,DepthUpdateCount,ExchangeDurationSeconds,ReceiptDurationSeconds,DepthImbalance," +
            "TopOfBookImbalance,TobDepthDivergence,OrderFlowImbalance,FutureCvdProxyNet,BarDurationUrgency," +
            "Forward1Points,Forward2Points,Forward4Points,Forward1ObservedVolume,Forward2ObservedVolume," +
            "Forward4ObservedVolume,MaxUp1Points,MaxUp2Points,MaxUp4Points,MaxDown1Points,MaxDown2Points,MaxDown4Points");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte,
                r.BarIndex,
                r.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                r.AvailableAt.ToString("O", CultureInfo.InvariantCulture),
                r.FuturesClose,
                r.SignalBarOpen,
                r.SignalBarHigh,
                r.SignalBarLow,
                r.SignalBarChangePoints,
                r.SignalBarReturn,
                r.SignalBarRangePoints,
                r.ObservedVolume,
                r.OvershootVolume,
                r.FeedUpdateCount,
                r.DepthUpdateCount,
                r.ExchangeDurationSeconds,
                r.ReceiptDurationSeconds,
                r.DepthImbalance,
                r.TopOfBookImbalance,
                r.TobDepthDivergence,
                r.OrderFlowImbalance,
                r.FutureCvdProxyNet,
                r.BarDurationUrgency,
                r.Forward1Points,
                r.Forward2Points,
                r.Forward4Points,
                r.Forward1ObservedVolume,
                r.Forward2ObservedVolume,
                r.Forward4ObservedVolume,
                r.MaxUp1Points,
                r.MaxUp2Points,
                r.MaxUp4Points,
                r.MaxDown1Points,
                r.MaxDown2Points,
                r.MaxDown4Points));
        }
    }

    static string Csv(params object?[] values) =>
        string.Join(",", values.Select(CsvField));

    static string CsvField(object? value)
    {
        if (value is null) { return ""; }

        var field = value switch
        {
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return field;

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    static string Iso(DateTimeOffset? value) =>
        value is null ? "" : value.Value.ToString("O", CultureInfo.InvariantCulture);
}
