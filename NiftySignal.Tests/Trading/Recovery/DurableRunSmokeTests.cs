using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Recovery;
using Xunit.Abstractions;

namespace NiftySignal.Tests.Trading.Recovery;

public class DurableRunSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void Uninterrupted_DurableRun_MatchesThePlainRun_OnAllNineChannels()
    {
        var rows = RecoveryKit.ShortRows();
        var freeze = SyntheticSession.Freeze(rows);
        var spec = RecoveryKit.Spec(freeze);
        var plain = RecoveryKit.Plain(spec, rows);

        var durable = RecoveryKit.Uninterrupted(spec, rows);

        Assert.Equal(RecoveryKit.ChannelHashes(plain), RecoveryKit.ChannelHashes(durable.Result));
        Assert.True(durable.Snapshot.Completed);
        Assert.Equal(plain.Outputs, durable.Snapshot.Outputs.Count);
        var byType = durable.Snapshot.Outputs.GroupBy(o => o.Type).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}");
        output.WriteLine($"rows={rows.Count} steps={durable.Snapshot.Steps.Count} outputs={durable.Snapshot.Outputs.Count} fills={plain.Fills} commits={durable.Store.CommitCount}");
        output.WriteLine(string.Join(" ", byType));
        foreach (var kind in durable.Snapshot.Steps.GroupBy(s => s.Kind))
        {
            output.WriteLine($"{kind.Key}: {kind.Count()}");
        }
    }
}
