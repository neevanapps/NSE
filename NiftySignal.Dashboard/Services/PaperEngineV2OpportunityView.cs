namespace NiftySignal.Dashboard.Services;

/// <summary>Per-strategy opportunity counts for one trade date. Signals = directional (CE / PE) decisions; Attempts = orders attempted; Fills = placed orders that filled; Suppressed = signals blocked by a holding, a pending order, the entry window or risk; Expired = orders accepted but never filled.</summary>
public sealed record V2OpportunityCounts(string StrategyId, int Bars, int Signals, int Attempts, int Fills, int Suppressed, int Expired);

public sealed record V2RejectionCount(string Category, string Reason, int Count);

public sealed record V2OpportunityRecent(string StrategyId, int BarSeq, string Stage, string Direction, string Category, string Reason, string Detail, string OrderResult, DateTimeOffset At);

/// <summary>The compact opportunity view. <see cref="Available"/> = false carries a plain-language message (table not installed, no permission, database unreachable); the Dashboard never crashes on it.</summary>
public sealed record V2OpportunityView(bool Available, string? Message, DateOnly TradeDate, IReadOnlyList<V2OpportunityCounts> Strategies, IReadOnlyList<V2RejectionCount> Rejections, IReadOnlyList<V2OpportunityRecent> Recent)
{
    public static V2OpportunityView Unavailable(DateOnly day, string message) => new(false, message, day, [], [], []);
}
