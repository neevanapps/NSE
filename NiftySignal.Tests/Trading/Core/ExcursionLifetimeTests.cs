using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Reporting;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>CP3: MAE / MFE of every position, marked on the executable bid, from the entry fill to the final exit, kept on the closed trade.</summary>
public class ExcursionLifetimeTests
{
    private static readonly VariantKey X1 = new("X", "X1");
    private static readonly VariantKey X2 = new("X", "X2");

    private static PlumbingVariantScript Long(long from, long to) => PaperKit.Script(new[] { (from, to) });

    private static PlumbingVariantScript Short(long from, long to) => new(new[] { new PlumbingWindow(from, to, StrategyDirection.Short) });

    private static PaperKit One(ExitSpec? exit = null, decimal capital = 10_000_000m)
        => new(PaperKit.Config(capital, PaperKit.Spec("X", "X1", exit)), PaperKit.Family("X", ("X1", Long(1, 1))));

    // ------------------------------------------------------------------ starts at the entry fill

    [Fact]
    public void Tracking_StartsWithTheBidOfTheEntryFill_AndMarksOnTheBid()
    {
        var kit = One();

        kit.Bar(10, 0, advance: 1.1);                                          // entry at ask 125, the quote bid is 124

        var e = PositionExcursion.From(kit.Kit.Position(X1));
        Assert.Equal((0m, -1m), (e.Mfe, e.Mae));                         // points per unit versus the average entry fill (125)
        Assert.Equal(-0.8m, e.MaePct);                                   // -1 / 125 = -0.8 %
        Assert.Equal(0m, e.MfePct);
        Assert.Equal((124m, 124m, 1, 0), (e.MaxBid, e.MinBid, e.Observations, e.Gaps));
        Assert.Equal(ExcursionCoverage.Complete, e.Coverage);
        Assert.Equal(kit.Kit.Position(X1).OpenedAt, e.FirstObservedAt);
    }

    [Fact]
    public void TheMark_IsTheBid_NotTheAsk_NotTheLastPrice_AndAOneSidedQuoteIsIgnored()
    {
        var kit = One();
        var t = kit.Bar(10, 0, advance: 1.1);
        var before = PositionExcursion.From(kit.Kit.Position(X1));

        kit.Feed(kit.Kit.Q(t.AddSeconds(2.2), 124m, 300m, token: PaperKit.Ce));                         // the ask jumps to 300: irrelevant
        kit.Feed(new QuoteEvent(t.AddSeconds(3.2), EventKind.OptionQuote, 90_001, PaperKit.Ce, 124m, 125m, last: 999m));   // the last traded price is 999: irrelevant
        kit.Feed(kit.Kit.Q(t.AddSeconds(4.2), null, 125m, token: PaperKit.Ce));                         // no bid at all: no observation
        kit.Feed(kit.Kit.Q(t.AddSeconds(5.2), 0m, 125m, token: PaperKit.Ce));                           // a zero bid is not an executable quote

        var after = PositionExcursion.From(kit.Kit.Position(X1));
        Assert.Equal(before.MaxBid, after.MaxBid);
        Assert.Equal(before.MinBid, after.MinBid);
        Assert.Equal((before.Mfe, before.Mae), (after.Mfe, after.Mae));
    }

    // ------------------------------------------------------------------ lifetime values on the closed trade

    [Fact]
    public void TheClosedTrade_KeepsTheLifetimeExcursion_InPointsAndPercent()
    {
        var kit = One(new ExitSpec { TradeSlPct = 0.10m });
        var t = kit.Bar(10, 0, advance: 1.1);
        kit.Move(t.AddSeconds(1.2), ceBid: 130m, advance: 0.5);                          // favourable excursion +5
        kit.Move(t.AddSeconds(2.2), ceBid: 128m, advance: 0.5);
        kit.Move(t.AddSeconds(3.2), ceBid: 100m);                          // the stop (125 x 0.9 = 112.5) triggers; the exit fills one latency later

        var position = kit.Kit.Position(X1);
        Assert.Equal(PositionState.Closed, position.State);
        var closed = Assert.Single(kit.Out<PositionClosed>(X1));
        var e = closed.Excursion;
        Assert.Equal(5m, e.Mfe);
        Assert.Equal(-25m, e.Mae);
        Assert.Equal(4m, e.MfePct);                                      // 5 / 125
        Assert.Equal(-20m, e.MaePct);                                    // -25 / 125
        Assert.Equal((130m, 100m), (e.MaxBid, e.MinBid));
        Assert.Equal(ExcursionCoverage.Complete, e.Coverage);
        Assert.True(e.Observations >= 4);
        Assert.Equal(e, PositionExcursion.From(position));               // what is stored is what the live position reports
    }

