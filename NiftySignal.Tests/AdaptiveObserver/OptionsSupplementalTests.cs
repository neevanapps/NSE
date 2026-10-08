using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>08-Oct plan Slice 3: center-contract options observations (options-supp-v2) and the shared residual projection.</summary>
public sealed class OptionsSupplementalTests
{
    // ---- freshness rule is the existing one, not a second threshold ----

    [Fact]
    public void QuoteFreshness_IsPinnedToTheExistingBandCalculatorDefault()
    {
        var existing = typeof(AdaptiveOptionBandCalculator).GetMethod(nameof(AdaptiveOptionBandCalculator.Select))!
            .GetParameters().Single(p => p.Name == "maxQuoteAgeSeconds").DefaultValue;
        Assert.Equal(5d, existing);
        Assert.Equal((double)existing!, OptionsSupplementalBar.QuoteFreshnessSeconds);
    }

    static readonly DateTimeOffset T0 = new(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);

    static InstrumentFlowSnapshot Snap(string token, double? bid, double? ask, DateTimeOffset? at, long? oi = 1000,
        long strictBuy = 0, long strictSell = 0, long strictUnknown = 0) =>
        new(token, at, null, bid, ask, oi, strictBuy, strictSell, strictUnknown, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [Theory]
    [InlineData(5.0, true)]       // exactly 5 s is fresh (the existing rule is "older than 5 s is stale")
    [InlineData(5.001, false)]
    [InlineData(0.0, true)]
    public void FreshMid_AppliesTheFiveSecondRuleAtTheBoundary(double ageSeconds, bool fresh)
    {
        var at = T0.AddSeconds(100);
        var s = Snap("X", 99, 101, at.AddSeconds(-ageSeconds));
        Assert.Equal(fresh ? 100d : null, OptionsSupplementalCalculator.FreshMid(s, at));
    }

    [Fact]
    public void FreshMid_RejectsLaterQuotesCrossedBooksAndMissingSides()
    {
        var at = T0;
        Assert.Null(OptionsSupplementalCalculator.FreshMid(Snap("X", 99, 101, at.AddSeconds(1)), at));   // a quote from after the boundary is never used
        Assert.Null(OptionsSupplementalCalculator.FreshMid(Snap("X", 101, 99, at), at));                  // crossed
        Assert.Null(OptionsSupplementalCalculator.FreshMid(Snap("X", 0, 101, at), at));                   // no bid
        Assert.Null(OptionsSupplementalCalculator.FreshMid(Snap("X", null, null, at), at));
        Assert.Null(OptionsSupplementalCalculator.FreshMid(Snap("X", 99, 101, null), at));
    }

    // ---- Position labels ----

    [Theory]
    [InlineData(OptionType.Call, 10L, -1.0, "CallWriting")]
    [InlineData(OptionType.Call, 10L, 1.0, "CallLongBuild")]
    [InlineData(OptionType.Call, -10L, 1.0, "CallShortCover")]
    [InlineData(OptionType.Call, -10L, -1.0, "CallLongUnwind")]
    [InlineData(OptionType.Put, 10L, -1.0, "PutWriting")]
    [InlineData(OptionType.Put, 10L, 1.0, "PutLongBuild")]
    [InlineData(OptionType.Put, -10L, 1.0, "PutShortCover")]
    [InlineData(OptionType.Put, -10L, -1.0, "PutLongUnwind")]
    [InlineData(OptionType.Call, 0L, 1.0, "Neutral")]            // zero OI change: no inference
    [InlineData(OptionType.Put, 10L, 0.0, "Neutral")]            // zero midpoint change: no inference
    public void Position_FollowsTheOiAndMidpointTable(OptionType side, long oi, double mid, string expected) =>
        Assert.Equal(expected, OptionsSupplementalCalculator.Position(side, oi, mid));

    [Fact]
    public void Position_IsUnavailableWhenAnInputIsMissing()
    {
        Assert.Null(OptionsSupplementalCalculator.Position(OptionType.Call, null, 1.0));
        Assert.Null(OptionsSupplementalCalculator.Position(OptionType.Call, 10, null));
    }

    // ---- Roll Vol PCR is a ratio of sums, never an average of ratios ----

    // The record has 39 positional members of which only the traded quantity matters here; clone an empty instance with just that set.
    static OptionBandSideMetrics Band(OptionType side, long total) =>
        ((OptionBandSideMetrics)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(OptionBandSideMetrics)))
        with { Side = side, CenterStrike = 23000, ContractTotalQuantity = total };

