using System.Security.Cryptography;
using System.Text;
using NiftySignal.MarketSummary;
using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Metrics;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Frames;

namespace NiftySignal.Tests.PaperEngineV2;

sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

static class FrameFixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 7, 4, 0, 0, TimeSpan.Zero);          // 09:30 IST
    public static readonly DateOnly Day = new(2026, 10, 7);
    public static readonly DateOnly Expiry = new(2026, 10, 13);

    public static DateTimeOffset End(int seq) => T0.AddMinutes(seq);

    static readonly string[] Tokens = ["CE25900", "CE25950", "CE26000", "CE26050", "CE26100", "PE25900", "PE25950", "PE26000", "PE26050", "PE26100"];

    public static IReadOnlyList<ListedStrike> Listed() =>
        Enumerable.Range(0, 11).Select(i => 25_750d + i * 50).Select(s => new ListedStrike(s, $"CE{s:0}", $"PE{s:0}")).ToList();

    public static Basket Basket(double centre = 26_000) => BasketResolver.Resolve(Expiry, centre, Listed());

    public static BasketIdentity BasketId(double centre = 26_000) => new(Expiry, centre, 50,
        Basket(centre).Contracts.Select(c => new BasketSlot(c.Offset, c.Strike, c.IsCall, c.Token, c.Moneyness == NiftySignal.MarketSummary.Moneyness.Itm ? NiftySignal.PaperEngineV2.Core.Frames.Moneyness.Itm : c.Moneyness == NiftySignal.MarketSummary.Moneyness.Atm ? NiftySignal.PaperEngineV2.Core.Frames.Moneyness.Atm : NiftySignal.PaperEngineV2.Core.Frames.Moneyness.Otm)).ToList(), []);

    public static FrameIdentity Identity(long session, int seq, DateTimeOffset? end = null, long? tick = null, int ordinal = 0, bool verified = true, double duration = 60, DateTimeOffset? start = null, string model = "adaptive-fixed13650-v1") =>
        new(FrameIdentity.CurrentSchema, MetricRegistry.SourceModel, model, "spot-atm-v1", session, Day, seq, start ?? (end ?? End(seq)).AddSeconds(-duration), end ?? End(seq), tick ?? 1_000 + seq, ordinal, duration, verified, verified ? null : "sidecar integrity unknown");

    public static int H(string id) => MetricRegistry.Get(id).Handle;

    /// <summary>A small frame with a handful of metrics derived from the bar sequence (enough for the pipeline and the 30-strategy tests).</summary>
    public static MarketStateFrame Light(long session, int seq, DateTimeOffset? end = null, long? tick = null, int ordinal = 0, bool verified = true, double duration = 60, double? strict = null, bool omitStrict = false, string? variant = null)
    {
        var metrics = new List<MetricValue>
        {
            new(H("fut.strict:pct"), SummaryWindowId.W5, 1 + seq % 3),
            new(H("opt.atm.coverage_full"), SummaryWindowId.W10, 1),
            new(H("vix.fresh"), SummaryWindowId.W5, 1),
        };
        if (!omitStrict) metrics.Add(new MetricValue(H("opt.atm.strict.dir:pct"), SummaryWindowId.W10, strict ?? Math.Sin(seq / 2d) * 10));
        if (variant is not null) metrics.Add(new MetricValue(H("fut.enriched:pct"), SummaryWindowId.W5, variant.Length % 7 + 1));
        var e = end ?? End(seq);
        var quotes = new[] { new QuoteSnapshot("CE26000", 120, 121, 650, 650, e.AddMilliseconds(-300), 900 + seq) };
        return MarketStateFrame.Create(Identity(session, seq, e, tick, ordinal, verified, duration), BasketId(), metrics, quotes, [], new VixAvailability(e.AddSeconds(-2), 2, "Fresh"));
    }

    // ---- full Market Summary inputs for the builder ----

    sealed record Series(List<ContractBar> Contracts, Dictionary<int, FuturesBar> Futures, Dictionary<int, double> Durations);

    static Series MakeSeries(Basket basket, int bars)
    {
        var r = new Random(11);
        var contracts = new List<ContractBar>();
        for (var seq = 1; seq <= bars; seq++)
            foreach (var c in basket.Contracts)
                contracts.Add(new ContractBar(seq, c.Token!, c.IsCall, 100 + r.Next(50), 100 + r.Next(50), r.Next(100, 900), r.Next(-80, 80), r.Next(10, 100), r.Next(10, 100), r.Next(0, 20), r.Next(-50, 50),
                    10_000 + r.Next(500), 10_000 + r.Next(500), 12 + r.NextDouble(), 12 + r.NextDouble(), 0.4 + r.NextDouble() * 0.1, 0.4 + r.NextDouble() * 0.1, 5 + r.NextDouble(), 5 + r.NextDouble(),
                    r.NextDouble() - 0.5, r.NextDouble() - 0.5, 5 + r.NextDouble() * 20, r.Next(-30, 30), r.Next(40, 90)));
        var futures = Enumerable.Range(1, bars).ToDictionary(i => i, i => new FuturesBar(i, 60, 26_000 + i, 26_000 + i + r.Next(-5, 6), 13_650, r.Next(-500, 500), 1000, 1100, 100, r.Next(-300, 300), 1_000_000 + i * 10, 1_000_000 + (i + 1) * 10,
            r.NextDouble() - 0.5, r.NextDouble() - 0.5, 20, 22, r.Next(-100, 100), r.Next(150, 400)));
        return new Series(contracts, futures, Enumerable.Range(1, bars).ToDictionary(i => i, _ => 60d));
    }

    public sealed record SummaryInputs(Basket Basket, IReadOnlyList<FuturesWindowSummary> Futures, IReadOnlyList<GridSummary> Grids, IReadOnlyList<VixWindowSummary>? Vix, IReadOnlyList<QuoteInput> Quotes);

    public static SummaryInputs Summary(int bar, double centre = 26_000, bool withVix = true, DateTimeOffset? end = null, int historyBars = 130)
    {
        var basket = Basket(centre);
        var series = MakeSeries(basket, Math.Max(bar, historyBars));
        var h = new ContractHistory(series.Contracts, series.Durations);
        var grids = OptionSummaryCalculator.ComputeAll(basket, h, bar);
        var futures = FuturesSummaryCalculator.Compute(series.Futures, bar);
        var e = end ?? End(bar);
        IReadOnlyList<VixWindowSummary>? vix = null;
        if (withVix)
        {
            var obs = Enumerable.Range(-60, bar * 20 + 80).Select(i => new VixObservation(T0.AddSeconds(i * 3), 13 + i * 0.001)).ToList();
            var instants = SummaryWindow.All.Select(w => new WindowInstant(w, w.Bars is { } n ? bar - n + 1 >= 1 ? End(bar - n) : null : T0, e)).ToList();
            vix = VixSummaryCalculator.Compute(obs, instants, VixSummaryCalculator.FreshnessLimit);
        }

        var quotes = basket.Contracts.Select((c, i) => new QuoteInput(c.Token!, 120 + i, 121 + i, 650, 650, e.AddMilliseconds(-300 - i), 5_000 + i)).ToList();
        return new SummaryInputs(basket, futures, grids, vix, quotes);
    }

    public static BarInput Bar(long session, int seq, DateTimeOffset? end = null, long? tick = null, double duration = 60, bool verified = true, string model = MarketStateFrameBuilder.FixedClockModelVersion, DateTimeOffset? start = null)
    {
        var e = end ?? End(seq);
        return new BarInput(session, Day, model, "spot-atm-v1", seq, start ?? e.AddSeconds(-duration), e, tick ?? 1_000 + seq, duration, verified, verified ? null : "sidecar integrity unknown");
    }

    public static MarketStateFrame Build(MarketStateFrameBuilder builder, long session, int seq, SummaryInputs? s = null, DateTimeOffset? end = null, long? tick = null, double duration = 60, bool verified = true)
    {
        s ??= Summary(seq, end: end);
        return builder.Build(Bar(session, seq, end, tick, duration, verified), s.Basket, s.Futures, s.Grids, s.Vix, s.Quotes);
    }
}

