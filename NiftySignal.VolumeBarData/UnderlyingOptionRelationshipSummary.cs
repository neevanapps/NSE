using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, descriptive research layer. Change/aggregation calculators over
/// <see cref="RelationshipObservation"/> rows -- computed on demand from the observation list
/// (never stored per-row for every horizon, to avoid a combinatorial explosion of CSV columns;
/// section 4/6's per-event fields are already on the row itself for the 1-event-bar horizon).
/// Every calculator here returns null/false rather than fabricating a value when a required
/// observation is unavailable, per spec section 6's explicit requirement.
/// </summary>
public static class UnderlyingOptionRelationshipSummary
{
    public sealed record SeriesChange(decimal? AbsoluteChange, decimal? PercentChange, bool StartAvailable, bool EndAvailable, bool SameContract, bool StaleInvolved);

    static SeriesChange NoChange(bool startAvailable, bool endAvailable, bool sameContract = true, bool staleInvolved = false)
        => new(null, null, startAvailable, endAvailable, sameContract, staleInvolved);

    public static SeriesChange ComputeFuturesChange(IReadOnlyList<RelationshipObservation> rows, int eventId, int horizon)
    {
        var endId = eventId + horizon;
        if (eventId < 0 || eventId >= rows.Count || endId < 0 || endId >= rows.Count)
        {
            return NoChange(eventId >= 0 && eventId < rows.Count, endId >= 0 && endId < rows.Count);
        }
        var start = rows[eventId].FuturesClose;
        var end = rows[endId].FuturesClose;
        return new SeriesChange(end - start, start != 0 ? (end - start) / start * 100m : null, true, true, true, false);
    }

    public static SeriesChange ComputeSpotChange(IReadOnlyList<RelationshipObservation> rows, int eventId, int horizon)
    {
        var endId = eventId + horizon;
        if (eventId < 0 || eventId >= rows.Count || endId < 0 || endId >= rows.Count)
        {
            return NoChange(false, false);
        }
        var startRow = rows[eventId];
        var endRow = rows[endId];
        var startAvailable = startRow.SpotAvailable && !startRow.SpotMissingData && startRow.SpotClose is not null;
        var endAvailable = endRow.SpotAvailable && !endRow.SpotMissingData && endRow.SpotClose is not null;
        if (!startAvailable || !endAvailable)
        {
            return NoChange(startAvailable, endAvailable);
        }
        var start = startRow.SpotClose!.Value;
        var end = endRow.SpotClose!.Value;
        return new SeriesChange(end - start, start != 0 ? (end - start) / start * 100m : null, true, true, true, false);
    }

    public static SeriesChange ComputeCeChange(IReadOnlyList<RelationshipObservation> rows, int eventId, int horizon) => ComputeOptionChange(rows, eventId, horizon, isCe: true);
    public static SeriesChange ComputePeChange(IReadOnlyList<RelationshipObservation> rows, int eventId, int horizon) => ComputeOptionChange(rows, eventId, horizon, isCe: false);

    static SeriesChange ComputeOptionChange(IReadOnlyList<RelationshipObservation> rows, int eventId, int horizon, bool isCe)
    {
        var endId = eventId + horizon;
        if (eventId < 0 || eventId >= rows.Count || endId < 0 || endId >= rows.Count)
        {
            return NoChange(false, false);
        }
        var startRow = rows[eventId];
        var endRow = rows[endId];

        var startMissing = isCe ? startRow.CeMissingData : startRow.PeMissingData;
        var endMissing = isCe ? endRow.CeMissingData : endRow.PeMissingData;
        var startToken = isCe ? startRow.CeToken : startRow.PeToken;
        var endToken = isCe ? endRow.CeToken : endRow.PeToken;

        // Same-contract requires the endpoints to share a token AND no transition anywhere
        // strictly inside the window (an ATM strike that rolls away and back would otherwise
        // look, wrongly, like one continuous contract).
        var sameContract = startToken == endToken;
        var staleInvolved = (isCe ? startRow.CeIsStale : startRow.PeIsStale) || (isCe ? endRow.CeIsStale : endRow.PeIsStale);
        for (var i = eventId + 1; sameContract && i <= endId; i++)
        {
            var transitioned = isCe ? rows[i].CeContractTransition : rows[i].PeContractTransition;
            if (transitioned)
            {
                sameContract = false;
            }
            if (isCe ? rows[i].CeIsStale : rows[i].PeIsStale)
            {
                staleInvolved = true;
            }
        }

        var startAvailable = !startMissing;
        var endAvailable = !endMissing;
        if (!startAvailable || !endAvailable || !sameContract)
        {
            return NoChange(startAvailable, endAvailable, sameContract, staleInvolved);
        }

        var start = (isCe ? startRow.CeAverageLtp : startRow.PeAverageLtp)!.Value;
        var end = (isCe ? endRow.CeAverageLtp : endRow.PeAverageLtp)!.Value;
        return new SeriesChange(end - start, start != 0 ? (end - start) / start * 100m : null, true, true, true, staleInvolved);
    }

