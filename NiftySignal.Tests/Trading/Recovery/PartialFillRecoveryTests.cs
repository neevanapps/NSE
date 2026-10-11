using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;
using Xunit.Abstractions;

namespace NiftySignal.Tests.Trading.Recovery;

/// <summary>
/// Partial entries and multi-fill exit orders: the synthetic session is regenerated with small, varying displayed quantities so that the level-1
/// liquidity rule fills part of an order and the rest later. A death between the fills of one order must neither duplicate a fill nor charge the
/// per-order brokerage twice (contract section A.18.10; the in-memory limits of <see cref="RecoveryMatrixTests"/> apply).
/// </summary>
public class PartialFillRecoveryTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<SourceTick> Rows = SyntheticSession.Rows(new SyntheticSession.Options
    {
        Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(9, 44)) },
        OptionQtyMin = 60,
        OptionQtyMax = 500,
    });

    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);
    private static readonly DurableRunSpec Spec = RecoveryKit.Spec(Freeze);
    private static readonly Lazy<RecoveryKit.Outcome> Reference = new(() => RecoveryKit.Uninterrupted(Spec, Rows));

    private static IReadOnlyList<(OutputRecord Record, FillRecord Fill)> Fills(RunSnapshot s)
        => s.Outputs.Where(o => o.Type == "FillRecord").Select(o => (o, (FillRecord)OutputCodec.Decode(o.Payload))).ToList();

    [Fact]
    public void TheSessionReallyContainsAPartialEntryAndAMultiFillExitOrder()
    {
        var fills = Fills(Reference.Value.Snapshot);
        var requested = 650;

        Assert.Contains(fills, f => f.Fill.Side == TradeSide.Buy && f.Fill.Quantity < requested);
        Assert.Contains(fills.Where(f => f.Fill.Side == TradeSide.Sell).GroupBy(f => f.Fill.OrderId), g => g.Count() >= 2);
        output.WriteLine($"fills={fills.Count} buys={fills.Count(f => f.Fill.Side == TradeSide.Buy)} sells={fills.Count(f => f.Fill.Side == TradeSide.Sell)} partial-entry-quantities=[{string.Join(",", fills.Where(f => f.Fill.Side == TradeSide.Buy).Select(f => f.Fill.Quantity))}]");
    }

    private static IEnumerable<(string Name, long Step)> Targets()
    {
        var s = Reference.Value.Snapshot;
        var fills = Fills(s);
        yield return ("first-partial-entry", fills.First(f => f.Fill.Side == TradeSide.Buy && f.Fill.Quantity < 650).Record.StepSeq);
        var multi = fills.Where(f => f.Fill.Side == TradeSide.Sell).GroupBy(f => f.Fill.OrderId).First(g => g.Count() >= 2).OrderBy(f => f.Record.Seq).ToList();
        yield return ("first-fill-of-a-multi-fill-exit", multi[0].Record.StepSeq);
        yield return ("second-fill-of-the-same-exit", multi[1].Record.StepSeq);
    }

    [Fact]
    public void ADeathAroundEveryFillOfThoseOrders_RecoversWithNoDuplicateFill_AndTheBrokerageChargedOncePerOrder()
    {
        var referenceFills = Fills(Reference.Value.Snapshot);
        var checkedScenarios = 0;
        foreach (var (name, step) in Targets())
        {
            foreach (var point in new[] { CrashPoint.BeforeCommit, CrashPoint.AfterCommit })
            {
                var store = new InMemoryRunStore();
                RecoveryKit.RunUntilDeath(store, Spec, Rows, new CrashAt(point, 1, (s, _) => s == step));

                var resumed = RecoveryKit.ResumeAndFinish(store, Spec, Rows);

                RecoveryKit.AssertSameOutcome(Reference.Value, resumed);
                var fills = Fills(resumed.Snapshot);
                Assert.Equal(fills.Count, fills.Select(f => f.Fill.FillId).Distinct().Count());
                Assert.Equal(referenceFills.Select(f => f.Fill), fills.Select(f => f.Fill));
                foreach (var order in fills.GroupBy(f => f.Fill.OrderId))
                {
                    Assert.Equal(HarnessSettingsBrokerage, order.Sum(f => f.Fill.Costs.Brokerage));
                }

                Assert.Equal(referenceFills.Sum(f => f.Fill.Costs.Total), fills.Sum(f => f.Fill.Costs.Total));
                checkedScenarios++;
                output.WriteLine($"{name}/{point}: step {step}, fills {fills.Count}, orders {fills.Select(f => f.Fill.OrderId).Distinct().Count()}");
            }
        }

        Assert.Equal(6, checkedScenarios);
    }

    private const decimal HarnessSettingsBrokerage = 20m;
}