/// <summary>A deterministic processor for the pipeline tests: evaluates 30 compiled strategies per frame and chains a digest of everything that matters (bar, disposition, previous link, signals).</summary>
sealed class StrategyDigestProcessor : IFrameProcessor
{
    readonly IReadOnlyList<CompiledStrategy> _strategies;
    byte[] _digest = new byte[32];

    public StrategyDigestProcessor(IReadOnlyList<CompiledStrategy> strategies) { _strategies = strategies; }

    public Action<MarketStateFrame>? OnProcess { get; set; }

    public List<(int Bar, bool Allowed, int? PreviousBar, IntegrityFlags Reasons, string Signals)> Log { get; } = [];

    public string Digest => Convert.ToHexString(_digest);

    public ValueTask ProcessAsync(MarketStateFrame frame, MarketStateFrame? previous, FrameDisposition disposition, CancellationToken ct)
    {
        OnProcess?.Invoke(frame);
        var ctx = new EvaluationContext(frame, previous);
        var sb = new StringBuilder();
        foreach (var s in _strategies)
        {
            var e = s.EvaluateEntry(ctx);
            var x = s.EvaluateExit(ctx);
            // an entry signal only counts when the disposition allows entries; exits are evaluated either way
            sb.Append(disposition.EntriesAllowed ? (char)('0' + (int)e.BuyCall) : '-').Append(disposition.EntriesAllowed ? (char)('0' + (int)e.BuyPut) : '-').Append((char)('0' + (int)x));
        }

        var line = $"{frame.Identity.BarSeq}|{frame.Hash}|{disposition.EntriesAllowed}|{(int)disposition.BlockReasons}|{disposition.ProcessingLagMs}|{disposition.RunLength}|{previous?.Identity.BarSeq}|{sb}";
        _digest = SHA256.HashData([.. _digest, .. Encoding.UTF8.GetBytes(line)]);
        Log.Add((frame.Identity.BarSeq, disposition.EntriesAllowed, previous?.Identity.BarSeq, disposition.BlockReasons, sb.ToString()));
        return ValueTask.CompletedTask;
    }

