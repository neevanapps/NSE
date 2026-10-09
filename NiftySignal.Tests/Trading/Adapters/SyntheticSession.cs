using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Adapters;

/// <summary>
/// Deterministic synthetic recorded session for the CP5 adapter tests: a futures token with cumulative volume, ten weekly options with
/// Black-Scholes-consistent quotes around ₹125 premium, quantised 100 ms timestamps so that many rows share an availability instant, a fraction of rows
/// whose received time precedes the exchange stamp (clamp case) and a fraction of exact per-token duplicates. No engineering claim about markets.
/// </summary>
internal static class SyntheticSession
{
    public const string Future = "FUT";
    public static readonly DateOnly Day = new(2026, 9, 22);
    public static readonly DateOnly Expiry = Day.AddDays(3);
    private const double RiskFree = 0.065;
    private static readonly double[] Strikes = { 23800, 23850, 23900, 23950, 24000 };

    public static DateTimeOffset Ist(int h, int m, int s = 0)
        => new DateTimeOffset(Day.ToDateTime(new TimeOnly(h, m, s)), TimeSpan.FromHours(5.5)).ToUniversalTime();

    public static IReadOnlyList<ObserverOptionInstrument> Chain()
        => Strikes.SelectMany(strike => new[] { OptionType.Call, OptionType.Put }
            .Select(side => new ObserverOptionInstrument((side == OptionType.Call ? "CE" : "PE") + (int)strike, "OPT", side, strike, Expiry, 65))).ToList();

    public static IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> DefaultWindows()
        => new[] { (Ist(9, 15), Ist(10, 5)), (Ist(14, 50), Ist(15, 36)) };

    public sealed record Options
    {
        public int Seed { get; init; } = 7;

        public double ClampFraction { get; init; } = 0.01;

        public double DuplicateFraction { get; init; } = 0.02;

        public IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)>? Windows { get; init; }

        /// <summary>Intervals in which the futures token emits nothing while options keep quoting (stale futures).</summary>
        public IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> FutureSilences { get; init; } = Array.Empty<(DateTimeOffset, DateTimeOffset)>();

