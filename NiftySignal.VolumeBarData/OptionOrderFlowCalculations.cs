using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.VolumeBarData;

/// <summary>How one trade's aggressor side was classified against the pre-trade book. See spec Section 5.</summary>
public enum TradeAggressor { AggressiveBuy, AggressiveSell, Unknown }

/// <summary>
/// One detected trade event (Section 3/4): a strictly positive incremental-volume step between two
/// consecutive ticks of the same token, priced and classified against the PRE-TRADE book (the
/// previous tick's own depth -- never the current tick's, which may already reflect the trade).
/// </summary>
public sealed record OptionTradeEvent(
    long TickId, DateTimeOffset ExchangeTimestamp, decimal TradePrice, long TradeQuantity,
    decimal? PreviousBestBid, decimal? PreviousBestAsk, TradeAggressor Aggressor);

/// <summary>
/// Per-contract, per-window aggregate of aggressive-trade flow (Section 6). Positive TradeOFI =
/// buy-heavy aggressive flow; negative = sell-heavy. TradeOFI excludes Unknown volume from its
/// denominator by construction (ratio of Buy/Sell only, out of ClassifiedVolume).
/// </summary>
public sealed record ContractTradeFlow(
    long AggressiveBuyVolume, long AggressiveSellVolume, long UnknownVolume,
    long ClassifiedVolume, long TotalTradedVolume, double? TradeOFI, double ClassificationCoverage);

/// <summary>Top-5 depth imbalance for one snapshot (Section 7). Kept entirely separate from trade flow -- never combined.</summary>
public sealed record DepthSnapshotImbalance(long TotalBidDepth, long TotalAskDepth, double? DepthImbalance);

/// <summary>
/// Options order-flow calculation primitives for the (not-yet-built) Options Order Flow research
/// track. Pure functions only -- no Pattern A/B integration, no trading rule, no thresholds; this
/// is the reusable arithmetic the eventual experiment module will call, built and unit-tested
/// ahead of that module per the user's explicit "implement the pipeline first, do not run the
/// experiment yet" instruction.
/// </summary>
public static class OptionOrderFlowCalculations
{
    /// <summary>
    /// Section 5's aggressor rule, exposed standalone so both <see cref="DetectTradeEvents"/> and
    /// any independent validation script apply the exact same, single, deterministic rule:
    /// TradePrice &gt;= PreviousBestAsk -&gt; AggressiveBuy; TradePrice &lt;= PreviousBestBid -&gt;
    /// AggressiveSell; strictly between -&gt; Unknown; missing/locked/crossed book -&gt; Unknown.
    /// Never forces an inside-spread trade to Buy/Sell and never falls back to a tick-rule.
    /// </summary>
    public static TradeAggressor ClassifyAggressor(decimal tradePrice, decimal? previousBestBid, decimal? previousBestAsk)
    {
        if (previousBestBid is not decimal bid || previousBestAsk is not decimal ask) { return TradeAggressor.Unknown; }
        if (bid <= 0 || ask <= 0 || bid >= ask) { return TradeAggressor.Unknown; } // locked/crossed/invalid book.
        if (tradePrice >= ask) { return TradeAggressor.AggressiveBuy; }
        if (tradePrice <= bid) { return TradeAggressor.AggressiveSell; }
        return TradeAggressor.Unknown; // strictly inside the spread.
    }

    /// <summary>
    /// Section 3/4: walks ticks in the file's own deterministic order, deriving
    /// IncrementalTradedVolume = CurrentCumulativeVolume - PreviousCumulativeVolume per step. A
    /// trade event exists only when that delta is strictly positive; delta == 0 is a quote-only
    /// update (not counted as flow); delta &lt; 0 is bad data / a session reset and is reported via
    /// <paramref name="negativeDeltaCount"/>, never treated as a trade. The pre-trade book is
    /// always the PREVIOUS tick's own Depth (never the current tick's, which may already reflect
    /// the trade that just happened) -- no look-ahead.
    /// </summary>
    public static List<OptionTradeEvent> DetectTradeEvents(IReadOnlyList<OptionTickV2> ticks, out int negativeDeltaCount)
    {
        var events = new List<OptionTradeEvent>();
        negativeDeltaCount = 0;
        for (var i = 1; i < ticks.Count; i++)
        {
            var prev = ticks[i - 1];
            var cur = ticks[i];
            var delta = cur.Volume - prev.Volume;
            if (delta < 0) { negativeDeltaCount++; continue; }
            if (delta == 0) { continue; } // quote-only update.

            var prevBid = prev.Depth?.Bid1Price;
            var prevAsk = prev.Depth?.Ask1Price;
            // A zero-valued price/qty pair from a missing Depth is not a real quote -- treat as absent.
            if (prev.Depth is null || (prevBid == 0 && prev.Depth.Bid1Qty == 0)) { prevBid = null; }
            if (prev.Depth is null || (prevAsk == 0 && prev.Depth.Ask1Qty == 0)) { prevAsk = null; }

            var aggressor = ClassifyAggressor(cur.LastPrice, prevBid, prevAsk);
            events.Add(new OptionTradeEvent(cur.Id, cur.ExchangeTimestamp, cur.LastPrice, delta, prevBid, prevAsk, aggressor));
        }
        return events;
    }

