using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Quotes;

namespace NiftySignal.Trading.Core.Selection;

public enum ExpiryPolicy
{
    /// <summary>The single nearest expiry in the universe that is at least <see cref="ContractSelectionConfig.MinCalendarDaysToExpiry"/> calendar days after the trade date. No fall-through to later expiries.</summary>
    NearestExpiryOnOrAfterTradeDate = 0,
}

/// <summary>Ranking criteria applied in configured order to the eligible candidates; contract identifier (ordinal) is always the final tie-break.</summary>
public enum SelectionRanking
{
    /// <summary>Smallest |ask - band midpoint|, midpoint = (Minimum + Maximum) / 2.</summary>
    AskNearestBandMidpoint = 0,

    /// <summary>Smallest ask - bid.</summary>
    SmallestSpread = 1,

    /// <summary>Lowest ask.</summary>
    LowestAsk = 2,
}

/// <summary>
/// Entry premium band and contract-selection policy (versioned, hashed). Applies to entries only, never to exits.
/// Confirmed band: MinimumEntryPremium = 100, MaximumEntryPremium = 150, inclusive. <see cref="MaxQuoteAge"/> is a PLACEHOLDER
/// (below the 5 s adaptive freshness rule) pending Phase 3 calibration; the expiry and ranking choices are explicit and
/// configurable, with the recommended ranking as the default.
/// </summary>
public sealed record ContractSelectionConfig
{
    public decimal MinimumEntryPremium { get; init; } = 100m;

    public decimal MaximumEntryPremium { get; init; } = 150m;

    public ExpiryPolicy ExpiryPolicy { get; init; } = ExpiryPolicy.NearestExpiryOnOrAfterTradeDate;

    /// <summary>0 admits the expiry day itself. Structural default pending decision D-4.</summary>
    public int MinCalendarDaysToExpiry { get; init; }

    /// <summary>A quote older than this at selection/recheck time is not fresh. PLACEHOLDER.</summary>
    public TimeSpan MaxQuoteAge { get; init; } = TimeSpan.FromSeconds(2);

    public IReadOnlyList<SelectionRanking> Ranking { get; init; } = new[] { SelectionRanking.AskNearestBandMidpoint, SelectionRanking.SmallestSpread };

    public decimal BandMidpoint() => (MinimumEntryPremium + MaximumEntryPremium) / 2m;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MinimumEntryPremium <= 0m)
        {
            errors.Add("MinimumEntryPremium must be positive.");
        }

        if (MaximumEntryPremium < MinimumEntryPremium)
        {
            errors.Add("MaximumEntryPremium must not be below MinimumEntryPremium.");
        }

        if (MinCalendarDaysToExpiry < 0)
        {
            errors.Add("MinCalendarDaysToExpiry must not be negative.");
        }

        if (MaxQuoteAge <= TimeSpan.Zero)
        {
            errors.Add("MaxQuoteAge must be positive.");
        }

        if (!Enum.IsDefined(ExpiryPolicy))
        {
            errors.Add("ExpiryPolicy is not defined.");
        }

        if (Ranking is null || Ranking.Count == 0 || Ranking.Any(r => !Enum.IsDefined(r)) || Ranking.Distinct().Count() != Ranking.Count)
        {
            errors.Add("Ranking must be a non-empty list of distinct defined criteria.");
        }

        return errors;
    }
}

public sealed record ContractCandidate(OptionContract Contract, QuoteSnapshot? Quote);

public enum CandidateRejection
{
    None = 0,
    WrongSide = 1,
    WrongExpiry = 2,
    NoQuote = 3,
    QuoteNotCausal = 4,
    QuoteStale = 5,
    QuoteNotExecutable = 6,
    AskBelowBand = 7,
    AskAboveBand = 8,
}

public enum ContractSelectionOutcome
{
    Selected = 0,
    NoEligibleContract = 1,
}

public sealed record CandidateEvaluation(OptionContract Contract, CandidateRejection Reason, decimal? Bid, decimal? Ask, decimal? Spread, decimal? DistanceToMidpoint);

public sealed record ContractSelectionResult(
    ContractSelectionOutcome Outcome,
    OptionContract? Selected,
    QuoteSnapshot? SelectedQuote,
    DateOnly? Expiry,
    IReadOnlyList<CandidateEvaluation> Evaluations);

