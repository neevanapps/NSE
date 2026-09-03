using NiftySignal.Domain.Enums;
using NiftySignal.Rules;

namespace NiftySignal.Execution;

/// <summary>
/// v1 strike selection logic (plan section 8): direction picks the side (CE for bullish,
/// PE for bearish), then premium band / spread / OI / IV filters, then rank survivors.
///
/// The plan asks for ranking by "a composite of liquidity (OI + volume) and delta, prefer
/// delta in a sensible directional range" without giving an exact formula or delta band --
/// implemented here as: partition into a preferred delta band first, then rank by
/// liquidity (OI, then volume) within each partition. That keeps liquidity as the
/// tie-breaker within an already-reasonable delta range, rather than letting a single
/// blended score obscure which factor actually decided the pick -- easier to reason about
/// and to retune once real data shows whether this band is right (plan section 15).
/// </summary>
public sealed class StrikeSelector : IStrikeSelector
{
    public const double PreferredMinAbsDelta = 0.30;
    public const double PreferredMaxAbsDelta = 0.60;

    public StrikeSelectionResult SelectBestCandidate(IReadOnlyList<StrikeCandidate> candidates, EntryDirection direction, RulesetConfig config)
    {
        var expectedOptionType = direction switch
        {
            EntryDirection.Bullish => OptionType.Call,
            EntryDirection.Bearish => OptionType.Put,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Must be Bullish or Bearish to select a strike."),
        };

        var diagnostics = new List<string>();
        var survivors = new List<StrikeCandidate>();

        foreach (var candidate in candidates.Where(c => c.OptionType == expectedOptionType))
        {
            if (candidate.Mid < config.StrikeSelection.MinPremium || candidate.Mid > config.StrikeSelection.MaxPremium)
            {
                diagnostics.Add($"{candidate.TradingSymbol}: premium {candidate.Mid} outside [{config.StrikeSelection.MinPremium}, {config.StrikeSelection.MaxPremium}]");
                continue;
            }

            if (candidate.BidPrice is not > 0 || candidate.AskPrice is not > 0)
            {
                diagnostics.Add($"{candidate.TradingSymbol}: no valid bid/ask quote");
                continue;
            }

            var spreadPctOfMid = candidate.Mid == 0
                ? double.MaxValue
                : (double)((candidate.AskPrice.Value - candidate.BidPrice.Value) / candidate.Mid) * 100.0;
            if (spreadPctOfMid > config.StrikeSelection.MaxSpreadPctOfMid)
            {
                diagnostics.Add($"{candidate.TradingSymbol}: spread {spreadPctOfMid:F2}% exceeds MaxSpreadPctOfMid ({config.StrikeSelection.MaxSpreadPctOfMid}%)");
                continue;
            }

            if (candidate.OpenInterest < config.StrikeSelection.MinOpenInterest)
            {
                diagnostics.Add($"{candidate.TradingSymbol}: OI {candidate.OpenInterest} below MinOpenInterest ({config.StrikeSelection.MinOpenInterest})");
                continue;
            }

            if (candidate.ImpliedVolatility is null)
            {
                diagnostics.Add($"{candidate.TradingSymbol}: IV unavailable");
                continue;
            }

            survivors.Add(candidate);
        }

        if (survivors.Count == 0)
        {
            diagnostics.Add("No candidate passed all filters");
            return new StrikeSelectionResult(null, diagnostics);
        }

        var best = survivors
            .OrderByDescending(IsPreferredDelta)
            .ThenByDescending(c => c.OpenInterest)
            .ThenByDescending(c => c.Volume)
            .First();

        return new StrikeSelectionResult(best, diagnostics);
    }

    static bool IsPreferredDelta(StrikeCandidate candidate) =>
        candidate.Delta is { } delta && Math.Abs(delta) is >= PreferredMinAbsDelta and <= PreferredMaxAbsDelta;
}
