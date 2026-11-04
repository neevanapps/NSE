using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Dashboard.Services;

/// <summary>Display-only (no entry filter, no V2 input): the 5- and 10-bar average option price of ONE exact contract and its current quote.</summary>
public enum AveragePriceStatus { Ok, Unavailable }

/// <summary>
/// Average of the completed-bar prices (PriceEnd = the completed bar's two-sided quote mid) of one exact contract over the latest <see cref="Window"/> completed bars ending at the target bar.
/// A window is valid only when ALL <see cref="Window"/> consecutive bars have a stored row of this exact expiry / strike / token with a finite positive PriceEnd: no bridging, no zero-fill, no other strike.
/// </summary>
public sealed record AveragePrice(int Window, double? Value, int Observations, string? WhyUnavailable)
{
    public bool IsAvailable => Value is not null;
}

public sealed record OptionQuoteView(double? Ltp, double? Bid, double? Ask, DateTimeOffset? AvailableAtUtc, double? AgeSeconds, bool Fresh);

/// <summary>One side (CE or PE) of the panel: the exact contract, its Avg5 / Avg10, the current quote and the ask-minus-average gaps.</summary>
public sealed record OptionSidePriceView(string Side, string? Symbol, string? Token, AveragePrice Avg5, AveragePrice Avg10, OptionQuoteView? Quote)
{
    public double? AskMinus5 => Gap(Avg5);
    public double? AskMinus10 => Gap(Avg10);
    public double? AskMinus5Pct => Pct(Avg5);
    public double? AskMinus10Pct => Pct(Avg10);

    double? Gap(AveragePrice a) => Quote is { Fresh: true, Ask: { } ask } && a.Value is { } v ? ask - v : null;

    double? Pct(AveragePrice a) => Quote is { Fresh: true, Ask: { } ask } && a.Value is { } v && v > 0d ? (ask - v) / v * 100d : null;
}

public sealed record OptionPriceAveragesView(int TargetBarSeq, DateTimeOffset? BoundaryUtc, OptionSidePriceView Ce, OptionSidePriceView Pe);

public static class OptionPriceAverages
{
    public const int ShortWindow = 5, LongWindow = 10;

    /// <summary>A quote older than this at the boundary is shown as stale and is not used for the ask-minus-average gap.</summary>
    public const double FreshSeconds = 10d;

    /// <summary>Average over the <paramref name="window"/> consecutive completed bars ending at <paramref name="targetBarSeq"/> using ONLY rows at or before that bar (pinned history never sees later bars).</summary>
    public static AveragePrice Average(IEnumerable<FixedStrikeRow> rows, bool call, int targetBarSeq, int window)
    {
        var bySeq = rows.Where(r => r.BarSeq <= targetBarSeq && r.BarSeq > targetBarSeq - window).GroupBy(r => r.BarSeq).ToDictionary(g => g.Key, g => g.First());
        double sum = 0d;
        var n = 0;
        for (var seq = targetBarSeq - window + 1; seq <= targetBarSeq; seq++)
        {
            if (seq < 1) { return new AveragePrice(window, null, n, $"only {targetBarSeq} completed bar(s) exist (need {window})"); }
            if (!bySeq.TryGetValue(seq, out var row)) { return new AveragePrice(window, null, n, $"bar {seq} is not displayed"); }
            var p = (call ? row.Ce : row.Pe)?.PriceEnd;
            if (p is not { } v || !double.IsFinite(v) || v <= 0d) { return new AveragePrice(window, null, n, $"bar {seq} has no valid price for this contract"); }
            sum += v;
            n++;
        }

        return new AveragePrice(window, sum / n, n, null);
    }

    public static OptionQuoteView? Quote(PinnedQuote? q, DateTimeOffset boundaryUtc)
    {
        if (q is null) { return null; }
        var age = Math.Max(0d, (boundaryUtc - q.AvailableAtUtc).TotalSeconds);
        double? ltp = q.Quote.Ltp > 0m ? (double)q.Quote.Ltp : null;
        double? bid = q.Quote.Bid is > 0m ? (double)q.Quote.Bid.Value : null;
        double? ask = q.Quote.Ask is > 0m ? (double)q.Quote.Ask.Value : null;
        var crossed = bid is { } b && ask is { } a && b > a;
        if (crossed) { bid = null; ask = null; }
        return new OptionQuoteView(ltp, bid, ask, q.AvailableAtUtc, age, age <= FreshSeconds && !crossed);
    }

    public static OptionPriceAveragesView Build(FixedStrikeHistory history, int targetBarSeq, DateTimeOffset? boundaryUtc, PinnedQuote? ce, PinnedQuote? pe)
    {
        OptionSidePriceView Side(bool call, string name, string? symbol, string? token, PinnedQuote? q) =>
            new(name, symbol, token, Average(history.Rows, call, targetBarSeq, ShortWindow), Average(history.Rows, call, targetBarSeq, LongWindow),
                boundaryUtc is { } bu ? Quote(q, bu) : null);
        return new OptionPriceAveragesView(targetBarSeq, boundaryUtc,
            Side(true, "CE", history.Contract.CeSymbol, history.Contract.CeToken, ce),
            Side(false, "PE", history.Contract.PeSymbol, history.Contract.PeToken, pe));
    }
}