    /// <summary>Section 6: aggregates trade events (already time-windowed by the caller) into one contract's flow for that window.</summary>
    public static ContractTradeFlow AggregateContractFlow(IReadOnlyList<OptionTradeEvent> events)
    {
        long buy = 0, sell = 0, unknown = 0;
        foreach (var e in events)
        {
            switch (e.Aggressor)
            {
                case TradeAggressor.AggressiveBuy: buy += e.TradeQuantity; break;
                case TradeAggressor.AggressiveSell: sell += e.TradeQuantity; break;
                default: unknown += e.TradeQuantity; break;
            }
        }
        var classified = buy + sell;
        var total = classified + unknown;
        double? ofi = classified > 0 ? (double)(buy - sell) / classified : null;
        var coverage = total > 0 ? (double)classified / total : 0.0;
        return new ContractTradeFlow(buy, sell, unknown, classified, total, ofi, coverage);
    }

    /// <summary>Section 7: top-5 depth imbalance for a single snapshot. Never mixed with trade flow.</summary>
    public static DepthSnapshotImbalance ComputeDepthImbalance(MarketDepth depth)
    {
        var bidTotal = depth.TotalBidQty;
        var askTotal = depth.TotalAskQty;
        var sum = bidTotal + askTotal;
        double? imbalance = sum > 0 ? (double)(bidTotal - askTotal) / sum : null;
        return new DepthSnapshotImbalance(bidTotal, askTotal, imbalance);
    }

    /// <summary>
    /// Section 9's depth aggregation, time-weighted-average variant: each valid snapshot's
    /// DepthImbalance is weighted by how long it was the prevailing book state within
    /// [windowStart, windowEnd]. Snapshots outside the window are ignored; the first snapshot's
    /// weight starts at windowStart (not its own timestamp), and the last runs to windowEnd.
    /// </summary>
    public static double? TimeWeightedAverageDepthImbalance(
        IReadOnlyList<(DateTimeOffset Timestamp, MarketDepth? Depth)> snapshots,
        DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var inWindow = snapshots.Where(s => s.Timestamp >= windowStart && s.Timestamp <= windowEnd && s.Depth is not null)
            .OrderBy(s => s.Timestamp).ToList();
        if (inWindow.Count == 0) { return null; }

        double weightedSum = 0;
        double totalWeight = 0;
        for (var i = 0; i < inWindow.Count; i++)
        {
            var (ts, depth) = inWindow[i];
            var segmentStart = i == 0 ? windowStart : ts;
            var segmentEnd = i == inWindow.Count - 1 ? windowEnd : inWindow[i + 1].Timestamp;
            var weight = (segmentEnd - segmentStart).TotalSeconds;
            if (weight <= 0) { continue; }
            var imbalance = ComputeDepthImbalance(depth!).DepthImbalance;
            if (imbalance is null) { continue; }
            weightedSum += imbalance.Value * weight;
            totalWeight += weight;
        }
        return totalWeight > 0 ? weightedSum / totalWeight : null;
    }

    /// <summary>Section 9's depth aggregation fallback: plain median of valid per-snapshot DepthImbalance values.</summary>
    public static double? MedianDepthImbalance(IReadOnlyList<MarketDepth> validSnapshots)
    {
        var values = validSnapshots.Select(d => ComputeDepthImbalance(d).DepthImbalance)
            .Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
        if (values.Count == 0) { return null; }
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }

    /// <summary>Section 10: five-strike band aggregation from per-contract TradeOFI values (median; skips nulls, never substitutes a missing strike with a default).</summary>
    public static double? MedianOfBand(IReadOnlyList<double?> perStrikeValues)
    {
        var values = perStrikeValues.Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
        if (values.Count == 0) { return null; }
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }

    /// <summary>
    /// Section 28 validation utility: independently re-derives the aggressor classification for a
    /// batch of already-computed trade events straight from the rule in
    /// <see cref="ClassifyAggressor"/>, and reports any mismatch. Used both by unit tests and by
    /// the eventual experiment's own "recompute at least 20 trade classifications per session"
    /// validation step -- one shared implementation, so "independent" recompute still means
    /// re-running the same predeclared rule against the recorded inputs, not a second guess.
    /// </summary>
    public static int CountAggressorMismatches(IReadOnlyList<OptionTradeEvent> events)
    {
        var mismatches = 0;
        foreach (var e in events)
        {
            var recomputed = ClassifyAggressor(e.TradePrice, e.PreviousBestBid, e.PreviousBestAsk);
            if (recomputed != e.Aggressor) { mismatches++; }
        }
        return mismatches;
    }
}
