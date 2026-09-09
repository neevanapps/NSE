using NiftySignal.Domain.Configuration;

namespace NiftySignal.Rules;

/// <summary>
/// The appsettings-bindable shape of <see cref="RulesetConfig"/> (2026-09-09, external review
/// -- "put ruleset + weights on IOptionsMonitor, the item skipped to go live"). <see cref="
/// RulesetConfig"/> itself stays a record -- converting it (and its nested config records) to
/// mutable classes would have broken every `with`-expression test already written against it
/// (see e.g. ExitRuleEvaluatorTests' `config with { Exit = config.Exit with { ... } }`) for no
/// real benefit. Records with positional constructors and default parameter values don't bind
/// reliably through the standard reflection-based ConfigurationBinder, especially nested several
/// levels deep -- confirmed by hands-on testing when this was first deferred, not a guess. This
/// class is the safer, standard pattern instead: a plain mutable DTO the binder has no trouble
/// with, mapped to the real immutable <see cref="RulesetConfig"/> via <see cref="ToRulesetConfig"/>
/// once bound. See <see cref="Configuration.ValidatedOptionsMonitor{TOptions,TDomain}"/> for the
/// validate-before-swap layer this feeds into, and <see cref="RulesetConfigValidator"/> for what
/// "invalid" means here.
///
/// Every default below is today's live value (<c>live-2026-09-09</c>) -- if the "Ruleset" config
/// section is ever missing entirely (a botched deploy, a blank appsettings.json), binding falls
/// back to these defaults, which must behave identically to what's actually running today, not
/// silently regress to some other historical value.
/// </summary>
public sealed class RulesetConfigOptions
{
    public const string SectionName = "Ruleset";

    public string RulesetVersion { get; set; } = "live-2026-09-09";

    public CapitalConfigOptions Capital { get; set; } = new();
    public SessionConfigOptions Session { get; set; } = new();
    public EntryConfigOptions Entry { get; set; } = new();
    public StrikeSelectionConfigOptions StrikeSelection { get; set; } = new();
    public ExitConfigOptions Exit { get; set; } = new();
    public CostsConfigOptions Costs { get; set; } = new();
    public RiskLimitsConfigOptions RiskLimits { get; set; } = new();

    /// <summary>Already a bindable mutable class -- reused directly, not re-wrapped.</summary>
    public KillSwitchOptions KillSwitch { get; set; } = new();

    public RulesetConfig ToRulesetConfig() => new(
        RulesetVersion: RulesetVersion,
        Capital: new CapitalConfig(Capital.Total, Capital.LotSize, Capital.MaxConcurrentPositions, Capital.LotsPerTrade),
        Session: new SessionConfig(Session.NoEntryBeforeMinutes, Session.NoEntryAfterTime, Session.SquareOffTime, Session.ExpiryDayEnabled, Session.ExpiryDayNoEntryAfterTime),
        Entry: new EntryConfig(Entry.MinAbsScore, Entry.MinScoreSustainedSeconds, Entry.ReEntryGapSameDirectionMinutes, Entry.MaxTradesPerDay, Entry.MaxIvRankForEntry, Entry.MinScoreSustainedCadences),
        StrikeSelection: new StrikeSelectionConfig(StrikeSelection.MinPremium, StrikeSelection.MaxPremium, StrikeSelection.MaxSpreadPctOfMid, StrikeSelection.MinOpenInterest),
        Exit: new ExitConfig(Exit.PartialBookAtProfitPct, Exit.PartialBookFraction, Exit.StopLossPct, Exit.TrailAfterPartialBook, Exit.ExitOnScoreFlip, Exit.ExitOnScoreBelowAbs, Exit.MaxHoldMinutes),
        Costs: new CostsConfig(Costs.BrokeragePerOrder, Costs.SlippageTicks),
        RiskLimits: new RiskLimitsConfig(RiskLimits.MaxDailyLossPct, RiskLimits.MaxDailyProfitPct, RiskLimits.MaxConsecutiveLosses),
        KillSwitch: KillSwitch);
}

