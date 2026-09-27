using System.Globalization;
using System.Text;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Episode/state-entry diagnostic for the two clean 6500-bar reversal mechanisms and their
/// unweighted agreement state. Maximal consecutive same-sign states become one episode. The
/// primary comparison is made from the first bar of each episode; a separate exact state-age
/// diagnostic reports what happens on continuation bars without selecting a confirmation age.
/// </summary>
public static class VolumeBar6500EpisodeStateAnalysis
{
    public const string PriceState = "PriceReversalState";
    public const string CvdState = "CvdExhaustionState";
    public const string AgreementState = "PriceCvdAgreementState";

    public static readonly string[] Mechanisms = [PriceState, CvdState, AgreementState];

    public sealed record Episode(
        string EpisodeId,
        DateOnly TradingDate,
        int Dte,
        string Mechanism,
        int StateSign,
        int ExpectedDirectionSign,
        string OptionType,
        int StartBarIndex,
        int EndBarIndex,
        DateTimeOffset StartAvailableAt,
        DateTimeOffset EndAvailableAt,
        int BarCount,
        double ElapsedSeconds,
        long ObservedVolume,
        double EntryPriceMove,
        double? EntryCvd,
        double? EntryPriceAbsExpandingPercentile,
        double? EntryCvdAbsExpandingPercentile);

    public sealed record StateBar(
        string EpisodeId,
        DateOnly TradingDate,
        int Dte,
        string Mechanism,
        int StateSign,
        int ExpectedDirectionSign,
        string OptionType,
        int StateAge,
        int BarIndex,
        DateTimeOffset AvailableAt,
        double PriceMove,
        double? Cvd,
        double? PriceAbsExpandingPercentile,
        double? CvdAbsExpandingPercentile);

    public sealed record EntryObservation(
        string EpisodeId,
        DateOnly TradingDate,
        int Dte,
        string Mechanism,
        int StateSign,
        int ExpectedDirectionSign,
        string OptionType,
        int HorizonBars,
        int StartBarIndex,
        DateTimeOffset StartAvailableAt,
        int EpisodeBarCount,
        double EpisodeElapsedSeconds,
        long EpisodeObservedVolume,
        double EntryPriceMove,
        double? EntryCvd,
        double? EntryPriceAbsExpandingPercentile,
        double? EntryCvdAbsExpandingPercentile,
        double? ForwardPoints,
        double? AlignedForwardPoints,
        bool? UnderlyingCorrect,
        VolumeBar6500OptionTranslationAnalysis.SignalStatus? OptionSignalStatus,
        string? Token,
        decimal? Strike,
        VolumeBar6500OptionTranslationAnalysis.OutcomeStatus? OptionOutcomeStatus,
        double? OptionNetReturnPct,
        bool? OptionProfitable,
        double? MfePercent,
        double? MaePercent,
        double? TimeToMfeSeconds,
        string TranslationOutcomeCategory);

    public sealed record StructureRow(
        string Mechanism,
        int SessionCount,
        int StateBarCount,
        int EpisodeCount,
        double EpisodesPerSession,
        double CompressionRatio,
        double MeanEpisodeBars,
        double MedianEpisodeBars,
        double P90EpisodeBars,
        double MeanEpisodeSeconds,
        double MedianEpisodeSeconds,
        double P90EpisodeSeconds,
        int UpExpectedEpisodes,
        int DownExpectedEpisodes);

    public sealed record EntrySummaryRow(
        string Mechanism,
        int HorizonBars,
        int EpisodeCount,
        double EpisodesPerSession,
        int UnderlyingDirectionalCount,
        double? UnderlyingHitRate,
        double? MeanAlignedForwardPoints,
        double? MedianAlignedForwardPoints,
        int EligibleEntryCount,
        int ExecutableOptionCount,
        double? OptionNetPositiveRate,
        double? MeanOptionNetReturnPct,
        double? MedianOptionNetReturnPct,
        double? MeanMfePercent,
        double? MeanMaePercent,
        double? MedianTimeToMfeSeconds,
        int FuturesCorrectOptionProfit,
        int FuturesCorrectOptionLoss,
        int FuturesWrongOptionProfit,
        int FuturesWrongOptionLoss);

    public sealed record SessionRow(
        DateOnly TradingDate,
        int Dte,
        string Mechanism,
        int HorizonBars,
        int EpisodeCount,
        int UnderlyingDirectionalCount,
        double? UnderlyingHitRate,
        double? MeanAlignedForwardPoints,
        int EligibleEntryCount,
        int ExecutableOptionCount,
        double? OptionNetPositiveRate,
        double? MeanOptionNetReturnPct,
        double? MedianOptionNetReturnPct,
        int FuturesCorrectOptionProfit,
        int FuturesCorrectOptionLoss,
        int FuturesWrongOptionProfit,
        int FuturesWrongOptionLoss);

    public sealed record DteRow(
        int Dte,
        string Mechanism,
        int HorizonBars,
        int SessionCount,
        int EpisodeCount,
        int UnderlyingDirectionalCount,
        double? UnderlyingHitRate,
        double? MeanAlignedForwardPoints,
        int EligibleEntryCount,
        int ExecutableOptionCount,
        double? OptionNetPositiveRate,
        double? MeanOptionNetReturnPct,
        double? MedianOptionNetReturnPct,
        int FuturesCorrectOptionProfit,
        int FuturesCorrectOptionLoss,
        int FuturesWrongOptionProfit,
        int FuturesWrongOptionLoss);

