using System.Globalization;
using System.Text;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Observation-level option translation for the two clean 6500-bar Futures candidates:
/// 1) current-bar price reversal, and 2) FutureCvdProxyNet exhaustion/reversal.
///
/// This is NOT a sequential trading strategy. Observations may overlap and P&L is never summed.
/// The purpose is to measure whether a Futures-side directional relationship survives realistic
/// same-token weekly-option translation under fixed, causal execution assumptions.
/// </summary>
public static class VolumeBar6500OptionTranslationAnalysis
{
    public const decimal MinEntryPremium = 100m;
    public const decimal MaxEntryPremium = 150m;
    public static readonly TimeSpan QuoteFreshness = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan EntryLatency = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan EntryExpiry = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ExitLatency = TimeSpan.FromSeconds(1);

    public sealed record CandidateSpec(
        string Name,
        Func<VolumeBar6500Revalidation.Observation, double?> Driver,
        string DriverDescription);

    public static readonly CandidateSpec[] Candidates =
    [
        new(
            "CurrentBarReversal",
            o => o.SignalBarChangePoints == 0 ? null : o.SignalBarChangePoints,
            "Close-Open; positive predicts DOWN/PE, negative predicts UP/CE"),
        new(
            "FutureCvdProxyExhaustion",
            o => o.FutureCvdProxyNet is { } x && x != 0 ? x : null,
            "FutureCvdProxyNet; positive predicts DOWN/PE, negative predicts UP/CE"),
    ];

    public sealed record HorizonSpec(
        int Bars,
        Func<VolumeBar6500Revalidation.Observation, double?> ForwardPoints);

    public static readonly HorizonSpec[] Horizons =
    [
        new(1, o => o.Forward1Points),
        new(2, o => o.Forward2Points),
        new(4, o => o.Forward4Points),
    ];

    public enum SignalStatus
    {
        Eligible,
        NoEligibleContract,
        NoTimelyEntryQuote,
        EntryMovedOutOfBand,
    }

    public enum OutcomeStatus
    {
        Executable,
        NoTargetBar,
        HorizonBeforeEntry,
        NoTargetLtp,
        NoExecutableExit,
    }

    public sealed record SignalAuditRow(
        DateOnly TradingDate,
        int Dte,
        string Candidate,
        int SignalBarIndex,
        DateTimeOffset SignalAvailableAt,
        decimal FuturesAtSignal,
        double DriverValue,
        double DriverAbsExpandingPercentile,
        int ExpectedDirectionSign,
        string OptionType,
        SignalStatus Status,
        string? Token,
        decimal? Strike,
        decimal? DecisionBid,
        decimal? DecisionAsk,
        double? DecisionRelativeSpreadPct,
        DateTimeOffset? DecisionQuoteAvailableAt,
        double? DecisionQuoteAgeSeconds,
        DateTimeOffset? EntryAvailableAt,
        decimal? EntryBid,
        decimal? EntryAsk,
        decimal? EntryFill,
        decimal? EntryLtp,
        int? LotSize,
        decimal? TickSize);

    public sealed record ObservationRow(
        DateOnly TradingDate,
        int Dte,
        string Candidate,
        int HorizonBars,
        int SignalBarIndex,
        DateTimeOffset SignalAvailableAt,
        DateTimeOffset TargetAvailableAt,
        double DriverValue,
        double DriverAbsExpandingPercentile,
        double? CvdRaw,
        double? CvdAbsExpandingPercentile,
        int ExpectedDirectionSign,
        string OptionType,
        string Token,
        decimal Strike,
        int LotSize,
        decimal TickSize,
        decimal FuturesAtSignal,
        decimal FuturesAtTarget,
        double UnderlyingForwardPoints,
        bool? UnderlyingCorrect,
        DateTimeOffset EntryAvailableAt,
        decimal EntryLtp,
        decimal EntryBid,
        decimal EntryAsk,
        decimal EntryFill,
        double EntryRelativeSpreadPct,
        decimal? TargetLtp,
        double? TargetLtpAgeSeconds,
        double? LtpReturnPct,
        OutcomeStatus OutcomeStatus,
        DateTimeOffset? ExitAvailableAt,
        double? ExitDelaySeconds,
        decimal? ExitBid,
        decimal? ExitAsk,
        decimal? ExitFill,
        double? ExitRelativeSpreadPct,
        double? ExecutableGrossReturnPct,
        double? ExecutableNetReturnPct,
        decimal? ModeledFeesOneLot,
        decimal MfePoints,
        decimal MaePoints,
        double MfePercent,
        double MaePercent,
        double? TimeToMfeSeconds,
        bool? OptionProfitable,
        string OutcomeCategory);

    public sealed record SummaryRow(
        string Candidate,
        int HorizonBars,
        int SignalCount,
        int EligibleEntryCount,
        int ExecutableOutcomeCount,
        int NoEligibleContractCount,
        int NoTimelyEntryQuoteCount,
        int EntryMovedOutOfBandCount,
        int HorizonBeforeEntryCount,
        int NoTargetLtpCount,
        int NoExecutableExitCount,
        int UnderlyingDirectionalCount,
        double? UnderlyingHitRate,
        int OptionDirectionalCount,
        double? OptionNetPositiveRate,
        int FuturesCorrectOptionProfit,
        int FuturesCorrectOptionLoss,
        int FuturesWrongOptionProfit,
        int FuturesWrongOptionLoss,
        double? MeanUnderlyingForwardPointsAligned,
        double? MeanLtpReturnPct,
        double? MedianLtpReturnPct,
        double? MeanExecutableGrossReturnPct,
        double? MedianExecutableGrossReturnPct,
        double? MeanExecutableNetReturnPct,
        double? MedianExecutableNetReturnPct,
        double? MeanMfePercent,
        double? MeanMaePercent,
        double? MedianTimeToMfeSeconds);

