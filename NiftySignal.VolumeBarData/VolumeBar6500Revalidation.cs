using System.Globalization;
using System.Text;
using NiftySignal.Features;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Correctness-first 6500-contract Futures event-bar revalidation built directly from the
/// committed research-ticks-v2 raw tick export. This deliberately does not reuse the older
/// persisted VolumeBarRow dataset.
///
/// The older VolumeBarBuilder assigns the whole threshold-crossing tick to the open bar and then
/// resets cadence volume to zero. The later FutureEventBarBuilder assigns that same whole tick to
/// exactly one bar but carries threshold excess into the next bar's accounting balance. This
/// harness uses the latter accounting convention and makes the carry explicit on every bar.
/// </summary>
public static class VolumeBar6500Revalidation
{
    public const long ThresholdContracts = 6500;

    /// <summary>
    /// Fixed historical revalidation set. Discovery 2026-09-22/23, consumed OOS 2026-09-24 and
    /// provisional 2026-09-25 are deliberately excluded.
    /// </summary>
    public static readonly DateOnly[] PrimaryResearchDates =
    [
        new(2026, 9, 4),
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
        long FirstTickId,
        long LastTickId,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        long RealAssignedVolume,
        long ThresholdCarryIn,
        long ThresholdBalanceAtClose,
        long? ThresholdCarryOut,
        long? OpenInterestAtClose,
        int TickCount,
        int DepthTickCount,
        long? FutureCvdNet,
        double? DepthImbalance,
        double? OrderFlowImbalance,
        double? TopOfBookImbalance,
        bool IsFinalPartialBar)
    {
        public double ExchangeDurationSeconds => (EndTimestamp - StartTimestamp).TotalSeconds;
        public double ReceiptDurationSeconds => (AvailableAt - StartReceivedAt).TotalSeconds;

        /// <summary>
        /// Raw, untuned frame. Direction is intentionally not assumed in this revalidation.
        /// </summary>
        public double? TobDepthDivergence =>
            DepthImbalance is { } depth && TopOfBookImbalance is { } tob ? depth - tob : null;

        /// <summary>
        /// Historical formulation reproduced exactly before any ranking:
        /// sign(Close-Open) / exchange-duration-seconds.
        /// </summary>
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
        long RealAssignedVolume,
        long ThresholdCarryIn,
        long? ThresholdCarryOut,
        int TickCount,
        int DepthTickCount,
        double ExchangeDurationSeconds,
        double ReceiptDurationSeconds,
        double? DepthImbalance,
        double? TopOfBookImbalance,
        double? TobDepthDivergence,
        double? OrderFlowImbalance,
        long? FutureCvdNet,
        double? BarDurationUrgency,
        double? Forward1Points,
        double? Forward2Points,
        double? Forward4Points,
        double? MaxUp1Points,
        double? MaxUp2Points,
        double? MaxUp4Points,
        double? MaxDown1Points,
        double? MaxDown2Points,
        double? MaxDown4Points);

    public sealed record SessionAudit(
        DateOnly TradingDate,
        string FutureToken,
        int Dte,
        int TotalTicks,
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
        int BarsWithCarryIn,
        long MaxCarryOut);

