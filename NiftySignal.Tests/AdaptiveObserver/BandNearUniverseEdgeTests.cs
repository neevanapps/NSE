using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// Characterisation (incident 2026-11-05): under spot-atm-v1 the option band needs the ATM centre to have two listed strikes on EACH side. The option universe is frozen for the session, so when the fresh spot drifts to
/// within two strikes of an edge of that frozen ladder the Observer produces NO band (null selection) for every bar from then on, and V2 - which needs the band's centre strike - can build no frame. This pins the
/// behaviour with valid fresh two-sided quotes on every strike, so "no band" can only come from the ladder edge. It documents the contract; it does not change it.
/// </summary>
public class BandNearUniverseEdgeTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 5, 4, 30, 0, TimeSpan.Zero);
    static readonly DateOnly Expiry = new(2026, 11, 10);
    static readonly double[] Ladder = Enumerable.Range(0, 11).Select(i => 23_900d + 50 * i).ToArray();     // 23900 .. 24400: 11 listed strikes

    static (List<ObserverOptionInstrument> Chain, Dictionary<string, OptionQuoteSnapshot> Quotes) Market(double spot, double[]? ladder = null)
    {
        var chain = new List<ObserverOptionInstrument>();
        var quotes = new Dictionary<string, OptionQuoteSnapshot>();
        foreach (var k in ladder ?? Ladder)
        {
            // put-call parity at rate 0: C - P = S - K, both legs two-sided and fresh
            var intrinsic = Math.Max(0, spot - k);
            double c = intrinsic + 40, p = c - (spot - k);
            chain.Add(new($"C{k}", $"C{k}", OptionType.Call, k, Expiry, 75));
            chain.Add(new($"P{k}", $"P{k}", OptionType.Put, k, Expiry, 75));
            quotes[$"C{k}"] = new($"C{k}", T0, c, c - 0.5, c + 0.5, 1000);
            quotes[$"P{k}"] = new($"P{k}", T0, p, p - 0.5, p + 0.5, 1000);
        }

        return (chain, quotes);
    }

    static OptionBandSelection? Select(double spot, double[]? ladder = null)
    {
        var (chain, quotes) = Market(spot, ladder);
        return AdaptiveOptionBandCalculator.Select(T0, Expiry, spot, chain, quotes, 0d, 5d, 2, AtmCenterRule.SpotV1, spot);
    }

    [Theory]
    [InlineData(24_150d, 24_150d)]   // mid-ladder
    [InlineData(24_300d, 24_300d)]   // the last centre that still has two strikes above it (24350, 24400)
    [InlineData(24_000d, 24_000d)]   // the first centre that has two strikes below it (23900, 23950)
    public void AtmWithTwoStrikesEachSide_YieldsABand(double spot, double centre)
    {
        var s = Select(spot);
        Assert.NotNull(s);
        Assert.Equal(centre, s!.CenterStrike);
    }

    [Theory]
    [InlineData(24_350d)]            // one strike from the top edge
    [InlineData(24_400d)]            // on the top edge
    [InlineData(24_700d)]            // beyond the ladder (clamped to the highest strike)
    [InlineData(23_950d)]            // one strike from the bottom edge
    [InlineData(23_900d)]
    public void AtmWithinTwoStrikesOfTheFrozenLadderEdge_YieldsNoBand_NeverASubstitutedCentre(double spot)
    {
        Assert.Null(Select(spot));
    }

    /// <summary>
    /// The 2026-11-05 shape: the universe is the broker chain of +-10 strikes around the PREVIOUS CLOSE (here 23950 -> 23450..24450). Spot 24350 (centre 24350, needs 24400 and 24450) has a band; once spot crosses 24375 the
    /// nearest strike is 24400, which needs 24450 AND 24500 - and 24500 is not in the frozen ladder - so there is no band from that bar on, with every listed strike freshly and two-sidedly quoted.
    /// </summary>
    [Theory]
    [InlineData(24_350d, true)]
    [InlineData(24_374d, true)]
    [InlineData(24_376d, false)]
    [InlineData(24_383.05d, false)]     // the live spot the Dashboard showed after the band was lost
    public void AGapUpDay_PushesTheCentreOutOfAPreCloseLadder_AndTheBandDisappearsAtTheSameInstant(double spot, bool hasBand)
    {
        var ladder = Enumerable.Range(0, 21).Select(i => 23_450d + 50 * i).ToArray();      // prev close 23950 +- 10 strikes
        Assert.Equal(24_450d, ladder.Max());
        Assert.Equal(hasBand, Select(spot, ladder) is not null);
    }
}
