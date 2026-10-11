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
        return new TradingConfig
        {
            ConfigVersion = "paperlive-v1",
            Costs = new CostsConfig { Schedules = new[] { NseIndexOptionsCostSchedules.From20260401(new FlatBrokerage { Amount = brokerage, Unit = BrokerageUnit.PerOrder }) } },
            Session = SessionConfig.PaperLiveV1(),
            Variants = strategies.SelectMany(s => s.Variants).ToDictionary(v => v.StrategyId + "/" + v.VariantId),
            Risk = RiskProfile.PaperLiveV1(options.Capital),
        };
    }
}