    [Fact]
    public void RollVolPcr_IsSumOfPutVolumeOverSumOfCallVolume_NotTheMeanOfBarRatios()
    {
        // Bar PCRs: 10/10, 90/10 and 10/90. Mean of ratios = (1 + 9 + 0.111)/3 = 3.37; ratio of sums = 110/110 = 1.0.
        var calls = new[] { Band(OptionType.Call, 10), Band(OptionType.Call, 10), Band(OptionType.Call, 90) };
        var puts = new[] { Band(OptionType.Put, 10), Band(OptionType.Put, 90), Band(OptionType.Put, 10) };
        var bar = Build(callHistory: calls!, putHistory: puts!, window: 3, call: calls[^1], put: puts[^1]);
        Assert.Equal(110, bar.RollPeQuantity); Assert.Equal(110, bar.RollCeQuantity);
        Assert.Equal(1.0, bar.RollVolPcr!.Value, 12);
        Assert.Equal(10d / 90d, bar.VolPcr!.Value, 12);               // current bar: PE 10 / CE 90
    }

    [Fact]
    public void RollVolPcr_IsBlankWhenAnyBarInTheWindowHasNoBand_OrCallVolumeIsZero()
    {
        var calls = new OptionBandSideMetrics?[] { Band(OptionType.Call, 10), null, Band(OptionType.Call, 90) };
        var puts = new OptionBandSideMetrics?[] { Band(OptionType.Put, 10), Band(OptionType.Put, 5), Band(OptionType.Put, 10) };
        var gapped = Build(callHistory: calls, putHistory: puts, window: 3, call: calls[^1], put: puts[^1]);
        Assert.Null(gapped.RollVolPcr); Assert.Null(gapped.RollCeQuantity);
        Assert.NotNull(gapped.VolPcr);                                // the current bar itself is still available

        var zero = Build(callHistory: [Band(OptionType.Call, 0)], putHistory: [Band(OptionType.Put, 5)], window: 1, call: Band(OptionType.Call, 0), put: Band(OptionType.Put, 5));
        Assert.Null(zero.VolPcr); Assert.Null(zero.RollVolPcr);       // never infinity
    }

    // ---- through the engine with Black-Scholes-consistent quotes ----

    static (AdaptiveObserverEngine Engine, List<ObserverOptionInstrument> Chain, DateOnly Expiry) Setup()
    {
        var day = new DateOnly(2026, 10, 8); var expiry = day.AddDays(4);
        var chain = new List<ObserverOptionInstrument>();
        foreach (var strike in new double[] { 22900, 22950, 23000, 23050, 23100 })
            foreach (var side in new[] { OptionType.Call, OptionType.Put })
                chain.Add(new((side == OptionType.Call ? "C" : "P") + strike, "OPT", side, strike, expiry, 65));
        return (new AdaptiveObserverEngine(new(day, "FUT", "FUT", expiry, 65, 100), expiry, .065, null, T0, chain, null), chain, expiry);
    }

    static CleanObserverTick Quote(long id, DateTimeOffset at, ObserverOptionInstrument i, double vol, long volume, long oi)
    {
        var p = AdaptiveResearchPricing.Price(i.OptionType, 23000, i.Strike, TimeToExpiry.YearsUntilExpiry(i.ExpiryDate, at), .065, vol);
        return new(id, at, at, p, p - .01, p + .01, 100, 100, volume, oi);
    }

    static CleanObserverTick Fut(long id, DateTimeOffset at, long volume) => new(id, at, at, 23000, 22999, 23001, 100, 100, volume, 5000);

