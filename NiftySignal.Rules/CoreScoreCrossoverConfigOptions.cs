namespace NiftySignal.Rules;

/// <summary>The appsettings-bindable shape of <see cref="CoreScoreCrossoverConfig"/> (Batch 5, A9) -- see <see cref="CoreScoreHysteresisConfigOptions"/>'s own doc comment for the pattern this mirrors.</summary>
public sealed class CoreScoreCrossoverConfigOptions
{
    public const string SectionName = "CoreScoreCrossover";

    public int FastWindowMinutes { get; set; } = 10;
    public int SlowWindowMinutes { get; set; } = 30;
    public CoreScoreRiskLimitsConfigOptions RiskLimits { get; set; } = new();
    public bool ShadowMode { get; set; } = true;

    public CoreScoreCrossoverConfig ToConfig() => new(
        FastWindowMinutes: FastWindowMinutes,
        SlowWindowMinutes: SlowWindowMinutes,
        RiskLimits: new CoreScoreRiskLimitsConfig(RiskLimits.MaxDailyLossPct, RiskLimits.MaxConsecutiveLosses, RiskLimits.MaxTradesPerDay),
        ShadowMode: ShadowMode);
}

public static class CoreScoreCrossoverConfigValidator
{
    public static IReadOnlyList<string> Validate(CoreScoreCrossoverConfig config)
    {
        var errors = new List<string>();

        if (config.FastWindowMinutes <= 0)
        {
            errors.Add($"FastWindowMinutes must be positive (was {config.FastWindowMinutes}).");
        }

        if (config.SlowWindowMinutes <= config.FastWindowMinutes)
        {
            errors.Add($"SlowWindowMinutes ({config.SlowWindowMinutes}) must be greater than FastWindowMinutes ({config.FastWindowMinutes}).");
        }

        errors.AddRange(CoreScoreRiskLimitsConfigValidator.Validate(config.RiskLimits, nameof(CoreScoreCrossoverConfig)));

        return errors;
    }
}
