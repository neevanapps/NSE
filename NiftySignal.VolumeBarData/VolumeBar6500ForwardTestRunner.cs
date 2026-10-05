namespace NiftySignal.VolumeBarData;

/// <summary>
/// Ad hoc, explicitly-compromised forward-test check for the futures-execution gate in
/// VolumeBar6500FuturesExecutionAnalysis. The session passed in here has, in every case available
/// to this project, already been spent -- 2026-09-24 specifically was already consumed as the OOS
/// check for an earlier candidate batch (docs/VOLUME_BAR_6500_REVALIDATION.md,
/// docs/REVERSAL_RESEARCH_2026-09-25.md). Re-using it here was an explicit, logged user decision
/// to accept a weaker leave-one-out guarantee in exchange for an immediate directional read,
/// documented in docs/VOLUME_BAR_6500_FUTURES_EXECUTION.md. It is not a substitute for
/// accumulating genuinely new, never-inspected sessions.
///
/// This intentionally does not run the weekly-option translation pipeline: the futures-execution
/// gate only needs direction-aligned forward Futures points, and BuildEpisodes +
/// HorizonSpec.ForwardPoints already compute those independently of any option chain, so a
/// forward-test day can be checked without touching option-chain data or fill assumptions at all.
/// </summary>
public static class VolumeBar6500ForwardTestRunner
{
    public static async Task<int> RunAsync(string inputRoot, DateOnly forwardTestDate, CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"6500 forward-test check -- {forwardTestDate:yyyy-MM-dd} is a previously-spent/contaminated session " +
            "(see docs/VOLUME_BAR_6500_FUTURES_EXECUTION.md); re-use explicitly authorized by the user, NOT a clean OOS day.");

        var primaryEntries = new List<VolumeBar6500EpisodeStateAnalysis.EntryObservation>();
        foreach (var date in VolumeBar6500Revalidation.PrimaryResearchDates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (_, observations, _) = await VolumeBar6500RevalidationRunner.LoadSessionAsync(inputRoot, date);
            primaryEntries.AddRange(BuildFuturesOnlyEntries(observations));
        }

        var (forwardAudit, forwardObservations, _) = await VolumeBar6500RevalidationRunner.LoadSessionAsync(inputRoot, forwardTestDate);
        var forwardEntries = BuildFuturesOnlyEntries(forwardObservations);

        var primaryResult = VolumeBar6500FuturesExecutionAnalysis.Analyze(primaryEntries);
        var forwardResult = VolumeBar6500FuturesExecutionAnalysis.Analyze(forwardEntries);

        Console.WriteLine(
            $"Forward-test session {forwardTestDate:yyyy-MM-dd}: feedUpdates={forwardAudit.TotalFeedUpdates:N0}, " +
            $"fullBars={forwardAudit.FullBarCount:N0}, legacyParityMismatch={forwardAudit.LegacyParityMismatchCount}.");

        var cost = VolumeBar6500FuturesExecutionAnalysis.GatePrimaryCostPoints;
        Console.WriteLine(
            $"Comparing the forward-test day's single-session mean net points (@{cost:0.0}pt round-trip cost) " +
            "against the primary-9 per-session distribution at the same cost:");

        foreach (var mechanism in VolumeBar6500EpisodeStateAnalysis.Mechanisms)
        {
            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                var primarySessions = primaryResult.Sessions
                    .Where(x => x.Mechanism == mechanism && x.HorizonBars == horizon.Bars && x.CostPoints == cost)
                    .Select(x => x.MeanNetPoints)
                    .OrderBy(x => x)
                    .ToArray();

                var forwardSession = forwardResult.Sessions
                    .FirstOrDefault(x => x.Mechanism == mechanism && x.HorizonBars == horizon.Bars && x.CostPoints == cost);

                if (primarySessions.Length == 0 || forwardSession is null)
                {
                    Console.WriteLine($"  {mechanism,-23} +{horizon.Bars}: no episodes on one side, skipped.");
                    continue;
                }

                var min = primarySessions.Min();
                var max = primarySessions.Max();
                var withinRange = forwardSession.MeanNetPoints >= min && forwardSession.MeanNetPoints <= max;

                Console.WriteLine(
                    $"  {mechanism,-23} +{horizon.Bars}: primaryRange=[{min:0.00}, {max:0.00}] (median={Median(primarySessions):0.00}), " +
                    $"forward={forwardSession.MeanNetPoints:0.00} (N={forwardSession.N}), " +
                    $"{(forwardSession.MeanNetPoints > 0 ? "net+" : "net-")}, withinPrimaryRange={withinRange}");
            }
        }

        return 0;
    }

    static double Median(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    static List<VolumeBar6500EpisodeStateAnalysis.EntryObservation> BuildFuturesOnlyEntries(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        var (episodes, _) = VolumeBar6500EpisodeStateAnalysis.BuildEpisodes(observations);
        var observationByDateBar = observations.ToDictionary(x => (x.TradingDate, x.BarIndex));
        var entries = new List<VolumeBar6500EpisodeStateAnalysis.EntryObservation>();

        foreach (var episode in episodes)
        {
            if (!observationByDateBar.TryGetValue((episode.TradingDate, episode.StartBarIndex), out var source))
            {
                continue;
            }

            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                var forward = horizon.ForwardPoints(source);
                var aligned = forward is { } f ? episode.ExpectedDirectionSign * f : (double?)null;
                bool? correct = aligned is null || aligned == 0 ? null : aligned > 0;

                entries.Add(new VolumeBar6500EpisodeStateAnalysis.EntryObservation(
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
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "NoTranslationSignal"));
            }
        }

        return entries;
    }
}