    public sealed record SessionRow(
        DateOnly TradingDate,
        int Dte,
        string Candidate,
        int HorizonBars,
        int SignalCount,
        int ExecutableOutcomeCount,
        double? UnderlyingHitRate,
        double? OptionNetPositiveRate,
        double? MeanExecutableNetReturnPct,
        int FuturesCorrectOptionProfit,
        int FuturesCorrectOptionLoss,
        int FuturesWrongOptionProfit,
        int FuturesWrongOptionLoss);

    public sealed record DteRow(
        int Dte,
        string Candidate,
        int HorizonBars,
        int SignalCount,
        int ExecutableOutcomeCount,
        double? UnderlyingHitRate,
        double? OptionNetPositiveRate,
        double? MeanExecutableNetReturnPct,
        double? MeanLtpReturnPct,
        int FuturesCorrectOptionProfit,
        int FuturesCorrectOptionLoss,
        int FuturesWrongOptionProfit,
        int FuturesWrongOptionLoss);

    public sealed record AnalysisResult(
        IReadOnlyList<SignalAuditRow> SignalAudit,
        IReadOnlyList<ObservationRow> Observations,
        IReadOnlyList<SummaryRow> Summary,
        IReadOnlyList<SessionRow> Sessions,
        IReadOnlyList<DteRow> Dte);

    public readonly record struct QuoteTick(
        long Id,
        DateTimeOffset ExchangeTimestamp,
        DateTimeOffset ReceivedAt,
        DateTimeOffset AvailableAt,
        decimal LastPrice,
        decimal Bid,
        decimal Ask,
        long BidQty,
        long AskQty);

    public sealed class TokenSeries
    {
        readonly QuoteTick[] _rows;

        TokenSeries(QuoteTick[] rows) => _rows = rows;

        public static TokenSeries FromTicks(IReadOnlyList<OptionTickV2> ticks)
        {
            var rows = ticks
                .Select(t =>
                {
                    var available = t.ReceivedAt > t.ExchangeTimestamp
                        ? t.ReceivedAt
                        : t.ExchangeTimestamp;

                    var depth = t.Depth;
                    return new QuoteTick(
                        t.Id,
                        t.ExchangeTimestamp,
                        t.ReceivedAt,
                        available,
                        t.LastPrice,
                        depth?.Bid1Price ?? 0m,
                        depth?.Ask1Price ?? 0m,
                        depth?.Bid1Qty ?? 0,
                        depth?.Ask1Qty ?? 0);
                })
                .OrderBy(x => x.AvailableAt)
                .ThenBy(x => x.Id)
                .ToArray();

            return new TokenSeries(rows);
        }

        public static TokenSeries Load(string path) =>
            FromTicks(OptionTickReaderV2.LoadFile(path));

        public QuoteTick? ValidQuoteAtOrBefore(
            DateTimeOffset at,
            int lotSize,
            TimeSpan maxAge)
        {
            var index = LastIndexAtOrBefore(at);
            if (index < 0)
            {
                return null;
            }

            var oldest = at - maxAge;
            for (var i = index; i >= 0; i--)
            {
                var row = _rows[i];
                if (row.AvailableAt < oldest)
                {
                    break;
                }

                if (ValidQuote(row, lotSize))
                {
                    return row;
                }
            }

            return null;
        }

        public QuoteTick? FirstValidQuoteAtOrAfter(
            DateTimeOffset at,
            int lotSize,
            DateTimeOffset? noLaterThan = null)
        {
            var index = FirstIndexAtOrAfter(at);
            if (index < 0)
            {
                return null;
            }

            for (var i = index; i < _rows.Length; i++)
            {
                var row = _rows[i];
                if (noLaterThan is { } max && row.AvailableAt > max)
                {
                    break;
                }

                if (ValidQuote(row, lotSize))
                {
                    return row;
                }
            }

            return null;
        }

        public QuoteTick? LtpAtOrBefore(DateTimeOffset at)
        {
            var index = LastIndexAtOrBefore(at);
            for (var i = index; i >= 0; i--)
            {
                if (_rows[i].LastPrice > 0)
                {
                    return _rows[i];
                }
            }

            return null;
        }

        public IReadOnlyList<QuoteTick> LtpPath(
            DateTimeOffset startInclusive,
            DateTimeOffset endInclusive)
        {
            var index = FirstIndexAtOrAfter(startInclusive);
            if (index < 0)
            {
                return [];
            }

            var result = new List<QuoteTick>();
            for (var i = index; i < _rows.Length; i++)
            {
                var row = _rows[i];
                if (row.AvailableAt > endInclusive)
                {
                    break;
                }

                if (row.LastPrice > 0)
                {
                    result.Add(row);
                }
            }

            return result;
        }

        static bool ValidQuote(QuoteTick row, int lotSize) =>
            row.Bid > 0 &&
            row.Ask > 0 &&
            row.Ask >= row.Bid &&
            row.BidQty >= lotSize &&
            row.AskQty >= lotSize;