        /// <summary>Intervals in which nothing at all is emitted (a data gap).</summary>
        public IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> Gaps { get; init; } = Array.Empty<(DateTimeOffset, DateTimeOffset)>();
    }

    public static IReadOnlyList<SourceTick> Rows(Options? options = null)
    {
        var o = options ?? new Options();
        var rng = new Random(o.Seed);
        var rows = new List<SourceTick>();
        var chain = Chain();
        long id = 0;
        long volume = 10_000;
        var future = 23_900d;
        var last = new Dictionary<string, SourceTick>(StringComparer.Ordinal);
        foreach (var (from, to) in o.Windows ?? DefaultWindows())
        {
            for (var t = from; t < to; t = t.AddMilliseconds(100))
            {
                var inGap = o.Gaps.Any(g => t >= g.From && t < g.To);
                var futureSilent = o.FutureSilences.Any(g => t >= g.From && t < g.To);
                var futureRoll = rng.NextDouble();
                if (!inGap && !futureSilent && futureRoll < 0.18)
                {
                    future = Math.Round(Math.Max(23_700d, Math.Min(24_100d, future + (rng.Next(-3, 4) * 0.5))), 1);
                    volume += rng.Next(1, 25);
                    var bid = (decimal)(future - 0.5);
                    rows.Add(Stamp(rng, o, ref id, Future, t, (decimal)future, bid, bid + 1m, rng.Next(100, 800), rng.Next(100, 800), volume, 5_000));
                }

                foreach (var instrument in chain)
                {
                    var optionRoll = rng.NextDouble();
                    if (inGap || optionRoll >= 0.08)
                    {
                        continue;
                    }

                    var years = TimeToExpiry.YearsUntilExpiry(Expiry, t);
                    var vol = 0.125 + (rng.NextDouble() * 0.01);
                    var mid = Math.Round(AdaptiveResearchPricing.Price(instrument.OptionType, future, instrument.Strike, years, RiskFree, vol), 2);
                    var bid = (decimal)Math.Max(0.05, mid - 0.5);
                    var ask = bid + 1m;
                    if (last.TryGetValue(instrument.Token, out var previous) && rng.NextDouble() < o.DuplicateFraction)
                    {
                        rows.Add(Stamp(rng, o, ref id, instrument.Token, t, previous.Last, previous.Bid, previous.Ask, previous.BidQty, previous.AskQty, previous.Volume, previous.OpenInterest, forceExchange: previous.ExchangeTimestamp));
                    }
                    else
                    {
                        rows.Add(Stamp(rng, o, ref id, instrument.Token, t, (decimal)mid, bid, ask, 2_000, 2_000, 1_000 + id, 5_000 + (id % 50)));
                    }

                    last[instrument.Token] = rows[^1];
                }
            }
        }

        return rows;
    }

    private static SourceTick Stamp(Random rng, Options o, ref long id, string token, DateTimeOffset t, decimal lastPrice, decimal bid, decimal ask, long bidQty, long askQty, long volume, long? oi, DateTimeOffset? forceExchange = null)
    {
        var exchange = forceExchange ?? new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, TimeSpan.Zero);
        var received = rng.NextDouble() < o.ClampFraction ? t.AddMilliseconds(-1_000) : t;
        return new SourceTick(token, ++id, exchange, received, lastPrice, bid, ask, bidQty, askQty, volume, oi);
    }

    /// <summary>The frozen session: base bar volume 1000, signal start 09:30, observation start 09:15, optionally with a 09:30 residual anchor built from the rows.</summary>
    public static SessionFreeze Freeze(IReadOnlyList<SourceTick> rows, bool anchor = true)
    {
        var chain = Chain();
        OptionResidualAnchor? residual = null;
        if (anchor)
        {
            var cutoff = Ist(9, 30);
            var quotes = new Dictionary<string, OptionQuoteSnapshot>(StringComparer.Ordinal);
            double future0930 = 23_900d;
            foreach (var r in rows.Where(r => r.ReceivedAt <= cutoff))
            {
                var at = r.ReceivedAt < r.ExchangeTimestamp ? r.ExchangeTimestamp : r.ReceivedAt;
                if (r.Token == Future)
                {
                    future0930 = (double)r.Last;
                }
                else
                {
                    quotes[r.Token] = new OptionQuoteSnapshot(r.Token, at, (double)r.Last, (double)r.Bid, (double)r.Ask, r.OpenInterest);
                }
            }

            residual = OptionResidualModel.BuildAnchor(cutoff, Expiry, future0930, chain, quotes, RiskFree, 5d);
        }

        return new SessionFreeze(
            new AdaptiveSessionDefinition(Day, Future, Future, Day.AddDays(7), 65, 1_000),
            Expiry,
            RiskFree,
            null,
            Ist(9, 30),
            Ist(9, 15),
            chain,
            residual,
            chain.ToDictionary(c => c.Token, _ => 0.05m, StringComparer.Ordinal),
            "synthetic-session-v1");
    }

    public static TranslatorOptions Translator(SessionFreeze freeze, TimeSpan? lag = null)
        => new() { StableOrderingLag = lag ?? TimeSpan.FromSeconds(2), AcceptFromUtc = freeze.ObservationStartUtc, AcceptThroughUtc = Ist(15, 35) };

    public static IReadOnlyList<VariantSpec> Variants()
        => new[]
        {
            new VariantSpec { VariantId = "A", StrategyId = "P6", EntryCondition = new NiftySignal.Trading.Core.Entry.ImmediateEntrySpec() },
            new VariantSpec
            {
                VariantId = "B",
                StrategyId = "P4",
                EntryCondition = new NiftySignal.Trading.Core.Entry.AskBelowCandleMaSpec { N = 2, PriceSource = NiftySignal.Trading.Core.Candles.CandlePriceSource.AskClose },
                Exit = new NiftySignal.Trading.Core.Exits.ExitSpec { Tp1Pct = 0.02m, Tp1Proportion = 0.5m, TradeSlPct = 0.04m },
            },
        };

    public static IReadOnlyDictionary<string, IBaseStrategy> Strategies()
        => new Dictionary<string, IBaseStrategy>
        {
            ["P6"] = new PlumbingEveryNthBarStrategy("P6", 6, 3),
            ["P4"] = new PlumbingEveryNthBarStrategy("P4", 4, 2),
        };

    public static TradingConfig Config(Func<TradingConfig, TradingConfig>? tweak = null)
    {
        var config = HarnessSettings.Config("cp5-tests", Variants());
        return tweak?.Invoke(config) ?? config;
    }

    /// <summary>Builds a fresh run (translator + engine + loop) over the synthetic freeze.</summary>
    public static TradingRun NewRun(SessionFreeze freeze, RecorderOptions? recorder = null, string mode = "Historical", IEnumerable<NiftySignal.Trading.Core.Events.EngineEvent>? controls = null, TimeSpan? lag = null, TradingConfig? config = null, RunSettings? settings = null)
        => new(freeze, Translator(freeze, lag), config ?? Config(), Strategies(), settings ?? new RunSettings { RunMode = mode, RunId = "cp5-" + mode }, recorder, controls);
}
