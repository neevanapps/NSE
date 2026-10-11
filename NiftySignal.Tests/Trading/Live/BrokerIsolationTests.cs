using System.Reflection;
using NiftySignal.Host.PaperLive;
using NiftySignal.Tests.Trading.Core;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Live;

namespace NiftySignal.Tests.Trading.Live;

/// <summary>
/// RELEASE BLOCKER tests: this project is PAPER ONLY. Nothing in the live paper path — the engine, the strategy contract, the persistence, the recovery, the bridge and its Host glue —
/// may be able to place a broker order. The first test proves it structurally over the whole dependency graph; the others prove that every configuration that asks for real orders is refused.
/// </summary>
public class BrokerIsolationTests
{
    private static readonly string[] PaperPathAssemblies =
    {
        "NiftySignal.Trading.Core", "NiftySignal.Trading.Adapters", "NiftySignal.Trading.Recovery", "NiftySignal.Trading.Data", "NiftySignal.Trading.Signals", "NiftySignal.Trading.Live",
    };

    /// <summary>Product assemblies that can talk to a broker, place orders, call the network or send messages. None may be reachable from the paper path.</summary>
    private static readonly string[] ForbiddenAssemblies =
    {
        "NiftySignal.Ingestion", "NiftySignal.Execution", "NiftySignal.Notifications", "NiftySignal.Rules", "NiftySignal.Persistence", "NiftySignal.Host", "NiftySignal.Dashboard",
        "Microsoft.AspNetCore.SignalR.Client", "Websocket.Client",
    };

    private static readonly string[] ForbiddenWords = { "PlaceOrder", "SubmitOrder", "BrokerOrder", "FlatTrade", "NorenApi", "ModifyOrder", "CancelBrokerOrder", "HttpClient" };

    private static IReadOnlyDictionary<string, Assembly> Closure(IEnumerable<string> roots)
    {
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var queue = new Queue<AssemblyName>(roots.Select(r => new AssemblyName(r)));
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (name.Name is null || seen.ContainsKey(name.Name))
            {
                continue;
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.Load(name);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            seen[name.Name] = assembly;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                queue.Enqueue(reference);
            }
        }

        return seen;
    }

    [Fact]
    public void TheWholePaperPathDependencyGraph_HasNoPathToABrokerAnOrderEndpointOrTheNetwork()
    {
        var closure = Closure(PaperPathAssemblies);

        foreach (var forbidden in ForbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden, closure.Keys);
        }

        // the assemblies of the paper path themselves never reference the network stack (the database driver does, one level below, for PostgreSQL only)
        foreach (var name in PaperPathAssemblies)
        {
            Assert.DoesNotContain(closure[name].GetReferencedAssemblies(), r => r.Name!.StartsWith("System.Net", StringComparison.Ordinal) && r.Name != "System.Net.Primitives");       // Primitives = exception and address types only (the database error handling), no I/O
        }

        // and nothing the paper path itself declares mentions an order endpoint
        foreach (var name in PaperPathAssemblies)
        {
            foreach (var type in closure[name].GetTypes())
            {
                var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                var text = type.FullName + " " + string.Join(" ", members.Select(m => m.Name));
                foreach (var word in ForbiddenWords)
                {
                    Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{type.FullName} mentions '{word}'");
                }

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    Assert.DoesNotContain("System.Net", field.FieldType.Namespace ?? string.Empty, StringComparison.Ordinal);
                }

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    Assert.DoesNotContain("System.Net.Http", method.ReturnType.FullName ?? string.Empty, StringComparison.Ordinal);
                    Assert.DoesNotContain(method.GetParameters(), p => (p.ParameterType.Namespace ?? string.Empty).StartsWith("System.Net", StringComparison.Ordinal));
                }
            }
        }
    }

    [Fact]
    public void TheHostGlueOfThePaperPath_HasNoBrokerOrNetworkDependency()
    {
        var types = typeof(PaperLiveBridge).Assembly.GetTypes().Where(t => t.Namespace == typeof(PaperLiveBridge).Namespace).ToList();
        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            var referenced = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(f => f.FieldType)
                .Concat(type.GetConstructors().SelectMany(c => c.GetParameters().Select(p => p.ParameterType)))
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));
            foreach (var t in referenced)
            {
                var assembly = t.Assembly.GetName().Name!;
                Assert.DoesNotContain(assembly, ForbiddenAssemblies.Where(f => f != "NiftySignal.Host"));
                Assert.False((t.Namespace ?? string.Empty).StartsWith("System.Net", StringComparison.Ordinal), $"{type.Name} uses {t.FullName}");
            }
        }
    }

    [Fact]
    public void EveryRequestForRealOrders_IsRefusedByConfigurationValidation()
    {
        var broker = PaperKit.Config(1_000_000m) with { Execution = new ExecutionConfig { Adapter = ExecutionAdapter.Broker } };
        Assert.Contains(broker.Validate(), e => e.Contains("real-order / broker execution is not available", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => new NiftySignal.Trading.Core.Trading.TradingEngine(broker, Clock.Day, Kit.StandardUniverse().ToList(), new Dictionary<string, NiftySignal.Trading.Core.Strategies.IStrategyFamily>()));

        var realOrder = PaperKit.Config(1_000_000m) with { Risk = new RiskProfile { Name = RiskProfile.RealOrderName } };
        Assert.NotEmpty(realOrder.Risk.StartBlockers());
        Assert.Throws<ArgumentException>(() => new NiftySignal.Trading.Core.Trading.TradingEngine(realOrder, Clock.Day, Kit.StandardUniverse().ToList(), new Dictionary<string, NiftySignal.Trading.Core.Strategies.IStrategyFamily>()));

        // the live path builds PaperLiveV1 only, whatever the options say
        var options = new PaperLiveOptions { Enabled = true, Capital = 1_000_000m, BrokeragePerOrder = 20m, OptionTickSize = 0.05m };
        var config = PaperLiveSetup.Config(options, PaperLiveSetup.Strategies(options));
        Assert.Equal(RiskProfile.PaperLiveV1Name, config.Risk.Name);
        Assert.Equal(ExecutionAdapter.Simulated, config.Execution.Adapter);
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void ThePaperPathIsOffInSource_AndTheBridgeDoesNothingUnlessEnabled()
    {
        var defaults = new PaperLiveOptions();
        Assert.False(defaults.Enabled);
        Assert.False(defaults.PlumbingFamily.Enabled);
        Assert.Empty(PaperLiveSetup.Strategies(defaults));
        Assert.Empty(PaperLiveSetup.Strategies(new PaperLiveOptions { Enabled = true, Capital = 1m }));            // enabled alone registers no strategy

        var shipped = File.ReadAllText(Path.Combine(TradingRepo.Root(), "NiftySignal.Host", "appsettings.json"));
        Assert.Matches(@"""PaperLive""\s*:\s*\{\s*""Enabled""\s*:\s*false\s*\}", shipped);
    }
}

internal static class TradingRepo
{
    public static string Root() => NiftySignal.Tests.Trading.Data.TradingModelTests.RepoRoot();
}
