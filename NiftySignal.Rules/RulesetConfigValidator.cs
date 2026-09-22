namespace NiftySignal.Rules;

/// <summary>
/// The "reject a malformed config -- typo'd weight, negative number -- rather than applying
/// garbage or crashing" gate from docs/PLAN.md section 1.9, finally wired up (2026-09-09,
/// external review). Deliberately checks for structurally dangerous values (negative/zero
/// where that can't be right, percentages outside 0-100, an ordering constraint like
/// MinPremium &lt; MaxPremium) -- not a re-litigation of whether today's specific numbers are
/// good trading parameters. A config that passes this can still be a bad idea; it just isn't a
/// fat-fingered one.
/// </summary>
public static class RulesetConfigValidator
{
    public static IReadOnlyList<string> Validate(RulesetConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.RulesetVersion))
        {
            errors.Add("RulesetVersion must not be blank.");
        }

        if (config.Capital.Total <= 0)
        {
            errors.Add($"Capital.Total must be positive (was {config.Capital.Total}).");
        }

        if (config.Capital.LotSize <= 0)
        {
            errors.Add($"Capital.LotSize must be positive (was {config.Capital.LotSize}).");
        }

        if (config.Capital.MaxConcurrentPositions <= 0)
        {
            errors.Add($"Capital.MaxConcurrentPositions must be positive (was {config.Capital.MaxConcurrentPositions}).");
        }

        if (config.Capital.LotsPerTrade <= 0)
        {
            errors.Add($"Capital.LotsPerTrade must be positive (was {config.Capital.LotsPerTrade}).");
        }

        if (config.Session.NoEntryBeforeMinutes < 0)
        {
            errors.Add($"Session.NoEntryBeforeMinutes must not be negative (was {config.Session.NoEntryBeforeMinutes}).");
        }

        if (config.Session.NoEntryAfterTime <= EntryRuleEvaluator.MarketOpen.AddMinutes(config.Session.NoEntryBeforeMinutes))
        {
            errors.Add($"Session.NoEntryAfterTime ({config.Session.NoEntryAfterTime}) must be after the entry window opens.");
        }

        if (config.Session.SquareOffTime < config.Session.NoEntryAfterTime)
        {
            errors.Add($"Session.SquareOffTime ({config.Session.SquareOffTime}) must not be before NoEntryAfterTime ({config.Session.NoEntryAfterTime}).");
        }

        if (config.Session.ExpiryDayEnabled && config.Session.ExpiryDayNoEntryAfterTime > config.Session.NoEntryAfterTime)
        {
            errors.Add("Session.ExpiryDayNoEntryAfterTime should not be later than the normal NoEntryAfterTime -- expiry day is meant to be more conservative, not less.");
        }

        if (config.Entry.MinAbsScore is < 0 or > 100)
        {
            errors.Add($"Entry.MinAbsScore must be within [0,100] (was {config.Entry.MinAbsScore}).");
        }

        if (config.Entry.MinScoreSustainedSeconds < 0)
        {
            errors.Add($"Entry.MinScoreSustainedSeconds must not be negative (was {config.Entry.MinScoreSustainedSeconds}).");
        }

        if (config.Entry.MinScoreSustainedCadences < 0)
        {
            errors.Add($"Entry.MinScoreSustainedCadences must not be negative (was {config.Entry.MinScoreSustainedCadences}).");
        }

        if (config.Entry.ReEntryGapSameDirectionMinutes < 0)
        {
            errors.Add($"Entry.ReEntryGapSameDirectionMinutes must not be negative (was {config.Entry.ReEntryGapSameDirectionMinutes}).");
        }

        if (config.Entry.MaxTradesPerDay <= 0)
        {
            errors.Add($"Entry.MaxTradesPerDay must be positive (was {config.Entry.MaxTradesPerDay}).");
        }

        if (config.Entry.MaxIvRankForEntry is < 0 or > 100)
        {
            errors.Add($"Entry.MaxIvRankForEntry must be within [0,100] (was {config.Entry.MaxIvRankForEntry}).");
        }

        if (config.StrikeSelection.MinPremium <= 0)
        {
            errors.Add($"StrikeSelection.MinPremium must be positive (was {config.StrikeSelection.MinPremium}).");
        }

        if (config.StrikeSelection.MaxPremium <= config.StrikeSelection.MinPremium)
        {
            errors.Add($"StrikeSelection.MaxPremium ({config.StrikeSelection.MaxPremium}) must be greater than MinPremium ({config.StrikeSelection.MinPremium}).");
        }

        if (config.StrikeSelection.MaxSpreadPctOfMid <= 0)
        {
            errors.Add($"StrikeSelection.MaxSpreadPctOfMid must be positive (was {config.StrikeSelection.MaxSpreadPctOfMid}).");
        }

        if (config.StrikeSelection.MinOpenInterest < 0)
        {
            errors.Add($"StrikeSelection.MinOpenInterest must not be negative (was {config.StrikeSelection.MinOpenInterest}).");
        }

        if (config.Exit.PartialBookAtProfitPct <= 0)
        {
            errors.Add($"Exit.PartialBookAtProfitPct must be positive (was {config.Exit.PartialBookAtProfitPct}).");
        }

        if (config.Exit.PartialBookFraction is <= 0 or > 1)
        {
            errors.Add($"Exit.PartialBookFraction must be within (0,1] (was {config.Exit.PartialBookFraction}).");
        }

        if (config.Exit.StopLossPct <= 0)
        {
            errors.Add($"Exit.StopLossPct must be positive (was {config.Exit.StopLossPct}).");
        }

        if (config.Exit.ExitOnScoreBelowAbs is < 0 or > 100)
        {
            errors.Add($"Exit.ExitOnScoreBelowAbs must be within [0,100] (was {config.Exit.ExitOnScoreBelowAbs}).");
        }

        if (config.Exit.MaxHoldMinutes <= 0)
        {
            errors.Add($"Exit.MaxHoldMinutes must be positive (was {config.Exit.MaxHoldMinutes}).");
        }

        if (config.Costs.BrokeragePerOrder < 0)
        {
            errors.Add($"Costs.BrokeragePerOrder must not be negative (was {config.Costs.BrokeragePerOrder}).");
        }

        if (config.Costs.SlippageTicks < 0)
        {
            errors.Add($"Costs.SlippageTicks must not be negative (was {config.Costs.SlippageTicks}).");
        }

        if (config.RiskLimits.MaxDailyLossPct <= 0)
        {
            errors.Add($"RiskLimits.MaxDailyLossPct must be positive (was {config.RiskLimits.MaxDailyLossPct}).");
        }

        if (config.RiskLimits.MaxDailyProfitPct <= 0)
        {
            errors.Add($"RiskLimits.MaxDailyProfitPct must be positive (was {config.RiskLimits.MaxDailyProfitPct}).");
        }

        if (config.RiskLimits.MaxConsecutiveLosses <= 0)
        {
            errors.Add($"RiskLimits.MaxConsecutiveLosses must be positive (was {config.RiskLimits.MaxConsecutiveLosses}).");
        }

        return errors;
    }
}