/// <summary>
/// Pure contract selection (contract section 6.1 and the entry premium band requirement). Inputs are the contract universe with
/// the latest quotes, the required side, the trade date and the decision instant; nothing else is read. Filters in order: side,
/// expiry policy, quote present, quote causal (not after <c>now</c>), fresh (age &lt;= MaxQuoteAge), executable (two-sided), ask
/// inside [Minimum, Maximum] inclusive. Eligible candidates are ranked by the configured criteria then by contract identifier.
/// The result and its evaluation list are independent of candidate input order.
/// </summary>
public static class ContractSelector
{
    public static ContractSelectionResult Select(
        IReadOnlyList<ContractCandidate> candidates,
        OptionSide side,
        DateOnly tradeDate,
        DateTimeOffset now,
        ContractSelectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(config);
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid selection config: " + string.Join(" ", errors), nameof(config));
        }

        var earliest = tradeDate.AddDays(config.MinCalendarDaysToExpiry);
        DateOnly? expiry = null;
        foreach (var c in candidates)
        {
            if (c.Contract.Expiry >= earliest && (expiry is null || c.Contract.Expiry < expiry))
            {
                expiry = c.Contract.Expiry;
            }
        }

        var midpoint = config.BandMidpoint();
        var evaluations = new List<CandidateEvaluation>(candidates.Count);
        var eligible = new List<(CandidateEvaluation Evaluation, QuoteSnapshot Quote)>();
        foreach (var candidate in candidates.OrderBy(c => c.Contract.Token, StringComparer.Ordinal))
        {
            var (reason, quote) = Evaluate(candidate, side, expiry, now, config);
            var executable = quote is { IsExecutable: true };
            var evaluation = new CandidateEvaluation(
                candidate.Contract,
                reason,
                quote?.Bid,
                quote?.Ask,
                executable ? quote!.Spread : null,
                executable ? Math.Abs(quote!.Ask!.Value - midpoint) : null);
            evaluations.Add(evaluation);
            if (reason == CandidateRejection.None)
            {
                eligible.Add((evaluation, quote!));
            }
        }

        if (eligible.Count == 0)
        {
            return new ContractSelectionResult(ContractSelectionOutcome.NoEligibleContract, null, null, expiry, evaluations);
        }

        IOrderedEnumerable<(CandidateEvaluation Evaluation, QuoteSnapshot Quote)>? ordered = null;
        foreach (var criterion in config.Ranking)
        {
            ordered = ordered is null
                ? eligible.OrderBy(e => Key(e, criterion))
                : ordered.ThenBy(e => Key(e, criterion));
        }

        ordered = ordered!.ThenBy(e => e.Evaluation.Contract.Token, StringComparer.Ordinal);
        var best = ordered.First();
        return new ContractSelectionResult(ContractSelectionOutcome.Selected, best.Evaluation.Contract, best.Quote, expiry, evaluations);
    }

    private static decimal Key((CandidateEvaluation Evaluation, QuoteSnapshot Quote) e, SelectionRanking criterion) => criterion switch
    {
        SelectionRanking.AskNearestBandMidpoint => e.Evaluation.DistanceToMidpoint!.Value,
        SelectionRanking.SmallestSpread => e.Evaluation.Spread!.Value,
        SelectionRanking.LowestAsk => e.Quote.Ask!.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(criterion)),
    };

    private static (CandidateRejection Reason, QuoteSnapshot? Quote) Evaluate(
        ContractCandidate candidate,
        OptionSide side,
        DateOnly? expiry,
        DateTimeOffset now,
        ContractSelectionConfig config)
    {
        var quote = candidate.Quote;
        if (candidate.Contract.Side != side)
        {
            return (CandidateRejection.WrongSide, quote);
        }

        if (expiry is null || candidate.Contract.Expiry != expiry)
        {
            return (CandidateRejection.WrongExpiry, quote);
        }

        if (quote is null)
        {
            return (CandidateRejection.NoQuote, null);
        }

        var age = quote.Age(now);
        if (age < TimeSpan.Zero)
        {
            return (CandidateRejection.QuoteNotCausal, quote);
        }

        if (age > config.MaxQuoteAge)
        {
            return (CandidateRejection.QuoteStale, quote);
        }

        if (!quote.IsExecutable)
        {
            return (CandidateRejection.QuoteNotExecutable, quote);
        }

        if (quote.Ask < config.MinimumEntryPremium)
        {
            return (CandidateRejection.AskBelowBand, quote);
        }

        if (quote.Ask > config.MaximumEntryPremium)
        {
            return (CandidateRejection.AskAboveBand, quote);
        }

        return (CandidateRejection.None, quote);
    }
}
