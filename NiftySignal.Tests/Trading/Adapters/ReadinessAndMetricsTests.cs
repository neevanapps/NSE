using System.Reflection;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Trading.Adapters;

namespace NiftySignal.Tests.Trading.Adapters;

public class ReadinessAndMetricsTests
{
    private static readonly IReadOnlyList<SourceTick> Rows = SyntheticSession.Rows();
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);

    private static List<TranslatedGroup> Translate(IEnumerable<SourceTick> rows)
        => Tk.All(new AdaptiveEventTranslator(Freeze, SyntheticSession.Translator(Freeze)), rows);

    /// <summary>The existing production path, run directly: normalise every row in id order, sort by (availability, id), group, run the engine.</summary>
    private static List<AdaptiveCompletedBarPackage> Reference(IEnumerable<SourceTick> rows)
    {
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<(string Token, CleanObserverTick Tick)>();
        foreach (var row in rows)
        {
            if (normalizer.Process(row.Token, row.ToRaw()) is { } tick && tick.AvailableAt >= Freeze.ObservationStartUtc && tick.AvailableAt <= SyntheticSession.Ist(15, 35))
            {
                clean.Add((row.Token, tick));
            }
        }

        var engine = new AdaptiveObserverEngine(Freeze.Definition, Freeze.WeeklyOptionExpiry, Freeze.RiskFreeRate, Freeze.StrongThreshold, Freeze.SignalStartUtc, Freeze.Options, Freeze.ResidualAnchor);
        var packages = new List<AdaptiveCompletedBarPackage>();
        foreach (var group in clean.OrderBy(x => x.Tick.AvailableAt).ThenBy(x => x.Tick.Id).GroupBy(x => x.Tick.AvailableAt))
        {
            packages.AddRange(engine.ProcessAvailabilityGroup(group.ToArray()));
        }

        return packages;
    }

    [Fact]
    public void TheTranslator_ReproducesTheExistingFullSortPipeline_BarForBar()
    {
        var groups = Translate(Rows);
        var reference = Reference(Rows);

        var bars = groups.SelectMany(g => g.Bars).ToList();
        Assert.True(bars.Count > 100);
        Assert.Equal(reference.Count, bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            var expected = reference[i].FutureBar;
            var info = bars[i].Event.Info!;
            Assert.Equal(expected.BarSeq, bars[i].Event.BarSeq);
            Assert.Equal(expected.StartAvailableAtUtc, info.StartAvailableAt);
            Assert.Equal(expected.EndAvailableAtUtc, info.EndAvailableAt);
            Assert.Equal(expected.Volume, info.Volume);
            Assert.Equal((expected.Open, expected.High, expected.Low, expected.Close), (bars[i].Metrics.Open, bars[i].Metrics.High, bars[i].Metrics.Low, bars[i].Metrics.Close));
            Assert.Equal(reference[i].FlowState.State.ToString(), bars[i].Metrics.AdaptiveState);
            var atmPlus2 = reference[i].Residuals.SingleOrDefault(r => r.Variant == ResidualVariant.AtmPlusMinus2);
            var translated = bars[i].Metrics.Residuals.Single(r => r.Variant == nameof(ResidualVariant.AtmPlusMinus2));
            Assert.Equal(atmPlus2?.Reading?.DirectionalResidualPct, translated.DirectionalResidualPct);        // exactly the engine's reading, not a recomputation
        }
    }

    [Fact]
    public void ReadinessEvidence_FollowsTheExistingPolicy_AndTheFrozenSignalStart()
    {
        var bars = Translate(Rows).SelectMany(g => g.Bars).ToList();

        Assert.Equal(AdaptiveReadinessPolicy.RequiredBars, bars[0].Event.Info!.RequiredValidBars);
        Assert.Equal(Enumerable.Range(1, 9).Select(i => i).ToArray(), bars.Take(9).Select(b => b.Event.Info!.ConsecutiveValidBars).ToArray());   // warm-up counts up to the requirement
        Assert.All(bars, b =>
        {
            var info = b.Event.Info!;
            Assert.Equal(info.EndAvailableAt >= Freeze.SignalStartUtc, info.SignalStarted);
            Assert.Equal(info.ConsecutiveValidBars >= AdaptiveReadinessPolicy.RequiredBars && info.SignalStarted, info.Ready);
            Assert.Contains("adaptive-readiness-v1", info.ReadinessSpec, StringComparison.Ordinal);
            Assert.Contains("model=synthetic-session-v1", info.ReadinessSpec, StringComparison.Ordinal);
        });

        var firstFullWindow = bars.First(b => b.Event.Info!.ConsecutiveValidBars >= AdaptiveReadinessPolicy.RequiredBars);
        Assert.False(firstFullWindow.Event.Info!.Ready);                                                   // enough valid bars, but before 09:30: the calibration/signal start is not reached
        var firstReady = bars.First(b => b.Event.Info!.Ready);
        Assert.True(firstReady.Event.Info!.EndAvailableAt >= Freeze.SignalStartUtc);
        Assert.True(bars.Where(b => b.Event.BarSeq >= firstReady.Event.BarSeq && b.Event.Info!.EndAvailableAt < SyntheticSession.Ist(10, 5)).All(b => b.Event.Info!.Ready));
    }

    [Fact]
    public void ReadinessIsTheSameInstantEvenWhenTheStreamIsCutAndResumed_BecauseItDependsOnlyOnTheBarHistory()
    {
        var groups = Translate(Rows);
        var cut = SyntheticSession.Ist(9, 50);
        var truncated = Translate(Rows.Where(r => r.ReceivedAt <= cut));

        var full = groups.SelectMany(g => g.Bars).Where(b => b.Event.AvailableAt <= cut).ToList();
        var part = truncated.SelectMany(g => g.Bars).Where(b => b.Event.AvailableAt <= cut).ToList();

        Assert.True(full.Count > 40);
        Assert.Equal(full.Count, part.Count);
        for (var i = 0; i < full.Count; i++)
        {
            // causality: a bar's evidence and metrics are the same whether or not any later data exists
            Assert.Equal(Canonical.Json(full[i].Event.Info!), Canonical.Json(part[i].Event.Info!));
            Assert.Equal(Canonical.Json(full[i].Metrics), Canonical.Json(part[i].Metrics));
        }
    }

    [Fact]
    public void StraddleResidual_IsTheBasketWeightedAverageOfTheSideResiduals_OnTheFrozenAnchorBases()
    {
        Assert.NotNull(Freeze.ResidualAnchor);
        Assert.Equal(5, Freeze.ResidualAnchor!.Calls.Count);
        var bars = Translate(Rows).SelectMany(g => g.Bars).ToList();

        var available = bars.Select(b => b.Metrics.Residuals.Single(r => r.Variant == nameof(ResidualVariant.AtmPlusMinus2))).Where(r => r.Available).ToList();
        Assert.True(available.Count > 20);
        foreach (var r in available)
        {
            var expected = ((r.CeResidualPct!.Value * r.CeBase!.Value) + (r.PeResidualPct!.Value * r.PeBase!.Value)) / (r.CeBase.Value + r.PeBase.Value);
            Assert.Equal(expected, r.StraddleResidualPct!.Value, 9);                                        // 100 x (CeRes + PeRes) / (CeBase + PeBase), derived independently from the side percentages
            Assert.Equal(r.CeResidualPct.Value - r.PeResidualPct.Value, r.DirectionalResidualPct!.Value, 12);
            Assert.Equal((r.CeResidualPct.Value + r.PeResidualPct.Value) / 2d, r.CommonResidualPct!.Value, 12);
        }

        var anchorCe = Freeze.ResidualAnchor.Calls.Sum(c => c.Price0930);
        var anchorPe = Freeze.ResidualAnchor.Puts.Sum(c => c.Price0930);
        Assert.All(available, r =>
        {
            Assert.Equal(anchorCe, r.CeBase!.Value, 9);
            Assert.Equal(anchorPe, r.PeBase!.Value, 9);
            Assert.Contains("CE=[CE23800,CE23850,CE23900,CE23950,CE24000]", r.BasketIdentity, StringComparison.Ordinal);
        });

        var atm = bars.Select(b => b.Metrics.Residuals.Single(r => r.Variant == nameof(ResidualVariant.Atm))).First(r => r.Available);
        Assert.Contains("CE=[CE23900]", atm.BasketIdentity, StringComparison.Ordinal);
        Assert.NotEqual(available[0].CeBase, atm.CeBase);                                                   // the ATM basket has its own (center) bases; baskets are never mixed
    }

    [Fact]
    public void TheRelativeOptionRollingStrictRatio_IsListedAsAGap_AndNoSubstituteMetricExists()
    {
        var bars = Translate(Rows).SelectMany(g => g.Bars).ToList();
        var metrics = bars.First(b => b.Metrics.Call.Available && b.Metrics.Put.Available).Metrics;

        Assert.All(bars, b => Assert.Contains(b.Metrics.KnownGaps, g => g.StartsWith("RelativeOptionRollingStrictRatio:", StringComparison.Ordinal)));
        Assert.DoesNotContain(typeof(BarMetrics).GetProperties().Concat(typeof(BandSideMetrics).GetProperties()), (PropertyInfo p) => p.Name.Contains("Relative", StringComparison.OrdinalIgnoreCase));
        Assert.True(metrics.Call.Available);
        Assert.NotNull(metrics.Call.ContractRollingStrictDeltaRatioTotal);                                   // the per-side figures that do exist are carried as themselves
    }

    [Fact]
    public void BarsBeforeTheSignalStart_CarryNoResiduals_AndSaySo()
    {
        var early = Translate(Rows).SelectMany(g => g.Bars).First();

        Assert.All(early.Metrics.Residuals, r =>
        {
            Assert.False(r.Available);
            Assert.Contains("Before the signal start", r.UnavailableReason, StringComparison.Ordinal);
        });
        Assert.False(early.Event.Info!.SignalStarted);
    }

    [Fact]
    public void CountersBalance_OverAFullSession_AndEveryOfferedRowIsAccountedForOnce()
    {
        var translator = new AdaptiveEventTranslator(Freeze, SyntheticSession.Translator(Freeze));
        Tk.All(translator, Rows);

        var c = translator.Counters;
        Assert.Equal(Rows.Count, c.Input);
        Assert.True(c.Balanced);
        Assert.Equal(0, c.Quarantined);
        Assert.True(c.PayloadDuplicates > 100);                                                              // the generator emits per-token duplicates
        Assert.True(c.ClampedAvailability > 100);                                                           // and rows whose received time precedes the exchange stamp
        Assert.True(c.OutsideWindow > 0);                                                                   // rows after the 15:35 horizon
        Assert.Equal(c.BarsReleased, translator.FutureBars.Count);
    }
}
