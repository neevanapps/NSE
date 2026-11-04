using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Dashboard.Services;

/// <summary>One displayed mark: the published mark plus the status this Dashboard assigns now (a Fresh row from a silent publisher is shown Stale; a row of another trade date is never shown).</summary>
public sealed record V2LiveMarkRow(LiveMark Mark, MarkStatus Status, string StatusDetail, double? QuoteAgeSeconds, DateTimeOffset PublishedAt);

public enum V2PublisherState { Running, Silent, NotInstalled, NoAccess, Unreachable, Waiting }

/// <summary>
/// Live marks for the Dashboard. <see cref="Publisher"/> says whether the Host's read-only publisher is alive (its heartbeat row is recent), so "no open position" and "publisher down" are never confused.
/// Marks are an INDICATIVE liquidation estimate computed from continuously observed quotes; the ledger remains the only truth.
/// </summary>
public sealed record V2LiveMarksView(V2PublisherState Publisher, string? Message, DateOnly TradeDate, DateTimeOffset AsOf, DateTimeOffset? LastBeatAt, IReadOnlyList<V2LiveMarkRow> Rows)
{
    public static V2LiveMarksView Unavailable(V2PublisherState state, string message, DateOnly day, DateTimeOffset now) => new(state, message, day, now, null, []);

    /// <summary>The hypothetical sum of this strategy's realized net (ledger) and its open positions' estimated net liquidation, only when every open mark is Fresh.</summary>
    public static decimal? IndicativeTotal(IEnumerable<V2LiveMarkRow> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return null;
        var open = list.Where(r => r.Mark.Quantity > 0 && r.Status != MarkStatus.Closed).ToList();
        if (open.Any(r => r.Status != MarkStatus.Fresh || r.Mark.EstNetLiquidation is null)) return null;
        return list[0].Mark.RealizedNetToday + open.Sum(r => r.Mark.EstNetLiquidation!.Value);
    }
}
