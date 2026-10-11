using Microsoft.Extensions.Configuration;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Trading.Live;

/// <summary>One scripted entry window of the TEST/PLUMBING family (bar sequence numbers are the Adaptive bar numbers of the session).</summary>
public sealed class PlumbingWindowOptions
{
    public long FromBarSeq { get; set; }

    public long ToBarSeq { get; set; }

    /// <summary><c>Long</c> (a call) or <c>Short</c> (a put).</summary>
    public string Direction { get; set; } = "Long";
}

/// <summary>
/// The TEST/PLUMBING family: a deterministic script that exists only to prove the paper path end to end (entry, exit, persistence, restart, Dashboard). It claims no edge,
/// trades nothing unless explicitly enabled, and is never part of a normal paper session.
/// </summary>
public sealed class PlumbingFamilyOptions
{
    public bool Enabled { get; set; }

    public string StrategyId { get; set; } = "PLUMBING";

    public string VariantId { get; set; } = "P1";

    public List<PlumbingWindowOptions> Windows { get; set; } = new();

    public decimal? TradeSlPct { get; set; }

    public decimal? Tp1Pct { get; set; }

    public decimal? Tp1Proportion { get; set; }

    public int? MaxHoldMinutes { get; set; }
}

/// <summary>
/// Configuration of the live paper path (<c>Trading:PaperLive</c>). <b>Source defaults keep it OFF</b>: <see cref="Enabled"/> is false, no strategy is registered, and an
/// enabled instance with a missing required value refuses to start (fail closed, visibly) instead of assuming anything: costs are never assumed zero, the tick size is never
/// defaulted, the capital is never invented.
/// </summary>
public sealed class PaperLiveOptions
{
    public const string SectionName = "Trading:PaperLive";

    public bool Enabled { get; set; }

    /// <summary>Name of the connection string (under <c>ConnectionStrings</c>) of the PostgreSQL database that holds the <c>trading</c> schema. Must be set explicitly; never the tick or observer database by accident.</summary>
    public string ConnectionStringName { get; set; } = "TradingPaperLive";

    /// <summary>Total paper capital shared by every variant of every family (one global pool). Required, positive.</summary>
    public decimal Capital { get; set; }

    /// <summary>Flat brokerage per order of the paper cost schedule. Required (0 is a legitimate explicit choice; absent is not).</summary>
    public decimal? BrokeragePerOrder { get; set; }

    /// <summary>Exchange tick size of the option contracts (NSE index options: 0.05). Required: never defaulted.</summary>
    public decimal? OptionTickSize { get; set; }

    public PlumbingFamilyOptions PlumbingFamily { get; set; } = new();

    public PaperLiveRiskOptions Risk { get; set; } = new();

    /// <summary>Problems found while reading the configuration (an unknown or unenforced key, an unreadable value). Filled by <see cref="Bind"/>; any entry halts an enabled instance.</summary>
    public List<string> BindingErrors { get; set; } = new();

    private static readonly string[] GlobalKeys = { nameof(GlobalLimitOptions.MaxTradesPerDay), nameof(GlobalLimitOptions.MaxDailyLoss) };
    private static readonly string[] ScopeKeys = { nameof(ScopeLimitOptions.MaxTradesPerDay), nameof(ScopeLimitOptions.MaxDailyLoss), nameof(ScopeLimitOptions.MaxConsecutiveLosses) };

    /// <summary>
    /// Reads <c>Trading:PaperLive</c> without ever throwing and without silently ignoring anything in the risk section: an unreadable value or a key that is not an enforced limit
    /// (for example <c>Risk:Global:MaxConsecutiveLosses</c>, or a misspelt name) becomes a <see cref="BindingErrors"/> entry.
    /// </summary>
    public static PaperLiveOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        PaperLiveOptions options;
        try
        {
            options = section.Get<PaperLiveOptions>() ?? new PaperLiveOptions();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            options = new PaperLiveOptions { Enabled = bool.TryParse(section["Enabled"], out var enabled) && enabled };
            options.BindingErrors.Add($"{SectionName} could not be read: {e.Message}");
            return options;
        }

        var risk = section.GetSection("Risk");
        CheckKeys(risk.GetSection("Global"), GlobalKeys, "Risk:Global", options.BindingErrors);
        foreach (var scope in new[] { "Families", "Variants" })
        {
            foreach (var entry in risk.GetSection(scope).GetChildren())
            {
                CheckKeys(entry, ScopeKeys, $"Risk:{scope}:{entry.Key}", options.BindingErrors);
            }
        }

