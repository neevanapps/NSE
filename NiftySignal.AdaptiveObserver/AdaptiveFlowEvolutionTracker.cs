namespace NiftySignal.AdaptiveObserver;

public static class AdaptiveFlowEvolutionTracker
{
    public static IReadOnlyList<AdaptiveFlowState> Build(IReadOnlyList<ExactAdaptiveBar> bars)
    {
        var complete = bars.Where(x => x.IsComplete).OrderBy(x => x.BarSeq).ToList();
        var rolling = AdaptiveRollingStateTracker.BuildAll(complete).ToDictionary(x => x.EndBarSeq);
        var result = new List<AdaptiveFlowState>(complete.Count);
        AdaptiveRollingState? previous = null;

        foreach (var bar in complete)
        {
            rolling.TryGetValue(bar.BarSeq, out var current);

            long? strictChange = null;
            long? strictAbsChange = null;
            long? enrichedChange = null;
            double? oiPctChange = null;
            string? evolution = null;

            if (current is not null && previous is not null)
            {
                strictChange = current.StrictDelta - previous.StrictDelta;
                strictAbsChange = Math.Abs(current.StrictDelta) - Math.Abs(previous.StrictDelta);
                enrichedChange = current.EnrichedDelta - previous.EnrichedDelta;

                if (current.OiChangePct.HasValue && previous.OiChangePct.HasValue)
                {
                    oiPctChange = current.OiChangePct.Value - previous.OiChangePct.Value;
                }

                evolution = DescribeEvolution(previous.StrictDelta, current.StrictDelta);
            }

            result.Add(new AdaptiveFlowState
            {
                Bar = bar,
                Rolling = current,
                RollingStrictDeltaChange = strictChange,
                RollingStrictAbsDeltaChange = strictAbsChange,
                RollingEnrichedDeltaChange = enrichedChange,
                RollingOiChangePctChange = oiPctChange,
                StrictDominanceEvolution = evolution,
                State = AdaptiveStateKind.Normal,
            });

            if (current is not null)
            {
                previous = current;
            }
        }

        return result;
    }

    public static string DescribeEvolution(long previous, long current)
    {
        if (previous == 0 && current == 0) return "Unchanged";
        if (previous == 0) return current > 0 ? "BuyerDominanceStarted" : "SellerDominanceStarted";
        if (current == 0) return "DominanceNeutralized";
        if (Math.Sign(previous) != Math.Sign(current)) return current > 0 ? "FlipToBuyer" : "FlipToSeller";

        var pa = Math.Abs(previous);
        var ca = Math.Abs(current);
        if (ca > pa) return "Strengthening";
        if (ca < pa) return "Weakening";
        return "Unchanged";
    }
}
