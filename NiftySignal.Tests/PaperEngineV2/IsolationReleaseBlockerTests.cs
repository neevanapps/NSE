using System.Reflection;
using System.Text.RegularExpressions;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>
/// Phase 6f RELEASE BLOCKERS for the V2 shadow engine: it can never place a broker order, never reach the network from its engine path, never be referenced by the NPT1 / observer / Host code (so NPT1 cannot
/// depend on it), has no live-authorisation concept, and is disabled by default.
/// </summary>
public class IsolationReleaseBlockerTests
{
    static readonly string[] EngineRoots =
    [
        "NiftySignal.PaperEngineV2.Core", "NiftySignal.PaperEngineV2.Frames", "NiftySignal.PaperEngineV2.Execution", "NiftySignal.PaperEngineV2.Data",
    ];

    static readonly string[] Forbidden =
    [
        "NiftySignal.Ingestion", "NiftySignal.Execution", "NiftySignal.Notifications", "NiftySignal.Host", "NiftySignal.Dashboard", "NiftySignal.Trading.Live", "NiftySignal.Trading.Adapters",
        "NiftySignal.Trading.Signals", "NiftySignal.Trading.Recovery", "NiftySignal.Trading.Data", "NiftySignal.Npt1M1", "NiftySignal.AdaptiveObserver", "NiftySignal.Persistence", "NiftySignal.Rules",
        "Microsoft.AspNetCore.SignalR.Client", "Websocket.Client",
    ];

    static IReadOnlyDictionary<string, Assembly> Closure(IEnumerable<string> roots)
    {
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var queue = new Queue<AssemblyName>(roots.Select(r => new AssemblyName(r)));
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (name.Name is null || seen.ContainsKey(name.Name)) continue;
            Assembly assembly;
            try { assembly = Assembly.Load(name); }
            catch (FileNotFoundException) { continue; }
            seen[name.Name] = assembly;
            foreach (var r in assembly.GetReferencedAssemblies()) queue.Enqueue(r);
        }