    public static IReadOnlyList<CompiledStrategy> Thirty() => StrategySet.Load(Enumerable.Range(1, 30).Select(i => ($"f{i}", ConfigTests.Mutate(o =>
    {
        o["strategyId"] = $"S{i:00}";
        o["entry"]!["buyCall"] = System.Text.Json.Nodes.JsonNode.Parse("{ \"op\": \"AND\", \"items\": [ { \"metric\": \"opt.atm.strict.dir:pct\", \"window\": \"10\", \"cmp\": \">\", \"value\": " + (i % 7 - 3) + " }, { \"metric\": \"fut.strict:pct\", \"window\": \"5\", \"cmp\": \">\", \"value\": 0 } ] }");
        o["entry"]!["buyPut"] = System.Text.Json.Nodes.JsonNode.Parse("{ \"op\": \"OR\", \"items\": [ { \"metric\": \"opt.atm.strict.dir:pct\", \"window\": \"10\", \"cmp\": \"<\", \"value\": " + (-(i % 5)) + " } ] }");
        o["exit"]!["conditions"] = System.Text.Json.Nodes.JsonNode.Parse("{ \"op\": \"OR\", \"items\": [ { \"cross\": \"Down\", \"metric\": \"opt.atm.strict.dir:pct\", \"window\": \"10\", \"level\": 0 } ] }");
    }))).ToList()).Active;
}
