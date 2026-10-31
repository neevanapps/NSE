using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Dashboard.Services;

// READ-ONLY view of the Paper Engine V2 SHADOW ledger for the Home page. Everything here is derived from rows the V2 engine already wrote (events, journal, configs, outbox); nothing is written, nothing is invented:
// a value the ledger does not support (a mark with no fresh quote, MAE/MFE of an old event, a strategy that never ran) is null and renders as a dash, never zero.

public sealed record V2PositionView(
    string PositionId, OptionRight Right, string Token, double? Strike, DateOnly? Expiry, int Quantity, decimal EntryPrice, DateTimeOffset EntryAt, decimal EntryFees, bool ExitPending,
    decimal? MarkBid, double? MarkAgeSeconds, decimal? UnrealizedPnl);

public sealed record V2TradeView(
    string StrategyId, string PositionId, OptionRight Right, string Token, double? Strike, int Quantity, DateTimeOffset EntryAt, decimal EntryPrice, DateTimeOffset ExitAt, decimal ExitPrice,
    ExitReason ExitReason, decimal Gross, decimal Charges, decimal Net, decimal? Mfe, decimal? Mae);

public sealed record V2RejectionView(string StrategyId, DateTimeOffset At, OptionRight? Right, RejectionReason Reason, string Detail);

public sealed record V2FailureView(string StrategyId, DateTimeOffset At, string Kind, string Detail);

public sealed record V2StrategyPerformance(string StrategyId, int TradesToday, int WinsToday, decimal GrossToday, decimal ChargesToday, decimal NetToday, int Trades30d, int Wins30d, decimal Net30d);

public sealed record V2StrategyRow(
    string StrategyId, int? Version, bool Enabled, bool HasActivity, string Signal, IReadOnlyList<V2PositionView> OpenPositions, string? PendingEntry, decimal? RealizedNetToday, decimal? UnrealizedNow,
    int TradesToday, int RejectionsToday, V2StrategyPerformance? Performance);

public sealed record V2EngineHealth(
    bool SchemaOk, IReadOnlyList<string> SchemaProblems, DateTimeOffset? LastFrameAt, int? LastFrameBar, int FramesToday, int EntryBlockedFramesToday, int GapsToday,
    int AlertsPending, int AlertsUncertain, int AlertsFailed);

public sealed record V2HomeView(
    DateTimeOffset AsOf, DateOnly TradeDate, V2EngineHealth Health, IReadOnlyList<V2StrategyRow> Strategies, IReadOnlyList<V2TradeView> Trades, IReadOnlyList<V2RejectionView> Rejections,
    IReadOnlyList<V2FailureView> Failures, decimal? HypotheticalRealizedNet, decimal? HypotheticalUnrealized, bool EventsTruncated)
{
    public static V2HomeView Unavailable(DateTimeOffset asOf, DateOnly day, params string[] problems) =>
        new(asOf, day, new V2EngineHealth(false, problems, null, null, 0, 0, 0, 0, 0, 0), [], [], [], [], null, null, false);
}

/// <summary>A configured strategy as recorded in <c>pev2_strategy_configs</c> (the canonical JSON the engine ran with).</summary>
public sealed record V2ConfiguredStrategy(string StrategyId, int Version, bool Enabled);

/// <summary>The latest journaled frame's executable bids (token -> bid, availability) - the only source of a mark; absent when no frame exists.</summary>
public sealed record V2MarkSource(DateTimeOffset FrameAt, int Bar, IReadOnlyDictionary<string, (decimal Bid, DateTimeOffset AvailableAt)> Bids);

/// <summary>Pure projection of ledger events into the Home view (unit tested without a database).</summary>
public static class V2HomeProjection
{
    public static readonly TimeSpan MaxMarkAge = TimeSpan.FromMinutes(2);
    public const int MaxTrades = 200;
    public const int MaxRejections = 100;

