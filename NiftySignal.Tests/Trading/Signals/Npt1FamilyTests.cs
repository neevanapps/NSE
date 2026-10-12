using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Signals;

public class Npt1FamilyTests
{
    private static readonly DateOnly Day = new(2026, 10, 12);
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 3, 45, 0, TimeSpan.Zero);

    private static BarCompletedEvent Bar(long seq, double? m, double? w, long? p, bool ready = true, DateOnly? day = null)
    {
        var end = Start.AddSeconds(seq * 45);
        var frame = new AdaptiveStrategyFrame("npt1-m1-test", day ?? Day, seq, end.AddSeconds(-45), end, ready, ready, (int)seq, "npt1-m1-v1", "spec", "micro", m, w, p, 24000d, new DateOnly(2026, 10, 13), null);
        return new BarCompletedEvent(end, seq, "FUT", null, frame);
    }

    private static VariantDecision Decide(IStrategyFamily family, BarCompletedEvent bar) => Assert.Single(family.OnBar(bar).Decisions);

    // ------------------------------------------------------------------ R

    [Fact]
    public void R_IsTheUsableSecondsWeightedAverage_NotTheSimpleMean_AndAZeroWeightIsAllowed()
    {
        var m = new NullableWindow(10);
        var w = new NullableWindow(10);
        var ms = new double?[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var ws = new double?[] { 0, 1, 1, 1, 1, 1, 1, 1, 1, 8 };
        for (var i = 0; i < 10; i++)
        {
            m.Add(ms[i]);
            w.Add(ws[i]);
        }

        var expected = (2 + 3 + 4 + 5 + 6 + 7 + 8 + 9 + 80d) / 16d;
        Assert.Equal(expected, MicroDevFamilyBase.RollingR(m, w)!.Value, 12);
    }

    [Fact]
    public void R_IsUnavailable_NeverZero_WhenAnyInputIsMissingOrTheTotalWeightIsZero()
    {
        var m = new NullableWindow(10);
        var w = new NullableWindow(10);
        for (var i = 0; i < 9; i++)
        {
            m.Add(1);
            w.Add(1);
        }

        Assert.Null(MicroDevFamilyBase.RollingR(m, w));                        // only nine bars
        m.Add(null);
        w.Add(1);
        Assert.Null(MicroDevFamilyBase.RollingR(m, w));                        // a missing m
        m = new NullableWindow(10);
        w = new NullableWindow(10);
        for (var i = 0; i < 10; i++)
        {
            m.Add(5);
            w.Add(0);
        }

        Assert.Null(MicroDevFamilyBase.RollingR(m, w));                        // zero denominator
    }

    // ------------------------------------------------------------------ MD5

    [Fact]
    public void Md5_FirstDecisionIsBar14_AndAPositiveA5EmitsLong()
    {
        var f = new Npt1Md5Family();
        for (var i = 1; i <= 13; i++)
        {
            var d = Decide(f, Bar(i, 1d, 1d, null));
            Assert.Equal(StrategyAction.Wait, d.Action);
            Assert.Equal("A5Unavailable", d.ReasonCode);
        }

        var decision = Decide(f, Bar(14, 1d, 1d, null));
        Assert.Equal(StrategyAction.Enter, decision.Action);
        Assert.Equal(StrategyDirection.Long, decision.Direction);
        Assert.Contains("A5=1", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Md5_ANegativeA5EmitsShort_AndExactlyZeroRetainsThePreviousTarget()
    {
        var f = new Npt1Md5Family();
        for (var i = 1; i <= 10; i++)
        {
            Decide(f, Bar(i, 1d, 1d, null));
        }

        // zeros from bar 11: R falls to exactly 0 at bar 20, A5 is exactly 0 at bar 24 -> the +1 target is retained
        VariantDecision last = null!;
        for (var i = 11; i <= 24; i++)
        {
            last = Decide(f, Bar(i, 0d, 1d, null));
        }

        Assert.Equal(StrategyAction.Enter, last.Action);
        Assert.Equal(StrategyDirection.Long, last.Direction);
        Assert.Contains("A5=0", last.Detail, StringComparison.Ordinal);
        Assert.Equal(1, f.Target);

        // then negative flow: eventually A5 < 0 flips the target to short
        VariantDecision flip = null!;
        for (var i = 25; i <= 45; i++)
        {
            flip = Decide(f, Bar(i, -1d, 1d, null));
        }

        Assert.Equal(StrategyDirection.Short, flip.Direction);
        Assert.Equal(-1, f.Target);
    }

    [Fact]
    public void Md5_UnavailableA5SkipsTheDecision_AndAnOutageBarIsNeverZero()
    {
        var f = new Npt1Md5Family();
        for (var i = 1; i <= 14; i++)
        {
            Decide(f, Bar(i, 1d, 1d, null));
        }

        var d = Decide(f, Bar(15, null, null, null));                          // missing m and w
        Assert.Equal(StrategyAction.Wait, d.Action);
        Assert.Equal("A5Unavailable", d.ReasonCode);
        for (var i = 16; i <= 28; i++)
        {
            Assert.Equal(StrategyAction.Wait, Decide(f, Bar(i, 1d, 1d, null)).Action);   // ten bars until R is whole again, five more for A5
        }

        Assert.Equal(StrategyAction.Enter, Decide(f, Bar(29, 1d, 1d, null)).Action);
    }

    [Fact]
    public void AllFamilies_NeedObserverReadiness_ForADecision_ButStillFeedTheirWindows()
    {
        var f = new Npt1Md5Family();
        for (var i = 1; i <= 13; i++)
        {
            Decide(f, Bar(i, 1d, 1d, null, ready: false));
        }

        var notReady = Decide(f, Bar(14, 1d, 1d, null, ready: false));
        Assert.Equal("ObserverNotReady", notReady.ReasonCode);
        Assert.Equal(StrategyAction.Enter, Decide(f, Bar(15, 1d, 1d, null)).Action);   // windows were full: the first ready bar decides at once
    }

    [Fact]
    public void ASequenceGap_ResetsTheWindows_NoTradesAreFabricated_AndTheWarmupStartsAgain()
    {
        var f = new Npt1Md5Family();
        for (var i = 1; i <= 14; i++)
        {
            Decide(f, Bar(i, 1d, 1d, null));
        }

        var afterGap = Decide(f, Bar(20, 1d, 1d, null));                       // bars 15..19 were never seen
        Assert.Equal(StrategyAction.Wait, afterGap.Action);
        Assert.Equal(1, f.Resets);
        Assert.Contains("gapReset(14->20)", afterGap.Detail, StringComparison.Ordinal);
        for (var i = 21; i <= 32; i++)
        {
            Assert.Equal(StrategyAction.Wait, Decide(f, Bar(i, 1d, 1d, null)).Action);
        }

        Assert.Equal(StrategyAction.Enter, Decide(f, Bar(33, 1d, 1d, null)).Action);   // 14 consecutive bars after the gap (20..33)
    }

    [Fact]
    public void ANewTradeDate_ResetsWindowsAndTarget()
    {
        var f = new Npt1Md5Family();
        for (var i = 1; i <= 14; i++)
        {
            Decide(f, Bar(i, 1d, 1d, null));
        }

        Assert.Equal(1, f.Target);
        var d = Decide(f, Bar(15, 1d, 1d, null, day: Day.AddDays(1)));
        Assert.Equal(StrategyAction.Wait, d.Action);
        Assert.Equal(0, f.Target);
    }

    // ------------------------------------------------------------------ MDX3 / PE5 against an independent reference model

    private sealed record Row(double? M, double? W, long? P);

    private static IEnumerable<Row> RandomRows(int seed, int n, bool coarse)
    {
        var rng = new Random(seed);
        for (var i = 0; i < n; i++)
        {
            double? m = rng.Next(40) == 0 ? null : (coarse ? rng.Next(-2, 3) : Math.Round(rng.NextDouble() * 4 - 2, 3));
            double? w = rng.Next(40) == 0 ? null : (coarse ? rng.Next(0, 3) : rng.Next(0, 46));
            long? p = rng.Next(40) == 0 ? null : (coarse ? rng.Next(-3, 4) : rng.Next(-5000, 5000));
            yield return new Row(m, w, p);
        }
    }

    private static double? MeanLast(IReadOnlyList<double?> series, int index, int k)
    {
        if (index + 1 < k)
        {
            return null;
        }

        double sum = 0;
        for (var j = index - k + 1; j <= index; j++)
        {
            if (series[j] is not { } v)
            {
                return null;
            }

            sum += v;
        }

        return sum / k;
    }

    private static List<double?> ReferenceR(IReadOnlyList<Row> rows)
    {
        var r = new List<double?>();
        for (var t = 0; t < rows.Count; t++)
        {
            double? value = null;
            if (t >= 9)
            {
                double num = 0, den = 0;
                var ok = true;
                for (var j = t - 9; j <= t; j++)
                {
                    if (rows[j].M is not { } m || rows[j].W is not { } w)
                    {
                        ok = false;
                        break;
                    }

                    num += m * w;
                    den += w;
                }

                value = ok && den > 0 ? num / den : null;
            }

            r.Add(value);
        }

        return r;
    }

    /// <summary>Reference event stream: +1 CE, -1 PE, 0 none, for the MDX3 rules, straight from the specification text.</summary>
    private static int[] ReferenceMdx3(IReadOnlyList<Row> rows)
    {
        var r = ReferenceR(rows);
        var f = new List<double?>();
        var s = new List<double?>();
        for (var t = 0; t < rows.Count; t++)
        {
            f.Add(MeanLast(r, t, 3));
            s.Add(MeanLast(r, t, 10));
        }

        var events = new int[rows.Count];
        for (var t = 1; t < rows.Count; t++)
        {
            if (f[t] is not { } ft || s[t] is not { } st || f[t - 1] is not { } fp || s[t - 1] is not { } sp)
            {
                continue;
            }

            var d = ft - st;
            var dp = fp - sp;
            if (dp <= 0 && d > 0 && ft > 0)
            {
                events[t] = 1;
            }
            else if (dp >= 0 && d < 0 && ft < 0)
            {
                events[t] = -1;
            }
        }

        return events;
    }

    private static int[] ReferencePe5(IReadOnlyList<Row> rows)
    {
        var x = rows.Select(r => r.P is { } p ? (double?)-(double)p : null).ToList();
        var events = new int[rows.Count];
        for (var t = 1; t < rows.Count; t++)
        {
            var f = MeanLast(x, t, 5);
            var s = MeanLast(x, t, 10);
            var fp = MeanLast(x, t - 1, 5);
            var sp = MeanLast(x, t - 1, 10);
            if (f is null || s is null || fp is null || sp is null)
            {
                continue;
            }

            var d = f.Value - s.Value;
            var dp = fp.Value - sp.Value;
            var margin = 0.20 * Math.Abs(s.Value);
            var marginPrev = 0.20 * Math.Abs(sp.Value);
            if (dp <= marginPrev && d > margin)
            {
                events[t] = 1;
            }
            else if (dp >= -marginPrev && d < -margin)
            {
                events[t] = -1;
            }
        }

        return events;
    }

    private static int[] Run(IStrategyFamily family, IReadOnlyList<Row> rows)
    {
        var result = new int[rows.Count];
        for (var t = 0; t < rows.Count; t++)
        {
            var d = Decide(family, Bar(t + 1, rows[t].M, rows[t].W, rows[t].P));
            result[t] = d.Action == StrategyAction.Enter ? (d.Direction == StrategyDirection.Long ? 1 : -1) : 0;
        }

        return result;
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void Mdx3_MatchesTheReferenceModel_OnBothCoarseTieHeavyAndContinuousData(int seed, bool coarse)
    {
        var rows = RandomRows(seed, 600, coarse).ToList();
        var expected = ReferenceMdx3(rows);
        Assert.Equal(expected, Run(new Npt1Mdx3Family(), rows));
        Assert.Contains(expected, e => e != 0);                                // the data exercises real events
    }

    [Theory]
    [InlineData(11, true)]
    [InlineData(12, true)]
    [InlineData(13, false)]
    [InlineData(14, false)]
    [InlineData(15, true)]
    public void Pe5_MatchesTheReferenceModel_OnBothCoarseTieHeavyAndContinuousData(int seed, bool coarse)
    {
        var rows = RandomRows(seed, 600, coarse).ToList();
        var expected = ReferencePe5(rows);
        Assert.Equal(expected, Run(new Npt1Pe5Family(), rows));
        Assert.Contains(expected, e => e == 1);
        Assert.Contains(expected, e => e == -1);                               // PE flow produces both CE and PE events
    }

    [Fact]
    public void Mdx3_EarliestEventIsBar20_AndAnUpCrossWithANegativeFHolds()
    {
        // 10 R values of -0.15 (S = -0.15), then F rises from -0.20 to -0.10: an upward cross with F < 0 -> no event of either side.
        var f = new Npt1Mdx3Family();
        var decisions = new List<VariantDecision>();
        for (var i = 1; i <= 19; i++)
        {
            decisions.Add(Decide(f, Bar(i, -0.15d, 1d, null)));
        }

        Assert.All(decisions, d => Assert.Equal(StrategyAction.Wait, d.Action));
        Assert.Contains("dPrev=~", decisions[18].Detail, StringComparison.Ordinal);           // bar 19 has d but no previous d: bar 20 is the earliest event
        var twenty = Decide(f, Bar(20, -0.15d, 1d, null));
        Assert.Equal("NoEvent", twenty.ReasonCode);
    }

    [Fact]
    public void Mdx3_AnOppositeCrossoverThatFailsItsSignGate_EmitsNothing()
    {
        // Falling R through a positive S while F stays positive: d crosses below 0 but F > 0 -> no PE event.
        var rows = new List<Row>();
        for (var i = 0; i < 12; i++)
        {
            rows.Add(new Row(1d, 1d, null));
        }

        for (var i = 0; i < 40; i++)
        {
            rows.Add(new Row(0.4d, 1d, null));
        }

        var events = Run(new Npt1Mdx3Family(), rows);
        Assert.DoesNotContain(-1, events);
    }

    [Fact]
    public void Pe5_HasNoFSignGate_AndAZeroSMeansAZeroMargin()
    {
        // x = -P. P = +10 for ten bars then P falls to -20: x rises from -10 to +20 -> F crosses above S's margin band while F < 0 first.
        var rows = new List<Row>();
        for (var i = 0; i < 12; i++)
        {
            rows.Add(new Row(null, null, 10));
        }

        for (var i = 0; i < 8; i++)
        {
            rows.Add(new Row(null, null, -20));
        }

        var events = Run(new Npt1Pe5Family(), rows);
        var firstEvent = Array.FindIndex(events, e => e != 0);
        Assert.True(firstEvent > 0);
        Assert.Equal(1, events[firstEvent]);
        Assert.Equal(ReferencePe5(rows), events);

        // S = 0 exactly: all-zero flow gives margin 0, a strictly positive d crosses it.
        var zero = new List<Row>();
        for (var i = 0; i < 10; i++)
        {
            zero.Add(new Row(null, null, 0));
        }

        zero.Add(new Row(null, null, -5));                                     // x = +5 : F=1, S=0.5, d=0.5 > margin 0.1 ... previous d = 0 <= 0
        var ev = Run(new Npt1Pe5Family(), zero);
        Assert.Equal(1, ev[^1]);
    }

    [Fact]
    public void Pe5_MissingFlow_IsUnavailableForTenBars_NotZero()
    {
        var f = new Npt1Pe5Family();
        for (var i = 1; i <= 15; i++)
        {
            Decide(f, Bar(i, null, null, i % 2 == 0 ? 7 : -3));
        }

        Decide(f, Bar(16, null, null, null));
        for (var i = 17; i <= 25; i++)
        {
            Assert.Equal("WindowUnavailable", Decide(f, Bar(i, null, null, 5)).ReasonCode);
        }
    }
}
