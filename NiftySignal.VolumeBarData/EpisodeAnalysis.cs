using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, episode-level/cluster-level validation of the Pattern A/B forward-futures finding
/// (see docs/VolumeCandle_0DTE_Findings.md's "Episode-Level Validation" section). HARD FREEZE:
/// pure, additive calculators only, over the EXISTING, unmodified
/// <see cref="RelationshipObservation"/> sequence -- no bar/threshold/classification/forward-
/// return formula is touched. A "market episode" is a maximal run of CONSECUTIVE events sharing
/// the SAME <see cref="RelationshipObservation.RelationshipCategory"/> -- the point of this file
/// is to stop counting N consecutive event bars of one persistent condition as N independent
/// observations.
/// </summary>
public static class EpisodeAnalysis
{
    /// <param name="BeganAfterCrossover">True if an Experiment 1 crossover (either CE or PE, either direction, unmodified <see cref="Vc0DteBehaviorRecorder"/> observation) fired at StartEventId or StartEventId-1.</param>
    /// <param name="CrossoverType">"Call Bullish"/"Call Bearish"/"Put Bullish"/"Put Bearish" if <see cref="BeganAfterCrossover"/>, else null.</param>
    /// <param name="AtmTransitionDuringEpisode">Expected to always be False -- a transition on either leg forces a DIFFERENT <see cref="RelationshipObservation.RelationshipCategory"/> ("ContractTransition"), which by construction cannot match the episode's own pattern and therefore always ends the episode first. Computed and reported explicitly to CONFIRM this invariant, not assumed.</param>
    public sealed record Episode(
        DateOnly TradingDate, int EpisodeId, string Pattern, int StartEventId, int EndEventId,
        DateTimeOffset StartTimestamp, DateTimeOffset EndTimestamp, int EventCount,
        decimal StartFuturesPrice, decimal EndFuturesPrice, decimal? StartSpotPrice, decimal? EndSpotPrice,
        decimal? StartCePrice, decimal? EndCePrice, decimal? StartPePrice, decimal? EndPePrice,
        bool BeganAfterCrossover, string? CrossoverType,
        decimal AtmStrike, string CeToken, string PeToken, bool AtmTransitionDuringEpisode)
    {
        public double DurationMs => (EndTimestamp - StartTimestamp).TotalMilliseconds;
    }

    /// <summary>
    /// Detects episodes of <paramref name="pattern"/> within one day's own chronological row
    /// sequence. Continuity rule (spec section 2): consecutive events (by EventId) sharing the
    /// SAME <see cref="RelationshipObservation.RelationshipCategory"/> extend the current episode;
    /// any category change (including to "ContractTransition"/"OptionDataIncomplete"/another
    /// state) ends it -- gaps are never bridged, and the same pattern reappearing later always
    /// starts a NEW episode, never re-opens an old one.
    /// </summary>
    public static List<Episode> DetectEpisodes(
        DateOnly date, IReadOnlyList<RelationshipObservation> dayRows, string pattern,
        IReadOnlyList<Vc0DteBehaviorRecorder.ObservationRow> crossovers)
    {
        var episodes = new List<Episode>();
        var episodeId = 0;
        var startIdx = -1;

        void CloseEpisode(int endIdx)
        {
            if (startIdx < 0)
            {
                return;
            }
            var start = dayRows[startIdx];
            var end = dayRows[endIdx];
            var atmTransitionInside = false;
            for (var i = startIdx + 1; i <= endIdx; i++)
            {
                if (dayRows[i].CeContractTransition || dayRows[i].PeContractTransition)
                {
                    atmTransitionInside = true;
                }
            }
            var crossover = crossovers.FirstOrDefault(c => c.EventId == start.EventId || c.EventId == start.EventId - 1);
            episodes.Add(new Episode(
                date, episodeId++, pattern, start.EventId, end.EventId, start.StartTimestamp, end.EndTimestamp, endIdx - startIdx + 1,
                start.FuturesClose, end.FuturesClose, start.SpotClose, end.SpotClose,
                start.CeAverageLtp, end.CeAverageLtp, start.PeAverageLtp, end.PeAverageLtp,
                crossover is not null, crossover is not null ? $"{crossover.OptionType} {crossover.SignalDirection}" : null,
                start.AtmStrike, start.CeToken, start.PeToken, atmTransitionInside));
            startIdx = -1;
        }

        for (var i = 0; i < dayRows.Count; i++)
        {
            var isPattern = dayRows[i].RelationshipCategory == pattern;
            if (isPattern && startIdx < 0)
            {
                startIdx = i; // new episode begins.
            }
            else if (!isPattern && startIdx >= 0)
            {
                CloseEpisode(i - 1); // category changed -- close at the last matching event.
            }
        }
        CloseEpisode(dayRows.Count - 1); // day ended while still inside an episode.

        return episodes;
    }

    /// <summary>Descriptive-only duration buckets (spec section 9) -- NOT chosen to favor a result, just to distinguish "appears once" from "persists."</summary>
    public static string DurationBucket(int eventCount) => eventCount switch
    {
        1 => "1 event",
        2 or 3 => "2-3 events",
        <= 10 => "4-10 events",
        _ => ">10 events",
    };
}