    public static V2HomeView Build(
        DateTimeOffset asOf, DateOnly tradeDate, V2EngineHealth health, IReadOnlyList<V2ConfiguredStrategy> configured, IReadOnlyList<PaperEvent> events, IReadOnlyList<V2StrategyPerformance> performance,
        V2MarkSource? marks, bool truncated)
    {
        var ordered = events.OrderBy(e => e.At).ThenBy(e => e.EventId, StringComparer.Ordinal).ToList();
        var ids = configured.Select(c => c.StrategyId).Concat(ordered.Select(e => e.StrategyId)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var perfById = performance.ToDictionary(p => p.StrategyId, StringComparer.Ordinal);
        var rows = new List<V2StrategyRow>();
        var trades = new List<V2TradeView>();
        var rejections = new List<V2RejectionView>();
        var failures = new List<V2FailureView>();

        foreach (var id in ids)
        {
            var cfg = configured.FirstOrDefault(c => c.StrategyId == id);
            var mine = ordered.Where(e => e.StrategyId == id).ToList();
            var placed = mine.OfType<EntryOrderPlaced>().ToDictionary(p => p.IntentId, StringComparer.Ordinal);
            var filled = mine.OfType<EntryFilled>().ToList();
            var exits = mine.OfType<ExitFilled>().ToDictionary(x => x.PositionId, StringComparer.Ordinal);
            var requested = mine.OfType<ExitRequested>().Select(x => x.PositionId).ToHashSet(StringComparer.Ordinal);
            var unresolved = mine.OfType<PositionUnresolved>().Select(x => x.PositionId).ToHashSet(StringComparer.Ordinal);
            var rejected = mine.OfType<EntryRejected>().ToList();
            var faulted = mine.OfType<StrategyFaulted>().LastOrDefault();

            var open = new List<V2PositionView>();
            var closedHere = new List<V2TradeView>();
            foreach (var f in filled)
            {
                placed.TryGetValue(f.IntentId, out var order);
                if (exits.TryGetValue(f.PositionId, out var x))
                {
                    var charges = f.Fees + x.Fees;
                    closedHere.Add(new V2TradeView(id, f.PositionId, f.Right, f.Token, order?.Strike, f.Quantity, f.At, f.FillPrice, x.At, x.FillPrice, x.Reason, x.GrossPnl, charges, x.NetPnl, x.Mfe, x.Mae));
                }
                else if (!unresolved.Contains(f.PositionId))
                {
                    decimal? bid = null; double? age = null; decimal? upnl = null;
                    if (marks is not null && marks.Bids.TryGetValue(f.Token, out var m) && asOf - m.AvailableAt <= MaxMarkAge && m.AvailableAt >= f.At)
                    {
                        bid = m.Bid; age = (asOf - m.AvailableAt).TotalSeconds; upnl = (m.Bid - f.FillPrice) * f.Quantity;
                    }

                    open.Add(new V2PositionView(f.PositionId, f.Right, f.Token, order?.Strike, order?.Expiry, f.Quantity, f.FillPrice, f.At, f.Fees, requested.Contains(f.PositionId), bid, age, upnl));
                }
            }

            string? pending = null;
            var resolvedIntents = filled.Select(f => f.IntentId).Concat(rejected.Where(r => r.IntentId is not null).Select(r => r.IntentId!)).ToHashSet(StringComparer.Ordinal);
            var openOrder = placed.Values.Where(p => !resolvedIntents.Contains(p.IntentId)).OrderBy(p => p.At).LastOrDefault();
            if (openOrder is not null) pending = $"{openOrder.Right.ToString().ToUpperInvariant()} {openOrder.Token} x{openOrder.Quantity} (ask {openOrder.SelectedAsk:0.00})";

            string signal;
            if (cfg is { Enabled: false }) signal = "DISABLED";
            else if (faulted is not null) signal = "FAULTED";
            else if (open.Count > 0) signal = open.Any(p => p.ExitPending) ? "EXIT PENDING" : $"IN POSITION ({string.Join("/", open.Select(p => p.Right.ToString().ToUpperInvariant()).Distinct())})";
            else if (pending is not null) signal = "ENTRY PENDING";
            else if (rejected.Count > 0 && rejected[^1].At >= (mine.OfType<EntryFilled>().LastOrDefault()?.At ?? DateTimeOffset.MinValue)) signal = $"LAST: REJECTED {rejected[^1].Reason}";
            else if (mine.Count > 0) signal = "FLAT";
            else signal = cfg is null ? "NO DATA" : "NO ACTIVITY TODAY";

            decimal? realized = closedHere.Count > 0 || mine.Count > 0 ? closedHere.Sum(t => t.Net) : null;
            decimal? unrealizedNow = open.Count > 0 && open.All(p => p.UnrealizedPnl is not null) ? open.Sum(p => p.UnrealizedPnl!.Value) : null;
            rows.Add(new V2StrategyRow(id, cfg?.Version, cfg?.Enabled ?? true, mine.Count > 0, signal, open, pending, realized, unrealizedNow, closedHere.Count, rejected.Count,
                perfById.TryGetValue(id, out var perf) ? perf : null));
            trades.AddRange(closedHere);
            rejections.AddRange(rejected.Select(r => new V2RejectionView(id, r.At, r.Right, r.Reason, r.Detail)));
            failures.AddRange(mine.OfType<PositionUnresolved>().Select(u => new V2FailureView(id, u.At, "UNRESOLVED EXIT", u.Detail)));
            failures.AddRange(mine.OfType<StrategyFaulted>().Select(s => new V2FailureView(id, s.At, "ENGINE FAILURE", s.Message)));
        }

        var activity = rows.Where(r => r.HasActivity).ToList();
        decimal? totalRealized = activity.Count == 0 ? null : activity.Sum(r => r.RealizedNetToday ?? 0m);
        decimal? totalUnrealized = rows.SelectMany(r => r.OpenPositions).Any() && rows.SelectMany(r => r.OpenPositions).All(p => p.UnrealizedPnl is not null) ? rows.SelectMany(r => r.OpenPositions).Sum(p => p.UnrealizedPnl!.Value) : null;
        return new V2HomeView(asOf, tradeDate, health, rows, trades.OrderByDescending(t => t.ExitAt).Take(MaxTrades).ToList(), rejections.OrderByDescending(r => r.At).Take(MaxRejections).ToList(),
            failures.OrderByDescending(f => f.At).ToList(), totalRealized, totalUnrealized, truncated);
    }
}