    /// <summary>
    /// Builds fixed 6500-contract event bars from one session's raw Futures ticks. Input order is
    /// authoritative and must be ExchangeTimestamp non-decreasing with Id strictly increasing,
    /// exactly as TickExporterV2 writes it. Negative cumulative-volume deltas fail the run rather
    /// than being clamped or silently ignored.
    /// </summary>
    public static IReadOnlyList<Bar> BuildBars(IReadOnlyList<OptionTickV2> ticks)
    {
        var builder = new CarryAwareBuilder();
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

    /// <summary>
    /// Creates one metric/outcome row for every completed 6500 bar. The final partial bar is not
    /// a signal observation. +1/+2/+4 mean 6500/13000/26000 contracts later respectively.
    /// </summary>
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
                bar.RealAssignedVolume,
                bar.ThresholdCarryIn,
                bar.ThresholdCarryOut,
                bar.TickCount,
                bar.DepthTickCount,
                bar.ExchangeDurationSeconds,
                bar.ReceiptDurationSeconds,
                bar.DepthImbalance,
                bar.TopOfBookImbalance,
                bar.TobDepthDivergence,
                bar.OrderFlowImbalance,
                bar.FutureCvdNet,
                bar.BarDurationUrgency,
                Forward(full, i, 1),
                Forward(full, i, 2),
                Forward(full, i, 4),
                MaxUp(full, i, 1),
                MaxUp(full, i, 2),
                MaxUp(full, i, 4),
                MaxDown(full, i, 1),
                MaxDown(full, i, 2),
                MaxDown(full, i, 4)));
        }

        return rows;
    }

    public static SessionAudit AuditSession(
        DateOnly tradingDate,
        string futureToken,
        int dte,
        IReadOnlyList<OptionTickV2> ticks,
        IReadOnlyList<Bar> bars)
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
            bars.Count(b => b.ThresholdCarryIn > 0),
            bars.Where(b => b.ThresholdCarryOut is not null)
                .Select(b => b.ThresholdCarryOut!.Value)
                .DefaultIfEmpty(0)
                .Max());
    }

    static double? Forward(IReadOnlyList<Bar> bars, int index, int horizon)
    {
        var target = index + horizon;
        return target < bars.Count ? (double)(bars[target].Close - bars[index].Close) : null;
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

    sealed class CarryAwareBuilder
    {
        readonly FutureCvdProxyAccumulator _cvd = new();
        readonly DepthImbalanceAccumulator _depth = new();
        readonly OrderFlowImbalanceAccumulator _ofi = new();
        readonly TopOfBookImbalanceAccumulator _tob = new();

        long? _previousCumulativeVolume;
        long? _lastId;
        DateTimeOffset? _lastExchangeTimestamp;

        int _barIndex;
        long _thresholdBalance;
        long _carryIn;

        bool _hasOpenBar;
        DateTimeOffset _startTimestamp;
        DateTimeOffset _endTimestamp;
        DateTimeOffset _startReceivedAt;
        DateTimeOffset _availableAt;
        long _firstTickId;
        long _lastTickId;
        decimal _open;
        decimal _high;
        decimal _low;
        decimal _close;
        long _realAssignedVolume;
        long? _lastOpenInterest;
        int _tickCount;
        int _depthTickCount;

        public Bar? Apply(OptionTickV2 tick)
        {
            if (_lastId is { } lastId && tick.Id <= lastId)
            {
                throw new InvalidDataException($"Tick Id must strictly increase: {lastId} -> {tick.Id}.");
            }
            if (_lastExchangeTimestamp is { } lastTs && tick.ExchangeTimestamp < lastTs)
            {
                throw new InvalidDataException(
                    $"ExchangeTimestamp must not go backwards: {lastTs:O} -> {tick.ExchangeTimestamp:O}.");
            }

            var delta = _previousCumulativeVolume is { } previousVolume
                ? tick.Volume - previousVolume
                : 0;

            if (delta < 0)
            {
                throw new InvalidDataException(
                    $"Negative cumulative-volume delta at tick {tick.Id}: {_previousCumulativeVolume} -> {tick.Volume}.");
            }

            _previousCumulativeVolume = tick.Volume;
            _lastId = tick.Id;
            _lastExchangeTimestamp = tick.ExchangeTimestamp;

            StartBarIfNeeded(tick);

            _endTimestamp = tick.ExchangeTimestamp;
            _availableAt = tick.ReceivedAt > _availableAt ? tick.ReceivedAt : _availableAt;
            _lastTickId = tick.Id;
            _high = Math.Max(_high, tick.LastPrice);
            _low = Math.Min(_low, tick.LastPrice);
            _close = tick.LastPrice;
            _lastOpenInterest = tick.OpenInterest ?? _lastOpenInterest;
            _tickCount++;

            if (delta > 0)
            {
                _realAssignedVolume += delta;
            }

            if (tick.Depth is { } depth)
            {
                _depth.ApplyTick(depth);
                _ofi.ApplyTick(depth);
                _tob.ApplyTick(depth);
                _depthTickCount++;

                if (delta > 0)
                {
                    _cvd.ApplyTick(tick.LastPrice, depth, delta);
                }
            }

            _thresholdBalance += delta;

            return _thresholdBalance >= ThresholdContracts
                ? CompleteBar(isFinalPartialBar: false)
                : null;
        }

        public Bar? FlushPartial() =>
            _hasOpenBar ? CompleteBar(isFinalPartialBar: true) : null;

        void StartBarIfNeeded(OptionTickV2 tick)
        {
            if (_hasOpenBar) { return; }

            _hasOpenBar = true;
            _carryIn = _thresholdBalance;
            _startTimestamp = tick.ExchangeTimestamp;
            _endTimestamp = tick.ExchangeTimestamp;
            _startReceivedAt = tick.ReceivedAt;
            _availableAt = tick.ReceivedAt;
            _firstTickId = tick.Id;
            _lastTickId = tick.Id;
            _open = tick.LastPrice;
            _high = tick.LastPrice;
            _low = tick.LastPrice;
            _close = tick.LastPrice;
            _realAssignedVolume = 0;
            _tickCount = 0;
            _depthTickCount = 0;
        }

        Bar CompleteBar(bool isFinalPartialBar)
        {
            var balanceAtClose = _thresholdBalance;
            long? carryOut = null;

            if (!isFinalPartialBar)
            {
                carryOut = balanceAtClose - ThresholdContracts;
                if (carryOut.Value < 0)
                {
                    throw new InvalidOperationException(
                        $"Completed bar {_barIndex} below threshold: {balanceAtClose}.");
                }
            }

            var bar = new Bar(
                _barIndex++,
                _startTimestamp,
                _endTimestamp,
                _startReceivedAt,
                _availableAt,
                _firstTickId,
                _lastTickId,
                _open,
                _high,
                _low,
                _close,
                _realAssignedVolume,
                _carryIn,
                balanceAtClose,
                carryOut,
                _lastOpenInterest,
                _tickCount,
                _depthTickCount,
                _cvd.CadenceNet,
                _depth.CadenceImbalance,
                _ofi.CadenceNet,
                _tob.CadenceImbalance,
                isFinalPartialBar);

            _hasOpenBar = false;
            _thresholdBalance = carryOut ?? 0;
            _carryIn = _thresholdBalance;
            _realAssignedVolume = 0;
            _tickCount = 0;
            _depthTickCount = 0;
            _cvd.ResetCadence();
            _depth.Reset();
            _ofi.ResetCadence();
            _tob.Reset();

            return bar;
        }
    }
}

