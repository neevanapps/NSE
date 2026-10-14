using System.Globalization;

namespace NiftySignal.Dashboard.Services;

/// <summary>One running NPT1 trade for the primary page. <see cref="LivePnlAtBid"/> is a tick-level DISPLAY value; <see cref="Mfe"/> / <see cref="Mae"/> are the durable strategy values (bar-end definition) and are never recomputed here.</summary>
public sealed record RunningTradeRow(
    string StrategyId,
    string Side,
    string Strike,
    string Symbol,
    string Token,
    DateTimeOffset EntryAt,
    decimal EntryPrice,
    decimal? Bid,
    decimal? Ask,
    decimal? Ltp,
    long Quantity,
    decimal? LivePnlAtBid,
    decimal? Mfe,
    decimal? Mae,
    TimeSpan HoldingTime,
    TimeSpan? QuoteAge,
    bool ExitPending);

public sealed record CompletedTradeRow(
    string StrategyId,
    string Side,
    string Strike,
    string Symbol,
    DateTimeOffset EntryAt,
    decimal EntryPrice,
    DateTimeOffset ExitAt,
    decimal ExitPrice,
    long Quantity,
    string ExitReason,
    decimal Gross,
    decimal Fees,
    decimal Net,
    decimal? Mfe,
    decimal? Mae);

/// <summary>One compact row per strategy. The three are NEVER combined into a portfolio figure.</summary>
public sealed record StrategyTodayRow(string StrategyId, int Trades, int Wins, int Losses, decimal ClosedNet, string State);

public sealed record PaperTodayHeader(
    DateOnly TradeDate,
    string M1Status,
    long? Bar,
    decimal? Capital,
    decimal OpenExposure,
    string Integrity);

public sealed record PaperTodayView(
    PaperTodayHeader Header,
    IReadOnlyList<StrategyTodayRow> Strategies,
    IReadOnlyList<PaperOpenPosition> Open,
    IReadOnlyList<CompletedTradeRow> Completed);

/// <summary>
/// Pure projections of the simplified NPT1 paper page: today's durable ledger rows (authoritative: which positions are open, entry, quantity, durable MAE / MFE) joined, at render time, with the
/// in-memory pushed quotes of the exact held tokens. No I/O, no clock of its own (the caller passes <c>now</c>).
/// </summary>
public static class PaperTodayProjection
{
    public static readonly string[] Strategies = { "NPT1-MD5", "NPT1-MDX3", "NPT1-PE5" };

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public static PaperTodayView Build(PaperLiveView view, Npt1View? npt1, DateOnly todayIst, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(view);
        var isToday = view.Status.TradeDate == todayIst;
        var open = isToday ? view.Open.Where(p => IsNpt1(p.StrategyId)).ToList() : new List<PaperOpenPosition>();
        var closed = isToday ? view.Closed.Where(c => IsNpt1(c.StrategyId) && DateOnly.FromDateTime(c.ExitAt.ToOffset(Ist).DateTime) == todayIst).ToList() : new List<PaperClosedTrade>();
        var completed = closed.OrderByDescending(c => c.ExitAt).Select(ToCompleted).ToList();
        var rows = Strategies.Select(id =>
        {
            var mine = completed.Where(c => c.StrategyId == id).ToList();
            var panel = npt1?.Strategies.FirstOrDefault(s => s.StrategyId == id);
            return new StrategyTodayRow(id, mine.Count, mine.Count(c => c.Net > 0m), mine.Count(c => c.Net < 0m), mine.Sum(c => c.Net), StateText(panel, open.Any(p => p.StrategyId == id)));
        }).ToList();
        var header = new PaperTodayHeader(view.Status.TradeDate, M1Status(npt1?.Stream, now), view.Status.LastBarSeq, view.Status.GlobalCapital, view.Risk.OpenExposure, view.Risk.IntegrityState);
        return new PaperTodayView(header, rows, open, completed);
    }

    /// <summary>
    /// Running trades with the pushed quote of EXACTLY the held token. Live P&amp;L = (bid - entry price) x remaining quantity: the executable BID of a long option, never the LTP. No bid => no P&amp;L
    /// (null, never invented). The durable MFE / MAE (rupees per unit, bar-end definition) are only scaled by the quantity for display.
    /// </summary>
    public static IReadOnlyList<RunningTradeRow> Running(IEnumerable<PaperOpenPosition> open, IReadOnlyDictionary<string, LiveOptionQuote> quotes, DateTimeOffset now)
    {
        var list = new List<RunningTradeRow>();
        foreach (var p in open.OrderBy(p => p.EntryAt))
        {
            var entry = Dec(p.AvgEntryPrice) ?? 0m;
            quotes.TryGetValue(p.Token, out var q);
            var bid = q?.Bid;
            decimal? pnl = bid is { } b ? (b - entry) * p.RemainingQuantity : null;
            TimeSpan? age = q?.BookAt is { } at ? Max(now - at) : null;
            list.Add(new RunningTradeRow(
                p.StrategyId, p.OptionType, string.IsNullOrEmpty(p.Strike) ? "—" : p.Strike, p.Symbol, p.Token, p.EntryAt, entry, bid, q?.Ask, q?.Ltp, p.RemainingQuantity, pnl,
                Dec(p.Mfe) is { } mfe ? mfe * p.RemainingQuantity : null, Dec(p.Mae) is { } mae ? mae * p.RemainingQuantity : null,
                Max(now - p.EntryAt), age, p.ExitPending));
        }

        return list;
    }

    private static CompletedTradeRow ToCompleted(PaperClosedTrade c) => new(
        c.StrategyId, c.OptionType, string.IsNullOrEmpty(c.Strike) ? "—" : c.Strike, c.Symbol, c.EntryAt, Dec(c.AvgEntryPrice) ?? 0m, c.ExitAt, Dec(c.AvgExitPrice) ?? 0m, c.Quantity, c.ExitReason,
        Dec(c.Gross) ?? 0m, Dec(c.Costs) ?? 0m, Dec(c.Net) ?? 0m, Dec(c.Mfe) is { } m1 ? m1 * c.Quantity : null, Dec(c.Mae) is { } m2 ? m2 * c.Quantity : null);

    private static bool IsNpt1(string strategyId) => strategyId.StartsWith(PaperLiveDataService.Npt1Prefix, StringComparison.Ordinal);

    private static decimal? Dec(string? text) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static TimeSpan Max(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    private static string StateText(Npt1StrategyPanel? panel, bool holding)
    {
        var state = panel?.State ?? string.Empty;
        if (holding || state.StartsWith("HOLDING", StringComparison.Ordinal))
        {
            return state.Contains("EXIT PENDING", StringComparison.Ordinal) ? "HOLDING (exit pending)" : "HOLDING";
        }

        return state.StartsWith("ENTRY PENDING", StringComparison.Ordinal) ? "PENDING" : "FLAT";
    }

    private static string M1Status(Npt1StreamHealth? stream, DateTimeOffset now)
    {
        if (stream is null)
        {
            return "UNKNOWN";
        }

        if (string.Equals(stream.Status, "Closed", StringComparison.OrdinalIgnoreCase))
        {
            return "CLOSED";
        }

        return now - stream.LastHeartbeat <= TimeSpan.FromSeconds(30) ? "LIVE" : "STALE";
    }
}
