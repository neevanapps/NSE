using System.Text.Json;
using System.Text.Json.Serialization;

namespace NiftySignal.PaperEngineV2.Execution;

/// <summary>
/// An immutable view of one simulated position (open, exit-pending or closed today) for the READ-ONLY live-mark publisher. It is rebuilt by the engine after every processed frame and published as a volatile
/// snapshot, so the publisher (another thread) never touches engine state. Nothing in it is ever read back by a decision, a fill or a risk check.
/// </summary>
public sealed record MarkPosition(
    string StrategyId, int Version, DateOnly TradeDate, long SessionId, string PositionId, OptionRight Right, string Token, double Strike, DateOnly Expiry, int Quantity, int Lots,
    decimal EntryPrice, decimal EntryFees, DateTimeOffset EntryAt, PositionState State, int MaxQuoteAgeMs, decimal ExitSlippage, Func<decimal, decimal> ExitFees,
    decimal RealizedNetToday, decimal? ExitPrice, decimal? ClosedGross, decimal? ClosedNet);

/// <summary>The latest observed top of book of one exact contract. <see cref="AvailableAt"/> is when the source tick became available; <see cref="SourceTickId"/> identifies that tick.</summary>
public sealed record MarkQuote(string Token, double Bid, double Ask, double Ltp, DateTimeOffset AvailableAt, long SourceTickId);

public enum MarkStatus { Fresh, Stale, Missing, Invalid, Closed }

/// <summary>
/// One indicative mark. The unrealized figures are present ONLY when the quote is Fresh; a Stale quote still shows the last bid / ask (flagged) but no P&amp;L; Missing / Invalid carry no quote-derived value at all
/// (never zero). <see cref="EstNetLiquidation"/> = (max(0, bid - exit slippage) - entry fill) x remaining quantity - entry charges already incurred - estimated exit charges: every charge is counted once.
/// It is an INDICATIVE liquidation estimate, not a fill; the ledger remains the only truth.
/// </summary>
public sealed record LiveMark(
    string StrategyId, int Version, DateOnly TradeDate, long SessionId, string PositionId, OptionRight Right, string Token, double Strike, DateOnly Expiry, int Quantity, int Lots,
    decimal EntryPrice, DateTimeOffset EntryAt, MarkStatus Status, string StatusDetail, double? Bid, double? Ask, double? Ltp, DateTimeOffset? QuoteAvailableAt, long? QuoteTickId, double? QuoteAgeSeconds,
    decimal? GrossBidMarked, decimal? EstNetLiquidation, decimal EntryFeesIncurred, decimal? EstExitFees, decimal RealizedNetToday, decimal? ClosedNet, long MarkSeq, DateTimeOffset ComputedAt);

public static class LiveMarkCalculator
{
    public static LiveMark Compute(MarkPosition p, MarkQuote? q, DateTimeOffset now, long markSeq)
    {
        LiveMark Build(MarkStatus status, string detail, double? bid = null, double? ask = null, double? ltp = null, DateTimeOffset? at = null, long? tick = null, double? age = null,
            decimal? gross = null, decimal? net = null, decimal? exitFees = null) =>
            new(p.StrategyId, p.Version, p.TradeDate, p.SessionId, p.PositionId, p.Right, p.Token, p.Strike, p.Expiry, p.Quantity, p.Lots, p.EntryPrice, p.EntryAt, status, detail, bid, ask, ltp, at, tick, age,
                gross, net, p.EntryFees, exitFees, p.RealizedNetToday, p.ClosedNet, markSeq, now);

        if (p.State == PositionState.Closed)
            return Build(MarkStatus.Closed, "closed: the realized net comes from the ledger");
        if (p.State == PositionState.Unresolved)
            return Build(MarkStatus.Invalid, "the position is UNRESOLVED in the ledger; no mark is computed");
        if (q is null)
            return Build(MarkStatus.Missing, "no quote has been observed for this exact contract since the Host started; waiting for a fresh quote");

        var age = Math.Max(0d, (now - q.AvailableAt).TotalSeconds);
        var valid = double.IsFinite(q.Bid) && double.IsFinite(q.Ask) && q.Bid > 0d && q.Ask > 0d && q.Bid <= q.Ask;
        double? ltp = double.IsFinite(q.Ltp) && q.Ltp > 0d ? q.Ltp : null;
        if (!valid)
            return Build(MarkStatus.Invalid, q.Bid <= 0d || q.Ask <= 0d ? "the book has a zero or missing side" : q.Bid > q.Ask ? "the book is crossed" : "the quote is not finite", null, null, ltp, q.AvailableAt, q.SourceTickId, age);
        if (age * 1000d > p.MaxQuoteAgeMs)
            return Build(MarkStatus.Stale, $"the last quote is {age:0.0}s old (limit {p.MaxQuoteAgeMs / 1000d:0.0}s); no P&L is shown from it", q.Bid, q.Ask, ltp, q.AvailableAt, q.SourceTickId, age);

        var bid = (decimal)q.Bid;
        var gross = (bid - p.EntryPrice) * p.Quantity;
        var exitPrice = Math.Max(0m, bid - p.ExitSlippage);
        var exitFees = p.ExitFees(exitPrice * p.Quantity);
        var net = ((exitPrice - p.EntryPrice) * p.Quantity) - p.EntryFees - exitFees;
        return Build(MarkStatus.Fresh, "fresh", q.Bid, q.Ask, ltp, q.AvailableAt, q.SourceTickId, age, gross, net, exitFees);
    }
}

/// <summary>The one JSON shape shared by the Host publisher and the Dashboard reader.</summary>
public static class LiveMarkJson
{
    public static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() }, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static string Serialize(LiveMark m) => JsonSerializer.Serialize(m, Options);

    public static LiveMark? Deserialize(string json) => JsonSerializer.Deserialize<LiveMark>(json, Options);
}