        foreach (var child in risk.GetChildren().Where(c => c.Key is not ("Global" or "Families" or "Variants")))
        {
            options.BindingErrors.Add($"Risk:{child.Key} is not a known risk scope (Global, Families, Variants).");
        }

        return options;
    }

    private static void CheckKeys(IConfigurationSection section, string[] allowed, string path, List<string> errors)
    {
        foreach (var child in section.GetChildren().Where(c => !allowed.Contains(c.Key, StringComparer.OrdinalIgnoreCase)))
        {
            errors.Add($"{path}:{child.Key} is not an enforced limit (allowed: {string.Join(", ", allowed)}); it would be silently ignored, so it is refused.");
        }
    }

    /// <summary>
    /// Validates the risk configuration. Zero or negative limits, malformed or duplicate keys are always refused. Existence checks (a configured family or variant must be registered)
    /// only apply when strategies are actually registered: with none, nothing is invented and the keys are only checked for form.
    /// </summary>
    public IReadOnlyList<string> ValidateRisk(IReadOnlyList<PaperLiveStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        var errors = new List<string>(BindingErrors);
        if (Risk.Global is { } global)
        {
            errors.AddRange(global.ToLimits().Validate("Risk:Global"));
        }

        var familyKeys = Risk.Families.Keys.ToList();
        var variantKeys = Risk.Variants.Keys.ToList();
        foreach (var group in familyKeys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"Risk:Families key '{group.Key}' is duplicated.");
        }

        foreach (var group in variantKeys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"Risk:Variants key '{group.Key}' is duplicated.");
        }

        var registeredFamilies = strategies.Select(s => s.Family.StrategyId).ToHashSet(StringComparer.Ordinal);
        var registeredVariants = strategies.SelectMany(s => s.Variants).Select(v => v.StrategyId + "/" + v.VariantId).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, limits) in Risk.Families)
        {
            if (!VariantKey.IsValidPart(key))
            {
                errors.Add($"Risk:Families key '{key}' is not a valid StrategyId.");
                continue;
            }

            errors.AddRange((limits ?? new ScopeLimitOptions()).ToLimits().Validate($"Risk:Families:{key}"));
            if (strategies.Count > 0 && !registeredFamilies.Contains(key))
            {
                errors.Add($"Risk:Families:{key} configures a strategy family that is not registered.");
            }
        }

        foreach (var (key, limits) in Risk.Variants)
        {
            if (!VariantKey.TryParse(key, out var parsed))
            {
                errors.Add($"Risk:Variants key '{key}' is not a valid StrategyId/VariantId.");
                continue;
            }

            errors.AddRange((limits ?? new ScopeLimitOptions()).ToLimits().Validate($"Risk:Variants:{key}"));
            if (strategies.Count > 0 && !registeredVariants.Contains(parsed.ToString()))
            {
                errors.Add($"Risk:Variants:{key} configures a variant that is not registered.");
            }
        }

        return errors;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enabled)
        {
            return errors;
        }

        if (Capital <= 0m)
        {
            errors.Add("Trading:PaperLive:Capital must be a positive amount.");
        }

        if (BrokeragePerOrder is null or < 0m)
        {
            errors.Add("Trading:PaperLive:BrokeragePerOrder must be set explicitly (0 or more).");
        }

        if (OptionTickSize is null or <= 0m)
        {
            errors.Add("Trading:PaperLive:OptionTickSize must be set explicitly (a positive number).");
        }

        if (string.IsNullOrWhiteSpace(ConnectionStringName))
        {
            errors.Add("Trading:PaperLive:ConnectionStringName must name a connection string.");
        }

        if (PlumbingFamily.Enabled)
        {
            if (PlumbingFamily.Windows.Count == 0)
            {
                errors.Add("Trading:PaperLive:PlumbingFamily is enabled but has no entry window.");
            }

            foreach (var w in PlumbingFamily.Windows)
            {
                if (w.FromBarSeq < 1 || w.ToBarSeq < w.FromBarSeq || !Enum.TryParse<StrategyDirection>(w.Direction, out var d) || d == StrategyDirection.Neutral)
                {
                    errors.Add($"Trading:PaperLive:PlumbingFamily window [{w.FromBarSeq},{w.ToBarSeq}] {w.Direction} is invalid (Long or Short, 1 <= From <= To).");
                }
            }
        }

        return errors;
    }
}

/// <summary>Limits of one variant or one family (all optional; null = not enforced). Counts and losses are per IST trading day; the daily loss is realized net P&amp;L in rupees.</summary>
public sealed class ScopeLimitOptions
{
    public int? MaxTradesPerDay { get; set; }