    public sealed record AgeRow(
        string Mechanism,
        int HorizonBars,
        int StateAge,
        int SessionCount,
        int ObservationCount,
        int UnderlyingDirectionalCount,
        double? UnderlyingHitRate,
        double? MeanAlignedForwardPoints,
        int EligibleEntryCount,
        int ExecutableOptionCount,
        double? OptionNetPositiveRate,
        double? MeanOptionNetReturnPct,
        double? MedianOptionNetReturnPct,
        double? MeanMfePercent,
        double? MeanMaePercent,
        double? MedianTimeToMfeSeconds);

    public sealed record AnalysisResult(
        IReadOnlyList<Episode> Episodes,
        IReadOnlyList<StateBar> StateBars,
        IReadOnlyList<EntryObservation> EntryObservations,
        IReadOnlyList<StructureRow> Structure,
        IReadOnlyList<EntrySummaryRow> EntrySummary,
        IReadOnlyList<SessionRow> Sessions,
        IReadOnlyList<DteRow> Dte,
        IReadOnlyList<AgeRow> Age);

    public static int? StateSign(string mechanism, VolumeBar6500Revalidation.Observation row)
    {
        var priceSign = SignOrNull(row.SignalBarChangePoints);
        var cvdSign = row.FutureCvdProxyNet is { } cvd ? SignOrNull(cvd) : null;

        return mechanism switch
        {
            PriceState => priceSign,
            CvdState => cvdSign,
            AgreementState => priceSign is { } p && cvdSign is { } c && p == c ? p : null,
            _ => throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, "Unknown state mechanism."),
        };
    }

    public static (IReadOnlyList<Episode> Episodes, IReadOnlyList<StateBar> StateBars) BuildEpisodes(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        var episodes = new List<Episode>();
        var stateBars = new List<StateBar>();

        foreach (var day in observations.GroupBy(x => x.TradingDate).OrderBy(g => g.Key))
        {
            var rows = day.OrderBy(x => x.BarIndex).ToArray();
            var priceRanks = VolumeBar6500OptionTranslationAnalysis.ExpandingAbsolutePercentiles(
                rows.Select(x => x.SignalBarChangePoints == 0 ? (double?)null : x.SignalBarChangePoints).ToArray());
            var cvdRanks = VolumeBar6500OptionTranslationAnalysis.ExpandingAbsolutePercentiles(
                rows.Select(x => x.FutureCvdProxyNet is { } cvd && cvd != 0 ? (double?)cvd : null).ToArray());

            foreach (var mechanism in Mechanisms)
            {
                var episodeSequence = 0;
                var startIndex = -1;
                int? currentSign = null;

                void Flush(int endExclusive)
                {
                    if (startIndex < 0 || currentSign is null)
                    {
                        return;
                    }

                    var endIndex = endExclusive - 1;
                    var first = rows[startIndex];
                    var last = rows[endIndex];
                    var sequence = ++episodeSequence;
                    var id = $"{first.TradingDate:yyyyMMdd}-{mechanism}-{sequence:D4}";
                    var expectedDirection = -currentSign.Value;

                    episodes.Add(new Episode(
                        id,
                        first.TradingDate,
                        first.Dte,
                        mechanism,
                        currentSign.Value,
                        expectedDirection,
                        expectedDirection > 0 ? "Call" : "Put",
                        first.BarIndex,
                        last.BarIndex,
                        first.AvailableAt,
                        last.AvailableAt,
                        endIndex - startIndex + 1,
                        (last.AvailableAt - first.AvailableAt).TotalSeconds,
                        rows[startIndex..(endIndex + 1)].Sum(x => x.ObservedVolume),
                        first.SignalBarChangePoints,
                        first.FutureCvdProxyNet is { } cvd ? cvd : null,
                        priceRanks[startIndex],
                        cvdRanks[startIndex]));

                    for (var i = startIndex; i <= endIndex; i++)
                    {
                        stateBars.Add(new StateBar(
                            id,
                            rows[i].TradingDate,
                            rows[i].Dte,
                            mechanism,
                            currentSign.Value,
                            expectedDirection,
                            expectedDirection > 0 ? "Call" : "Put",
                            i - startIndex + 1,
                            rows[i].BarIndex,
                            rows[i].AvailableAt,
                            rows[i].SignalBarChangePoints,
                            rows[i].FutureCvdProxyNet is { } barCvd ? barCvd : null,
                            priceRanks[i],
                            cvdRanks[i]));
                    }

                    startIndex = -1;
                    currentSign = null;
                }

                for (var i = 0; i < rows.Length; i++)
                {
                    var sign = StateSign(mechanism, rows[i]);

                    if (sign is null)
                    {
                        Flush(i);
                        continue;
                    }

                    if (currentSign is null)
                    {
                        startIndex = i;
                        currentSign = sign;
                        continue;
                    }

                    if (currentSign.Value != sign.Value)
                    {
                        Flush(i);
                        startIndex = i;
                        currentSign = sign;
                    }
                }

                Flush(rows.Length);
            }
        }

        return (episodes, stateBars);
    }

    public static AnalysisResult Analyze(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        VolumeBar6500OptionTranslationAnalysis.AnalysisResult translation)
    {
        var built = BuildEpisodes(observations);
        var episodes = built.Episodes.ToArray();
        var stateBars = built.StateBars.ToArray();

        var observationByDateBar = observations.ToDictionary(x => (x.TradingDate, x.BarIndex));
        var audit = translation.SignalAudit.ToDictionary(
            x => (x.TradingDate, x.Candidate, x.SignalBarIndex));
        var optionOutcomes = translation.Observations.ToDictionary(
            x => (x.TradingDate, x.Candidate, x.SignalBarIndex, x.HorizonBars));

        ValidateAgreementTranslationParity(stateBars, audit, optionOutcomes);

        var entryObservations = new List<EntryObservation>();
        foreach (var episode in episodes)
        {
            if (!observationByDateBar.TryGetValue((episode.TradingDate, episode.StartBarIndex), out var source))
            {
                continue;
            }

            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                entryObservations.Add(BuildEntryObservation(
                    episode,
                    source,
                    horizon,
                    audit,
                    optionOutcomes));
            }
        }

        var structure = BuildStructure(episodes, stateBars, observations);
        var entrySummary = BuildEntrySummary(entryObservations, episodes, observations);
        var sessions = BuildSessions(entryObservations);
        var dte = BuildDte(entryObservations);
        var age = BuildAge(stateBars, observationByDateBar, audit, optionOutcomes);

        return new AnalysisResult(
            episodes,
            stateBars,
            entryObservations,
            structure,
            entrySummary,
            sessions,
            dte,
            age);
    }

    public static async Task<AnalysisResult> WriteReportsAsync(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        VolumeBar6500OptionTranslationAnalysis.AnalysisResult translation,
        string outputDirectory)
    {
        var result = Analyze(observations, translation);
        Directory.CreateDirectory(outputDirectory);

        await WriteEpisodesAsync(Path.Combine(outputDirectory, "episode-state-episodes.csv"), result.Episodes);
        await WriteEntryObservationsAsync(Path.Combine(outputDirectory, "episode-state-entry-observations.csv"), result.EntryObservations);
        await WriteStructureAsync(Path.Combine(outputDirectory, "episode-state-structure.csv"), result.Structure);
        await WriteEntrySummaryAsync(Path.Combine(outputDirectory, "episode-state-entry-summary.csv"), result.EntrySummary);
        await WriteSessionsAsync(Path.Combine(outputDirectory, "episode-state-entry-sessions.csv"), result.Sessions);
        await WriteDteAsync(Path.Combine(outputDirectory, "episode-state-entry-dte.csv"), result.Dte);
        await WriteAgeAsync(Path.Combine(outputDirectory, "episode-state-age-summary.csv"), result.Age);

        PrintPrimarySummary(result);
        return result;
    }

    static EntryObservation BuildEntryObservation(
        Episode episode,
        VolumeBar6500Revalidation.Observation source,
        VolumeBar6500OptionTranslationAnalysis.HorizonSpec horizon,
        IReadOnlyDictionary<(DateOnly, string, int), VolumeBar6500OptionTranslationAnalysis.SignalAuditRow> audit,
        IReadOnlyDictionary<(DateOnly, string, int, int), VolumeBar6500OptionTranslationAnalysis.ObservationRow> optionOutcomes)
    {
        var forward = horizon.ForwardPoints(source);
        var aligned = forward is { } f ? episode.ExpectedDirectionSign * f : (double?)null;
        bool? correct = aligned is null || aligned == 0 ? null : aligned > 0;
        var candidate = TranslationCandidate(episode.Mechanism);

        audit.TryGetValue((episode.TradingDate, candidate, episode.StartBarIndex), out var signalAudit);
        optionOutcomes.TryGetValue((episode.TradingDate, candidate, episode.StartBarIndex, horizon.Bars), out var option);

        var category = option?.OutcomeCategory ??
            (signalAudit is null ? "NoTranslationSignal" : signalAudit.Status.ToString());

        return new EntryObservation(
            episode.EpisodeId,
            episode.TradingDate,
            episode.Dte,
            episode.Mechanism,
            episode.StateSign,
            episode.ExpectedDirectionSign,
            episode.OptionType,
            horizon.Bars,
            episode.StartBarIndex,
            episode.StartAvailableAt,
            episode.BarCount,
            episode.ElapsedSeconds,
            episode.ObservedVolume,
            episode.EntryPriceMove,
            episode.EntryCvd,
            episode.EntryPriceAbsExpandingPercentile,
            episode.EntryCvdAbsExpandingPercentile,
            forward,
            aligned,
            correct,
            signalAudit?.Status,
            signalAudit?.Token,
            signalAudit?.Strike,
            option?.OutcomeStatus,
            option?.ExecutableNetReturnPct,
            option?.OptionProfitable,
            option?.MfePercent,
            option?.MaePercent,
            option?.TimeToMfeSeconds,
            category);
    }

    static List<StructureRow> BuildStructure(
        IReadOnlyList<Episode> episodes,
        IReadOnlyList<StateBar> stateBars,
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        var sessionCount = observations.Select(x => x.TradingDate).Distinct().Count();
        var rows = new List<StructureRow>();

        foreach (var mechanism in Mechanisms)
        {
            var eps = episodes.Where(x => x.Mechanism == mechanism).ToArray();
            var bars = stateBars.Where(x => x.Mechanism == mechanism).ToArray();
            var lengths = eps.Select(x => (double)x.BarCount).ToArray();
            var seconds = eps.Select(x => x.ElapsedSeconds).ToArray();

            rows.Add(new StructureRow(
                mechanism,
                sessionCount,
                bars.Length,
                eps.Length,
                sessionCount > 0 ? eps.Length / (double)sessionCount : 0,
                bars.Length > 0 ? eps.Length / (double)bars.Length : 0,
                MeanOrNull(lengths) ?? 0,
                MedianOrNull(lengths) ?? 0,
                PercentileOrNull(lengths, 0.90) ?? 0,
                MeanOrNull(seconds) ?? 0,
                MedianOrNull(seconds) ?? 0,
                PercentileOrNull(seconds, 0.90) ?? 0,
                eps.Count(x => x.ExpectedDirectionSign > 0),
                eps.Count(x => x.ExpectedDirectionSign < 0)));
        }

        return rows;
    }

    static List<EntrySummaryRow> BuildEntrySummary(
        IReadOnlyList<EntryObservation> observations,
        IReadOnlyList<Episode> episodes,
        IReadOnlyList<VolumeBar6500Revalidation.Observation> source)
    {
        var sessionCount = source.Select(x => x.TradingDate).Distinct().Count();
        var rows = new List<EntrySummaryRow>();

        foreach (var mechanism in Mechanisms)
        {
            var episodeCount = episodes.Count(x => x.Mechanism == mechanism);
            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                var group = observations
                    .Where(x => x.Mechanism == mechanism && x.HorizonBars == horizon.Bars)
                    .ToArray();

                rows.Add(SummarizeEntry(
                    mechanism,
                    horizon.Bars,
                    episodeCount,
                    sessionCount > 0 ? episodeCount / (double)sessionCount : 0,
                    group));
            }
        }

        return rows;
    }

    static EntrySummaryRow SummarizeEntry(
        string mechanism,
        int horizon,
        int episodeCount,
        double episodesPerSession,
        IReadOnlyList<EntryObservation> group)
    {
        var directional = group.Where(x => x.UnderlyingCorrect is not null).ToArray();
        var executable = group.Where(x =>
            x.OptionOutcomeStatus == VolumeBar6500OptionTranslationAnalysis.OutcomeStatus.Executable &&
            x.OptionProfitable is not null).ToArray();

        return new EntrySummaryRow(
            mechanism,
            horizon,
            episodeCount,
            episodesPerSession,
            directional.Length,
            RateOrNull(directional.Select(x => x.UnderlyingCorrect!.Value).ToArray()),
            MeanOrNull(group.Where(x => x.AlignedForwardPoints is not null).Select(x => x.AlignedForwardPoints!.Value).ToArray()),
            MedianOrNull(group.Where(x => x.AlignedForwardPoints is not null).Select(x => x.AlignedForwardPoints!.Value).ToArray()),
            group.Count(x => x.OptionSignalStatus == VolumeBar6500OptionTranslationAnalysis.SignalStatus.Eligible),
            executable.Length,
            RateOrNull(executable.Select(x => x.OptionProfitable!.Value).ToArray()),
            MeanOrNull(executable.Where(x => x.OptionNetReturnPct is not null).Select(x => x.OptionNetReturnPct!.Value).ToArray()),
            MedianOrNull(executable.Where(x => x.OptionNetReturnPct is not null).Select(x => x.OptionNetReturnPct!.Value).ToArray()),
            MeanOrNull(executable.Where(x => x.MfePercent is not null).Select(x => x.MfePercent!.Value).ToArray()),
            MeanOrNull(executable.Where(x => x.MaePercent is not null).Select(x => x.MaePercent!.Value).ToArray()),
            MedianOrNull(executable.Where(x => x.TimeToMfeSeconds is not null).Select(x => x.TimeToMfeSeconds!.Value).ToArray()),
            group.Count(x => x.TranslationOutcomeCategory == "FuturesCorrectOptionProfit"),
            group.Count(x => x.TranslationOutcomeCategory == "FuturesCorrectOptionLoss"),
            group.Count(x => x.TranslationOutcomeCategory == "FuturesWrongOptionProfit"),
            group.Count(x => x.TranslationOutcomeCategory == "FuturesWrongOptionLoss"));
    }

    static List<SessionRow> BuildSessions(IReadOnlyList<EntryObservation> observations)
    {
        var rows = new List<SessionRow>();
        foreach (var group in observations
            .GroupBy(x => (x.TradingDate, x.Dte, x.Mechanism, x.HorizonBars))
            .OrderBy(g => g.Key.TradingDate)
            .ThenBy(g => g.Key.Mechanism)
            .ThenBy(g => g.Key.HorizonBars))
        {
            var items = group.ToArray();
            var directional = items.Where(x => x.UnderlyingCorrect is not null).ToArray();
            var executable = items.Where(x =>
                x.OptionOutcomeStatus == VolumeBar6500OptionTranslationAnalysis.OutcomeStatus.Executable &&
                x.OptionProfitable is not null).ToArray();

            rows.Add(new SessionRow(
                group.Key.TradingDate,
                group.Key.Dte,
                group.Key.Mechanism,
                group.Key.HorizonBars,
                items.Select(x => x.EpisodeId).Distinct().Count(),
                directional.Length,
                RateOrNull(directional.Select(x => x.UnderlyingCorrect!.Value).ToArray()),
                MeanOrNull(items.Where(x => x.AlignedForwardPoints is not null).Select(x => x.AlignedForwardPoints!.Value).ToArray()),
                items.Count(x => x.OptionSignalStatus == VolumeBar6500OptionTranslationAnalysis.SignalStatus.Eligible),
                executable.Length,
                RateOrNull(executable.Select(x => x.OptionProfitable!.Value).ToArray()),
                MeanOrNull(executable.Where(x => x.OptionNetReturnPct is not null).Select(x => x.OptionNetReturnPct!.Value).ToArray()),
                MedianOrNull(executable.Where(x => x.OptionNetReturnPct is not null).Select(x => x.OptionNetReturnPct!.Value).ToArray()),
                items.Count(x => x.TranslationOutcomeCategory == "FuturesCorrectOptionProfit"),
                items.Count(x => x.TranslationOutcomeCategory == "FuturesCorrectOptionLoss"),
                items.Count(x => x.TranslationOutcomeCategory == "FuturesWrongOptionProfit"),
                items.Count(x => x.TranslationOutcomeCategory == "FuturesWrongOptionLoss")));
        }

        return rows;
    }

    static List<DteRow> BuildDte(IReadOnlyList<EntryObservation> observations)
    {
        var rows = new List<DteRow>();
        foreach (var group in observations
            .GroupBy(x => (x.Dte, x.Mechanism, x.HorizonBars))
            .OrderBy(g => g.Key.Dte)
            .ThenBy(g => g.Key.Mechanism)
            .ThenBy(g => g.Key.HorizonBars))
        {
            var items = group.ToArray();
            var directional = items.Where(x => x.UnderlyingCorrect is not null).ToArray();
            var executable = items.Where(x =>
                x.OptionOutcomeStatus == VolumeBar6500OptionTranslationAnalysis.OutcomeStatus.Executable &&
                x.OptionProfitable is not null).ToArray();

            rows.Add(new DteRow(
                group.Key.Dte,
                group.Key.Mechanism,
                group.Key.HorizonBars,
                items.Select(x => x.TradingDate).Distinct().Count(),
                items.Select(x => x.EpisodeId).Distinct().Count(),
                directional.Length,
                RateOrNull(directional.Select(x => x.UnderlyingCorrect!.Value).ToArray()),
                MeanOrNull(items.Where(x => x.AlignedForwardPoints is not null).Select(x => x.AlignedForwardPoints!.Value).ToArray()),
                items.Count(x => x.OptionSignalStatus == VolumeBar6500OptionTranslationAnalysis.SignalStatus.Eligible),
                executable.Length,
                RateOrNull(executable.Select(x => x.OptionProfitable!.Value).ToArray()),
                MeanOrNull(executable.Where(x => x.OptionNetReturnPct is not null).Select(x => x.OptionNetReturnPct!.Value).ToArray()),
                MedianOrNull(executable.Where(x => x.OptionNetReturnPct is not null).Select(x => x.OptionNetReturnPct!.Value).ToArray()),
                items.Count(x => x.TranslationOutcomeCategory == "FuturesCorrectOptionProfit"),
                items.Count(x => x.TranslationOutcomeCategory == "FuturesCorrectOptionLoss"),
                items.Count(x => x.TranslationOutcomeCategory == "FuturesWrongOptionProfit"),
                items.Count(x => x.TranslationOutcomeCategory == "FuturesWrongOptionLoss")));
        }

        return rows;
    }

    static List<AgeRow> BuildAge(
        IReadOnlyList<StateBar> stateBars,
        IReadOnlyDictionary<(DateOnly, int), VolumeBar6500Revalidation.Observation> observationByDateBar,
        IReadOnlyDictionary<(DateOnly, string, int), VolumeBar6500OptionTranslationAnalysis.SignalAuditRow> audit,
        IReadOnlyDictionary<(DateOnly, string, int, int), VolumeBar6500OptionTranslationAnalysis.ObservationRow> optionOutcomes)
    {
        var raw = new List<(StateBar Bar, int Horizon, double? Forward, double? Aligned, bool? Correct,
            VolumeBar6500OptionTranslationAnalysis.SignalStatus? SignalStatus,
            VolumeBar6500OptionTranslationAnalysis.ObservationRow? Option)>();

        foreach (var stateBar in stateBars)
        {
            if (!observationByDateBar.TryGetValue((stateBar.TradingDate, stateBar.BarIndex), out var source))
            {
                continue;
            }

            var candidate = TranslationCandidate(stateBar.Mechanism);
            audit.TryGetValue((stateBar.TradingDate, candidate, stateBar.BarIndex), out var signalAudit);

            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                var forward = horizon.ForwardPoints(source);
                var aligned = forward is { } f ? stateBar.ExpectedDirectionSign * f : (double?)null;
                bool? correct = aligned is null || aligned == 0 ? null : aligned > 0;
                optionOutcomes.TryGetValue((stateBar.TradingDate, candidate, stateBar.BarIndex, horizon.Bars), out var option);

                raw.Add((stateBar, horizon.Bars, forward, aligned, correct, signalAudit?.Status, option));
            }
        }

        var rows = new List<AgeRow>();
        foreach (var group in raw.GroupBy(x => (x.Bar.Mechanism, x.Horizon, x.Bar.StateAge))
            .OrderBy(g => g.Key.Mechanism)
            .ThenBy(g => g.Key.Horizon)
            .ThenBy(g => g.Key.StateAge))
        {
            var items = group.ToArray();
            var directional = items.Where(x => x.Correct is not null).ToArray();
            var executable = items.Where(x =>
                x.Option is
                {
                    OutcomeStatus: VolumeBar6500OptionTranslationAnalysis.OutcomeStatus.Executable,
                    OptionProfitable: not null
                }).ToArray();

            rows.Add(new AgeRow(
                group.Key.Mechanism,
                group.Key.Horizon,
                group.Key.StateAge,
                items.Select(x => x.Bar.TradingDate).Distinct().Count(),
                items.Length,
                directional.Length,
                RateOrNull(directional.Select(x => x.Correct!.Value).ToArray()),
                MeanOrNull(items.Where(x => x.Aligned is not null).Select(x => x.Aligned!.Value).ToArray()),
                items.Count(x => x.SignalStatus == VolumeBar6500OptionTranslationAnalysis.SignalStatus.Eligible),
                executable.Length,
                RateOrNull(executable.Select(x => x.Option!.OptionProfitable!.Value).ToArray()),
                MeanOrNull(executable.Where(x => x.Option!.ExecutableNetReturnPct is not null).Select(x => x.Option!.ExecutableNetReturnPct!.Value).ToArray()),
                MedianOrNull(executable.Where(x => x.Option!.ExecutableNetReturnPct is not null).Select(x => x.Option!.ExecutableNetReturnPct!.Value).ToArray()),
                MeanOrNull(executable.Select(x => x.Option!.MfePercent).ToArray()),
                MeanOrNull(executable.Select(x => x.Option!.MaePercent).ToArray()),
                MedianOrNull(executable.Where(x => x.Option!.TimeToMfeSeconds is not null).Select(x => x.Option!.TimeToMfeSeconds!.Value).ToArray())));
        }

        return rows;
    }

    static void ValidateAgreementTranslationParity(
        IReadOnlyList<StateBar> stateBars,
        IReadOnlyDictionary<(DateOnly, string, int), VolumeBar6500OptionTranslationAnalysis.SignalAuditRow> audit,
        IReadOnlyDictionary<(DateOnly, string, int, int), VolumeBar6500OptionTranslationAnalysis.ObservationRow> optionOutcomes)
    {
        foreach (var bar in stateBars.Where(x => x.Mechanism == AgreementState))
        {
            if (!audit.TryGetValue((bar.TradingDate, "CurrentBarReversal", bar.BarIndex), out var priceAudit) ||
                !audit.TryGetValue((bar.TradingDate, "FutureCvdProxyExhaustion", bar.BarIndex), out var cvdAudit))
            {
                throw new InvalidDataException($"{bar.TradingDate:yyyy-MM-dd} bar {bar.BarIndex}: agreement bar missing one translation audit.");
            }

            if (priceAudit.ExpectedDirectionSign != cvdAudit.ExpectedDirectionSign ||
                priceAudit.OptionType != cvdAudit.OptionType ||
                priceAudit.Status != cvdAudit.Status ||
                priceAudit.Token != cvdAudit.Token ||
                priceAudit.Strike != cvdAudit.Strike ||
                priceAudit.EntryAvailableAt != cvdAudit.EntryAvailableAt ||
                priceAudit.EntryFill != cvdAudit.EntryFill)
            {
                throw new InvalidDataException(
                    $"{bar.TradingDate:yyyy-MM-dd} bar {bar.BarIndex}: price/CVD agreement produced different option translation selection/execution.");
            }

            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                var priceFound = optionOutcomes.TryGetValue((bar.TradingDate, "CurrentBarReversal", bar.BarIndex, horizon.Bars), out var price);
                var cvdFound = optionOutcomes.TryGetValue((bar.TradingDate, "FutureCvdProxyExhaustion", bar.BarIndex, horizon.Bars), out var cvd);

                if (priceFound != cvdFound)
                {
                    throw new InvalidDataException(
                        $"{bar.TradingDate:yyyy-MM-dd} bar {bar.BarIndex} +{horizon.Bars}: agreement outcome presence mismatch.");
                }

                if (!priceFound)
                {
                    continue;
                }

                if (price!.Token != cvd!.Token ||
                    price.EntryFill != cvd.EntryFill ||
                    price.OutcomeStatus != cvd.OutcomeStatus ||
                    price.ExitFill != cvd.ExitFill ||
                    price.ExecutableNetReturnPct != cvd.ExecutableNetReturnPct)
                {
                    throw new InvalidDataException(
                        $"{bar.TradingDate:yyyy-MM-dd} bar {bar.BarIndex} +{horizon.Bars}: agreement outcome mismatch.");
                }
            }
        }
    }

    static string TranslationCandidate(string mechanism) => mechanism switch
    {
        PriceState => "CurrentBarReversal",
        CvdState => "FutureCvdProxyExhaustion",
        AgreementState => "FutureCvdProxyExhaustion",
        _ => throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, "Unknown state mechanism."),
    };

    static int? SignOrNull(double value) =>
        !double.IsFinite(value) || value == 0 ? null : Math.Sign(value);

    static int? SignOrNull(long value) =>
        value == 0 ? null : Math.Sign(value);

    static double? RateOrNull(IReadOnlyList<bool> values) =>
        values.Count == 0 ? null : values.Count(x => x) / (double)values.Count;

    static double? MeanOrNull(IReadOnlyList<double> values) =>
        values.Count == 0 ? null : values.Average();

    static double? MedianOrNull(IReadOnlyList<double> values)
    {
        if (values.Count == 0) { return null; }
        var sorted = values.OrderBy(x => x).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    static double? PercentileOrNull(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) { return null; }
        var sorted = values.OrderBy(x => x).ToArray();
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * sorted.Length));
        return sorted[rank - 1];
    }

    static void PrintPrimarySummary(AnalysisResult result)
    {
        Console.WriteLine("6500 episode/state-entry analysis — maximal same-sign states, first bar only for primary entry comparison:");

        foreach (var structure in result.Structure)
        {
            Console.WriteLine(
                $"  {structure.Mechanism,-23} stateBars={structure.StateBarCount:N0}, episodes={structure.EpisodeCount:N0}, " +
                $"episodes/day={structure.EpisodesPerSession:0.0}, compression={structure.CompressionRatio:P1}, " +
                $"medianBars={structure.MedianEpisodeBars:0.0}, p90Bars={structure.P90EpisodeBars:0.0}");
        }

        foreach (var mechanism in Mechanisms)
        {
            foreach (var row in result.EntrySummary.Where(x => x.Mechanism == mechanism).OrderBy(x => x.HorizonBars))
            {
                Console.WriteLine(
                    $"  {row.Mechanism,-23} +{row.HorizonBars}: episodes={row.EpisodeCount:N0}, " +
                    $"underlyingHit={FormatRate(row.UnderlyingHitRate)}, alignedPts={Format(row.MeanAlignedForwardPoints)}, " +
                    $"option+={FormatRate(row.OptionNetPositiveRate)}, meanNet={FormatPct(row.MeanOptionNetReturnPct)}, " +
                    $"medianNet={FormatPct(row.MedianOptionNetReturnPct)}, F+/O+={row.FuturesCorrectOptionProfit}, " +
                    $"F+/O-={row.FuturesCorrectOptionLoss}, F-/O+={row.FuturesWrongOptionProfit}, F-/O-={row.FuturesWrongOptionLoss}");
            }
        }

        Console.WriteLine("Exact state-age diagnostics are exported separately; no confirmation age is selected by this run.");
    }

    static string Format(double? value) => value is null ? "NA" : value.Value.ToString("0.0000", CultureInfo.InvariantCulture);
    static string FormatRate(double? value) => value is null ? "NA" : (value.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    static string FormatPct(double? value) => value is null ? "NA" : value.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%";

    static async Task WriteEpisodesAsync(string path, IReadOnlyList<Episode> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("EpisodeId,TradingDate,Dte,Mechanism,StateSign,ExpectedDirectionSign,OptionType,StartBarIndex,EndBarIndex,StartAvailableAt,EndAvailableAt,BarCount,ElapsedSeconds,ObservedVolume,EntryPriceMove,EntryCvd,EntryPriceAbsExpandingPercentile,EntryCvdAbsExpandingPercentile");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.EpisodeId, r.TradingDate.ToString("yyyy-MM-dd"), r.Dte, r.Mechanism, r.StateSign, r.ExpectedDirectionSign, r.OptionType, r.StartBarIndex, r.EndBarIndex, Iso(r.StartAvailableAt), Iso(r.EndAvailableAt), r.BarCount, r.ElapsedSeconds, r.ObservedVolume, r.EntryPriceMove, r.EntryCvd, r.EntryPriceAbsExpandingPercentile, r.EntryCvdAbsExpandingPercentile));
    }

    static async Task WriteEntryObservationsAsync(string path, IReadOnlyList<EntryObservation> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("EpisodeId,TradingDate,Dte,Mechanism,StateSign,ExpectedDirectionSign,OptionType,HorizonBars,StartBarIndex,StartAvailableAt,EpisodeBarCount,EpisodeElapsedSeconds,EpisodeObservedVolume,EntryPriceMove,EntryCvd,EntryPriceAbsExpandingPercentile,EntryCvdAbsExpandingPercentile,ForwardPoints,AlignedForwardPoints,UnderlyingCorrect,OptionSignalStatus,Token,Strike,OptionOutcomeStatus,OptionNetReturnPct,OptionProfitable,MfePercent,MaePercent,TimeToMfeSeconds,TranslationOutcomeCategory");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.EpisodeId, r.TradingDate.ToString("yyyy-MM-dd"), r.Dte, r.Mechanism, r.StateSign, r.ExpectedDirectionSign, r.OptionType, r.HorizonBars, r.StartBarIndex, Iso(r.StartAvailableAt), r.EpisodeBarCount, r.EpisodeElapsedSeconds, r.EpisodeObservedVolume, r.EntryPriceMove, r.EntryCvd, r.EntryPriceAbsExpandingPercentile, r.EntryCvdAbsExpandingPercentile, r.ForwardPoints, r.AlignedForwardPoints, r.UnderlyingCorrect, r.OptionSignalStatus, r.Token, r.Strike, r.OptionOutcomeStatus, r.OptionNetReturnPct, r.OptionProfitable, r.MfePercent, r.MaePercent, r.TimeToMfeSeconds, r.TranslationOutcomeCategory));
    }

    static async Task WriteStructureAsync(string path, IReadOnlyList<StructureRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("Mechanism,SessionCount,StateBarCount,EpisodeCount,EpisodesPerSession,CompressionRatio,MeanEpisodeBars,MedianEpisodeBars,P90EpisodeBars,MeanEpisodeSeconds,MedianEpisodeSeconds,P90EpisodeSeconds,UpExpectedEpisodes,DownExpectedEpisodes");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.Mechanism, r.SessionCount, r.StateBarCount, r.EpisodeCount, r.EpisodesPerSession, r.CompressionRatio, r.MeanEpisodeBars, r.MedianEpisodeBars, r.P90EpisodeBars, r.MeanEpisodeSeconds, r.MedianEpisodeSeconds, r.P90EpisodeSeconds, r.UpExpectedEpisodes, r.DownExpectedEpisodes));
    }

    static async Task WriteEntrySummaryAsync(string path, IReadOnlyList<EntrySummaryRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("Mechanism,HorizonBars,EpisodeCount,EpisodesPerSession,UnderlyingDirectionalCount,UnderlyingHitRate,MeanAlignedForwardPoints,MedianAlignedForwardPoints,EligibleEntryCount,ExecutableOptionCount,OptionNetPositiveRate,MeanOptionNetReturnPct,MedianOptionNetReturnPct,MeanMfePercent,MeanMaePercent,MedianTimeToMfeSeconds,FuturesCorrectOptionProfit,FuturesCorrectOptionLoss,FuturesWrongOptionProfit,FuturesWrongOptionLoss");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.Mechanism, r.HorizonBars, r.EpisodeCount, r.EpisodesPerSession, r.UnderlyingDirectionalCount, r.UnderlyingHitRate, r.MeanAlignedForwardPoints, r.MedianAlignedForwardPoints, r.EligibleEntryCount, r.ExecutableOptionCount, r.OptionNetPositiveRate, r.MeanOptionNetReturnPct, r.MedianOptionNetReturnPct, r.MeanMfePercent, r.MeanMaePercent, r.MedianTimeToMfeSeconds, r.FuturesCorrectOptionProfit, r.FuturesCorrectOptionLoss, r.FuturesWrongOptionProfit, r.FuturesWrongOptionLoss));
    }

    static async Task WriteSessionsAsync(string path, IReadOnlyList<SessionRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("TradingDate,Dte,Mechanism,HorizonBars,EpisodeCount,UnderlyingDirectionalCount,UnderlyingHitRate,MeanAlignedForwardPoints,EligibleEntryCount,ExecutableOptionCount,OptionNetPositiveRate,MeanOptionNetReturnPct,MedianOptionNetReturnPct,FuturesCorrectOptionProfit,FuturesCorrectOptionLoss,FuturesWrongOptionProfit,FuturesWrongOptionLoss");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.TradingDate.ToString("yyyy-MM-dd"), r.Dte, r.Mechanism, r.HorizonBars, r.EpisodeCount, r.UnderlyingDirectionalCount, r.UnderlyingHitRate, r.MeanAlignedForwardPoints, r.EligibleEntryCount, r.ExecutableOptionCount, r.OptionNetPositiveRate, r.MeanOptionNetReturnPct, r.MedianOptionNetReturnPct, r.FuturesCorrectOptionProfit, r.FuturesCorrectOptionLoss, r.FuturesWrongOptionProfit, r.FuturesWrongOptionLoss));
    }

    static async Task WriteDteAsync(string path, IReadOnlyList<DteRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("Dte,Mechanism,HorizonBars,SessionCount,EpisodeCount,UnderlyingDirectionalCount,UnderlyingHitRate,MeanAlignedForwardPoints,EligibleEntryCount,ExecutableOptionCount,OptionNetPositiveRate,MeanOptionNetReturnPct,MedianOptionNetReturnPct,FuturesCorrectOptionProfit,FuturesCorrectOptionLoss,FuturesWrongOptionProfit,FuturesWrongOptionLoss");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.Dte, r.Mechanism, r.HorizonBars, r.SessionCount, r.EpisodeCount, r.UnderlyingDirectionalCount, r.UnderlyingHitRate, r.MeanAlignedForwardPoints, r.EligibleEntryCount, r.ExecutableOptionCount, r.OptionNetPositiveRate, r.MeanOptionNetReturnPct, r.MedianOptionNetReturnPct, r.FuturesCorrectOptionProfit, r.FuturesCorrectOptionLoss, r.FuturesWrongOptionProfit, r.FuturesWrongOptionLoss));
    }

    static async Task WriteAgeAsync(string path, IReadOnlyList<AgeRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("Mechanism,HorizonBars,StateAge,SessionCount,ObservationCount,UnderlyingDirectionalCount,UnderlyingHitRate,MeanAlignedForwardPoints,EligibleEntryCount,ExecutableOptionCount,OptionNetPositiveRate,MeanOptionNetReturnPct,MedianOptionNetReturnPct,MeanMfePercent,MeanMaePercent,MedianTimeToMfeSeconds");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.Mechanism, r.HorizonBars, r.StateAge, r.SessionCount, r.ObservationCount, r.UnderlyingDirectionalCount, r.UnderlyingHitRate, r.MeanAlignedForwardPoints, r.EligibleEntryCount, r.ExecutableOptionCount, r.OptionNetPositiveRate, r.MeanOptionNetReturnPct, r.MedianOptionNetReturnPct, r.MeanMfePercent, r.MeanMaePercent, r.MedianTimeToMfeSeconds));
    }

    static StreamWriter Writer(string path) => new(path, false, new UTF8Encoding(false));
    static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    static string Csv(params object?[] values) => string.Join(",", values.Select(CsvField));
    static string CsvField(object? value)
    {
        if (value is null) return "";
        var field = value switch
        {
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return field.IndexOfAny([',', '"', '\r', '\n']) < 0 ? field : "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}