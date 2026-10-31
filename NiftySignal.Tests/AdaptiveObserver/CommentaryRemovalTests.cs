using System.Reflection;
using NiftySignal.Dashboard.Services;
using NiftySignal.Host;
using NiftySignal.Notifications;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// Structural regression for the removal of the Running Market Commentary (docs/COMMENTARY_REMOVAL.md): no active commentary code remains in the Host, the Dashboard or any trading assembly, the NPT1 /
/// paper path has no way to depend on it, and only the historical data types are kept. Trading decisions, fill timing, accounting and recovery are covered by the unchanged NPT1 suites
/// (Npt1ExecutionTests, Npt1EndToEndTests, Npt1FamilyTests, StrategyFrameCodecTests, Postgres trading schema/recovery tests), all of which still pass untouched.
/// </summary>
public class CommentaryRemovalTests
{
    static IEnumerable<string> CommentaryTypes(Assembly a) => a.GetTypes().Where(t => t.FullName!.Contains("Commentary", StringComparison.OrdinalIgnoreCase)).Select(t => t.FullName!);

    [Fact]
    public void HostDashboardAndNotificationAssemblies_ContainNoCommentaryCode()
    {
        Assert.Empty(CommentaryTypes(typeof(AdaptiveObserverWorker).Assembly));
        Assert.Empty(CommentaryTypes(typeof(ToastFactory).Assembly));
        Assert.Empty(CommentaryTypes(typeof(TelegramTextSender).Assembly));
    }

    [Fact]
    public void TradingAndPaperAssemblies_NeverReferenceCommentary()
    {
        foreach (var name in new[] { "NiftySignal.Trading.Core", "NiftySignal.Trading.Live", "NiftySignal.Trading.Signals", "NiftySignal.Trading.Adapters", "NiftySignal.Trading.Recovery", "NiftySignal.Trading.Data", "NiftySignal.Npt1M1", "NiftySignal.Npt1M1.Data" })
        {
            var asm = Assembly.Load(name);
            Assert.Empty(CommentaryTypes(asm));
            Assert.DoesNotContain(asm.GetReferencedAssemblies(), r => r.Name!.Contains("Commentary", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ObserverWorker_NoLongerTakesAnyCommentaryDependency_AndTheStartedPaperGateKeepsItsSidecarInputs()
    {
        var ctor = typeof(AdaptiveObserverWorker).GetConstructors().Single();
        Assert.DoesNotContain(ctor.GetParameters(), p => p.ParameterType.Name.Contains("Commentary", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(typeof(AdaptiveObserverWorker).GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static), m => m.Name == "IsBarVerifiedForPaper");
        Assert.DoesNotContain(typeof(AdaptiveObserverWorker).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).SelectMany(t => t.GetProperties()), p => p.Name.Contains("Commentary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HomePage_AndToastHost_HaveNoCommentarySurface()
    {
        var root = NiftySignal.Tests.Trading.Data.TradingModelTests.RepoRoot();
        foreach (var file in new[] { "Components/Pages/Home.razor", "Components/Dashboard/ToastHost.razor", "Components/Dashboard/AdaptiveObserverPanel.razor", "Components/Dashboard/ResearchHeader.razor" })
            Assert.DoesNotContain("commentary", File.ReadAllText(Path.Combine(root, "NiftySignal.Dashboard", file.Replace('/', Path.DirectorySeparatorChar))), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(root, "NiftySignal.Dashboard", "Components", "Dashboard", "CommentaryPanel.razor")));
    }

    [Fact]
    public void HistoricalCommentaryEntities_AreKept_SoTheTablesStayCompatible()
    {
        var data = typeof(NiftySignal.AdaptiveObserverData.AdaptiveObserverDbContext).Assembly;
        Assert.Contains("NiftySignal.AdaptiveObserverData.AdaptiveCommentaryEventRow", data.GetTypes().Select(t => t.FullName));
        var model = typeof(NiftySignal.AdaptiveObserverData.AdaptiveObserverDbContext).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("CommentaryEvents", model);
        Assert.Contains("CommentaryRuntime", model);
        Assert.Contains("CommentaryNotificationJobs", model);
    }
}