        return seen;
    }

    [Fact]
    public void EnginePath_TransitiveClosure_ReachesNoBrokerNetworkHostOrNpt1Assembly()
    {
        var closure = Closure(EngineRoots);
        Assert.All(EngineRoots, r => Assert.True(closure.ContainsKey(r)));
        Assert.Empty(closure.Keys.Intersect(Forbidden, StringComparer.Ordinal));
        // the database driver may pull System.Net.Http in transitively; the engine's OWN assemblies must not use it directly
        foreach (var r in EngineRoots) Assert.DoesNotContain(closure[r].GetReferencedAssemblies(), a => a.Name == "System.Net.Http");
    }

    [Fact]
    public void AlertsPath_ReachesNoBrokerHostOrNpt1Assembly_OnlyTheTextSenderOverTheNetwork()
    {
        var closure = Closure(["NiftySignal.PaperEngineV2.Alerts"]);
        var banned = Forbidden.Where(f => f != "NiftySignal.Notifications").ToArray();
        Assert.Empty(closure.Keys.Intersect(banned, StringComparer.Ordinal));
    }

    [Fact]
    public void NoNpt1ObserverOrHostProject_ReferencesAV2Project_ExceptTheHostToTheRuntimeOnly()
    {
        var root = TradingModelTests.RepoRoot();
        foreach (var proj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var name = Path.GetFileNameWithoutExtension(proj);
            if (name.StartsWith("NiftySignal.PaperEngineV2", StringComparison.Ordinal) || name == "NiftySignal.Tests") continue;
            var text = File.ReadAllText(proj);
            if (name is "NiftySignal.Dashboard") continue;                       // Phase 7: a READ-ONLY reference to the Data project, guarded by HomeV2Tests
            if (name is "NiftySignal.Host")
            {
                // The owner-authorised Host integration references the Runtime project and nothing else of V2.
                var refs = Regex.Matches(text, @"NiftySignal\.PaperEngineV2\.[A-Za-z]+").Select(m => m.Value).Distinct().ToArray();
                Assert.Equal(["NiftySignal.PaperEngineV2.Runtime"], refs);
                continue;
            }

            Assert.DoesNotContain("PaperEngineV2", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Host_MentionsV2OnlyInTheBridge_TheHookSites_AndADisabledByDefaultConfig()
    {
        var host = Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.Host");
        var allowed = new[] { "PaperEngineV2Bridge.cs", "AdaptiveObserverWorker.cs", "Program.cs", "NiftySignal.Host.csproj", "appsettings.json" };
        foreach (var file in Directory.EnumerateFiles(host, "*", SearchOption.AllDirectories).Where(f => (f.EndsWith(".cs") || f.EndsWith(".json") || f.EndsWith(".csproj"))
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.EndsWith("appsettings.Local.json", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            var mentions = text.Contains("PaperEngineV2", StringComparison.Ordinal) || text.Contains("pev2", StringComparison.OrdinalIgnoreCase);
            if (!allowed.Contains(Path.GetFileName(file))) Assert.False(mentions, $"{Path.GetFileName(file)} mentions V2 but is not an approved V2 file");
        }

        // V2 is DISABLED by default: the source config has Enabled=false, the options binder's missing-value default is false, and the (owner-local) Development file does not turn it on.
        var settings = File.ReadAllText(Path.Combine(host, "appsettings.json"));
        Assert.Matches(@"""PaperEngineV2""\s*:\s*\{\s*""Enabled""\s*:\s*false", settings);
        Assert.False(NiftySignal.Host.PaperEngineV2.PaperEngineV2Bridge.Bind(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Enabled);
        Assert.DoesNotContain("PaperEngineV2", File.ReadAllText(Path.Combine(host, "appsettings.Development.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimePath_ReachesNoBrokerIngestionNpt1OrLiveAssembly_AndNoDirectHttp()
    {
        var closure = Closure(["NiftySignal.PaperEngineV2.Runtime"]);
        var banned = Forbidden.Where(f => f is not ("NiftySignal.AdaptiveObserver" or "NiftySignal.Persistence" or "NiftySignal.Notifications")).ToArray();
        Assert.Empty(closure.Keys.Intersect(banned, StringComparer.Ordinal));
        Assert.DoesNotContain(closure["NiftySignal.PaperEngineV2.Runtime"].GetReferencedAssemblies(), a => a.Name == "System.Net.Http");
    }
    [Fact]
    public void V2Sources_ContainNoOrderSubmissionBrokerOrLiveAuthorisationVocabulary()
    {
        var banned = new[] { "PlaceOrder", "SubmitOrder", "BrokerOrder", "FlatTrade", "Flattrade", "NorenApi", "ModifyOrder", "HttpClient", "LiveAuthor", "AllowLive", "LiveStrategy", "SellToOpen", "ShortSell", "WriteOption" };
        var root = TradingModelTests.RepoRoot();
        foreach (var project in new[] { "NiftySignal.PaperEngineV2.Frames", "NiftySignal.PaperEngineV2.Execution", "NiftySignal.PaperEngineV2.Data", "NiftySignal.PaperEngineV2.Alerts", "NiftySignal.PaperEngineV2.Runtime" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
            {
                var text = File.ReadAllText(file);
                foreach (var word in banned) Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} contains '{word}'");
            }
    }

    [Fact]
    public void StrategyJson_CanNeverCarryALiveFlag_OrASellSide()
    {
        foreach (var key in new[] { "live", "liveEnabled", "liveAuthorized", "broker", "sell", "sellToOpen", "short", "writeOptions" })
        {
            var json = ConfigTests.Mutate(o => o[key] = true);
            var r = StrategyConfigParser.Parse(json);
            Assert.False(r.IsValid, $"'{key}' must be refused");
            Assert.Null(r.Config);
        }
    }

    [Fact]
    public void ExistingNpt1GateAndFrozenFeeSchedule_AreStillPresent_AndUnchangedInShape()
    {
        // The verified-bar gate the commentary removal had to preserve, and the NPT1 frozen fee schedule the V2 costs are measured against.
        Assert.True(NiftySignal.Host.AdaptiveObserverWorker.IsBarVerifiedForPaper(1, false, null));
        Assert.False(NiftySignal.Host.AdaptiveObserverWorker.IsBarVerifiedForPaper(1, true, null));
        Assert.True(NiftySignal.Host.AdaptiveObserverWorker.IsBarVerifiedForPaper(4, false, 5));
        Assert.False(NiftySignal.Host.AdaptiveObserverWorker.IsBarVerifiedForPaper(5, false, 5));
        var frozen = NiftySignal.Trading.Core.Costs.NseIndexOptionsCostSchedules.Npt1FrozenV1(new NiftySignal.Trading.Core.Costs.FlatBrokerage { Unit = NiftySignal.Trading.Core.Costs.BrokerageUnit.PerFill, Amount = 0m });
        Assert.Equal(("npt1-frozen-fees-v1", 0.0003503m, 0.0015m), (frozen.Version, frozen.ExchangeTransactionRate, frozen.SttSellRate));
    }

    [Fact]
    public void AMigrationOfTheV2SchemaNeverTouchesAnExistingTable()
    {
        var root = TradingModelTests.RepoRoot();
        var sql = File.ReadAllText(Path.Combine(root, "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql"));
        var schemas = Regex.Matches(sql, @"\b([a-z_][a-z0-9_]*)\.[a-z_""]", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(schemas);
        Assert.All(schemas, x => Assert.Equal("pev2", x));
        Assert.DoesNotContain("trading.", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("public.", sql, StringComparison.Ordinal);
    }
}