    public sealed record CategoryCount(string Category, int Count);

    /// <summary>Point 9 -- deterministic relationship-category counts, exactly as stamped onto each row by <see cref="UnderlyingOptionRelationshipRecorder"/> -- no re-derivation here.</summary>
    public static List<CategoryCount> CountCategories(IReadOnlyList<RelationshipObservation> rows)
        => rows.GroupBy(r => r.RelationshipCategory).OrderByDescending(g => g.Count()).Select(g => new CategoryCount(g.Key, g.Count())).ToList();

    /// <summary>
    /// Point 11 -- the user's own two named hypothesis patterns, checked directly against the
    /// 1-event-bar directions already on each row (never a fabricated new horizon). Returns the
    /// matching observations for further reporting (counts, days, averages) by the caller -- this
    /// method only finds/classifies, it does not itself decide "reversal" or any causal label.
    /// </summary>
    public static List<RelationshipObservation> FindPattern(IReadOnlyList<RelationshipObservation> rows, RelationshipDirection underlyingDirection, RelationshipDirection ceDirection, RelationshipDirection peDirection)
        => rows.Where(r => r.FuturesDirection1 == underlyingDirection && r.CeDirection1 == ceDirection && r.PeDirection1 == peDirection).ToList();

    /// <summary>Point 12 -- pre/post crossover context. Attaches futures/spot/CE/PE movement around an EXISTING (unmodified) Experiment 1 crossover observation, joined purely by (TradingDate, EventId) -- <see cref="Vc0DteBehaviorRecorder"/> itself is never touched.</summary>
    public sealed record CrossoverContextRow(
        OptionType OptionType, string SignalDirection, int EventId, DateOnly TradingDate,
        SeriesChange PreFutures3, SeriesChange PreFutures5, SeriesChange PreFutures10,
        SeriesChange PreCe3, SeriesChange PreCe5, SeriesChange PreCe10,
        SeriesChange PrePe3, SeriesChange PrePe5, SeriesChange PrePe10,
        SeriesChange PostFutures1, SeriesChange PostFutures3, SeriesChange PostFutures5, SeriesChange PostFutures10,
        SeriesChange PostCe1, SeriesChange PostCe3, SeriesChange PostCe5, SeriesChange PostCe10,
        SeriesChange PostPe1, SeriesChange PostPe3, SeriesChange PostPe5, SeriesChange PostPe10);

    public static List<CrossoverContextRow> AttachCrossoverContext(
        IReadOnlyList<Vc0DteBehaviorRecorder.ObservationRow> crossovers, IReadOnlyDictionary<DateOnly, List<RelationshipObservation>> byDay)
    {
        var result = new List<CrossoverContextRow>();
        foreach (var c in crossovers)
        {
            if (!byDay.TryGetValue(c.TradingDate, out var rows))
            {
                continue; // this day was never loaded into the relationship dataset.
            }

            result.Add(new CrossoverContextRow(
                c.OptionType, c.SignalDirection, c.EventId, c.TradingDate,
                ComputeFuturesChange(rows, c.EventId - 3, 3), ComputeFuturesChange(rows, c.EventId - 5, 5), ComputeFuturesChange(rows, c.EventId - 10, 10),
                ComputeCeChange(rows, c.EventId - 3, 3), ComputeCeChange(rows, c.EventId - 5, 5), ComputeCeChange(rows, c.EventId - 10, 10),
                ComputePeChange(rows, c.EventId - 3, 3), ComputePeChange(rows, c.EventId - 5, 5), ComputePeChange(rows, c.EventId - 10, 10),
                ComputeFuturesChange(rows, c.EventId, 1), ComputeFuturesChange(rows, c.EventId, 3), ComputeFuturesChange(rows, c.EventId, 5), ComputeFuturesChange(rows, c.EventId, 10),
                ComputeCeChange(rows, c.EventId, 1), ComputeCeChange(rows, c.EventId, 3), ComputeCeChange(rows, c.EventId, 5), ComputeCeChange(rows, c.EventId, 10),
                ComputePeChange(rows, c.EventId, 1), ComputePeChange(rows, c.EventId, 3), ComputePeChange(rows, c.EventId, 5), ComputePeChange(rows, c.EventId, 10)));
        }
        return result;
    }

    public sealed record DayAudit(
        DateOnly TradingDate, int EventCount, int FuturesAvailable, int SpotAvailable, int CeAvailable, int PeAvailable,
        int CeContractTransitions, int PeContractTransitions);

    public static List<DayAudit> AuditByDay(IReadOnlyList<RelationshipObservation> rows)
        => rows.GroupBy(r => r.TradingDate).OrderBy(g => g.Key).Select(g => new DayAudit(
            g.Key, g.Count(),
            g.Count(), // futures is never missing by construction (every FutureEventBar row has a real Close).
            g.Count(r => r.SpotAvailable && !r.SpotMissingData),
            g.Count(r => !r.CeMissingData),
            g.Count(r => !r.PeMissingData),
            g.Count(r => r.CeContractTransition),
            g.Count(r => r.PeContractTransition)))
        .ToList();
}
