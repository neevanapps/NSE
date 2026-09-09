using NiftySignal.Rules;

namespace NiftySignal.Tests.Rules;

public class RulesetConfigValidatorTests
{
    [Fact]
    public void Validate_AcceptsTheDefaultOptions_MappedToRulesetConfig()
    {
        var config = new RulesetConfigOptions().ToRulesetConfig();

        var errors = RulesetConfigValidator.Validate(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsNonPositiveCapital()
    {
        var options = new RulesetConfigOptions();
        options.Capital.Total = 0;

        var errors = RulesetConfigValidator.Validate(options.ToRulesetConfig());

        Assert.Contains(errors, e => e.Contains("Capital.Total"));
    }

    [Fact]
    public void Validate_RejectsMaxPremiumAtOrBelowMinPremium()
    {
        var options = new RulesetConfigOptions();
        options.StrikeSelection.MinPremium = 150;
        options.StrikeSelection.MaxPremium = 150;

        var errors = RulesetConfigValidator.Validate(options.ToRulesetConfig());

        Assert.Contains(errors, e => e.Contains("MaxPremium"));
    }

    [Fact]
    public void Validate_RejectsMinAbsScoreOutOfRange()
    {
        var options = new RulesetConfigOptions();
        options.Entry.MinAbsScore = 150;

        var errors = RulesetConfigValidator.Validate(options.ToRulesetConfig());

        Assert.Contains(errors, e => e.Contains("MinAbsScore"));
    }

    [Fact]
    public void Validate_RejectsSquareOffTimeBeforeNoEntryAfterTime()
    {
        var options = new RulesetConfigOptions();
        options.Session.NoEntryAfterTime = new TimeOnly(15, 0);
        options.Session.SquareOffTime = new TimeOnly(14, 0);

        var errors = RulesetConfigValidator.Validate(options.ToRulesetConfig());

        Assert.Contains(errors, e => e.Contains("SquareOffTime"));
    }

    [Fact]
    public void Validate_RejectsPartialBookFractionAboveOne()
    {
        var options = new RulesetConfigOptions();
        options.Exit.PartialBookFraction = 1.5;

        var errors = RulesetConfigValidator.Validate(options.ToRulesetConfig());

        Assert.Contains(errors, e => e.Contains("PartialBookFraction"));
    }

    [Fact]
    public void Validate_ReportsEveryFailure_NotJustTheFirst()
    {
        var options = new RulesetConfigOptions();
        options.Capital.Total = -1;
        options.Entry.MaxTradesPerDay = 0;

        var errors = RulesetConfigValidator.Validate(options.ToRulesetConfig());

        Assert.True(errors.Count >= 2);
    }
}