    public decimal? MaxDailyLoss { get; set; }

    public int? MaxConsecutiveLosses { get; set; }

    public ScopeLimits ToLimits() => new() { MaxTradesPerDay = MaxTradesPerDay, MaxDailyLoss = MaxDailyLoss, MaxConsecutiveLosses = MaxConsecutiveLosses };
}

/// <summary>Account-wide limits. Only what PaperLiveV1 enforces is offered: a consecutive-loss limit exists for variants and families, not for the account.</summary>
public sealed class GlobalLimitOptions
{
    public int? MaxTradesPerDay { get; set; }

    public decimal? MaxDailyLoss { get; set; }

    public ScopeLimits ToLimits() => new() { MaxTradesPerDay = MaxTradesPerDay, MaxDailyLoss = MaxDailyLoss };
}

/// <summary>
/// <c>Trading:PaperLive:Risk</c>: the limits of every scope, set by configuration only (no value is a source default). Variants are keyed <c>StrategyId/VariantId</c>, families by <c>StrategyId</c>.
/// The shared capital pool is <see cref="PaperLiveOptions.Capital"/>.
/// </summary>
public sealed class PaperLiveRiskOptions
{
    public GlobalLimitOptions? Global { get; set; }

    public Dictionary<string, ScopeLimitOptions> Families { get; set; } = new();

    public Dictionary<string, ScopeLimitOptions> Variants { get; set; } = new();
}

/// <summary>A strategy family together with the variants (configuration) it is run with.</summary>
public sealed record PaperLiveStrategy(IStrategyFamily Family, IReadOnlyList<VariantSpec> Variants);

/// <summary>Builds the strategies and the trading configuration of the live paper path from <see cref="PaperLiveOptions"/>.</summary>
public static class PaperLiveSetup
{
    /// <summary>The strategies that are enabled by the options. By default there are none: the plumbing family only exists when it is switched on explicitly.</summary>
    public static IReadOnlyList<PaperLiveStrategy> Strategies(PaperLiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var list = new List<PaperLiveStrategy>();
        var p = options.PlumbingFamily;
        if (options.Enabled && p.Enabled)
        {
            var windows = p.Windows.Select(w => new PlumbingWindow(w.FromBarSeq, w.ToBarSeq, Enum.Parse<StrategyDirection>(w.Direction))).ToList();
            var family = new PlumbingFamily(p.StrategyId, new Dictionary<string, PlumbingVariantScript> { [p.VariantId] = new(windows) });
            var exit = new ExitSpec
            {
                TradeSlPct = p.TradeSlPct,
                Tp1Pct = p.Tp1Pct,
                Tp1Proportion = p.Tp1Proportion,
                MaxHold = p.MaxHoldMinutes is { } m ? TimeSpan.FromMinutes(m) : null,
            };
            list.Add(new PaperLiveStrategy(family, new[] { new VariantSpec { StrategyId = p.StrategyId, VariantId = p.VariantId, EntryCondition = new ImmediateEntrySpec(), Exit = exit } }));
        }

        return list;
    }

    /// <summary>The PaperLiveV1 trading configuration: fixed session rules, one global capital pool, explicit costs.</summary>
    public static TradingConfig Config(PaperLiveOptions options, IReadOnlyList<PaperLiveStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(strategies);
        var brokerage = options.BrokeragePerOrder ?? throw new InvalidOperationException("Trading:PaperLive:BrokeragePerOrder is not set.");
        var familyLimits = options.Risk.Families.ToDictionary(f => f.Key, f => (f.Value ?? new ScopeLimitOptions()).ToLimits(), StringComparer.Ordinal);
        var variantLimits = options.Risk.Variants.ToDictionary(v => v.Key, v => (v.Value ?? new ScopeLimitOptions()).ToLimits(), StringComparer.Ordinal);
        var variants = strategies.SelectMany(s => s.Variants)
            .Select(v => variantLimits.TryGetValue(v.StrategyId + "/" + v.VariantId, out var limits) ? v with { Limits = limits } : v)
            .ToList();
        return new TradingConfig
        {
            ConfigVersion = "paperlive-v1",
            Costs = new CostsConfig { Schedules = new[] { NseIndexOptionsCostSchedules.From20260401(new FlatBrokerage { Amount = brokerage, Unit = BrokerageUnit.PerOrder }) } },
            Session = SessionConfig.PaperLiveV1(),
            Variants = variants.ToDictionary(v => v.StrategyId + "/" + v.VariantId),
            Risk = RiskProfile.PaperLiveV1(options.Capital, options.Risk.Global?.ToLimits(), familyLimits),
        };
    }
}
