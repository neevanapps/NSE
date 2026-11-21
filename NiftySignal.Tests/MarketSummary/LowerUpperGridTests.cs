using NiftySignal.MarketSummary;

namespace NiftySignal.Tests.MarketSummary;

/// <summary>Lower / ATM / Upper TOTAL baskets: the same ten-slot basket shifted by one listed step around the latest completed bar's ATM.</summary>
public class LowerUpperGridTests
{
    static readonly DateOnly Expiry = new(2026, 11, 26);

    static IReadOnlyList<ListedStrike> Ladder(double from, double to, double step = 50, Func<double, bool>? skip = null) =>
        Enumerable.Range(0, (int)((to - from) / step) + 1).Select(i => from + i * step).Where(s => skip?.Invoke(s) != true).Select(s => new ListedStrike(s, $"CE{s:0}", $"PE{s:0}")).ToList();

    static double[] A(params double[] x) => x;

    static double[] Strikes(Basket b, bool call) => b.Contracts.Where(c => c.IsCall == call && c.Strike is not null).Select(c => c.Strike!.Value).OrderBy(x => x).ToArray();

    [Fact]
    public void Centre25100_LowerAtmUpper_AreTheSpecifiedFiveStrikes_AndCeAndPeShareThem()
    {
        var listed = Ladder(24_800, 25_400);
        var lower = BasketResolver.Resolve(Expiry, 25_100, listed, -1);
        var atm = BasketResolver.Resolve(Expiry, 25_100, listed);
        var upper = BasketResolver.Resolve(Expiry, 25_100, listed, 1);
        Assert.Equal(A(24_950d, 25_000, 25_050, 25_100, 25_150), Strikes(lower, true));
        Assert.Equal(A(25_000d, 25_050, 25_100, 25_150, 25_200), Strikes(atm, true));
        Assert.Equal(A(25_050d, 25_100, 25_150, 25_200, 25_250), Strikes(upper, true));
        foreach (var b in new[] { lower, atm, upper })
        {
            Assert.Equal(Strikes(b, true), Strikes(b, false));                                  // CE and PE: exactly the same five strikes
            Assert.Empty(b.Problems);
            Assert.Equal(10, b.For(GridKind.Total).Count(c => c.Token is not null));
            Assert.Equal(50d, b.Step);
        }

        Assert.Equal(25_050d, lower.Center);
        Assert.Equal(25_150d, upper.Center);
        Assert.Equivalent(BasketResolver.Resolve(Expiry, 25_100, listed), atm, strict: true);   // shift 0 is the existing ATM basket, untouched
    }

    [Fact]
    public void Step100_ShiftsByOneListedStep()
    {
        var listed = Ladder(24_000, 26_000, 100);
        var upper = BasketResolver.Resolve(Expiry, 25_000, listed, 1);
        Assert.Equal(A(24_900d, 25_000, 25_100, 25_200, 25_300), Strikes(upper, true));
        Assert.Equal(100d, upper.Step);
    }

    [Fact]
    public void LadderEdge_MissingStrikes_StayUnresolved_NeverReplaced()
    {
        var listed = Ladder(24_800, 25_200);                       // top of the ladder is 25200
        var upper = BasketResolver.Resolve(Expiry, 25_100, listed, 1);    // wants 25050..25250: 25250 is not listed
        Assert.Equal(2, upper.Contracts.Count(c => c.Token is null));
        Assert.All(upper.Contracts.Where(c => c.Token is null), c => { Assert.Equal(25_250d, c.Strike); });
        Assert.NotEmpty(upper.Problems);
        Assert.DoesNotContain(upper.Contracts, c => c.Token is { } t && t.EndsWith("25250"));
        Assert.Equal(A(25_050d, 25_100, 25_150, 25_200), upper.Contracts.Where(c => c.IsCall && c.Token is not null).Select(c => c.Strike!.Value).OrderBy(x => x).ToArray());

        var bottom = Ladder(25_000, 25_400);
        var lower = BasketResolver.Resolve(Expiry, 25_100, bottom, -1);   // wants 24950..25150: 24950 is not listed
        Assert.Equal(2, lower.Contracts.Count(c => c.Token is null));
        Assert.Equal(24_950d, lower.Contracts.First(c => c.Token is null).Strike);

        var gap = Ladder(24_800, 25_400, skip: s => s == 25_000);          // a hole inside the ladder
        var g = BasketResolver.Resolve(Expiry, 25_100, gap, -1);
        Assert.Equal(2, g.Contracts.Count(c => c.Token is null));
        Assert.All(g.Contracts.Where(c => c.Token is null), c => Assert.Equal(25_000d, c.Strike));
    }

    [Fact]
    public void CentreNotListed_EveryShiftedSlotIsUnresolved()
    {
        var b = BasketResolver.Resolve(Expiry, 25_110, Ladder(24_800, 25_400), 1);
        Assert.All(b.Contracts, c => Assert.Null(c.Token));
        Assert.NotEmpty(b.Problems);
    }

    [Fact]
    public void WhenTheCentreMoves_AllThreeBasketsMoveTogether()
    {
        var listed = Ladder(24_800, 25_400);
        var before = new[] { -1, 0, 1 }.Select(s => Strikes(BasketResolver.Resolve(Expiry, 25_100, listed, s), true)).ToArray();
        var after = new[] { -1, 0, 1 }.Select(s => Strikes(BasketResolver.Resolve(Expiry, 25_150, listed, s), true)).ToArray();
        for (var i = 0; i < 3; i++) Assert.Equal(before[i].Select(x => x + 50), after[i]);
    }
}