/// <summary>
/// File-backed exporter for the first revalidation pass. It reads only the ten predeclared
/// primary sessions. Running it creates regenerable research CSV output; it does not touch the
/// volume-bar database, live Host/Dashboard, or any strategy.
/// </summary>
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
            {
                throw new DirectoryNotFoundException($"Missing predeclared primary session: {dayDir}");
            }

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
            {
                throw new InvalidDataException($"{date:yyyy-MM-dd}: no nearest-expiry option chain in instruments.json.");
            }

            var dte = nearestOptionExpiry.DayNumber - date.DayNumber;
            var futurePath = Path.Combine(dayDir, $"{future.Token}.ndjson");
            if (!File.Exists(futurePath))
            {
                throw new FileNotFoundException($"{date:yyyy-MM-dd}: Future tick file missing.", futurePath);
            }

            var ticks = OptionTickReaderV2.LoadFile(futurePath);

            var orderingViolations = OptionTickReaderV2.ValidateOrdering(ticks);
            if (orderingViolations.Count > 0)
            {
                throw new InvalidDataException(
                    $"{date:yyyy-MM-dd}: deterministic tick ordering failed: {orderingViolations[0]}");
            }

            var volumeReport = OptionTickReaderV2.ValidateCumulativeVolume(ticks);
            if (volumeReport.NegativeDeltaCount > 0)
            {
                throw new InvalidDataException(
                    $"{date:yyyy-MM-dd}: {volumeReport.NegativeDeltaCount} negative cumulative-volume delta(s).");
            }

            var bars = VolumeBar6500Revalidation.BuildBars(ticks);
            var audit = VolumeBar6500Revalidation.AuditSession(date, future.Token, dte, ticks, bars);
            var observations = VolumeBar6500Revalidation.BuildObservations(date, dte, bars);

            audits.Add(audit);
            allObservations.AddRange(observations);
            allBars.AddRange(bars.Select(b => (date, dte, b)));

            Console.WriteLine(
                $"{date:yyyy-MM-dd}: ticks={ticks.Count:N0}, fullBars={audit.FullBarCount:N0}, " +
                $"partialBars={audit.PartialBarCount}, carryBars={audit.BarsWithCarryIn:N0}, " +
                $"receivedAtBackwards={audit.ReceivedAtBackwards:N0}, observations={observations.Count:N0}");
        }

        await WriteAuditCsvAsync(Path.Combine(outputDirectory, "session-audit.csv"), audits);
        await WriteBarsCsvAsync(Path.Combine(outputDirectory, "bars-6500.csv"), allBars);
        await WriteObservationsCsvAsync(Path.Combine(outputDirectory, "observations-6500.csv"), allObservations);

        Console.WriteLine($"6500 raw-tick revalidation export complete -> {outputDirectory}");
        return 0;
    }

    static async Task WriteAuditCsvAsync(
        string path,
        IReadOnlyList<VolumeBar6500Revalidation.SessionAudit> rows)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync(
            "TradingDate,FutureToken,Dte,TotalTicks,FirstExchangeTimestamp,LastExchangeTimestamp," +
            "FirstReceivedAt,LastReceivedAt,ExchangeOrderingViolations,IdOrderingViolations," +
            "ReceivedAtBackwards,NegativeVolumeDeltas,FullBarCount,PartialBarCount,BarsWithCarryIn,MaxCarryOut");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.FutureToken,
                r.Dte,
                r.TotalTicks,
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
                r.BarsWithCarryIn,
                r.MaxCarryOut));
        }
    }

    static async Task WriteBarsCsvAsync(
        string path,
        IReadOnlyList<(DateOnly Date, int Dte, VolumeBar6500Revalidation.Bar Bar)> rows)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync(
            "TradingDate,Dte,BarIndex,StartTimestamp,EndTimestamp,StartReceivedAt,AvailableAt," +
            "FirstTickId,LastTickId,Open,High,Low,Close,RealAssignedVolume,ThresholdCarryIn," +
            "ThresholdBalanceAtClose,ThresholdCarryOut,OpenInterestAtClose,TickCount,DepthTickCount," +
            "FutureCvdNet,DepthImbalance,OrderFlowImbalance,TopOfBookImbalance,TobDepthDivergence," +
            "ExchangeDurationSeconds,ReceiptDurationSeconds,BarDurationUrgency,IsFinalPartialBar");

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
                b.FirstTickId,
                b.LastTickId,
                b.Open,
                b.High,
                b.Low,
                b.Close,
                b.RealAssignedVolume,
                b.ThresholdCarryIn,
                b.ThresholdBalanceAtClose,
                b.ThresholdCarryOut,
                b.OpenInterestAtClose,
                b.TickCount,
                b.DepthTickCount,
                b.FutureCvdNet,
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
            "TradingDate,Dte,BarIndex,Timestamp,AvailableAt,FuturesClose,RealAssignedVolume," +
            "ThresholdCarryIn,ThresholdCarryOut,TickCount,DepthTickCount,ExchangeDurationSeconds," +
            "ReceiptDurationSeconds,DepthImbalance,TopOfBookImbalance,TobDepthDivergence," +
            "OrderFlowImbalance,FutureCvdNet,BarDurationUrgency,Forward1Points,Forward2Points," +
            "Forward4Points,MaxUp1Points,MaxUp2Points,MaxUp4Points,MaxDown1Points,MaxDown2Points,MaxDown4Points");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte,
                r.BarIndex,
                r.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                r.AvailableAt.ToString("O", CultureInfo.InvariantCulture),
                r.FuturesClose,
                r.RealAssignedVolume,
                r.ThresholdCarryIn,
                r.ThresholdCarryOut,
                r.TickCount,
                r.DepthTickCount,
                r.ExchangeDurationSeconds,
                r.ReceiptDurationSeconds,
                r.DepthImbalance,
                r.TopOfBookImbalance,
                r.TobDepthDivergence,
                r.OrderFlowImbalance,
                r.FutureCvdNet,
                r.BarDurationUrgency,
                r.Forward1Points,
                r.Forward2Points,
                r.Forward4Points,
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
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    static string Iso(DateTimeOffset? value) =>
        value is null ? "" : value.Value.ToString("O", CultureInfo.InvariantCulture);
}