    [Fact]
    public void Tp1_DoesNotResetTheExcursion_AndTheValuesAtTheFirstExitAreKeptSeparately()
    {
        var kit = One(new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m });
        var t = kit.Bar(10, 0, advance: 1.1);
        kit.Move(t.AddSeconds(1.2), ceBid: 122m, advance: 0.5);                          // MAE -3
        kit.Move(t.AddSeconds(2.2), ceBid: 140m);                          // TP1 trigger (+10% = 137.5); the leg fills about one latency later
        var afterTp1 = PositionExcursion.From(kit.Kit.Position(X1));
        Assert.Equal(325, kit.Kit.Position(X1).OpenQuantity);
        Assert.Equal((15m, -3m), (afterTp1.Mfe, afterTp1.Mae));          // nothing was reset by the partial exit
        Assert.Equal(15m, afterTp1.MfeAtFirstExit);
        Assert.Equal(-3m, afterTp1.MaeAtFirstExit);

        kit.Move(t.AddSeconds(4.2), ceBid: 119m, advance: 0.5);                          // after the partial exit the path keeps being recorded ...
        kit.Move(t.AddSeconds(5.2), ceBid: 145m, advance: 0.5);                          // ... in both directions
        var running = PositionExcursion.From(kit.Kit.Position(X1));
        Assert.Equal((20m, -6m), (running.Mfe, running.Mae));
        Assert.Equal((15m, -3m), (running.MfeAtFirstExit, running.MaeAtFirstExit));   // the first-exit snapshot does not move

        kit.Kit.Engine.Execution.RequestStrategyExit(X1, kit.Kit.Position(X1).Id, null, kit.Kit.Loop);
        kit.Move(t.AddSeconds(7.2), ceBid: 143m);
        var closed = Assert.Single(kit.Out<PositionClosed>(X1));
        Assert.Equal((20m, -6m), (closed.Excursion.Mfe, closed.Excursion.Mae));       // lifetime values on the stored trade
        Assert.Equal((15m, -3m), (closed.Excursion.MfeAtFirstExit, closed.Excursion.MaeAtFirstExit));
    }

    [Fact]
    public void ConcurrentPositions_HaveIndependentExcursion()
    {
        var kit = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1"), PaperKit.Spec("X", "X2")),
            PaperKit.Family("X", ("X1", Long(1, 1)), ("X2", Short(1, 1))));
        var t = kit.Bar(10, 0, advance: 1.1);                                          // X1 long (CE), X2 short (PE), both entered at 125

        kit.Move(t.AddSeconds(1.2), ceBid: 140m, peBid: 90m, advance: 0.5);
        kit.Move(t.AddSeconds(2.2), ceBid: 135m, peBid: 110m);

        var e1 = PositionExcursion.From(kit.Kit.Position(X1));
        var e2 = PositionExcursion.From(kit.Kit.Position(X2));
        Assert.Equal((15m, -1m), (e1.Mfe, e1.Mae));                      // the CE path only
        Assert.Equal((0m, -35m), (e2.Mfe, e2.Mae));                      // the PE path only
        Assert.NotEqual(e1.MaxBid, e2.MaxBid);
    }

    // ------------------------------------------------------------------ coverage and gaps

    [Fact]
    public void AGapLongerThanTheQuoteAgeLimit_IsCounted_AndTheCoverageSaysSo()
    {
        var kit = One();
        var t = kit.Bar(10, 0, advance: 1.1);
        kit.Move(t.AddSeconds(1.2), ceBid: 126m, advance: 0.5);                          // continuous so far
        Assert.Equal(ExcursionCoverage.Complete, PositionExcursion.From(kit.Kit.Position(X1)).Coverage);

        kit.Move(t.AddSeconds(30), ceBid: 90m);                          // 29 s of silence: the bid path was unobserved in between

        var e = PositionExcursion.From(kit.Kit.Position(X1));
        Assert.Equal(1, e.Gaps);
        Assert.Equal(ExcursionCoverage.HasGaps, e.Coverage);
        Assert.Equal(-35m, e.Mae);
    }

    [Fact]
    public void ADeterministicRun_ProducesTheSameExcursionOnTheStoredTrade()
    {
        static string Run()
        {
            var kit = One(new ExitSpec { TradeSlPct = 0.10m, Tp1Pct = 0.10m, Tp1Proportion = 0.5m });
            var t = kit.Bar(10, 0, advance: 1.1);
            kit.Move(t.AddSeconds(1.2), ceBid: 140m, advance: 0.5);
            kit.Move(t.AddSeconds(2.2), ceBid: 138m, advance: 0.5);
            kit.Move(t.AddSeconds(3.2), ceBid: 100m);
            return TradeKit.Signature(kit.Kit.Engine.Outputs);
        }

        var first = Run();
        Assert.Equal(first, Run());
        Assert.Contains("PositionClosed", first, StringComparison.Ordinal);
    }

    [Fact]
    public void APositionStillOpenAtTheHardClose_ReportsItsExcursionToo()
    {
        var kit = One();
        kit.Bar(10, 0);
        kit.AdvanceTo(Clock.Ist(15, 36));                                // no bid ever returns

        var report = Assert.Single(kit.Out<UnresolvedAtSessionEnd>(X1));
        Assert.Equal((0m, -1m), (report.Excursion.Mfe, report.Excursion.Mae));
        Assert.Equal(PositionState.Unresolved, kit.Kit.Position(X1).State);
    }
}