    [Fact]
    public void Engine_AttachesCenterContractObservations_WithIvPositionPcrAndUniverseVolumes()
    {
        var (engine, chain, _) = Setup();
        var ordered = chain.ToList();
        long id = 1;
        // Start: every option quoted at 20% IV with OI 1000, future baseline tick.
        var start = ordered.Select(i => (i.Token, Quote(id++, T0, i, .20, 1000, 1000))).ToList();
        start.Add(("FUT", Fut(id++, T0, 1000)));
        Assert.Empty(engine.ProcessAvailabilityGroup(start));

        // 1.5 s later: IV jumps to 23% everywhere, a little volume trades, center CE OI rises and center PE OI falls.
        var mid = T0.AddSeconds(1.5);
        var move = ordered.Select(i => (i.Token, Quote(id++, mid, i, .23, 1000 + (i.Token == "C23000" ? 30 : 10), i.Token == "C23000" ? 1100 : i.Token == "P23000" ? 900 : 1000))).ToList();
        Assert.Empty(engine.ProcessAvailabilityGroup(move));

        var closeAt = T0.AddSeconds(2);
        var package = Assert.Single(engine.ProcessAvailabilityGroup([("FUT", Fut(id++, closeAt, 1100))]));
        var o = package.OptionsSupplemental!;

        Assert.Null(o.UnavailableReason);
        Assert.Equal(23000d, o.CenterStrike);
        Assert.Equal(1000, o.CeOiStart); Assert.Equal(1100, o.CeOiEnd); Assert.Equal(100, o.CeOiDelta);
        Assert.Equal(-100, o.PeOiDelta);
        Assert.True(o.CeMidDelta > 0 && o.PeMidDelta > 0);             // higher IV lifts both premiums
        Assert.Equal("CallLongBuild", o.CePosition);                   // OI up, mid up
        Assert.Equal("PutShortCover", o.PePosition);                   // OI down, mid up
        Assert.Equal(20d, o.CeIvStart!.Value, 1); Assert.Equal(23d, o.CeIvEnd!.Value, 1); Assert.Equal(3d, o.CeDeltaIv!.Value, 1);
        Assert.Equal(3d, o.PeDeltaIv!.Value, 1);
        Assert.Equal(0d, o.IvSkewStart!.Value, 1);                     // flat smile in, flat smile out (symmetric wings)
        Assert.Equal(0d, o.IvSkewEnd!.Value, 1);
        Assert.Equal(o.CeMidStart + o.PeMidStart, o.StraddleMidStart);
        Assert.True(o.StraddleDelta > 0);

        // PCR and universe volumes agree with the band metrics / flow of the same bar.
        Assert.Equal(package.OptionBand.Call!.ContractTotalQuantity, o.BarCeQuantity);
        Assert.Equal(package.OptionBand.Put!.ContractTotalQuantity, o.BarPeQuantity);
        Assert.Equal((double)o.BarPeQuantity! / o.BarCeQuantity!.Value, o.VolPcr!.Value, 12);
        Assert.Equal(10, o.UniverseTokenCount); Assert.Equal(10, o.TokensObserved);
        Assert.True(o.DayCeVolume > 0 && o.DayPeVolume > 0);
        Assert.NotNull(o.CeMicroDevTimeWeighted); Assert.NotNull(o.CeOfi);
        Assert.Equal(1.5, T0.AddSeconds(1.5).Subtract(T0).TotalSeconds);
        Assert.True(o.CeActivityPerSecond > 0);
    }

    [Fact]
    public void Engine_StaleQuotesMakeIvAndPositionUnavailable_NotFabricated()
    {
        var (engine, chain, _) = Setup();
        long id = 1;
        var start = chain.Select(i => (i.Token, Quote(id++, T0, i, .20, 1000, 1000))).ToList();
        start.Add(("FUT", Fut(id++, T0, 1000)));
        engine.ProcessAvailabilityGroup(start);
        // Only the future moves for 20 s: at the bar end every option quote is 20 s old (> 5 s).
        engine.ProcessAvailabilityGroup([("FUT", Fut(id++, T0.AddSeconds(10), 1050))]);
        var package = Assert.Single(engine.ProcessAvailabilityGroup([("FUT", Fut(id++, T0.AddSeconds(20), 1100))]));
        var o = package.OptionsSupplemental!;
        Assert.NotNull(o.CeMidStart);                                  // fresh at the start boundary...
        Assert.Null(o.CeMidEnd); Assert.Null(o.CeMidDelta);            // ...stale at the end
        Assert.Null(o.CePosition); Assert.Null(o.PePosition);
        Assert.Null(o.CeIvEnd); Assert.Null(o.CeDeltaIv); Assert.Null(o.IvSkewEnd); Assert.Null(o.DeltaSkew);
        Assert.Null(o.StraddleDelta);
    }

