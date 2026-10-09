using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NiftySignal.Trading.Core.Scheduling;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>
/// Static guard that supplements (never replaces) the behavioural determinism tests. It reads the assembly's metadata
/// (member and type references) and fails on references to wall-clock, randomness, machine-state, culture-state and
/// threading primitives. LIMITATIONS: it only sees what Trading.Core's own IL references directly; it cannot see a banned
/// call hidden behind another assembly's helper, reflection or delegates built at run time, culture-sensitive overloads that
/// share a name with safe ones (for example string.ToLower() versus ToLowerInvariant()), or non-determinism that needs no
/// banned API (hash-set iteration order, floating-point summation order, threading through collections). Those risks are
/// covered by the culture, repeated-run, parallel-run and permutation tests.
/// </summary>
public class BannedApiGuardTests
{
    private static readonly Dictionary<string, string[]?> BannedMembers = new()
    {
        ["System.DateTime"] = new[] { "get_Now", "get_UtcNow", "get_Today" },
        ["System.DateTimeOffset"] = new[] { "get_Now", "get_UtcNow" },
        ["System.TimeProvider"] = new[] { "get_System", "GetUtcNow", "GetLocalNow", "GetTimestamp", "CreateTimer" },
        ["System.TimeZoneInfo"] = new[] { "get_Local", "FindSystemTimeZoneById", "GetSystemTimeZones" },
        ["System.Environment"] = new[] { "get_TickCount", "get_TickCount64", "get_MachineName", "get_UserName", "get_CurrentManagedThreadId", "GetEnvironmentVariable", "get_ProcessorCount", "get_NewLine" },
        ["System.Guid"] = new[] { "NewGuid", "CreateVersion7" },
        ["System.Globalization.CultureInfo"] = new[] { "get_CurrentCulture", "get_CurrentUICulture", "set_CurrentCulture", "set_CurrentUICulture", "get_DefaultThreadCurrentCulture" },
        ["System.Threading.Tasks.Task"] = new[] { "Delay", "Run", "Wait", "WaitAll", "WaitAny", "Factory", "get_Factory" },
        ["System.Threading.Tasks.Parallel"] = null,
        ["System.Threading.Thread"] = null,
        ["System.Threading.ThreadPool"] = null,
        ["System.Threading.Timer"] = null,
        ["System.Threading.PeriodicTimer"] = null,
        ["System.Random"] = null,
        ["System.Diagnostics.Stopwatch"] = null,
        ["System.Security.Cryptography.RandomNumberGenerator"] = null,
    };

    public static IReadOnlyList<string> Scan(string assemblyPath)
    {
        var hits = new List<string>();
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        foreach (var handle in md.TypeReferences)
        {
            var tr = md.GetTypeReference(handle);
            var full = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
            if (BannedMembers.TryGetValue(full, out var members) && members is null)
            {
                hits.Add($"type {full}");
            }
        }

        foreach (var handle in md.MemberReferences)
        {
            var mr = md.GetMemberReference(handle);
            if (mr.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            var full = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
            var member = md.GetString(mr.Name);
            if (BannedMembers.TryGetValue(full, out var banned) && (banned is null || banned.Contains(member, StringComparer.Ordinal)))
            {
                hits.Add($"{full}.{member}");
            }
        }

        return hits.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void TradingCoreAssembly_ReferencesNoBannedApi()
    {
        var path = typeof(EngineLoop).Assembly.Location;

        var hits = Scan(path);

        Assert.True(hits.Count == 0, "Banned API references in Trading.Core: " + string.Join(", ", hits));
    }

    [Fact]
    public void TradingSignalsAssembly_ReferencesNoBannedApi_AndDependsOnlyOnCore()
    {
        var assembly = typeof(NiftySignal.Trading.Signals.PlumbingEveryNthBarStrategy).Assembly;

        var hits = Scan(assembly.Location);

        Assert.True(hits.Count == 0, "Banned API references in Trading.Signals: " + string.Join(", ", hits));
        Assert.Equal(
            new[] { "NiftySignal.Trading.Core" },
            assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("NiftySignal.", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public void TradingAdaptersAssembly_ReferencesNoBannedApi_NoDatabase_AndDependsOnlyOnCoreAdaptiveObserverAndDomain()
    {
        var assembly = typeof(NiftySignal.Trading.Adapters.AdaptiveEventTranslator).Assembly;

        var hits = Scan(assembly.Location);

        Assert.True(hits.Count == 0, "Banned API references in Trading.Adapters: " + string.Join(", ", hits));
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.Equal(
            new[] { "NiftySignal.AdaptiveObserver", "NiftySignal.Domain", "NiftySignal.Trading.Core" },
            references.Where(n => n.StartsWith("NiftySignal.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFramework", StringComparison.Ordinal) || r.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_DetectsEachBannedFamily_InAFixtureAssembly()
    {
        var hits = Scan(typeof(BannedApiFixtures).Assembly.Location);

        // The test assembly contains BannedApiFixtures on purpose; the scanner must find every family it uses.
        foreach (var expected in new[]
        {
            "System.DateTimeOffset.get_UtcNow", "System.DateTime.get_Now", "System.Guid.NewGuid", "System.Random",
            "System.Diagnostics.Stopwatch", "System.Threading.Tasks.Task.Delay", "System.Environment.get_TickCount64",
            "System.TimeProvider.get_System", "System.Globalization.CultureInfo.get_CurrentCulture", "System.Threading.Thread",
        })
        {
            Assert.Contains(hits, h => h.Contains(expected, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TradingCoreAssembly_DependsOnlyOnDomainAndTheFramework()
    {
        var references = typeof(EngineLoop).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        var internalReferences = references.Where(r => r.StartsWith("NiftySignal.", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "NiftySignal.Domain" }, internalReferences);
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFramework", StringComparison.Ordinal) || r.StartsWith("Npgsql", StringComparison.Ordinal));
    }
}

/// <summary>Deliberately uses banned APIs so the scanner can prove it detects them. Never called.</summary>
public static class BannedApiFixtures
{
    public static object[] Touch() => new object[]
    {
        DateTimeOffset.UtcNow,
        DateTime.Now,
        Guid.NewGuid(),
        new Random(),
        Stopwatch.StartNew(),
        Task.Delay(1),
        Environment.TickCount64,
        TimeProvider.System,
        CultureInfo.CurrentCulture,
        Thread.CurrentThread,
    };
}
