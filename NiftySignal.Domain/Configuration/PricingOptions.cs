namespace NiftySignal.Domain.Configuration;

/// <summary>
/// Bound from the "Pricing" config section, independently in both NiftySignal.Host and
/// NiftySignal.Dashboard -- audit finding F21 fixed (2026-09-09): RiskFreeRate was a hardcoded
/// `const double = 0.065` duplicated in LiveFeatureEngine.cs and LiveDataService.cs, the exact
/// kind of value (plan section 4.1: "static config value... reviewed weekly") that shouldn't
/// need a code change and a redeploy of two separate services just to update. Deliberately two
/// separate config-file entries (each service's own appsettings.json), not one shared source --
/// this project has no shared config file between Host and Dashboard to bind from, and building
/// one for a single rarely-changed number isn't worth the plumbing. Kept in sync by matching
/// values with a comment in each pointing at the other, same discipline as every other
/// deliberate config duplicate in this codebase (see RulesetConfigOptions' own note on this).
/// </summary>
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>91-day T-bill proxy. Also set in the other service's own appsettings.json "Pricing" section -- keep both in sync.</summary>
    public double RiskFreeRate { get; set; } = 0.065;
}