    [Fact]
    public void Engine_ReplayIsDeterministic_AndTheCoreOutputsDoNotDependOnTheSupplemental()
    {
        List<AdaptiveCompletedBarPackage> Run()
        {
            var (engine, chain, _) = Setup(); var rng = new Random(3); long id = 1; long vol = 1000; var bars = new List<AdaptiveCompletedBarPackage>();
            for (var s = 0; s < 60; s++)
            {
                var at = T0.AddSeconds(s * 1.1); vol += rng.Next(0, 40);
                var group = chain.Select(i => (i.Token, Quote(id++, at, i, .20 + rng.NextDouble() * .02, 1000 + s * 3, 1000 + rng.Next(-5, 6)))).ToList();
                group.Add(("FUT", Fut(id++, at, vol)));
                bars.AddRange(engine.ProcessAvailabilityGroup(group));
            }

            return bars;
        }

        var a = Run(); var b = Run();
        Assert.True(a.Count >= 3);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.NotNull(a[i].OptionsSupplemental);
            Assert.Equal(a[i].OptionsSupplemental, b[i].OptionsSupplemental);       // exact record equality across two independent replays
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a[i] with { OptionsSupplemental = null }),
                System.Text.Json.JsonSerializer.Serialize(b[i] with { OptionsSupplemental = null }));
        }
    }

    // ---- sidecar persistence ----

    [Fact]
    public async Task OptionsSidecar_InsertsVerifiesAndNeverOverwrites_WithTheFrozenObservationStart()
    {
        var (engine, chain, _) = Setup(); long id = 1; long vol = 1000; var bars = new List<AdaptiveCompletedBarPackage>();
        for (var s = 0; s < 40; s++)
        {
            var at = T0.AddSeconds(s * 1.1); vol += 30;
            var group = chain.Select(i => (i.Token, Quote(id++, at, i, .21, 1000 + s, 1000))).ToList(); group.Add(("FUT", Fut(id++, at, vol)));
            bars.AddRange(engine.ProcessAvailabilityGroup(group));
        }

        Assert.True(bars.Count >= 3);
        await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var session = new AdaptiveSessionStateRow
        {
            Id = 1, TradeDate = new DateOnly(2026, 10, 8), ModelVersion = "adaptive-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0,
            FutureToken = "FUT", FutureSymbol = "FUT", FutureExpiry = new DateOnly(2026, 10, 29), OpeningWindowStartUtc = T0.AddMinutes(-15),
            OpeningWindowEndUtc = T0, EstimatorName = "V1", CreatedAtUtc = T0, ObservationStartUtc = T0.AddMinutes(-15),
        };
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        foreach (var p in bars) Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyOptionsAsync(db, session, p, default));
        db.ChangeTracker.Clear();
        foreach (var p in bars) Assert.Equal(SupplementalOutcome.Verified, await service.PersistOrVerifyOptionsAsync(db, session, p, default));
        Assert.All(await db.OptionsSupplemental.AsNoTracking().ToListAsync(), r => Assert.Equal(T0.AddMinutes(-15), r.ObservationStartUtc));

        var stored = await db.OptionsSupplemental.FirstAsync(x => x.BarSeq == bars[0].FutureBar.BarSeq);
        stored.DayPeVolume += 1; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyOptionsAsync(db, session, bars[0], default));
        Assert.Equal(stored.DayPeVolume, (await db.OptionsSupplemental.AsNoTracking().FirstAsync(x => x.BarSeq == bars[0].FutureBar.BarSeq)).DayPeVolume);
    }

    // ---- shared residual projection (adjacent-bar semantics) ----

    static ResidualReadingPoint R(int seq, bool ok, double cePct, double pePct, double ceRes = 1, double peRes = 1) =>
        new(seq, ok, ceRes, peRes, cePct, pePct, cePct - pePct);

    [Fact]
    public void ResidualProjection_DeltasUseOnlyTheImmediatelyPrecedingBar_AndNeverBridgeAGap()
    {
        var readings = new[] { R(1, true, 1.0, -1.0), R(2, true, 3.0, -2.0), R(3, false, 0, 0), R(4, true, 6.0, -5.0), R(5, true, 7.0, -4.0) };
        var d = AdaptiveResidualProjection.Derive(readings, new ResidualBases(100, 100)).ToDictionary(x => x.BarSeq);

        Assert.Null(d[1].CeResidualDeltaPct);                          // no previous bar
        Assert.Equal(2.0, d[2].CeResidualDeltaPct!.Value, 12); Assert.Equal(-1.0, d[2].PeResidualDeltaPct!.Value, 12);
        Assert.Null(d[3].CeResidualDeltaPct);                          // unavailable bar
        Assert.Null(d[4].CeResidualDeltaPct); Assert.Null(d[4].PeResidualDeltaPct); Assert.Null(d[4].AdjacentDirectionalResidualDelta); // NOT bridged to bar 2
        Assert.Equal(1.0, d[5].CeResidualDeltaPct!.Value, 12); Assert.Equal(1.0, d[5].PeResidualDeltaPct!.Value, 12);
    }

    [Fact]
    public void ResidualProjection_AdjacentDirectionalDeltaEqualsCeDeltaMinusPeDelta_Exactly()
    {
        var rng = new Random(4);
        var readings = Enumerable.Range(1, 200).Select(i => R(i, i % 17 != 0, rng.NextDouble() * 10 - 5, rng.NextDouble() * 10 - 5)).ToArray();
        foreach (var d in AdaptiveResidualProjection.Derive(readings, new ResidualBases(120, 90)))
        {
            if (d.CeResidualDeltaPct is { } ce && d.PeResidualDeltaPct is { } pe)
                Assert.Equal(ce - pe, d.AdjacentDirectionalResidualDelta);             // identity holds exactly, not approximately
            else
                Assert.Null(d.AdjacentDirectionalResidualDelta);
        }
    }

    [Fact]
    public void ResidualProjection_StraddleValuesUseTheCommonZeroNineThirtyBaseline()
    {
        var d = AdaptiveResidualProjection.Derive([R(1, true, 2, -1, ceRes: 6, peRes: 2)], new ResidualBases(120, 80)).Single();
        Assert.Equal(100d * (6 + 2) / 200d, d.StraddleResidualPct!.Value, 12);          // (CEres + PEres) / (CEbase + PEbase)
        Assert.Equal(100d * (6 - 2) / 200d, d.StraddleNormalizedDirectionalPct!.Value, 12);
        Assert.Null(AdaptiveResidualProjection.Derive([R(1, true, 2, -1)], new ResidualBases(0, 0)).Single().StraddleResidualPct);
    }

    // ---- helper for the pure PCR tests ----

    static OptionsSupplementalBar Build(IReadOnlyList<OptionBandSideMetrics?> callHistory, IReadOnlyList<OptionBandSideMetrics?> putHistory,
        int window, OptionBandSideMetrics? call, OptionBandSideMetrics? put)
    {
        var strikes = new double[] { 22900, 22950, 23000, 23050, 23100 };
        var expiry = new DateOnly(2026, 10, 12);
        ObserverOptionInstrument[] Side(OptionType t) => strikes.Select(k => new ObserverOptionInstrument((t == OptionType.Call ? "C" : "P") + k, "O", t, k, expiry, 65)).ToArray();
        var calls = Side(OptionType.Call); var puts = Side(OptionType.Put);
        var selection = new OptionBandSelection(T0, expiry, 23000, 23000, strikes, calls, puts);
        return OptionsSupplementalCalculator.Build(new OptionsSupplementalInput
        {
            Selection = selection, UnavailableReason = null, Start = T0, End = T0.AddSeconds(10), DurationSeconds = 10, RiskFreeRate = .065,
            StartUnderlying = 23000, EndUnderlying = 23000,
            StartSnapshot = _ => null, EndSnapshot = t => Snap(t, null, null, null),
            CallBand = call, PutBand = put, CallHistory = callHistory, PutHistory = putHistory, RollingWindowBars = window,
            Universe = calls.Concat(puts).ToArray(), CenterBook = _ => null,
        });
    }
}
