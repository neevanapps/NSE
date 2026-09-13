namespace NiftySignal.Rules;

/// <summary>
/// The appsettings-bindable shape of <see cref="CoreScoreHysteresisConfig"/> (Batch 5, A9) --
/// same "plain mutable DTO the binder has no trouble with, mapped once bound" pattern as
/// <see cref="RulesetConfigOptions"/>; see that class's own doc comment for why records with
/// positional constructors don't bind reliably through the standard reflection-based
/// ConfigurationBinder. Every default below matches the backtest's own locked-in value
/// (docs/replication_plan.md) or the existing Ruleset's observation-mode risk-limit magnitude
/// (MaxDailyLossPct/MaxConsecutiveLosses/MaxTradesPerDay -- still paper trading, no real capital
/// at risk, so these stay generous rather than artificially constraining what this watch week is
/// meant to observe un-gated).
/// </summary>
public sealed class CoreScoreHysteresisConfigOptions
{
    public const string SectionName = "CoreScoreHysteresis";

    public double EntryScoreThreshold { get; set; } = 30.0;
    public CoreScoreRiskLimitsConfigOptions RiskLimits { get; set; } = new();

    public CoreScoreHysteresisConfig ToConfig() => new(
        EntryScoreThreshold: EntryScoreThreshold,
        RiskLimits: new CoreScoreRiskLimitsConfig(RiskLimits.MaxDailyLossPct, RiskLimits.MaxConsecutiveLosses, RiskLimits.MaxTradesPerDay));
}

/// <summary>Shared bindable shape for both new strategies' own <see cref="CoreScoreRiskLimitsConfig"/> -- see that record's own doc comment for why it isn't the existing <see cref="RiskLimitsConfigOptions"/>.</summary>
public sealed class CoreScoreRiskLimitsConfigOptions
{
    public double MaxDailyLossPct { get; set; } = 20.0;
    public int MaxConsecutiveLosses { get; set; } = 4;
    public int MaxTradesPerDay { get; set; } = 30;
}

/// <summary>Same "structurally dangerous, not a re-litigation of good trading parameters" scope as <see cref="RulesetConfigValidator"/>.</summary>
public static class CoreScoreHysteresisConfigValidator
{
    public static IReadOnlyList<string> Validate(CoreScoreHysteresisConfig config)
    {
        var errors = new List<string>();

        if (config.EntryScoreThreshold <= 0)
        {
            errors.Add($"EntryScoreThreshold must be positive (was {config.EntryScoreThreshold}).");
        }

        errors.AddRange(CoreScoreRiskLimitsConfigValidator.Validate(config.RiskLimits, nameof(CoreScoreHysteresisConfig)));

        return errors;
    }
}

/// <summary>Shared validation for both new strategies' own <see cref="CoreScoreRiskLimitsConfig"/>.</summary>
public static class CoreScoreRiskLimitsConfigValidator
{
    public static IReadOnlyList<string> Validate(CoreScoreRiskLimitsConfig riskLimits, string owner)
    {
        var errors = new List<string>();

        if (riskLimits.MaxDailyLossPct <= 0)
        {
            errors.Add($"{owner}.RiskLimits.MaxDailyLossPct must be positive (was {riskLimits.MaxDailyLossPct}).");
        }

        if (riskLimits.MaxConsecutiveLosses <= 0)
        {
            errors.Add($"{owner}.RiskLimits.MaxConsecutiveLosses must be positive (was {riskLimits.MaxConsecutiveLosses}).");
        }

        if (riskLimits.MaxTradesPerDay <= 0)
        {
            errors.Add($"{owner}.RiskLimits.MaxTradesPerDay must be positive (was {riskLimits.MaxTradesPerDay}).");
        }

        return errors;
    }
}