/// <summary>LotsPerTrade: 2 (2026-09-07) -- 1 lot made PartialBookFraction's 50% land on 32.5 units, not a valid multiple of LotSize.</summary>
public sealed class CapitalConfigOptions
{
    public decimal Total { get; set; } = 50000;
    public int LotSize { get; set; } = 65;
    public int MaxConcurrentPositions { get; set; } = 3;
    public int LotsPerTrade { get; set; } = 2;
}

public sealed class SessionConfigOptions
{
    public int NoEntryBeforeMinutes { get; set; } = 15;
    public TimeOnly NoEntryAfterTime { get; set; } = new(15, 0);
    public TimeOnly SquareOffTime { get; set; } = new(15, 15);
    public bool ExpiryDayEnabled { get; set; } = true;
    public TimeOnly ExpiryDayNoEntryAfterTime { get; set; } = new(14, 0);
}

/// <summary>
/// MinAbsScore (audit finding F13, re-derived 2026-09-09): 936 real replayed cadences under
/// the fully-corrected Batch 1-3 engine gave |CompositeScore| p90=60.7/p92.5=63.8/p95=68.6 --
/// 64 sits at ~p92.5, inside the audit's own "top 5-10% of cadences" target band. One partial
/// day (VM-restart gaps and all) -- a real starting point, not a final answer; watch the
/// qualification rate over the next several live sessions. MinScoreSustainedSeconds/Cadences
/// held up against the same replay's real streak data (5-6 of 8 streaks still cleared 45s/3
/// cadences even at the tighter thresholds) and stay unmoved.
///
/// MaxTradesPerDay: PENDING (audit finding F42, 2026-09-09 external review) -- 30 is
/// observation-mode sizing (raised from the plan's 7 to observe unconstrained behavior while no
/// real capital is at risk), not the plan's own risk box. Must be tightened back down before
/// this system is ever pointed at real money -- see docs/PLAN.md's "As-Built Status" section.
/// </summary>
public sealed class EntryConfigOptions
{
    public double MinAbsScore { get; set; } = 64;
    public int MinScoreSustainedSeconds { get; set; } = 45;
    public int ReEntryGapSameDirectionMinutes { get; set; } = 2;
    public int MaxTradesPerDay { get; set; } = 30;
    public double MaxIvRankForEntry { get; set; } = 70;
    public int MinScoreSustainedCadences { get; set; } = 3;
}

/// <summary>Narrowed 2026-09-07 (from 150-200) after the first live session -- cheaper premium means a smaller capital commitment per lot and generally higher gamma/more strikes near the money.</summary>
public sealed class StrikeSelectionConfigOptions
{
    public decimal MinPremium { get; set; } = 100;
    public decimal MaxPremium { get; set; } = 150;
    public double MaxSpreadPctOfMid { get; set; } = 2.0;
    public long MinOpenInterest { get; set; } = 100_000;
}

/// <summary>Tightened 2026-09-07 after the first day of live paper trading -- 25%/30% let a position round-trip a lot of premium before anything protected it.</summary>
public sealed class ExitConfigOptions
{
    public double PartialBookAtProfitPct { get; set; } = 15;
    public double PartialBookFraction { get; set; } = 0.5;
    public double StopLossPct { get; set; } = 10;
    public bool TrailAfterPartialBook { get; set; } = true;
    public bool ExitOnScoreFlip { get; set; } = true;
    public double ExitOnScoreBelowAbs { get; set; } = 30;
    public int MaxHoldMinutes { get; set; } = 120;
}

public sealed class CostsConfigOptions
{
    public decimal BrokeragePerOrder { get; set; } = 20;
    public int SlippageTicks { get; set; } = 2;
}

/// <summary>
/// PENDING (audit finding F42, 2026-09-09 external review): MaxDailyLossPct/MaxDailyProfitPct
/// are deliberately widened for paper-trading observation (this project's actual risk box, per
/// docs/PLAN.md, is single-digit) -- must be tightened before this system is ever pointed at
/// real money. See EntryConfigOptions.MaxTradesPerDay's own comment for the same gate.
/// </summary>
public sealed class RiskLimitsConfigOptions
{
    public double MaxDailyLossPct { get; set; } = 20.0;
    public double MaxDailyProfitPct { get; set; } = 30.0;
    public int MaxConsecutiveLosses { get; set; } = 4;
}