        int FirstIndexAtOrAfter(DateTimeOffset at)
        {
            var lo = 0;
            var hi = _rows.Length - 1;
            var result = -1;

            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                if (_rows[mid].AvailableAt >= at)
                {
                    result = mid;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return result;
        }

        int LastIndexAtOrBefore(DateTimeOffset at)
        {
            var lo = 0;
            var hi = _rows.Length - 1;
            var result = -1;

            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                if (_rows[mid].AvailableAt <= at)
                {
                    result = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return result;
        }
    }

    public readonly record struct DecisionQuoteCandidate(
        OptionTickReaderV2.ManifestRow Instrument,
        QuoteTick Quote,
        double RelativeSpreadPct,
        decimal StrikeDistance);

    public static async Task<AnalysisResult> AnalyzeAsync(
        string inputRoot,
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        CancellationToken cancellationToken)
    {
        var audit = new List<SignalAuditRow>();
        var outcomes = new List<ObservationRow>();

        foreach (var dayGroup in observations
            .GroupBy(o => o.TradingDate)
            .OrderBy(g => g.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var date = dayGroup.Key;
            var dayRows = dayGroup.OrderBy(o => o.BarIndex).ToArray();
            var byBarIndex = dayRows.ToDictionary(o => o.BarIndex);
            var dayDir = Path.Combine(inputRoot, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            var manifest = await OptionTickReaderV2.LoadManifestAsync(dayDir);
            var nearestExpiry = manifest
                .Where(m =>
                    string.Equals(m.InstrumentType, "Option", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Underlying, "NIFTY", StringComparison.OrdinalIgnoreCase) &&
                    m.ExpiryDate is { } expiry &&
                    expiry >= date)
                .Select(m => m.ExpiryDate!.Value)
                .OrderBy(x => x)
                .FirstOrDefault();

            if (nearestExpiry == default)
            {
                throw new InvalidDataException($"{date:yyyy-MM-dd}: no nearest-expiry NIFTY option chain.");
            }

            var chain = manifest
                .Where(m =>
                    string.Equals(m.InstrumentType, "Option", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Underlying, "NIFTY", StringComparison.OrdinalIgnoreCase) &&
                    m.ExpiryDate == nearestExpiry &&
                    m.StrikePrice is not null &&
                    File.Exists(Path.Combine(dayDir, $"{m.Token}.ndjson")))
                .ToArray();

            var seriesCache = new Dictionary<string, TokenSeries>(StringComparer.Ordinal);

            TokenSeries GetSeries(string token)
            {
                if (!seriesCache.TryGetValue(token, out var series))
                {
                    series = TokenSeries.Load(Path.Combine(dayDir, $"{token}.ndjson"));
                    seriesCache[token] = series;
                }

                return series;
            }

            var cvdRanks = ExpandingAbsolutePercentiles(
                dayRows.Select(o => o.FutureCvdProxyNet is { } x ? (double?)x : null).ToArray());

            foreach (var candidate in Candidates)
            {
                var drivers = dayRows.Select(candidate.Driver).ToArray();
                var driverRanks = ExpandingAbsolutePercentiles(drivers);

                for (var i = 0; i < dayRows.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var signal = dayRows[i];
                    var driver = drivers[i];
                    if (driver is null || !double.IsFinite(driver.Value) || driver.Value == 0)
                    {
                        continue;
                    }

                    var expectedDirection = -Math.Sign(driver.Value);
                    var optionType = expectedDirection > 0 ? "Call" : "Put";
                    var decision = signal.AvailableAt;

                    var selected = SelectDecisionContract(
                        chain,
                        optionType,
                        signal.FuturesClose,
                        decision,
                        GetSeries);

                    if (selected is null)
                    {
                        audit.Add(new SignalAuditRow(
                            date, signal.Dte, candidate.Name, signal.BarIndex, decision,
                            signal.FuturesClose, driver.Value, driverRanks[i] ?? 0,
                            expectedDirection, optionType, SignalStatus.NoEligibleContract,
                            null, null, null, null, null, null, null,
                            null, null, null, null, null, null, null, null));
                        continue;
                    }

                    var selection = selected.Value;
                    var series = GetSeries(selection.Instrument.Token);
                    var entry = series.FirstValidQuoteAtOrAfter(
                        decision + EntryLatency,
                        selection.Instrument.LotSize,
                        decision + EntryExpiry);

                    if (entry is null)
                    {
                        audit.Add(ToAudit(
                            date, signal, candidate.Name, driver.Value, driverRanks[i] ?? 0,
                            expectedDirection, optionType, selection,
                            SignalStatus.NoTimelyEntryQuote, null));
                        continue;
                    }

                    if (entry.Value.Ask < MinEntryPremium || entry.Value.Ask > MaxEntryPremium)
                    {
                        audit.Add(ToAudit(
                            date, signal, candidate.Name, driver.Value, driverRanks[i] ?? 0,
                            expectedDirection, optionType, selection,
                            SignalStatus.EntryMovedOutOfBand, entry));
                        continue;
                    }

                    var signalAudit = ToAudit(
                        date, signal, candidate.Name, driver.Value, driverRanks[i] ?? 0,
                        expectedDirection, optionType, selection,
                        SignalStatus.Eligible, entry);

                    audit.Add(signalAudit);

                    foreach (var horizon in Horizons)
                    {
                        if (!byBarIndex.TryGetValue(signal.BarIndex + horizon.Bars, out var target))
                        {
                            continue;
                        }

                        outcomes.Add(BuildOutcome(
                            signal,
                            target,
                            candidate.Name,
                            horizon.Bars,
                            driver.Value,
                            driverRanks[i] ?? 0,
                            signal.FutureCvdProxyNet is { } cvd ? cvd : null,
                            cvdRanks[i],
                            expectedDirection,
                            optionType,
                            selection.Instrument,
                            series,
                            entry.Value));
                    }
                }
            }
        }

        var summary = BuildSummary(audit, outcomes);
        var sessions = BuildSessions(audit, outcomes);
        var dte = BuildDte(audit, outcomes);

        return new AnalysisResult(audit, outcomes, summary, sessions, dte);
    }

    public static async Task<AnalysisResult> WriteReportsAsync(
        string inputRoot,
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var result = await AnalyzeAsync(inputRoot, observations, cancellationToken);
        Directory.CreateDirectory(outputDirectory);

        await WriteSignalAuditCsvAsync(
            Path.Combine(outputDirectory, "option-translation-signal-audit.csv"),
            result.SignalAudit);

        await WriteObservationsCsvAsync(
            Path.Combine(outputDirectory, "option-translation-observations.csv"),
            result.Observations);

        await WriteSummaryCsvAsync(
            Path.Combine(outputDirectory, "option-translation-summary.csv"),
            result.Summary);

        await WriteSessionsCsvAsync(
            Path.Combine(outputDirectory, "option-translation-sessions.csv"),
            result.Sessions);

        await WriteDteCsvAsync(
            Path.Combine(outputDirectory, "option-translation-dte.csv"),
            result.Dte);

        PrintPrimarySummary(result);
        return result;
    }

    public static DecisionQuoteCandidate? SelectDecisionContract(
        IReadOnlyList<OptionTickReaderV2.ManifestRow> chain,
        string optionType,
        decimal futuresPrice,
        DateTimeOffset decision,
        Func<string, TokenSeries> getSeries)
    {
        var candidates = new List<DecisionQuoteCandidate>();

        foreach (var instrument in chain.Where(i =>
            string.Equals(i.OptionType, optionType, StringComparison.OrdinalIgnoreCase)))
        {
            var quote = getSeries(instrument.Token)
                .ValidQuoteAtOrBefore(decision, instrument.LotSize, QuoteFreshness);

            if (quote is not { } q ||
                q.Ask < MinEntryPremium ||
                q.Ask > MaxEntryPremium)
            {
                continue;
            }

            var relativeSpread =
                q.Ask > 0
                    ? (double)((q.Ask - q.Bid) / q.Ask * 100m)
                    : double.PositiveInfinity;

            candidates.Add(new DecisionQuoteCandidate(
                instrument,
                q,
                relativeSpread,
                Math.Abs(instrument.StrikePrice!.Value - futuresPrice)));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderBy(c => c.RelativeSpreadPct)
            .ThenBy(c => c.StrikeDistance)
            .ThenBy(c => c.Instrument.Token, StringComparer.Ordinal)
            .First();
    }

    public static string ClassifyOutcome(bool? underlyingCorrect, bool? optionProfitable)
    {
        if (underlyingCorrect is null)
        {
            return "UnderlyingFlat";
        }

        if (optionProfitable is null)
        {
            return "OptionUnavailable";
        }

        return (underlyingCorrect.Value, optionProfitable.Value) switch
        {
            (true, true) => "FuturesCorrectOptionProfit",
            (true, false) => "FuturesCorrectOptionLoss",
            (false, true) => "FuturesWrongOptionProfit",
            (false, false) => "FuturesWrongOptionLoss",
        };
    }

    public static double?[] ExpandingAbsolutePercentiles(IReadOnlyList<double?> values)
    {
        var result = new double?[values.Count];
        var history = new List<double>();

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (value is null || !double.IsFinite(value.Value))
            {
                result[i] = null;
                continue;
            }

            var magnitude = Math.Abs(value.Value);
            history.Add(magnitude);

            var less = history.Count(x => x < magnitude);
            var equal = history.Count(x => x == magnitude);
            result[i] = (less + ((equal + 1) / 2.0)) / history.Count;
        }

        return result;
    }

    static SignalAuditRow ToAudit(
        DateOnly date,
        VolumeBar6500Revalidation.Observation signal,
        string candidate,
        double driver,
        double driverRank,
        int expectedDirection,
        string optionType,
        DecisionQuoteCandidate selection,
        SignalStatus status,
        QuoteTick? entry)
    {
        decimal? fill = entry is { } e
            ? e.Ask + selection.Instrument.TickSize
            : null;

        return new SignalAuditRow(
            date,
            signal.Dte,
            candidate,
            signal.BarIndex,
            signal.AvailableAt,
            signal.FuturesClose,
            driver,
            driverRank,
            expectedDirection,
            optionType,
            status,
            selection.Instrument.Token,
            selection.Instrument.StrikePrice,
            selection.Quote.Bid,
            selection.Quote.Ask,
            selection.RelativeSpreadPct,
            selection.Quote.AvailableAt,
            (signal.AvailableAt - selection.Quote.AvailableAt).TotalSeconds,
            entry?.AvailableAt,
            entry?.Bid,
            entry?.Ask,
            fill,
            entry?.LastPrice,
            selection.Instrument.LotSize,
            selection.Instrument.TickSize);
    }

    static ObservationRow BuildOutcome(
        VolumeBar6500Revalidation.Observation signal,
        VolumeBar6500Revalidation.Observation target,
        string candidate,
        int horizon,
        double driver,
        double driverRank,
        double? cvdRaw,
        double? cvdRank,
        int expectedDirection,
        string optionType,
        OptionTickReaderV2.ManifestRow instrument,
        TokenSeries series,
        QuoteTick entry)
    {
        var entryFill = entry.Ask + instrument.TickSize;
        var underlyingForward = (double)(target.FuturesClose - signal.FuturesClose);
        bool? underlyingCorrect = underlyingForward == 0
            ? null
            : expectedDirection * underlyingForward > 0;

        var targetLtpRow = series.LtpAtOrBefore(target.AvailableAt);
        decimal? targetLtp = targetLtpRow?.LastPrice;
        double? targetLtpAge = targetLtpRow is { } tl
            ? (target.AvailableAt - tl.AvailableAt).TotalSeconds
            : null;

        double? ltpReturn = targetLtp is { } ltp && entry.LastPrice > 0
            ? (double)((ltp - entry.LastPrice) / entry.LastPrice * 100m)
            : null;

        var status = OutcomeStatus.Executable;
        QuoteTick? exit = null;

        if (target.AvailableAt <= entry.AvailableAt)
        {
            status = OutcomeStatus.HorizonBeforeEntry;
        }
        else if (targetLtp is null)
        {
            status = OutcomeStatus.NoTargetLtp;
        }
        else
        {
            exit = series.FirstValidQuoteAtOrAfter(
                target.AvailableAt + ExitLatency,
                instrument.LotSize);

            if (exit is null)
            {
                status = OutcomeStatus.NoExecutableExit;
            }
        }

        decimal? exitFill = exit is { } ex
            ? Math.Max(0m, ex.Bid - instrument.TickSize)
            : null;

        double? grossReturn = exitFill is { } ef && entryFill > 0
            ? (double)((ef - entryFill) / entryFill * 100m)
            : null;

        decimal? fees = exitFill is { } feeExit
            ? ReversalResearch.Fees(entryFill, feeExit, instrument.LotSize)
            : null;

        double? netReturn = exitFill is { } netExit && fees is { } f && entryFill > 0
            ? (double)((((netExit - entryFill) * instrument.LotSize) - f) /
                (entryFill * instrument.LotSize) * 100m)
            : null;

        bool? optionProfitable = netReturn is { } nr ? nr > 0 : null;

        var path = target.AvailableAt > entry.AvailableAt
            ? series.LtpPath(entry.AvailableAt, target.AvailableAt)
            : [];

        var mfePoints = 0m;
        var maePoints = 0m;
        double? timeToMfe = null;

        if (path.Count > 0)
        {
            var maxPrice = path.Max(x => x.LastPrice);
            var minPrice = path.Min(x => x.LastPrice);
            mfePoints = Math.Max(0m, maxPrice - entryFill);
            maePoints = Math.Max(0m, entryFill - minPrice);

            if (mfePoints > 0)
            {
                var firstMfe = path.First(x => x.LastPrice == maxPrice);
                timeToMfe = (firstMfe.AvailableAt - entry.AvailableAt).TotalSeconds;
            }
        }

        var mfePct = entryFill > 0 ? (double)(mfePoints / entryFill * 100m) : 0;
        var maePct = entryFill > 0 ? (double)(maePoints / entryFill * 100m) : 0;

        return new ObservationRow(
            signal.TradingDate,
            signal.Dte,
            candidate,
            horizon,
            signal.BarIndex,
            signal.AvailableAt,
            target.AvailableAt,
            driver,
            driverRank,
            cvdRaw,
            cvdRank,
            expectedDirection,
            optionType,
            instrument.Token,
            instrument.StrikePrice!.Value,
            instrument.LotSize,
            instrument.TickSize,
            signal.FuturesClose,
            target.FuturesClose,
            underlyingForward,
            underlyingCorrect,
            entry.AvailableAt,
            entry.LastPrice,
            entry.Bid,
            entry.Ask,
            entryFill,
            RelativeSpreadPct(entry.Bid, entry.Ask),
            targetLtp,
            targetLtpAge,
            ltpReturn,
            status,
            exit?.AvailableAt,
            exit is { } e ? (e.AvailableAt - target.AvailableAt).TotalSeconds : null,
            exit?.Bid,
            exit?.Ask,
            exitFill,
            exit is { } eq ? RelativeSpreadPct(eq.Bid, eq.Ask) : null,
            grossReturn,
            netReturn,
            fees,
            mfePoints,
            maePoints,
            mfePct,
            maePct,
            timeToMfe,
            optionProfitable,
            ClassifyOutcome(underlyingCorrect, optionProfitable));
    }

    static List<SummaryRow> BuildSummary(
        IReadOnlyList<SignalAuditRow> audit,
        IReadOnlyList<ObservationRow> outcomes)
    {
        var rows = new List<SummaryRow>();

        foreach (var candidate in Candidates)
        {
            foreach (var horizon in Horizons)
            {
                var signals = audit.Where(a => a.Candidate == candidate.Name).ToArray();
                var group = outcomes
                    .Where(o => o.Candidate == candidate.Name && o.HorizonBars == horizon.Bars)
                    .ToArray();

                rows.Add(BuildSummaryRow(candidate.Name, horizon.Bars, signals, group));
            }
        }

        return rows;
    }

    static SummaryRow BuildSummaryRow(
        string candidate,
        int horizon,
        IReadOnlyList<SignalAuditRow> signals,
        IReadOnlyList<ObservationRow> outcomes)
    {
        var executable = outcomes
            .Where(o => o.OutcomeStatus == OutcomeStatus.Executable)
            .ToArray();

        var underlyingDirectional = outcomes
            .Where(o => o.UnderlyingCorrect is not null)
            .ToArray();

        var optionDirectional = executable
            .Where(o => o.OptionProfitable is not null)
            .ToArray();

        var alignedUnderlying = underlyingDirectional
            .Select(o => o.ExpectedDirectionSign * o.UnderlyingForwardPoints)
            .ToArray();

        return new SummaryRow(
            candidate,
            horizon,
            signals.Count,
            signals.Count(x => x.Status == SignalStatus.Eligible),
            executable.Length,
            signals.Count(x => x.Status == SignalStatus.NoEligibleContract),
            signals.Count(x => x.Status == SignalStatus.NoTimelyEntryQuote),
            signals.Count(x => x.Status == SignalStatus.EntryMovedOutOfBand),
            outcomes.Count(x => x.OutcomeStatus == OutcomeStatus.HorizonBeforeEntry),
            outcomes.Count(x => x.OutcomeStatus == OutcomeStatus.NoTargetLtp),
            outcomes.Count(x => x.OutcomeStatus == OutcomeStatus.NoExecutableExit),
            underlyingDirectional.Length,
            RateOrNull(underlyingDirectional.Select(x => x.UnderlyingCorrect!.Value).ToArray()),
            optionDirectional.Length,
            RateOrNull(optionDirectional.Select(x => x.OptionProfitable!.Value).ToArray()),
            outcomes.Count(x => x.OutcomeCategory == "FuturesCorrectOptionProfit"),
            outcomes.Count(x => x.OutcomeCategory == "FuturesCorrectOptionLoss"),
            outcomes.Count(x => x.OutcomeCategory == "FuturesWrongOptionProfit"),
            outcomes.Count(x => x.OutcomeCategory == "FuturesWrongOptionLoss"),
            MeanOrNull(alignedUnderlying),
            MeanOrNull(executable.Where(x => x.LtpReturnPct is not null).Select(x => x.LtpReturnPct!.Value).ToArray()),
            MedianOrNull(executable.Where(x => x.LtpReturnPct is not null).Select(x => x.LtpReturnPct!.Value).ToArray()),
            MeanOrNull(executable.Where(x => x.ExecutableGrossReturnPct is not null).Select(x => x.ExecutableGrossReturnPct!.Value).ToArray()),
            MedianOrNull(executable.Where(x => x.ExecutableGrossReturnPct is not null).Select(x => x.ExecutableGrossReturnPct!.Value).ToArray()),
            MeanOrNull(executable.Where(x => x.ExecutableNetReturnPct is not null).Select(x => x.ExecutableNetReturnPct!.Value).ToArray()),
            MedianOrNull(executable.Where(x => x.ExecutableNetReturnPct is not null).Select(x => x.ExecutableNetReturnPct!.Value).ToArray()),
            MeanOrNull(executable.Select(x => x.MfePercent).ToArray()),
            MeanOrNull(executable.Select(x => x.MaePercent).ToArray()),
            MedianOrNull(executable.Where(x => x.TimeToMfeSeconds is not null).Select(x => x.TimeToMfeSeconds!.Value).ToArray()));
    }

    static List<SessionRow> BuildSessions(
        IReadOnlyList<SignalAuditRow> audit,
        IReadOnlyList<ObservationRow> outcomes)
    {
        var rows = new List<SessionRow>();

        foreach (var candidate in Candidates)
        {
            foreach (var horizon in Horizons)
            {
                var dates = audit
                    .Where(a => a.Candidate == candidate.Name)
                    .Select(a => a.TradingDate)
                    .Distinct()
                    .OrderBy(x => x);

                foreach (var date in dates)
                {
                    var signals = audit
                        .Where(a => a.Candidate == candidate.Name && a.TradingDate == date)
                        .ToArray();

                    var group = outcomes
                        .Where(o =>
                            o.Candidate == candidate.Name &&
                            o.HorizonBars == horizon.Bars &&
                            o.TradingDate == date)
                        .ToArray();

                    var executable = group
                        .Where(o => o.OutcomeStatus == OutcomeStatus.Executable)
                        .ToArray();

                    var directional = group
                        .Where(o => o.UnderlyingCorrect is not null)
                        .ToArray();

                    rows.Add(new SessionRow(
                        date,
                        signals.Length > 0 ? signals[0].Dte : 0,
                        candidate.Name,
                        horizon.Bars,
                        signals.Length,
                        executable.Length,
                        RateOrNull(directional.Select(x => x.UnderlyingCorrect!.Value).ToArray()),
                        RateOrNull(executable.Where(x => x.OptionProfitable is not null).Select(x => x.OptionProfitable!.Value).ToArray()),
                        MeanOrNull(executable.Where(x => x.ExecutableNetReturnPct is not null).Select(x => x.ExecutableNetReturnPct!.Value).ToArray()),
                        group.Count(x => x.OutcomeCategory == "FuturesCorrectOptionProfit"),
                        group.Count(x => x.OutcomeCategory == "FuturesCorrectOptionLoss"),
                        group.Count(x => x.OutcomeCategory == "FuturesWrongOptionProfit"),
                        group.Count(x => x.OutcomeCategory == "FuturesWrongOptionLoss")));
                }
            }
        }

        return rows;
    }

    static List<DteRow> BuildDte(
        IReadOnlyList<SignalAuditRow> audit,
        IReadOnlyList<ObservationRow> outcomes)
    {
        var rows = new List<DteRow>();

        foreach (var candidate in Candidates)
        {
            foreach (var horizon in Horizons)
            {
                var dtes = audit
                    .Where(a => a.Candidate == candidate.Name)
                    .Select(a => a.Dte)
                    .Distinct()
                    .OrderBy(x => x);

                foreach (var dte in dtes)
                {
                    var signals = audit
                        .Where(a => a.Candidate == candidate.Name && a.Dte == dte)
                        .ToArray();

                    var group = outcomes
                        .Where(o =>
                            o.Candidate == candidate.Name &&
                            o.HorizonBars == horizon.Bars &&
                            o.Dte == dte)
                        .ToArray();

                    var executable = group
                        .Where(o => o.OutcomeStatus == OutcomeStatus.Executable)
                        .ToArray();

                    var directional = group
                        .Where(o => o.UnderlyingCorrect is not null)
                        .ToArray();

                    rows.Add(new DteRow(
                        dte,
                        candidate.Name,
                        horizon.Bars,
                        signals.Length,
                        executable.Length,
                        RateOrNull(directional.Select(x => x.UnderlyingCorrect!.Value).ToArray()),
                        RateOrNull(executable.Where(x => x.OptionProfitable is not null).Select(x => x.OptionProfitable!.Value).ToArray()),
                        MeanOrNull(executable.Where(x => x.ExecutableNetReturnPct is not null).Select(x => x.ExecutableNetReturnPct!.Value).ToArray()),
                        MeanOrNull(executable.Where(x => x.LtpReturnPct is not null).Select(x => x.LtpReturnPct!.Value).ToArray()),
                        group.Count(x => x.OutcomeCategory == "FuturesCorrectOptionProfit"),
                        group.Count(x => x.OutcomeCategory == "FuturesCorrectOptionLoss"),
                        group.Count(x => x.OutcomeCategory == "FuturesWrongOptionProfit"),
                        group.Count(x => x.OutcomeCategory == "FuturesWrongOptionLoss")));
                }
            }
        }

        return rows;
    }

    static double RelativeSpreadPct(decimal bid, decimal ask) =>
        ask > 0 ? (double)((ask - bid) / ask * 100m) : double.NaN;

    static double? RateOrNull(IReadOnlyList<bool> values) =>
        values.Count == 0 ? null : values.Count(x => x) / (double)values.Count;

    static double? MeanOrNull(IReadOnlyList<double> values) =>
        values.Count == 0 ? null : values.Average();

    static double? MedianOrNull(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(x => x).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    static void PrintPrimarySummary(AnalysisResult result)
    {
        Console.WriteLine(
            "6500 option translation — overlapping observations, NOT sequential strategy P&L:");

        foreach (var row in result.Summary
            .OrderBy(r => r.Candidate)
            .ThenBy(r => r.HorizonBars))
        {
            Console.WriteLine(
                $"  {row.Candidate,-26} +{row.HorizonBars}: signals={row.SignalCount:N0}, " +
                $"eligibleEntry={row.EligibleEntryCount:N0}, executable={row.ExecutableOutcomeCount:N0}, " +
                $"underlyingHit={FormatRate(row.UnderlyingHitRate)}, optionNetPositive={FormatRate(row.OptionNetPositiveRate)}, " +
                $"meanNetRet={FormatPct(row.MeanExecutableNetReturnPct)}, medianNetRet={FormatPct(row.MedianExecutableNetReturnPct)}, " +
                $"F+/O+={row.FuturesCorrectOptionProfit}, F+/O-={row.FuturesCorrectOptionLoss}, " +
                $"F-/O+={row.FuturesWrongOptionProfit}, F-/O-={row.FuturesWrongOptionLoss}");
        }

        Console.WriteLine(
            "Selection is causal: decision-time fresh quote in Rs100-150, smallest relative spread; " +
            "entry >=1s later within 15s, same token pinned, one adverse tick each side.");
    }

    static string FormatRate(double? value) =>
        value is null ? "NA" : (value.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    static string FormatPct(double? value) =>
        value is null ? "NA" : value.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%";

    static async Task WriteSignalAuditCsvAsync(string path, IReadOnlyList<SignalAuditRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "TradingDate,Dte,Candidate,SignalBarIndex,SignalAvailableAt,FuturesAtSignal,DriverValue," +
            "DriverAbsExpandingPercentile,ExpectedDirectionSign,OptionType,Status,Token,Strike,DecisionBid," +
            "DecisionAsk,DecisionRelativeSpreadPct,DecisionQuoteAvailableAt,DecisionQuoteAgeSeconds," +
            "EntryAvailableAt,EntryBid,EntryAsk,EntryFill,EntryLtp,LotSize,TickSize");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte, r.Candidate, r.SignalBarIndex, Iso(r.SignalAvailableAt), r.FuturesAtSignal,
                r.DriverValue, r.DriverAbsExpandingPercentile, r.ExpectedDirectionSign, r.OptionType,
                r.Status, r.Token, r.Strike, r.DecisionBid, r.DecisionAsk,
                r.DecisionRelativeSpreadPct, Iso(r.DecisionQuoteAvailableAt),
                r.DecisionQuoteAgeSeconds, Iso(r.EntryAvailableAt), r.EntryBid, r.EntryAsk,
                r.EntryFill, r.EntryLtp, r.LotSize, r.TickSize));
        }
    }

    static async Task WriteObservationsCsvAsync(string path, IReadOnlyList<ObservationRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "TradingDate,Dte,Candidate,HorizonBars,SignalBarIndex,SignalAvailableAt,TargetAvailableAt," +
            "DriverValue,DriverAbsExpandingPercentile,CvdRaw,CvdAbsExpandingPercentile,ExpectedDirectionSign," +
            "OptionType,Token,Strike,LotSize,TickSize,FuturesAtSignal,FuturesAtTarget,UnderlyingForwardPoints," +
            "UnderlyingCorrect,EntryAvailableAt,EntryLtp,EntryBid,EntryAsk,EntryFill,EntryRelativeSpreadPct," +
            "TargetLtp,TargetLtpAgeSeconds,LtpReturnPct,OutcomeStatus,ExitAvailableAt,ExitDelaySeconds,ExitBid," +
            "ExitAsk,ExitFill,ExitRelativeSpreadPct,ExecutableGrossReturnPct,ExecutableNetReturnPct," +
            "ModeledFeesOneLot,MfePoints,MaePoints,MfePercent,MaePercent,TimeToMfeSeconds,OptionProfitable," +
            "OutcomeCategory");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte, r.Candidate, r.HorizonBars, r.SignalBarIndex, Iso(r.SignalAvailableAt),
                Iso(r.TargetAvailableAt), r.DriverValue, r.DriverAbsExpandingPercentile,
                r.CvdRaw, r.CvdAbsExpandingPercentile, r.ExpectedDirectionSign, r.OptionType,
                r.Token, r.Strike, r.LotSize, r.TickSize, r.FuturesAtSignal, r.FuturesAtTarget,
                r.UnderlyingForwardPoints, r.UnderlyingCorrect, Iso(r.EntryAvailableAt),
                r.EntryLtp, r.EntryBid, r.EntryAsk, r.EntryFill, r.EntryRelativeSpreadPct,
                r.TargetLtp, r.TargetLtpAgeSeconds, r.LtpReturnPct, r.OutcomeStatus,
                Iso(r.ExitAvailableAt), r.ExitDelaySeconds, r.ExitBid, r.ExitAsk, r.ExitFill,
                r.ExitRelativeSpreadPct, r.ExecutableGrossReturnPct, r.ExecutableNetReturnPct,
                r.ModeledFeesOneLot, r.MfePoints, r.MaePoints, r.MfePercent, r.MaePercent,
                r.TimeToMfeSeconds, r.OptionProfitable, r.OutcomeCategory));
        }
    }

    static async Task WriteSummaryCsvAsync(string path, IReadOnlyList<SummaryRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Candidate,HorizonBars,SignalCount,EligibleEntryCount,ExecutableOutcomeCount,NoEligibleContractCount," +
            "NoTimelyEntryQuoteCount,EntryMovedOutOfBandCount,HorizonBeforeEntryCount,NoTargetLtpCount," +
            "NoExecutableExitCount,UnderlyingDirectionalCount,UnderlyingHitRate,OptionDirectionalCount," +
            "OptionNetPositiveRate,FuturesCorrectOptionProfit,FuturesCorrectOptionLoss,FuturesWrongOptionProfit," +
            "FuturesWrongOptionLoss,MeanUnderlyingForwardPointsAligned,MeanLtpReturnPct,MedianLtpReturnPct," +
            "MeanExecutableGrossReturnPct,MedianExecutableGrossReturnPct,MeanExecutableNetReturnPct," +
            "MedianExecutableNetReturnPct,MeanMfePercent,MeanMaePercent,MedianTimeToMfeSeconds");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Candidate, r.HorizonBars, r.SignalCount, r.EligibleEntryCount, r.ExecutableOutcomeCount,
                r.NoEligibleContractCount, r.NoTimelyEntryQuoteCount, r.EntryMovedOutOfBandCount,
                r.HorizonBeforeEntryCount, r.NoTargetLtpCount, r.NoExecutableExitCount,
                r.UnderlyingDirectionalCount, r.UnderlyingHitRate, r.OptionDirectionalCount,
                r.OptionNetPositiveRate, r.FuturesCorrectOptionProfit, r.FuturesCorrectOptionLoss,
                r.FuturesWrongOptionProfit, r.FuturesWrongOptionLoss,
                r.MeanUnderlyingForwardPointsAligned, r.MeanLtpReturnPct, r.MedianLtpReturnPct,
                r.MeanExecutableGrossReturnPct, r.MedianExecutableGrossReturnPct,
                r.MeanExecutableNetReturnPct, r.MedianExecutableNetReturnPct,
                r.MeanMfePercent, r.MeanMaePercent, r.MedianTimeToMfeSeconds));
        }
    }

    static async Task WriteSessionsCsvAsync(string path, IReadOnlyList<SessionRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "TradingDate,Dte,Candidate,HorizonBars,SignalCount,ExecutableOutcomeCount,UnderlyingHitRate," +
            "OptionNetPositiveRate,MeanExecutableNetReturnPct,FuturesCorrectOptionProfit," +
            "FuturesCorrectOptionLoss,FuturesWrongOptionProfit,FuturesWrongOptionLoss");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte, r.Candidate, r.HorizonBars, r.SignalCount, r.ExecutableOutcomeCount,
                r.UnderlyingHitRate, r.OptionNetPositiveRate, r.MeanExecutableNetReturnPct,
                r.FuturesCorrectOptionProfit, r.FuturesCorrectOptionLoss,
                r.FuturesWrongOptionProfit, r.FuturesWrongOptionLoss));
        }
    }

    static async Task WriteDteCsvAsync(string path, IReadOnlyList<DteRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Dte,Candidate,HorizonBars,SignalCount,ExecutableOutcomeCount,UnderlyingHitRate," +
            "OptionNetPositiveRate,MeanExecutableNetReturnPct,MeanLtpReturnPct," +
            "FuturesCorrectOptionProfit,FuturesCorrectOptionLoss,FuturesWrongOptionProfit,FuturesWrongOptionLoss");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Dte, r.Candidate, r.HorizonBars, r.SignalCount, r.ExecutableOutcomeCount,
                r.UnderlyingHitRate, r.OptionNetPositiveRate, r.MeanExecutableNetReturnPct,
                r.MeanLtpReturnPct, r.FuturesCorrectOptionProfit, r.FuturesCorrectOptionLoss,
                r.FuturesWrongOptionProfit, r.FuturesWrongOptionLoss));
        }
    }

    static StreamWriter Writer(string path) =>
        new(path, false, new UTF8Encoding(false));

    static string Iso(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    static string Iso(DateTimeOffset? value) =>
        value is null ? "" : value.Value.ToString("O", CultureInfo.InvariantCulture);

    static string Csv(params object?[] values) =>
        string.Join(",", values.Select(CsvField));

    static string CsvField(object? value)
    {
        if (value is null)
        {
            return "";
        }

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
}
