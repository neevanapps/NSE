namespace NiftySignal.Rules;

public enum ExitReason
{
    None,
    SquareOff,
    StopLoss,
    PartialBook,
    ScoreFlip,
    ScoreDecay,
    TimeStop,
}

public sealed record OpenPositionState(
    DateTimeOffset EntryTime,
    decimal EntryPremium,
    decimal CurrentPremium,
    bool HasPartiallyBooked,
    EntryDirection Direction);

public sealed record ExitDecision(bool ShouldExit, bool IsPartialExit, ExitReason Reason);

/// <summary>
/// Plan section 7.4's exit conditions, evaluated in a fixed priority order the plan itself
/// doesn't specify explicitly, so it's documented here: SquareOff (absolute, time-mandated)
/// &gt; StopLoss (the safety net -- checked before profit-taking, since a position deep enough
/// underwater to hit stop shouldn't also be evaluated for a partial-book that can't apply)
/// &gt; PartialBook &gt; ScoreFlip &gt; ScoreDecay &gt; TimeStop. "Stay in trade as long as signal is on"
/// (plan section 7.4) is ScoreFlip/ScoreDecay; StopLoss is explicitly the safety net, not
/// the primary exit.
/// </summary>
public static class ExitRuleEvaluator
{
    public static ExitDecision Evaluate(OpenPositionState position, double currentScore, DateTimeOffset now, RulesetConfig config)
    {
        var nowTime = TimeOnly.FromDateTime(now.DateTime);
        if (nowTime >= config.Session.SquareOffTime)
        {
            return new ExitDecision(true, false, ExitReason.SquareOff);
        }

        var profitPct = ComputeProfitPct(position);

        // After a partial book, TrailAfterPartialBook moves the stop to breakeven (0%
        // loss) rather than leaving it at the original, wider StopLossPct.
        var effectiveStopLossPct = position.HasPartiallyBooked && config.Exit.TrailAfterPartialBook
            ? 0m
            : (decimal)config.Exit.StopLossPct;

        if (profitPct <= -effectiveStopLossPct)
        {
            return new ExitDecision(true, false, ExitReason.StopLoss);
        }

        if (!position.HasPartiallyBooked && profitPct >= (decimal)config.Exit.PartialBookAtProfitPct)
        {
            return new ExitDecision(true, true, ExitReason.PartialBook);
        }

        if (config.Exit.ExitOnScoreFlip && HasScoreFlipped(position.Direction, currentScore))
        {
            return new ExitDecision(true, false, ExitReason.ScoreFlip);
        }

        if (Math.Abs(currentScore) < config.Exit.ExitOnScoreBelowAbs)
        {
            return new ExitDecision(true, false, ExitReason.ScoreDecay);
        }

        if (now - position.EntryTime >= TimeSpan.FromMinutes(config.Exit.MaxHoldMinutes))
        {
            return new ExitDecision(true, false, ExitReason.TimeStop);
        }

        return new ExitDecision(false, false, ExitReason.None);
    }

    static decimal ComputeProfitPct(OpenPositionState position) =>
        position.EntryPremium == 0 ? 0m : (position.CurrentPremium - position.EntryPremium) / position.EntryPremium * 100m;

    static bool HasScoreFlipped(EntryDirection direction, double currentScore) => direction switch
    {
        EntryDirection.Bullish => currentScore < 0,
        EntryDirection.Bearish => currentScore > 0,
        _ => false,
    };
}
