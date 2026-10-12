using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Quotes;

namespace NiftySignal.Trading.Core.Entry;

/// <summary>
/// Extra entry rule of a variant, separate from base-strategy validity (contract section A.4). Concrete specs are part of the
/// hashed configuration; <see cref="Kind"/> discriminates them so different rules never hash equal.
/// </summary>
public abstract record EntryConditionSpec
{
    public abstract string Kind { get; }
}

/// <summary>Enter as soon as the opportunity has a selected contract and every recheck passes (variant A).</summary>
public sealed record ImmediateEntrySpec : EntryConditionSpec
{
    public override string Kind => "Immediate";
}

/// <summary>
/// Variant A2: the selected contract's current executable ask must be strictly below the arithmetic mean of the last
/// <see cref="N"/> consecutive valid completed candles of that same contract, on the configured <see cref="PriceSource"/>.
/// Both fields are required: there is no research default for N or the price source.
/// </summary>
public sealed record AskBelowCandleMaSpec : EntryConditionSpec
{
    public required int N { get; init; }

    public required CandlePriceSource PriceSource { get; init; }

    public override string Kind => "AskBelowCandleMA";
}

/// <summary>How a bar-clocked variant is closed at its terminal time.</summary>
public enum NextBarTerminalMode
{
    /// <summary>PROSPECTIVE live protocol: at the terminal instant (a wall-clock/engine deadline) the position is closed at the held token's actually observed bid, never backdated; with no usable bid it stays visibly pending and may be closed later as a DelayedTerminalClose.</summary>
    LiveObservedBid = 0,

    /// <summary>HISTORICAL PARITY replay only: the terminal close uses the bid at the end of the last completed bar that ended at or before the terminal instant (known only retrospectively; unavailable online).</summary>
    HistoricalLastBarEnd = 1,
}

/// <summary>
/// Bar-clocked entry policy (the NPT1 execution contract): a strategy signal on completed bar t freezes ONE contract and creates a pending entry that can fill ONLY on the immediately following
/// completed bar t+1, at that bar's executable ask, if every condition still holds; otherwise it is cancelled (never postponed, never substituted). The signal bar itself never fills.
/// All numbers are the frozen research constants; they are part of the hashed configuration, and <see cref="PolicyLabel"/> names the timing policy so a prospective result can never be mistaken for a
/// different window's.
/// </summary>
public sealed record NextCompletedBarEntrySpec : EntryConditionSpec
{
    public decimal AskMin { get; init; } = 100m;

    public decimal AskMax { get; init; } = 150m;

    /// <summary>Contract ranking tie-break target: smaller absolute distance of the ask to this premium wins.</summary>
    public decimal AskTarget { get; init; } = 125m;

    /// <summary>The executable quote must be at most this old at the bar end (and not newer than the bar end).</summary>
    public TimeSpan MaxQuoteAge { get; init; } = TimeSpan.FromSeconds(2);

    public NextBarTerminalMode TerminalMode { get; init; } = NextBarTerminalMode.LiveObservedBid;

    public string PolicyLabel { get; init; } = "npt1-live1500-v1";

    public override string Kind => "NextCompletedBar";
}

public enum ConditionOutcome
{
    Satisfied = 0,
    NotSatisfied = 1,
    NotEvaluable = 2,
}

public sealed class ProhibitedConditionInputException(string message) : InvalidOperationException(message);

/// <summary>Everything an entry condition may look at: the opportunity's fixed token, the quote being evaluated and that token's own candle history.</summary>
public sealed record ConditionContext(string OpportunityToken, QuoteSnapshot Quote, CandleHistoryView History);

public sealed record ConditionResult(
    ConditionOutcome Outcome,
    string Reason,
    decimal? Ask,
    decimal? Average,
    int? N,
    CandlePriceSource? PriceSource)
{
    public static ConditionResult Satisfied(string reason, decimal? ask = null, decimal? average = null, int? n = null, CandlePriceSource? source = null)
        => new(ConditionOutcome.Satisfied, reason, ask, average, n, source);
}

public static class EntryConditions
{
    public static ConditionResult Evaluate(EntryConditionSpec spec, ConditionContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        return spec switch
        {
            ImmediateEntrySpec => ConditionResult.Satisfied("Immediate", context.Quote.Ask),
            AskBelowCandleMaSpec ma => EvaluateAskBelowMa(ma, context),
            _ => throw new NotSupportedException($"Unknown entry condition {spec.GetType().Name}."),
        };
    }

    private static ConditionResult EvaluateAskBelowMa(AskBelowCandleMaSpec spec, ConditionContext context)
    {
        // Input guard: the quote, the candle history and the opportunity must all be the SAME contract. A basket, strike splice or any
        // other series cannot be expressed as a CandleHistoryView, and a view of another token is refused here.
        if (!string.Equals(context.Quote.Token, context.OpportunityToken, StringComparison.Ordinal)
            || !string.Equals(context.History.Token, context.OpportunityToken, StringComparison.Ordinal))
        {
            throw new ProhibitedConditionInputException(
                $"AskBelowCandleMA inputs must all be contract '{context.OpportunityToken}' (quote '{context.Quote.Token}', history '{context.History.Token}').");
        }

        if (!context.Quote.IsExecutable)
        {
            return new ConditionResult(ConditionOutcome.NotEvaluable, "QuoteNotExecutable", context.Quote.Ask, null, spec.N, spec.PriceSource);
        }

        var window = context.History.ConsecutiveWindow(spec.N);
        if (window.Status != WindowStatus.Ready)
        {
            return new ConditionResult(ConditionOutcome.NotEvaluable, window.Status.ToString(), context.Quote.Ask, null, spec.N, spec.PriceSource);
        }

        var sum = 0m;
        foreach (var candle in window.Candles)
        {
            sum += candle.Price(spec.PriceSource)!.Value;
        }

        var average = sum / spec.N;
        var ask = context.Quote.Ask!.Value;
        return new ConditionResult(
            ask < average ? ConditionOutcome.Satisfied : ConditionOutcome.NotSatisfied,
            ask < average ? "AskBelowAverage" : "AskNotBelowAverage",
            ask,
            average,
            spec.N,
            spec.PriceSource);
    }
}
