using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// One enabled strategy's current position. <see cref="Unrealized"/> exists only for a Fresh mark of exactly the remaining quantity; <see cref="Total"/> = realized partial + unrealized and exists only then.
/// A position that is not Closed contributes nothing to the closed summary, so a partial profit is counted in exactly one of the two grids.
/// </summary>
public sealed record CurrentPositionRow(
    string StrategyId, string Status, bool Critical, PositionView? Position, PendingEntryView? Pending, decimal? Bid, double? QuoteAgeSeconds, MarkStatus? Quote, string QuoteDetail,
    decimal? RealizedPartial, decimal? Unrealized, decimal? Total);

/// <summary>One enabled strategy's fully closed positions of the trade date. Net includes the earlier partial of each closed position; charges = gross - net.</summary>
public sealed record ClosedSummaryRow(string StrategyId, int Closed, int Wins, int Losses, decimal Gross, decimal Charges, decimal Net, string? LastExit);

/// <summary><see cref="Notice"/> is set whenever the figures are not a current report of the execution writer.</summary>
public sealed record PaperPositionsView(
    DateTimeOffset AsOf, string? Notice, bool Stale, string? Status, DateOnly? TradeDate, DateTimeOffset? Heartbeat, string? EntryBlock, string? Flatten, IReadOnlyList<string> Conditions,
    IReadOnlyList<CurrentPositionRow> Current, IReadOnlyList<ClosedSummaryRow> Closed)
{
    public static PaperPositionsView Unavailable(DateTimeOffset now, string notice) => new(now, notice, true, null, null, null, null, null, [], [], []);
}

/// <summary>Pure projection of the execution writer's heartbeat snapshot plus the live marks into the two Home grids.</summary>
public static class PaperPositionsProjection
{
    public static PaperPositionsView Build(DateTimeOffset now, ExecutionStateView? state, V2LiveMarksView marks, TimeSpan staleAfter)
    {
        if (state is null) return PaperPositionsView.Unavailable(now, "the paper execution service has never reported: no position data");
        if (ExecutionStore.ReadSnapshot(state.SnapshotJson) is not { } snapshot)
            return PaperPositionsView.Unavailable(now, "the paper execution service has taken ownership but has not reported its state yet");

        var age = now - state.Heartbeat;
        var silent = age > staleAfter;
        var otherDay = snapshot.TradeDate != PaperEngineV2DataService.IstDate(now);
        var rebuilding = snapshot.Strategies.Count == 0;
        var notice = silent ? $"EXECUTION SERVICE SILENT: last report {age.TotalSeconds:0} s ago. Every figure below is the LAST REPORTED state, not current"
            : rebuilding ? "the execution service is recovering: positions are not known yet"
            : otherDay ? $"the last report is for {snapshot.TradeDate:yyyy-MM-dd}, not today" : null;
        var stale = silent || otherDay || rebuilding;

        var current = new List<CurrentPositionRow>();
        var closed = new List<ClosedSummaryRow>();
        foreach (var s in snapshot.Strategies.OrderBy(s => s.StrategyId, StringComparer.Ordinal))
        {
            var mine = snapshot.Positions.Where(p => p.StrategyId == s.StrategyId).ToList();
            var open = mine.Where(p => p.State != PositionState.Closed).OrderBy(p => p.EntryAt).LastOrDefault();
            current.Add(Current(now, s, open, open is null ? null : marks.Rows.FirstOrDefault(r => r.Mark.StrategyId == s.StrategyId && r.Mark.PositionId == open.PositionId), stale));

            if (otherDay) continue;             // an earlier day's trades are not today's summary
            var done = mine.Where(p => p.State == PositionState.Closed).OrderBy(p => p.EntryAt).ToList();
            var gross = done.Sum(p => p.GrossPnl ?? 0m);
            var net = done.Sum(p => p.NetPnl ?? 0m);
            closed.Add(new ClosedSummaryRow(s.StrategyId, done.Count, done.Count(p => p.NetPnl > 0m), done.Count(p => p.NetPnl < 0m), gross, gross - net, net, done.LastOrDefault()?.ExitReason?.ToString()));
        }

        return new PaperPositionsView(now, notice, stale, snapshot.Status, snapshot.TradeDate, state.Heartbeat, snapshot.EntryBlock, snapshot.Flatten, snapshot.Conditions, current, closed);
    }

    static CurrentPositionRow Current(DateTimeOffset now, StrategyStatus s, PositionView? p, V2LiveMarkRow? row, bool stale)
    {
        if (p is null)
        {
            var idle = s.Faulted ? "Faulted" : s.RiskBreached ? "Loss limit reached" : s.Pending is { } w ? (w.OrderPlaced ? "Entry order pending" : w.ReferenceToken is null ? "Entry pending" : "Waiting for Avg5")
                : s.TradesToday >= s.MaxDailyTrades ? "Trade limit reached" : "Flat";
            if (stale) idle = $"Last reported: {idle}";
            return new CurrentPositionRow(s.StrategyId, idle, s.Faulted, null, s.Pending, null, null, null, "", null, null, null);
        }

        var status = p.State switch { PositionState.Unresolved => "UNRESOLVED (no exit fill)", PositionState.ExitPending => p.ExitReason is { } why ? $"Exit pending ({why})" : "Exit pending", _ => "Open" };
        if (s.Faulted) status += " · strategy faulted";
        if (stale) status = $"Last reported: {status}";

        MarkStatus quote = row?.Status ?? MarkStatus.Missing;
        var detail = row?.StatusDetail ?? "no live mark has been published for this position";
        var age = row?.Mark.QuoteAvailableAt is { } at ? Math.Max(0d, (now - at).TotalSeconds) : (double?)null;
        if (stale && quote == MarkStatus.Fresh) { quote = MarkStatus.Stale; detail = "the execution service is not reporting current state"; }
        // a mark of another quantity belongs to the moment before or after a partial fill: combining it with this snapshot would count that quantity twice or not at all
        if (quote == MarkStatus.Fresh && row!.Mark.Quantity != p.RemainingQuantity) { quote = MarkStatus.Stale; detail = "the mark and the position quantity are updating"; }

        var unrealized = quote == MarkStatus.Fresh ? row!.Mark.EstNetLiquidation : null;
        var bid = row?.Mark.Bid is { } b && double.IsFinite(b) ? (decimal)b : (decimal?)null;
        return new CurrentPositionRow(s.StrategyId, status, p.State == PositionState.Unresolved || s.Faulted, p, s.Pending, bid, age, quote, detail,
            p.PartialNetPnl, unrealized, unrealized is { } u ? p.PartialNetPnl + u : null);
    }
}
