using System.Globalization;
using System.Reflection;
using System.Text.Json;
using NiftyResearcher.Bars;
using NiftyResearcher.Bars.Experiments.FuturesMarketState;
using NiftySignal.AdaptiveObserver;

// Run beside a pinned private research checkout. No dataset or research source is copied into NSE.
// Compare the independent reference library directly, before CSV formatting loses precision.
if (args.Length != 1) throw new ArgumentException("Expected research checkout path.");
var root = Path.GetFullPath(args[0]);
using var holdout = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config/holdout.json")));
var sealedFrom = DateOnly.Parse(holdout.RootElement.GetProperty("sealed_from").GetString()!);
using var thresholdReference=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"adaptive-threshold-reference.json")));
var history = new List<double>();
var reports = new List<object>();
var checkedFields = 0L;
await using var sourceParity = new SourceBootstrapParity();
foreach (var (day, spec) in OpeningVolumeProjectionV1.DiscoveryOutOfFold.OrderBy(x => x.Key))
{
    if (day >= sealedFrom) throw new InvalidOperationException($"Sealed-set violation: {day}.");
    var folder = Path.Combine(root, "NiftyResearcher/research-ticks", day.ToString("yyyy-MM-dd"));
    using var instruments = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "instruments.json")));
    var future = instruments.RootElement.EnumerateArray().Single(x => x.GetProperty("InstrumentType").GetString() == "Future");
    var token = future.GetProperty("Token").GetString()!;
    var symbol = future.GetProperty("TradingSymbol").GetString()!;
    var expiry = DateOnly.Parse(future.GetProperty("ExpiryDate").GetString()!);
    var lot = future.GetProperty("LotSize").GetInt32();
    var raw = NdjsonTickReader.Read(Path.Combine(folder, token + ".ndjson")).ToArray();
    var reference = TickCleaner.Clean(raw);
    var actual = AdaptiveTickCleaner.Clean(raw.Select(x => new ObserverRawTick(
        x.Id, x.ExchangeTimestamp, x.ReceivedAt, x.Last, x.Bid, x.Ask, x.BidQty, x.AskQty, x.Volume, x.OpenInterest)));
    Equal(reference.DuplicatesDropped, actual.DuplicatesDropped, "duplicates", day);
    Equal(reference.ClampedTimestamps, actual.ClampedTimestamps, "clamps", day);
    Equal(reference.Ticks.Count, actual.Ticks.Count, "clean count", day);
    for (var i = 0; i < reference.Ticks.Count; i++) Compare(reference.Ticks[i], actual.Ticks[i], day, [], []);

    var referenceEnricher = new Phase1TickEnricher();
    var actualEnricher = new AdaptiveTradeFlowEnricher();
    var referenceBuilder = new ExactVolumeBarBuilder(spec.AdaptiveBarVolume,
        new DaySessionInfo(day, symbol, expiry, lot, 0, 0, "parity"));
    var actualBuilder = new ExactAdaptiveFuturesBarBuilder(new AdaptiveSessionDefinition(
        day, token, symbol, expiry, lot, spec.AdaptiveBarVolume));
    var cutoff = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 30)), TimeSpan.FromHours(5.5)).ToUniversalTime();
    long opening = 0;
    for (var i = 0; i < reference.Ticks.Count; i++)
    {
        var r = referenceEnricher.Process(reference.Ticks[i]);
        var a = actualEnricher.Process(actual.Ticks[i]);
        Equal(r.TradeVolume, a.TradeVolume, "tick volume", day);
        Equal((int)r.StrictSide, (int)a.StrictSide, "strict side", day);
        Equal((int)r.EnrichedSide, (int)a.EnrichedSide, "enriched side", day);
        if (actual.Ticks[i].AvailableAt < cutoff) opening += a.TradeVolume;
        referenceBuilder.Add(r);
        actualBuilder.Add(a);
    }
    Equal(spec.OpeningVolume0930, opening, "opening volume", day);
    Equal(spec.ExpectedCompleteBars, actualBuilder.Bars.Count, "frozen complete bars", day);
    Equal(referenceBuilder.Bars.Count, actualBuilder.Bars.Count, "reference complete bars", day);
    Equal(referenceBuilder.SplitUpdates, actualBuilder.SplitUpdates, "split updates", day);
    for (var i = 0; i < referenceBuilder.Bars.Count; i++)
    {
        Compare(referenceBuilder.Bars[i], actualBuilder.Bars[i], day,
            [("StartUtc", "StartAvailableAtUtc"), ("EndUtc", "EndAvailableAtUtc"), ("StrictDeltaRatio", "StrictDeltaRatioTotal")],
            ["FutureExpiry", "DteCal", "DteTd", "DteBucket", "LotSize", "Lots", "Range", "FallbackVolume", "StrictCoveragePct"]);
        var r = referenceBuilder.Bars[i]; var a = actualBuilder.Bars[i];
        Equal(r.Range, a.High - a.Low, "range", day);
        Equal(r.FallbackVolume, a.StrictUnknownVolume - a.EnrichedUnknownVolume, "fallback volume", day);
        Equal(r.StrictCoveragePct, 100d * a.StrictCoverage, "coverage percent", day);
    }
    var rf = FlowEvolutionBuilder.Build(referenceBuilder.Bars);
    var af = AdaptiveFlowEvolutionTracker.Build(actualBuilder.Bars);
    Equal(rf.Count, af.Count, "flow count", day);
    for (var i = 0; i < rf.Count; i++)
    {
        Compare(rf[i], af[i], day, [], ["Bar", "Rolling130k"]);
        if (rf[i].Rolling130k is { } r && af[i].Rolling is { } a)
            Compare(r, a, day,
                [("StartUtc", "StartAvailableAtUtc"), ("EndUtc", "EndAvailableAtUtc"),
                 ("QuoteCoverage", "StrictQuoteCoverage"), ("QuoteDeltaRatioTotal", "StrictDeltaRatioTotal"),
                 ("QuoteDeltaRatioClassified", "StrictDeltaRatioClassified")], ["Symbol"]);
        else Equal(rf[i].Rolling130k is null, af[i].Rolling is null, "rolling availability", day);
    }
    double? threshold = history.Count == 0 ? null : AdaptiveWeak2Classifier.ComputeStrongThreshold(history);
    var rq=thresholdReference.RootElement.GetProperty(day.ToString("yyyy-MM-dd"));
    Equal(rq.ValueKind==JsonValueKind.Null ? (double?)null : rq.GetDouble(),threshold,"original Python daily strong threshold",day);
    if (threshold.HasValue) AdaptiveWeak2Classifier.Apply(af, threshold.Value);
    var referenceTriggers = new List<int>();
    for (var i = 2; i < rf.Count; i++)
    {
        var b = rf[i - 2].Rolling130k; var w = rf[i - 1]; var t = rf[i];
        if (threshold is null || b is null || w.Rolling130k is null || t.Rolling130k is null) continue;
        var d = b.PriceDirection;
        if (d != 0 && d == b.StrictDeltaDirection && Math.Abs(double.Parse(b.QuoteDeltaRatioTotal.ToString("0.######",CultureInfo.InvariantCulture),CultureInfo.InvariantCulture)) >= threshold.Value
            && w.StrictDominanceEvolution == "Weakening" && t.StrictDominanceEvolution == "Weakening"
            && w.Rolling130k.StrictDeltaDirection == d && t.Rolling130k.StrictDeltaDirection == d
            && t.Rolling130k.PriceDirection == d)
            referenceTriggers.Add(t.Bar.BarSeq);
    }
    Equal(string.Join(",", referenceTriggers), string.Join(",", af.Where(x => x.State == AdaptiveStateKind.Weak2).Select(x => x.Bar.BarSeq)), "Weak2 identities", day);
    history.AddRange(rf.Where(x => x.Rolling130k is not null).Select(x => Math.Abs(x.Rolling130k!.QuoteDeltaRatioTotal)));
    await sourceParity.CheckAsync(day,token,symbol,expiry,lot,raw.Select(x=>new ObserverRawTick(
        x.Id,x.ExchangeTimestamp,x.ReceivedAt,x.Last,x.Bid,x.Ask,x.BidQty,x.AskQty,x.Volume,x.OpenInterest)).ToArray(),af,threshold);
    ResidualParity.Check(root, day, instruments.RootElement, actualBuilder.Bars,threshold);
    await ObservationParity.CheckAsync(root, day, instruments.RootElement, af, spec.AdaptiveBarVolume, threshold);
    reports.Add(new { day, raw = raw.Length, clean = actual.Ticks.Count, bars = af.Count, opening, threshold, weak2 = referenceTriggers.Count });
    Console.WriteLine(JsonSerializer.Serialize(reports[^1]));
}
if (reports.Count != 13) throw new InvalidOperationException("Expected all 13 discovery sessions.");
Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", sessions = reports.Count, checkedFields,
    scope = "clean ticks, per-tick classification, exact bars, rolling states, evolution, thresholds, Weak2 identities; independent band/residual/H5 sub-gates above",
    pending = "relational restart and VM gates" }));

void Compare(object reference, object actual, DateOnly day,
    (string Source, string Target)[] aliases, string[] ignored)
{
    foreach (var p in reference.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (ignored.Contains(p.Name)) continue;
        var alias = aliases.FirstOrDefault(x => x.Source == p.Name);
        var name = alias.Target ?? p.Name;
        var target = actual.GetType().GetProperty(name)
            ?? throw new InvalidOperationException($"Unmapped reference field {reference.GetType().Name}.{p.Name}.");
        Equal(p.GetValue(reference), target.GetValue(actual), reference.GetType().Name + "." + p.Name, day);
    }
}

void Equal(object? reference, object? actual, string field, DateOnly day)
{
    checkedFields++;
    if (reference is double r && actual is double a)
    {
        if (double.IsFinite(r) && double.IsFinite(a) && Math.Abs(r - a) <= 1e-9) return;
    }
    else if (Equals(reference, actual)) return;
    throw new InvalidOperationException($"{day}: {field} reference={reference}, actual={actual}.");
}
