using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Metrics;

namespace NiftySignal.PaperEngineV2.Core.Config;

/// <summary>
/// Strict parser and validator of <c>pev2-strategy/1</c>. It reads the JSON with an explicit allow-list per object (unknown, duplicate or forbidden properties are errors), validates every value against the
/// frozen contract (docs/PAPER_ENGINE_V2_DESIGN.md section 0.2) and the typed metric registry, and returns ALL problems at once. No expressions, scripts or SQL exist in the format; it can only BUY calls and
/// puts, and it carries no live / broker permission of any kind (that lives outside the strategy file).
/// </summary>
public static partial class StrategyConfigParser
{
    public const string Schema = "pev2-strategy/1";
    public const int MaxConditionDepth = 4;
    public const int MaxLeavesPerTree = 32;
    public const string RankRule = "PremiumProximityThenCentreDistanceThenStrikeThenToken";

    static readonly TimeOnly EarliestEntry = new(9, 30, 0);
    static readonly TimeOnly LatestEntryCutoff = new(15, 0, 0);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,31}$")]
    private static partial Regex IdPattern();

    static readonly HashSet<string> SellVocabulary = new(StringComparer.OrdinalIgnoreCase) { "sell", "short", "write", "writecall", "writeput", "shortcall", "shortput", "long", "side", "direction", "sellcall", "sellput" };

    static readonly HashSet<string> LiveVocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        "live", "liveenabled", "liveauthorized", "liveauthorised", "authorization", "authorisation", "authorized", "authorised", "broker", "router", "route", "submitorders", "realorders", "production",
    };

    public static ParseResult Parse(string json)
    {
        var errors = new List<ConfigError>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        }
        catch (JsonException ex)
        {
            return new ParseResult(null, [new ConfigError("Json", "$", ex.Message)]);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var canonical = CanonicalJson.Canonicalize(root);
            var cfg = ReadRoot(root, canonical, errors);
            return errors.Count == 0 && cfg is not null ? new ParseResult(cfg, errors) : new ParseResult(null, errors);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------
    // object reader: allow-list, duplicates, forbidden vocabulary
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------

    sealed class Obj
    {
        readonly Dictionary<string, JsonElement> _props = new(StringComparer.Ordinal);
        readonly List<ConfigError> _errors;

        public string Path { get; }

        Obj(string path, List<ConfigError> errors) { Path = path; _errors = errors; }

        public static Obj? Open(JsonElement e, string path, List<ConfigError> errors, params string[] allowed)
        {
            if (e.ValueKind != JsonValueKind.Object) { errors.Add(new ConfigError("Type", path, "an object is required")); return null; }
            var o = new Obj(path, errors);
            var allow = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                if (!o._props.TryAdd(p.Name, p.Value)) { errors.Add(new ConfigError("Duplicate", $"{path}/{p.Name}", "duplicate property")); continue; }
                if (SellVocabulary.Contains(p.Name)) errors.Add(new ConfigError("ForbiddenSell", $"{path}/{p.Name}", "V2 only BUYS calls and puts: use buyCall / buyPut; selling, shorting and writing options cannot be expressed"));
                else if (LiveVocabulary.Contains(p.Name)) errors.Add(new ConfigError("ForbiddenLive", $"{path}/{p.Name}", "live / broker authorization is never part of a strategy file"));
                else if (!allow.Contains(p.Name)) errors.Add(new ConfigError("Unknown", $"{path}/{p.Name}", "unknown property"));
            }

            return o;
        }

        public bool Has(string name) => _props.ContainsKey(name);

        public JsonElement? Raw(string name) => _props.TryGetValue(name, out var v) ? v : null;

        string P(string name) => $"{Path}/{name}";

        public Obj? Child(string name, bool required, params string[] allowed)
        {
            if (!_props.TryGetValue(name, out var v)) { if (required) _errors.Add(new ConfigError("Required", P(name), "required")); return null; }
            return Open(v, P(name), _errors, allowed);
        }

        public string? Str(string name, bool required = true)
        {
            if (!_props.TryGetValue(name, out var v)) { if (required) _errors.Add(new ConfigError("Required", P(name), "required")); return null; }
            if (v.ValueKind != JsonValueKind.String) { _errors.Add(new ConfigError("Type", P(name), "a string is required")); return null; }
            return v.GetString();
        }

        public int? Int(string name, bool required = true)
        {
            if (!_props.TryGetValue(name, out var v)) { if (required) _errors.Add(new ConfigError("Required", P(name), "required")); return null; }
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var i)) { _errors.Add(new ConfigError("Type", P(name), "an integer is required")); return null; }
            return i;
        }

        public double? Num(string name, bool required = true)
        {
            if (!_props.TryGetValue(name, out var v)) { if (required) _errors.Add(new ConfigError("Required", P(name), "required")); return null; }
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || !double.IsFinite(d)) { _errors.Add(new ConfigError("Type", P(name), "a finite number is required")); return null; }
            return d;
        }

        public bool? Bool(string name, bool required = true)
        {
            if (!_props.TryGetValue(name, out var v)) { if (required) _errors.Add(new ConfigError("Required", P(name), "required")); return null; }
            if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { _errors.Add(new ConfigError("Type", P(name), "true or false is required")); return null; }
            return v.GetBoolean();
        }

        public void Error(string code, string name, string message) => _errors.Add(new ConfigError(code, name.Length == 0 ? Path : P(name), message));
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------
    // root
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------

    static StrategyConfig? ReadRoot(JsonElement root, string canonical, List<ConfigError> errors)
    {
        var o = Obj.Open(root, "$", errors, "schema", "strategyId", "version", "enabled", "sourceModel", "entry", "exit", "filters", "instrument", "execution", "quantity", "capital", "position", "risk", "exits", "fees");
        if (o is null) return null;

        var schema = o.Str("schema");
        if (schema is not null && schema != Schema) o.Error("Value", "schema", $"must be '{Schema}'");
        var id = o.Str("strategyId");
        if (id is not null && !IdPattern().IsMatch(id)) o.Error("Value", "strategyId", "1-32 characters: a letter first, then letters, digits, '_' or '-'");
        var version = o.Int("version");
        if (version is < 1) o.Error("Value", "version", "must be >= 1");
        var enabled = o.Bool("enabled");
        var source = o.Str("sourceModel");
        if (source is not null && source != MetricRegistry.SourceModel) o.Error("Value", "sourceModel", $"only '{MetricRegistry.SourceModel}' is supported; the fixed-13,650 frame and the NPT1 M1 values are never mixed");

        // entry
        GroupSpec? buyCall = null, buyPut = null;
        var entry = o.Child("entry", true, "buyCall", "buyPut");
        if (entry is not null)
        {
            if (!entry.Has("buyCall") && !entry.Has("buyPut")) entry.Error("Required", "", "at least one of buyCall / buyPut is required");
            if (entry.Raw("buyCall") is { } c) buyCall = ReadTree(c, $"{entry.Path}/buyCall", errors);
            if (entry.Raw("buyPut") is { } p) buyPut = ReadTree(p, $"{entry.Path}/buyPut", errors);
        }

        // exit
        GroupSpec? exitConditions = null;
        var exit = o.Child("exit", true, "conditions", "onOppositeEntrySignal");
        if (exit is not null)
        {
            if (exit.Raw("conditions") is { } ec) exitConditions = ReadTree(ec, $"{exit.Path}/conditions", errors);
            var opp = exit.Str("onOppositeEntrySignal");
            if (opp is not null && opp != "Exit") exit.Error("Value", "onOppositeEntrySignal", "only 'Exit' is allowed: an opposite signal exits, there is no automatic reversal");
        }

        // filters
        EntryFilters? filters = null;
        var f = o.Child("filters", true, "entryFrom", "entryBefore", "requireFreshVix");
        if (f is not null)
        {
            var from = ReadTime(f, "entryFrom");
            var before = ReadTime(f, "entryBefore");
            var fresh = f.Has("requireFreshVix") ? f.Bool("requireFreshVix") : false;
            if (from is { } a && a < EarliestEntry) f.Error("Value", "entryFrom", "entries begin at 09:30:00 IST at the earliest");
            if (before is { } b && b > LatestEntryCutoff) f.Error("Value", "entryBefore", "entries cease at 15:00:00 IST at the latest");
            if (from is { } a2 && before is { } b2 && a2 >= b2) f.Error("Value", "entryBefore", "must be later than entryFrom");
            if (from is { } fa && before is { } fb && fresh is { } ff) filters = new EntryFilters(fa, fb, ff);
        }

        // instrument
        PremiumRange? premium = null;
        var ins = o.Child("instrument", true, "basket", "premium", "rank");
        if (ins is not null)
        {
            var basket = ins.Str("basket");
            if (basket is not null && basket != "SignalTimeAtmPm2") ins.Error("Value", "basket", "only 'SignalTimeAtmPm2' (the signal-time exact ATM±2 basket) is supported");
            var rank = ins.Str("rank");
            if (rank is not null && rank != RankRule) ins.Error("Value", "rank", $"only '{RankRule}' is supported");
            var prem = ins.Child("premium", true, "min", "max");
            if (prem is not null)
            {
                var min = prem.Num("min"); var max = prem.Num("max");
                if (min is <= 0d) prem.Error("Value", "min", "must be > 0");
                if (min is { } mn && max is { } mx)
                {
                    if (mx <= mn) prem.Error("Value", "max", "must be greater than min");
                    else if (mn > 0d) premium = new PremiumRange(mn, mx);
                }
            }
        }

        // execution
        ExecutionPolicy? execution = null;
        var ex = o.Child("execution", true, "fillQuote", "entryPrice", "exitPrice", "slippage", "latencyMs", "maxQuoteAgeMs", "maxSpread", "entryOrderLifetimeMs", "maxProcessingLagMs", "fillMode");
        if (ex is not null)
        {
            var fq = ex.Str("fillQuote");
            FillQuoteRule? rule = fq switch { "FirstAfterDecision" => FillQuoteRule.FirstAfterDecision, "AsOfEligibleBarEnd" => FillQuoteRule.AsOfEligibleBarEnd, _ => null };
            if (fq is not null && rule is null) ex.Error("Value", "fillQuote", "must be 'FirstAfterDecision' or 'AsOfEligibleBarEnd' (both require a strictly newer quote than the decision)");
            var ep = ex.Str("entryPrice"); if (ep is not null && ep != "Ask") ex.Error("Value", "entryPrice", "entries are filled at the executable 'Ask' only");
            var xp = ex.Str("exitPrice"); if (xp is not null && xp != "Bid") ex.Error("Value", "exitPrice", "exits are filled at the executable 'Bid' only");
            double? se = null, sx = null;
            var sl = ex.Child("slippage", true, "entry", "exit");
            if (sl is not null)
            {
                se = sl.Num("entry"); sx = sl.Num("exit");
                if (se is < 0d) sl.Error("Value", "entry", "must be >= 0 (conservative: it always worsens the price)");
                if (sx is < 0d) sl.Error("Value", "exit", "must be >= 0");
            }

            var latency = ex.Int("latencyMs"); if (latency is < 0) ex.Error("Value", "latencyMs", "must be >= 0");
            var maxAge = ex.Int("maxQuoteAgeMs"); if (maxAge is <= 0) ex.Error("Value", "maxQuoteAgeMs", "must be > 0");
            var lifetime = ex.Int("entryOrderLifetimeMs");
            if (lifetime is { } lt && latency is { } lat && lt <= lat) ex.Error("Value", "entryOrderLifetimeMs", "must exceed latencyMs");
            var lag = ex.Int("maxProcessingLagMs"); if (lag is <= 0) ex.Error("Value", "maxProcessingLagMs", "must be > 0");
            double? spreadAbs = null, spreadFrac = null;
            var sp = ex.Child("maxSpread", false, "absolute", "fraction");
            if (sp is not null)
            {
                if (!sp.Has("absolute") && !sp.Has("fraction")) sp.Error("Required", "", "absolute and/or fraction required");
                if (sp.Has("absolute")) { spreadAbs = sp.Num("absolute"); if (spreadAbs is <= 0d) sp.Error("Value", "absolute", "must be > 0"); }
                if (sp.Has("fraction")) { spreadFrac = sp.Num("fraction"); if (spreadFrac is <= 0d or > 1d) sp.Error("Value", "fraction", "must be in (0, 1]"); }
            }

            var fm = ex.Str("fillMode");
            if (fm is not null && fm != "AllOrNone") ex.Error("Value", "fillMode", "only 'AllOrNone' is allowed until the size units and lot-multiple behaviour of the feed are verified (partial fills are never inferred from Level-1 quotes)");
            if (rule is { } r && se is { } e1 && sx is { } e2 && latency is { } l2 && maxAge is { } m2 && lifetime is { } lt2 && lag is { } lg && l2 >= 0 && m2 > 0 && lt2 > l2 && lg > 0 && e1 >= 0d && e2 >= 0d)
                execution = new ExecutionPolicy(r, e1, e2, l2, m2, lt2, lg, spreadAbs, spreadFrac, FillMode.AllOrNone);
        }

        // sizing, capital, position
        var lots = o.Child("quantity", true, "lots")?.Int("lots"); if (lots is < 1) errors.Add(new ConfigError("Value", "$/quantity/lots", "must be >= 1"));
        var capital = o.Child("capital", true, "virtualCapital")?.Num("virtualCapital"); if (capital is <= 0d) errors.Add(new ConfigError("Value", "$/capital/virtualCapital", "must be > 0 (explicit configuration, never an engine default)"));
        var maxConc = o.Child("position", true, "maxConcurrent")?.Int("maxConcurrent"); if (maxConc is < 1) errors.Add(new ConfigError("Value", "$/position/maxConcurrent", "must be >= 1"));

        // risk (all mandatory)
        RiskPolicy? risk = null;
        var rk = o.Child("risk", true, "maxDailyLoss", "maxDailyTrades", "onBreach");
        if (rk is not null)
        {
            var loss = rk.Num("maxDailyLoss"); if (loss is <= 0d) rk.Error("Value", "maxDailyLoss", "must be > 0");
            var trades = rk.Int("maxDailyTrades"); if (trades is < 1) rk.Error("Value", "maxDailyTrades", "must be >= 1");
            LiquidationPolicy? liq = null;
            var ob = rk.Child("onBreach", true, "blockEntries", "liquidation");
            if (ob is not null)
            {
                var block = ob.Bool("blockEntries"); if (block is false) ob.Error("Value", "blockEntries", "must be true: a breach always blocks new entries");
                var l = ob.Str("liquidation");
                liq = l switch { "None" => LiquidationPolicy.None, "ExitOpenPositions" => LiquidationPolicy.ExitOpenPositions, _ => null };
                if (l is not null && liq is null) ob.Error("Value", "liquidation", "must be 'None' or 'ExitOpenPositions' (the liquidation behaviour is stated explicitly)");
            }

            if (loss is > 0d && trades is >= 1 && liq is { } lq) risk = new RiskPolicy(loss.Value, trades.Value, lq);
        }

        // optional exits (disabled by default)
        var exits = OptionalExits.None;
        if (o.Has("exits"))
        {
            var xo = o.Child("exits", false, "stopLoss", "target", "trailing", "maxHoldBars");
            if (xo is not null)
            {
                int? hold = null;
                if (xo.Has("maxHoldBars")) { hold = xo.Int("maxHoldBars"); if (hold is < 1) xo.Error("Value", "maxHoldBars", "must be >= 1 (completed bars)"); }
                exits = new OptionalExits(ReadThresholdExit(xo, "stopLoss"), ReadThresholdExit(xo, "target"), ReadThresholdExit(xo, "trailing"), hold is >= 1 ? hold : null);
            }
        }

        var fee = o.Child("fees", true, "schedule")?.Str("schedule");
        if (fee is not null && (fee.Length is 0 or > 64)) errors.Add(new ConfigError("Value", "$/fees/schedule", "1-64 characters"));

        if (exitConditions is null && !exits.Any && errors.Count == 0) errors.Add(new ConfigError("NoExitMechanism", "$/exit", "a strategy needs at least one exit mechanism: exit.conditions or one of stopLoss / target / trailing / maxHoldBars"));

        if (errors.Count > 0 || id is null || version is not { } ver || enabled is not { } en || filters is null || premium is null || execution is null || lots is not { } lo || capital is not { } cap || maxConc is not { } mc || risk is null || fee is null)
            return null;

        return new StrategyConfig(new StrategyIdentity(id, ver, CanonicalJson.Sha256Hex(canonical)), en, MetricRegistry.SourceModel, buyCall, buyPut, exitConditions, OnOppositeEntrySignal.Exit,
            filters, premium, execution, lo, cap, mc, risk, exits, fee, canonical);
    }

    static TimeOnly? ReadTime(Obj o, string name)
    {
        var text = o.Str(name);
        if (text is null) return null;
        if (!TimeOnly.TryParseExact(text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) { o.Error("Value", name, "must be HH:mm:ss (IST)"); return null; }
        return t;
    }

    static ThresholdExit? ReadThresholdExit(Obj parent, string name)
    {
        if (!parent.Has(name)) return null;
        var o = parent.Child(name, false, "type", "value");
        if (o is null) return null;
        var type = o.Str("type");
        var value = o.Num("value");
        DistanceKind? kind = type switch { "Points" => DistanceKind.Points, "Percent" => DistanceKind.Percent, _ => null };
        if (type is not null && kind is null) o.Error("Value", "type", "must be 'Points' or 'Percent'");
        if (value is <= 0d) o.Error("Value", "value", "must be > 0");
        return kind is { } k && value is > 0d ? new ThresholdExit(k, value.Value) : null;
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------
    // conditions
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------

    static GroupSpec? ReadTree(JsonElement element, string path, List<ConfigError> errors)
    {
        var leaves = 0;
        var node = ReadNode(element, path, 1, errors, ref leaves);
        if (node is GroupSpec g) return g;
        if (node is not null) errors.Add(new ConfigError("Type", path, "the root of a condition tree must be a group { \"op\": \"AND|OR\", \"items\": [...] }"));
        return null;
    }

    static ConditionSpec? ReadNode(JsonElement element, string path, int depth, List<ConfigError> errors, ref int leaves)
    {
        if (element.ValueKind != JsonValueKind.Object) { errors.Add(new ConfigError("Type", path, "an object is required")); return null; }
        var hasOp = element.TryGetProperty("op", out _);
        var hasCross = element.TryGetProperty("cross", out _);
        if (hasOp && hasCross) { errors.Add(new ConfigError("Value", path, "a node is either a group (op) or a crossover (cross), not both")); return null; }
        if (hasOp) return ReadGroup(element, path, depth, errors, ref leaves);
        leaves++;
        if (leaves > MaxLeavesPerTree) { if (leaves == MaxLeavesPerTree + 1) errors.Add(new ConfigError("Limit", path, $"a condition tree has at most {MaxLeavesPerTree} leaf conditions")); return null; }
        return hasCross ? ReadCross(element, path, errors) : ReadCompare(element, path, errors);
    }

    static GroupSpec? ReadGroup(JsonElement element, string path, int depth, List<ConfigError> errors, ref int leaves)
    {
        var o = Obj.Open(element, path, errors, "op", "items");
        if (o is null) return null;
        if (depth > MaxConditionDepth) { errors.Add(new ConfigError("Limit", path, $"condition groups nest at most {MaxConditionDepth} levels")); return null; }
        var op = o.Str("op");
        if (op is not null && op is not ("AND" or "OR")) o.Error("Value", "op", "must be 'AND' or 'OR'");
        if (!element.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) { o.Error("Type", "items", "an array is required"); return null; }
        var list = new List<ConditionSpec>();
        var i = 0;
        var ok = op is "AND" or "OR";
        foreach (var item in items.EnumerateArray())
        {
            var child = ReadNode(item, $"{path}/items/{i++}", depth + 1, errors, ref leaves);
            if (child is null) ok = false; else list.Add(child);
        }

        if (list.Count == 0 && ok) { o.Error("Value", "items", "a group needs at least one condition"); ok = false; }
        return ok ? new GroupSpec(op == "AND", list) : null;
    }

    static readonly Dictionary<string, Comparison> Comparisons = new(StringComparer.Ordinal)
    {
        [">"] = Comparison.Gt, ["<"] = Comparison.Lt, [">="] = Comparison.Ge, ["<="] = Comparison.Le, ["=="] = Comparison.Eq, ["!="] = Comparison.Ne, ["between"] = Comparison.Between,
    };

    static MetricSpec? ReadMetric(Obj o, string name)
    {
        var id = o.Str(name);
        if (id is null) return null;
        if (!MetricRegistry.TryGet(id, out var spec)) { o.Error("Metric", name, $"unknown metric '{id}' (the registry is fixed; see MetricRegistry)"); return null; }
        return spec;
    }

    static SummaryWindowId? ReadWindow(Obj o, string name, SummaryWindowId? fallback = null)
    {
        if (fallback is not null && !o.Has(name)) return fallback;
        var text = o.Str(name);
        if (text is null) return null;
        if (!SummaryWindows.TryParse(text, out var w)) { o.Error("Window", name, "must be one of \"5\", \"10\", \"50\", \"100\", \"Day\""); return null; }
        return w;
    }

    static ConditionSpec? ReadCompare(JsonElement element, string path, List<ConfigError> errors)
    {
        var o = Obj.Open(element, path, errors, "metric", "window", "cmp", "value", "metricB", "windowB");
        if (o is null) return null;
        var metric = ReadMetric(o, "metric");
        var window = ReadWindow(o, "window");
        var cmpText = o.Str("cmp");
        Comparison? cmp = cmpText is not null && Comparisons.TryGetValue(cmpText, out var c) ? c : null;
        if (cmpText is not null && cmp is null) o.Error("Value", "cmp", "must be one of > < >= <= == != between");
        var hasValue = o.Has("value");
        var hasB = o.Has("metricB");
        if (hasValue == hasB) { o.Error("Value", "", "exactly one of value (constant) or metricB (metric vs metric) is required"); return null; }
        if (cmp is { } cm && metric is not null && cm is Comparison.Eq or Comparison.Ne && !metric.Discrete) { o.Error("Value", "cmp", $"'==' and '!=' are only allowed on discrete metrics; '{metric.Id}' is continuous (use a range or an inequality)"); return null; }

        if (hasB)
        {
            var metricB = ReadMetric(o, "metricB");
            var windowB = ReadWindow(o, "windowB", window);
            if (cmp == Comparison.Between) { o.Error("Value", "cmp", "'between' needs constant bounds, not metricB"); return null; }
            if (metric is null || metricB is null || window is null || windowB is null || cmp is null) return null;
            if (!MetricRegistry.AreComparable(metric, metricB, out var why)) { o.Error("Unit", "metricB", $"'{metric.Id}' and '{metricB.Id}' are not comparable: {why}"); return null; }
            if (metric.Handle == metricB.Handle && window == windowB) { o.Error("Value", "metricB", "a metric cannot be compared with itself in the same window"); return null; }
            return new MetricPairSpec(metric, window.Value, cmp.Value, metricB, windowB.Value);
        }

        double a = 0d, b = 0d;
        if (cmp == Comparison.Between)
        {
            var raw = o.Raw("value");
            if (raw is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() != 2 || !arr[0].TryGetDouble(out a) || !arr[1].TryGetDouble(out b) || !double.IsFinite(a) || !double.IsFinite(b)) { o.Error("Type", "value", "between needs [lower, upper] finite numbers"); return null; }
            if (a >= b) { o.Error("Value", "value", "lower must be less than upper"); return null; }
        }
        else
        {
            var v = o.Num("value");
            if (v is null) return null;
            a = b = v.Value;
        }

        if (metric is null || window is null || cmp is null) return null;
        if (metric.Unit == MetricUnit.Flag && (a is not (0d or 1d) || cmp is not (Comparison.Eq or Comparison.Ne))) { o.Error("Value", "value", $"'{metric.Id}' is a 0/1 flag: compare it with == or != against 0 or 1"); return null; }
        return new ThresholdSpec(metric, window.Value, cmp.Value, a, b);
    }

    static ConditionSpec? ReadCross(JsonElement element, string path, List<ConfigError> errors)
    {
        var o = Obj.Open(element, path, errors, "cross", "metric", "window", "level", "levelMetric", "levelWindow");
        if (o is null) return null;
        var dirText = o.Str("cross");
        CrossDirection? dir = dirText switch { "Up" => CrossDirection.Up, "Down" => CrossDirection.Down, _ => null };
        if (dirText is not null && dir is null) o.Error("Value", "cross", "must be 'Up' or 'Down'");
        var metric = ReadMetric(o, "metric");
        var window = ReadWindow(o, "window");
        var hasLevel = o.Has("level");
        var hasLevelMetric = o.Has("levelMetric");
        if (hasLevel == hasLevelMetric) { o.Error("Value", "", "exactly one of level (constant) or levelMetric is required"); return null; }
        if (metric is { Discrete: true }) { o.Error("Value", "metric", "a crossover of a discrete (flag/count) metric is not meaningful"); return null; }
        if (hasLevel)
        {
            var level = o.Num("level");
            if (dir is null || metric is null || window is null || level is null) return null;
            return new CrossSpec(dir.Value, metric, window.Value, level, null, window.Value);
        }

        var levelMetric = ReadMetric(o, "levelMetric");
        var levelWindow = ReadWindow(o, "levelWindow", window);
        if (dir is null || metric is null || window is null || levelMetric is null || levelWindow is null) return null;
        if (!MetricRegistry.AreComparable(metric, levelMetric, out var why)) { o.Error("Unit", "levelMetric", $"'{metric.Id}' and '{levelMetric.Id}' are not comparable: {why}"); return null; }
        return new CrossSpec(dir.Value, metric, window.Value, null, levelMetric, levelWindow.Value);
    }
}
