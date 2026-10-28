using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// spot-atm-v1 (the ATM center is the strike nearest the FRESH NIFTY spot, ties to the lower strike), the spot lookup that serves it, the 09:30 anchor under that rule,
/// and the option-strike-v3 Delta / Vega columns. The legacy synthetic-forward rule stays the default of every existing caller and is asserted unchanged here.
/// </summary>
public sealed class SpotAtmRuleTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Day = new(2026, 10, 8);
    static readonly DateOnly Expiry = Day.AddDays(4);

    // ---------------------------------------------------------------- the rule itself

    [Theory]
    [InlineData(26524d, 26500d)]      // 24 from 26500, 26 from 26550
    [InlineData(26526d, 26550d)]
    [InlineData(26525d, 26500d)]      // exact midpoint: the LOWER strike
    [InlineData(26524.95d, 26500d)]
    [InlineData(26525.05d, 26550d)]
    [InlineData(26500d, 26500d)]      // an exact strike selects itself
    [InlineData(26550d, 26550d)]
    [InlineData(26475.01d, 26500d)]
    [InlineData(26474.99d, 26450d)]
    [InlineData(100d, 26400d)]        // below the listed range: the lowest strike
    [InlineData(99999d, 26650d)]      // above the listed range: the highest strike
    public void NearestStrike_PicksTheClosestStrike_AndTiesChooseTheLower(double spot, double expected)
    {
        double[] strikes = [26400, 26450, 26500, 26550, 26600, 26650];
        Assert.Equal(expected, AtmCenterRules.NearestStrike(strikes, spot));
        Assert.Equal(expected, AtmCenterRules.NearestStrike(strikes.Reverse().ToArray(), spot));          // independent of the input order
    }

    [Fact]
    public void Versions_AreExplicit_NullIsLegacy_AndAnUnknownStampIsRefused()
    {
        Assert.Equal("spot-atm-v1", AtmCenterRules.SpotV1Version);
        Assert.Equal(AtmCenterRule.SyntheticForwardLegacy, AtmCenterRules.FromVersion(null));
        Assert.Equal(AtmCenterRule.SyntheticForwardLegacy, AtmCenterRules.FromVersion(""));
        Assert.Equal(AtmCenterRule.SpotV1, AtmCenterRules.FromVersion("spot-atm-v1"));
        Assert.Throws<NotSupportedException>(() => AtmCenterRules.FromVersion("spot-atm-v2"));              // never replay a newer contract under another rule
        Assert.Null(AtmCenterRules.ToVersion(AtmCenterRule.SyntheticForwardLegacy));
        Assert.Equal("spot-atm-v1", AtmCenterRules.ToVersion(AtmCenterRule.SpotV1));
    }

    // ---------------------------------------------------------------- spot freshness (the one existing rule: 0 <= age <= 5 s, inclusive)

    static CleanObserverTick Spot(long id, DateTimeOffset at, double price) => new(id, at, at, price, price - 0.5, price + 0.5, 1, 1, 0, null);

    [Fact]
    public void FreshSpotAt_UsesTheSameInclusiveFiveSecondRuleAsTheBasis()
    {
        var t = new FuturesBasisTracker();
        Assert.Null(t.FreshSpotAt(T0));                                         // missing spot
        t.ObserveSpot(Spot(1, T0, 23000));
        Assert.Equal(23000d, t.FreshSpotAt(T0));                                // age 0
        Assert.Equal(23000d, t.FreshSpotAt(T0.AddSeconds(5)));                  // exactly 5 s old: still fresh
        Assert.Null(t.FreshSpotAt(T0.AddSeconds(5).AddTicks(1)));               // older than 5 s: stale, no cached fallback
        Assert.Null(t.FreshSpotAt(T0.AddSeconds(600)));
        Assert.Equal(FuturesBasisBar.SpotFreshnessSeconds, 5d);                 // there is no second threshold
    }

    [Fact]
    public void FreshSpotAt_NeverReturnsALaterSpot_AndAnswersEarlierInstantsFromTheirOwnState()
    {
        var t = new FuturesBasisTracker();
        t.ObserveSpot(Spot(1, T0, 23000));
        t.ObserveSpot(Spot(2, T0.AddSeconds(3), 23100));
        Assert.Equal(23000d, t.FreshSpotAt(T0.AddSeconds(2)));                  // the 3 s tick had not happened yet
        Assert.Equal(23100d, t.FreshSpotAt(T0.AddSeconds(3)));
        Assert.Null(t.FreshSpotAt(T0.AddSeconds(-1)));                          // before the first spot
    }

    [Fact]
    public void FreshSpotAt_IgnoresOutOfOrderAndNonPositiveSpotTicks()
    {
        var t = new FuturesBasisTracker();
        t.ObserveSpot(Spot(1, T0.AddSeconds(2), 23000));
        t.ObserveSpot(Spot(2, T0, 22000));                                      // older than one already seen: ignored (counted as late)
        t.ObserveSpot(Spot(3, T0.AddSeconds(3), 0));                            // not a price
        t.ObserveSpot(Spot(4, T0.AddSeconds(3), double.NaN));
        Assert.Equal(1, t.LateSpotTicks);
        Assert.Equal(23000d, t.FreshSpotAt(T0.AddSeconds(4)));
    }

    [Fact]
    public void FreshSpotHelper_AgreesWithTheTrackerOnRandomizedStreams_SoThereIsOneFreshnessRule()
    {
        var rng = new Random(11);
        for (var round = 0; round < 40; round++)
        {
            var tracker = new FuturesBasisTracker();
            var observed = new List<(DateTimeOffset At, double Price)>();
            var at = T0;
            for (var i = 0; i < 60; i++)
            {
                at = at.AddSeconds(rng.Next(0, 4) * 0.7);                              // includes equal timestamps
                var valid = rng.Next(0, 8) != 0;
                var price = valid ? 23000 + rng.Next(-80, 80) : (rng.Next(0, 2) == 0 ? 0 : double.NaN);
                tracker.ObserveSpot(Spot(i + 1, at, price));
                observed.Add((at, price));
            }

            for (var q = 0; q < 80; q++)
            {
                var instant = T0.AddSeconds(rng.NextDouble() * 120 - 5);
                Assert.Equal(tracker.FreshSpotAt(instant), AtmCenterRules.FreshSpot(observed, instant));
            }

            // exact boundaries around the last accepted observation
            var last = observed.Last(o => o.Price > 0 && double.IsFinite(o.Price));
            Assert.Equal(tracker.FreshSpotAt(last.At.AddSeconds(5)), AtmCenterRules.FreshSpot(observed, last.At.AddSeconds(5)));
            Assert.Equal(tracker.FreshSpotAt(last.At.AddSeconds(5).AddTicks(1)), AtmCenterRules.FreshSpot(observed, last.At.AddSeconds(5).AddTicks(1)));
        }
    }

    static NiftySignal.Persistence.NiftySignalDbContext SourceDb() => new(new DbContextOptionsBuilder<NiftySignal.Persistence.NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    static NiftySignal.Domain.Entities.Instrument Index(string token, string underlying = "NIFTY", InstrumentType type = InstrumentType.Index, DateOnly? day = null, string? symbol = null) => new()
    { Token = token, TradingSymbol = symbol ?? "S" + token, Underlying = underlying, InstrumentType = type, Exchange = Exchange.Nse, AsOfDate = day ?? Day };

    [Fact]
    public async Task SpotResolver_RequiresExactlyOneNiftyIndexOfThatDay_AndIgnoresEverythingElse()
    {
        await using var db = SourceDb();
        db.Instruments.AddRange(Index("SPOT"), Index("BNF", underlying: "BANKNIFTY"), Index("OLD", day: Day.AddDays(-1)), Index("FUT", type: InstrumentType.Future));
        await db.SaveChangesAsync();
        var one = await NiftySignal.Persistence.NiftySpotInstrument.ResolveAsync(db, Day, default);
        Assert.True(one.IsUnique);
        Assert.Equal("SPOT", one.Token);
        Assert.Equal("SSPOT", one.Symbol);
        Assert.Equal(1, one.CandidateCount);

        db.Instruments.Add(Index("SPOT2"));
        await db.SaveChangesAsync();
        var two = await NiftySignal.Persistence.NiftySpotInstrument.ResolveAsync(db, Day, default);
        Assert.False(two.IsUnique);
        Assert.Null(two.Token);
        Assert.Equal(2, two.CandidateCount);                                       // ambiguous: never guessed

        var none = await NiftySignal.Persistence.NiftySpotInstrument.ResolveAsync(db, Day.AddDays(5), default);
        Assert.False(none.IsUnique);
        Assert.Equal(0, none.CandidateCount);
    }

    // ---------------------------------------------------------------- the band calculator

    static List<ObserverOptionInstrument> Chain()
    {
        var chain = new List<ObserverOptionInstrument>();
        for (var strike = 22800d; strike <= 23200d; strike += 50)
        {
            foreach (var side in new[] { OptionType.Call, OptionType.Put })
            {
                chain.Add(new((side == OptionType.Call ? "C" : "P") + strike, "OPT", side, strike, Expiry, 65));
            }
        }

        return chain;
    }

    static CleanObserverTick Quote(long id, DateTimeOffset at, ObserverOptionInstrument i, double vol, long volume = 1000, long oi = 1000, double underlying = 23000)
    {
        var p = AdaptiveResearchPricing.Price(i.OptionType, underlying, i.Strike, TimeToExpiry.YearsUntilExpiry(i.ExpiryDate, at), .065, vol);
        return new(id, at, at, p, p - .01, p + .01, 100, 100, volume, oi);
    }

    static CleanObserverTick Fut(long id, DateTimeOffset at, long volume) => new(id, at, at, 23000, 22999, 23001, 100, 100, volume, 5000);

    static Dictionary<string, OptionQuoteSnapshot> Quotes(DateTimeOffset at, double vol = .20)
    {
        long id = 1;
        return Chain().ToDictionary(i => i.Token, i => { var q = Quote(id++, at, i, vol); return new OptionQuoteSnapshot(i.Token, at, q.Last, q.Bid, q.Ask, q.OpenInterest); }, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(23024d, 23000d)]
    [InlineData(23026d, 23050d)]
    [InlineData(23025d, 23000d)]
    [InlineData(23000d, 23000d)]
    [InlineData(23080d, 23100d)]          // far from the synthetic forward (~23000): the spot decides the center, the forward does not
    [InlineData(22920d, 22900d)]
    public void Select_SpotRule_CentersOnTheSpot_NotOnTheSyntheticForward(double spot, double expectedCenter)
    {
        var quotes = Quotes(T0);
        var selection = AdaptiveOptionBandCalculator.Select(T0, Expiry, 23000, Chain(), quotes, .065, centerRule: AtmCenterRule.SpotV1, spot: spot);
        Assert.NotNull(selection);
        Assert.Equal(expectedCenter, selection!.CenterStrike);
        Assert.Equal(23000d, selection.SyntheticUnderlying, 0);                 // the forward is still computed (it prices IV) and is nowhere near the spot-chosen center for 23080
        Assert.Equal(5, selection.Strikes.Count);
        Assert.Equal(expectedCenter, selection.Strikes[2]);
    }

    [Fact]
    public void Select_SpotRule_WithoutAValidSpot_ProducesNoBand()
    {
        var quotes = Quotes(T0);
        Assert.Null(AdaptiveOptionBandCalculator.Select(T0, Expiry, 23000, Chain(), quotes, .065, centerRule: AtmCenterRule.SpotV1, spot: null));
        Assert.Null(AdaptiveOptionBandCalculator.Select(T0, Expiry, 23000, Chain(), quotes, .065, centerRule: AtmCenterRule.SpotV1, spot: 0));
        Assert.Null(AdaptiveOptionBandCalculator.Select(T0, Expiry, 23000, Chain(), quotes, .065, centerRule: AtmCenterRule.SpotV1, spot: double.NaN));
        Assert.Null(AdaptiveOptionBandCalculator.Select(T0, Expiry, 23000, Chain(), quotes, .065, centerRule: AtmCenterRule.SpotV1, spot: -5));
    }

    [Fact]
    public void Select_LegacyRule_IsUnchanged_AndIgnoresAnySpotPassedIn()
    {
        var quotes = Quotes(T0);
        var legacy = AdaptiveOptionBandCalculator.Select(T0, Expiry, 23000, Chain(), quotes, .065);
        var withSpot = AdaptiveOptionBandCalculator.Select(T0, Expiry, 23000, Chain(), quotes, .065, spot: 23080);
        Assert.Equal(23000d, legacy!.CenterStrike);
        Assert.Equal(legacy.CenterStrike, withSpot!.CenterStrike);
        Assert.Equal(legacy.SyntheticUnderlying, withSpot.SyntheticUnderlying);
    }

    // ---------------------------------------------------------------- the 09:30 residual anchor

    [Fact]
    public void BuildAnchor_SpotRule_UsesTheSpotCenter_AndNoSpotMeansNoAnchor()
    {
        var quotes = Quotes(T0);
        var legacy = OptionResidualModel.BuildAnchor(T0, Expiry, 23000, Chain(), quotes, .065);
        var spot = OptionResidualModel.BuildAnchor(T0, Expiry, 23000, Chain(), quotes, .065, 5d, AtmCenterRule.SpotV1, 23080);
        Assert.Equal(23000d, legacy!.CenterStrike);
        Assert.Equal(23100d, spot!.CenterStrike);
        Assert.Equal(23100d, Assert.Single(spot.Calls, c => c.IsCenterStrike).Instrument.Strike);
        Assert.Equal(legacy.SyntheticUnderlying0930, spot.SyntheticUnderlying0930, 6);     // pricing underlying unchanged
        Assert.Null(OptionResidualModel.BuildAnchor(T0, Expiry, 23000, Chain(), quotes, .065, 5d, AtmCenterRule.SpotV1, null));
    }

    // ---------------------------------------------------------------- through the engine

    static AdaptiveObserverEngine Engine(AtmCenterRule rule, string? spotToken = "SPOT", bool strikes = false, DateTimeOffset? selfAnchorCutoff = null) =>
        new(new AdaptiveSessionDefinition(Day, "FUT", "FUT", Expiry, 65, 100), Expiry, .065, null, T0, Chain(), null, spotToken, strikes, selfAnchorCutoff, rule);

    /// <summary>One availability group: every option quoted at <paramref name="vol"/>, optionally the future (cumulative volume) and a spot tick.</summary>
    static IReadOnlyList<AdaptiveCompletedBarPackage> Step(AdaptiveObserverEngine engine, ref long id, DateTimeOffset at, long? futVolume, double? spot, double vol = .20)
    {
        var group = new List<(string, CleanObserverTick)>();
        foreach (var i in Chain())
        {
            group.Add((i.Token, Quote(id++, at, i, vol, 1000 + (long)(at - T0).TotalSeconds * 3)));
        }

        if (spot is { } s)
        {
            group.Add(("SPOT", Spot(id++, at, s)));
        }

        if (futVolume is { } v)
        {
            group.Add(("FUT", Fut(id++, at, v)));
        }

        return engine.ProcessAvailabilityGroup(group);
    }

    [Fact]
    public void Engine_SpotRule_CentersBothBandContextsOnTheSpotInForceAtThatInstant()
    {
        var engine = Engine(AtmCenterRule.SpotV1);
        long id = 1;
        Assert.Empty(Step(engine, ref id, T0, 1000, spot: 23020));                 // first band context (the bar opens here): spot 23020 -> 23000
        Assert.Empty(Step(engine, ref id, T0.AddSeconds(1.5), null, spot: 23030)); // spot moves while the bar is open
        var bar1 = Assert.Single(Step(engine, ref id, T0.AddSeconds(2), 1100, spot: null));    // bar 1 closes; the next band context is chosen at T0+2 from the 0.5 s old spot 23030 -> 23050
        var bar2 = Assert.Single(Step(engine, ref id, T0.AddSeconds(4), 1200, spot: null));

        Assert.Equal(23000d, bar1.OptionBand.Selection!.CenterStrike);
        Assert.Equal(23050d, bar2.OptionBand.Selection!.CenterStrike);
        Assert.Equal(23000d, bar2.OptionBand.Selection.SyntheticUnderlying, 0);   // the forward is ~23000 for both: it did not choose either center
        Assert.Null(bar1.OptionBand.UnavailableReason);
        Assert.NotNull(bar1.OptionBand.Call);
    }

    [Fact]
    public void Engine_SpotRule_WithNoSpotAtAll_HasNoBandAndAClearReason_NeverFallingBack()
    {
        var engine = Engine(AtmCenterRule.SpotV1);
        long id = 1;
        Step(engine, ref id, T0, 1000, spot: null);
        var bar1 = Assert.Single(Step(engine, ref id, T0.AddSeconds(2), 1100, spot: null));
        var bar2 = Assert.Single(Step(engine, ref id, T0.AddSeconds(4), 1200, spot: null));
        foreach (var bar in new[] { bar1, bar2 })
        {
            Assert.Null(bar.OptionBand.Selection);
            Assert.Null(bar.OptionBand.Call);
            Assert.Null(bar.OptionBand.Put);
            Assert.Equal(AtmCenterRules.SpotUnavailableReason, bar.OptionBand.UnavailableReason);
        }
    }

    [Fact]
    public void Engine_SpotRule_WithoutAFrozenSpotToken_IsUnavailableToo()
    {
        var engine = Engine(AtmCenterRule.SpotV1, spotToken: null);
        long id = 1;
        Step(engine, ref id, T0, 1000, spot: 23020);                               // a spot tick is offered but the engine has no spot identity: it is not even recorded
        var bar1 = Assert.Single(Step(engine, ref id, T0.AddSeconds(2), 1100, spot: 23020));
        Assert.Null(bar1.OptionBand.Selection);
        Assert.Equal(AtmCenterRules.SpotUnavailableReason, bar1.OptionBand.UnavailableReason);
    }

    [Theory]
    [InlineData(5.0, true)]           // exactly 5 s old at the second band context: fresh
    [InlineData(5.001, false)]        // a hair older: stale -> no band for the next bar
    [InlineData(30.0, false)]
    public void Engine_SpotRule_StaleSpotAtTheSecondCallSite_MakesTheNextBandUnavailable(double closeAfterSeconds, bool available)
    {
        var engine = Engine(AtmCenterRule.SpotV1);
        long id = 1;
        Step(engine, ref id, T0, 1000, spot: 23020);                               // the only spot tick
        var bar1 = Assert.Single(Step(engine, ref id, T0.AddSeconds(closeAfterSeconds), 1100, spot: null));
        var bar2 = Assert.Single(Step(engine, ref id, T0.AddSeconds(closeAfterSeconds + 2), 1200, spot: null));
        Assert.Equal(23000d, bar1.OptionBand.Selection!.CenterStrike);             // bar 1's band was chosen at T0 with a fresh spot
        Assert.Equal(available, bar2.OptionBand.Selection is not null);
        if (!available)
        {
            Assert.Equal(AtmCenterRules.SpotUnavailableReason, bar2.OptionBand.UnavailableReason);
        }
    }

    /// <summary>
    /// Audit finding F4 (fixed): a spot observation that reaches the engine AFTER an instant it precedes was already processed used to be applied retroactively, so live (late arrival) and replay (sorted)
    /// chose DIFFERENT centers for the next band and a restart would fail strict core verification. Under spot-atm-v1 it is now refused like every other late input, forcing deterministic recovery
    /// (the worker discards its state and replays the ticks in availability order); the replay itself is unchanged and reproduces the center from ALL the ticks.
    /// </summary>
    [Fact]
    public void ALateSpotTick_IsRefusedUnderTheSpotRule_ForcingDeterministicRecovery_AndReplayOrderStillGivesTheCenterFromAllTicks()
    {
        var replay = Engine(AtmCenterRule.SpotV1);
        long id = 1;
        Step(replay, ref id, T0, 1000, spot: 23020);
        replay.ProcessAvailabilityGroup([("SPOT", Spot(9_000, T0.AddSeconds(1), 23080))]);           // replay order: the +1 s spot is seen before +2 s
        Step(replay, ref id, T0.AddSeconds(2), 1100, spot: null);
        var bar2 = Assert.Single(Step(replay, ref id, T0.AddSeconds(4), 1200, spot: null));
        Assert.Equal(23100d, bar2.OptionBand.Selection!.CenterStrike);

        var live = Engine(AtmCenterRule.SpotV1);
        id = 1;
        Step(live, ref id, T0, 1000, spot: 23020);
        Step(live, ref id, T0.AddSeconds(2), 1100, spot: null);                                      // the +2 s instant is processed first
        var ex = Assert.Throws<InvalidOperationException>(() => live.ProcessAvailabilityGroup([("SPOT", Spot(9_000, T0.AddSeconds(1), 23080))]));
        Assert.Contains("deterministic recovery required", ex.Message);
        Assert.Throws<InvalidOperationException>(() => live.Process("SPOT", Spot(9_001, T0.AddSeconds(2), 23080)));    // the same instant, after it was processed: also late
    }

    [Fact]
    public void ALateSpotTick_UnderTheLegacyRule_StillKeepsTheOldBehaviour_IgnoredWhenOlderThanTheLastSpot_AndNeverThrown()
    {
        var engine = Engine(AtmCenterRule.SyntheticForwardLegacy);
        long id = 1;
        Step(engine, ref id, T0, 1000, spot: 23020);
        Step(engine, ref id, T0.AddSeconds(2), 1100, spot: 23020);
        engine.ProcessAvailabilityGroup([("SPOT", Spot(9_000, T0.AddSeconds(1), 23080))]);           // legacy: the spot only feeds the basis sidecar
        Assert.Equal(1, engine.LateSpotTicks);
    }

    [Fact]
    public void Engine_SpotRule_AcceptsSpotAtTheVeryInstantOfAGroup_BeforeTheOtherRows()
    {
        var engine = Engine(AtmCenterRule.SpotV1);
        long id = 1;
        Step(engine, ref id, T0, 1000, spot: 23020);
        // The spot and the closing future tick share the instant T0+2: the spot is applied first, so the band for the NEXT bar is chosen from it (not from 23020).
        var bar1 = Assert.Single(Step(engine, ref id, T0.AddSeconds(2), 1100, spot: 23080));
        var bar2 = Assert.Single(Step(engine, ref id, T0.AddSeconds(4), 1200, spot: null));
        Assert.Equal(23000d, bar1.OptionBand.Selection!.CenterStrike);
        Assert.Equal(23100d, bar2.OptionBand.Selection!.CenterStrike);
    }

    [Fact]
    public void Engine_LegacyRule_IgnoresSpotCompletely_AndKeepsTheSyntheticForwardCenter()
    {
        var engine = Engine(AtmCenterRule.SyntheticForwardLegacy);
        long id = 1;
        Step(engine, ref id, T0, 1000, spot: 23080);
        var bar1 = Assert.Single(Step(engine, ref id, T0.AddSeconds(2), 1100, spot: 23080));
        Assert.Equal(23000d, bar1.OptionBand.Selection!.CenterStrike);             // by the forward, not by the 23080 spot

        var noSpotToken = Engine(AtmCenterRule.SyntheticForwardLegacy, spotToken: null);
        id = 1;
        Step(noSpotToken, ref id, T0, 1000, spot: null);
        var legacy = Assert.Single(Step(noSpotToken, ref id, T0.AddSeconds(2), 1100, spot: null));
        Assert.Equal(23000d, legacy.OptionBand.Selection!.CenterStrike);           // the default of every existing caller is untouched
    }

    [Fact]
    public void Engine_SelfAnchor_UsesTheSpotCenter_AndIsAbsentWithoutAFreshSpot()
    {
        var cutoff = T0.AddSeconds(3);
        AdaptiveObserverEngine Run(double? spot)
        {
            var engine = Engine(AtmCenterRule.SpotV1, selfAnchorCutoff: cutoff);
            long id = 1;
            Step(engine, ref id, T0, 1000, spot);
            Step(engine, ref id, T0.AddSeconds(2), 1100, spot);
            Step(engine, ref id, cutoff, 1150, spot);                                  // quotes and spot exactly at the cutoff
            Step(engine, ref id, cutoff.AddSeconds(1), 1160, null);                    // the first input strictly after the cutoff freezes the anchor
            return engine;
        }

        var withSpot = Run(23080);
        Assert.True(withSpot.SelfAnchorAttempted);
        Assert.Equal(23100d, withSpot.ResidualAnchor!.CenterStrike);                   // not 23000, which the forward would give

        var without = Run(null);
        Assert.True(without.SelfAnchorAttempted);                                      // attempted once...
        Assert.Null(without.ResidualAnchor);                                           // ...and unavailable: no invented fallback policy
    }

    // ---------------------------------------------------------------- option-strike-v3 Greeks

    static double Phi(double x) => Math.Exp(-0.5 * x * x) / Math.Sqrt(2 * Math.PI);

    /// <summary>Standard normal CDF by Abramowitz-Stegun 26.2.17 (|error| &lt; 7.5e-8): an independent reference, not the production Gaussian code.</summary>
    static double Cdf(double x)
    {
        var t = 1 / (1 + 0.2316419 * Math.Abs(x));
        var p = Phi(x) * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        return x >= 0 ? 1 - p : p;
    }

    static (double Delta, double Vega) Reference(OptionType side, double s, double k, double t, double r, double sigma)
    {
        var d1 = (Math.Log(s / k) + (r + 0.5 * sigma * sigma) * t) / (sigma * Math.Sqrt(t));
        return (side == OptionType.Call ? Cdf(d1) : Cdf(d1) - 1, s * Phi(d1) * Math.Sqrt(t));
    }

    [Fact]
    public void IndependentReference_MatchesPublishedNormalTableValues_AndTheProductionPricing()
    {
        // The reference CDF is checked against standard normal table values (N(0.25) = 0.5987063, N(1) = 0.8413447, N(-1.96) = 0.0249979, N(0) = 0.5).
        Assert.Equal(0.5987063, Cdf(0.25), 6);
        Assert.Equal(0.8413447, Cdf(1.0), 6);
        Assert.Equal(0.0249979, Cdf(-1.96), 6);
        Assert.Equal(0.5, Cdf(0.0), 7);
        Assert.Equal(0.3989423, Phi(0.0), 7);                                      // 1 / sqrt(2 pi)

        // At-the-money, zero rate: d1 = sigma * sqrt(T) / 2, so a one-year 50 % vol option has d1 = 0.25 EXACTLY: delta = N(0.25), put delta = N(0.25) - 1, vega = S * phi(0.25) * 1.
        var call = Reference(OptionType.Call, 100, 100, 1.0, 0.0, 0.50);
        var put = Reference(OptionType.Put, 100, 100, 1.0, 0.0, 0.50);
        Assert.Equal(0.5987063, call.Delta, 6);
        Assert.Equal(-0.4012937, put.Delta, 6);
        Assert.Equal(100 * Phi(0.25), call.Vega, 9);
        Assert.Equal(38.6668, call.Vega, 3);                                       // 100 * 0.3866681 (table phi(0.25))

        // The production pricing implementation reproduces the same independent numbers, and for a non-trivial case it agrees with the reference to the reference's own accuracy.
        var prod = BlackScholes.Calculate(OptionType.Call, 100, 100, 1.0, 0.0, 0.50).Greeks;
        Assert.Equal(call.Delta, prod.Delta, 6);
        Assert.Equal(call.Vega, prod.Vega, 6);
        var general = BlackScholes.Calculate(OptionType.Put, 23010, 23000, 30d / 365d, .065, .17).Greeks;
        var generalRef = Reference(OptionType.Put, 23010, 23000, 30d / 365d, .065, .17);
        Assert.Equal(generalRef.Delta, general.Delta, 6);
        Assert.Equal(generalRef.Vega, general.Vega, 3);
    }

    [Theory]
    [InlineData(AtmCenterRule.SpotV1)]
    [InlineData(AtmCenterRule.SyntheticForwardLegacy)]
    public void Strikes_CarryDeltaAndVegaAtStartAndEnd_ConsistentWithTheSolvedIv_AndChangeIsEndMinusStart(AtmCenterRule rule)
    {
        var engine = Engine(rule, strikes: true);
        long id = 1;
        double? spot = rule == AtmCenterRule.SpotV1 ? 23020 : null;
        Step(engine, ref id, T0, 1000, spot, vol: .20);
        var bar1 = Assert.Single(Step(engine, ref id, T0.AddSeconds(2), 1100, spot, vol: .23));
        var bar2 = Assert.Single(Step(engine, ref id, T0.AddSeconds(4), 1200, spot, vol: .23));
        var startUnderlying = bar1.OptionBand.Selection!.SyntheticUnderlying;       // the underlying IV used at the bar start
        var endUnderlying = bar2.OptionBand.Selection!.SyntheticUnderlying;         // ... and at the bar end (the next selection at the same instant)

        foreach (var token in new[] { "C23000", "P23000", "C23050", "P22950" })
        {
            var row = bar1.OptionStrikes!.Single(x => x.Token == token);
            var side = row.Side;
            Assert.Equal(20d, row.IvStart!.Value, 1);
            Assert.Equal(23d, row.IvEnd!.Value, 1);

            var tStart = TimeToExpiry.YearsUntilExpiry(Expiry, T0);
            var tEnd = TimeToExpiry.YearsUntilExpiry(Expiry, T0.AddSeconds(2));
            var refStart = Reference(side, startUnderlying, row.Strike, tStart, .065, row.IvStart.Value / 100d);
            var refEnd = Reference(side, endUnderlying, row.Strike, tEnd, .065, row.IvEnd.Value / 100d);
            Assert.Equal(refStart.Delta, row.DeltaStart!.Value, 6);
            Assert.Equal(refEnd.Delta, row.DeltaEnd!.Value, 6);
            Assert.Equal(refStart.Vega, row.VegaStart!.Value, 2);                    // vega is large (thousands): 1e-2 absolute is ~1e-6 relative
            Assert.Equal(refEnd.Vega, row.VegaEnd!.Value, 2);
            Assert.Equal(row.DeltaEnd.Value - row.DeltaStart.Value, row.DeltaChange!.Value);   // change = end - start, exactly
            Assert.Equal(row.VegaEnd.Value - row.VegaStart.Value, row.VegaChange!.Value);
            Assert.True(row.VegaStart > 0 && row.VegaEnd > 0);
            Assert.True(side == OptionType.Call ? row.DeltaStart > 0 && row.DeltaStart < 1 : row.DeltaStart < 0 && row.DeltaStart > -1);
        }

        // Call and put at the same strike: delta_call - delta_put = 1 (no dividend yield) and the vegas are equal.
        var ce = bar1.OptionStrikes!.Single(x => x.Token == "C23000");
        var pe = bar1.OptionStrikes!.Single(x => x.Token == "P23000");
        Assert.Equal(1d, ce.DeltaStart!.Value - pe.DeltaStart!.Value, 6);
        Assert.Equal(ce.VegaStart!.Value, pe.VegaStart!.Value, 2);
        Assert.Equal(ce.DeltaChange!.Value, pe.DeltaChange!.Value, 6);               // the same parity holds for the change
        Assert.Equal(OptionStrikeBarMetrics.MetricsVersion, "option-strike-v3");
    }

    [Fact]
    public void Strikes_GreeksAreNullWheneverIvIsNull_NeverZero()
    {
        // Spot rule without a spot: no band, hence no underlying, hence no IV and no Greeks.
        var engine = Engine(AtmCenterRule.SpotV1, strikes: true);
        long id = 1;
        Step(engine, ref id, T0, 1000, spot: null);
        var bar = Assert.Single(Step(engine, ref id, T0.AddSeconds(2), 1100, spot: null));
        Assert.NotEmpty(bar.OptionStrikes!);                                         // the per-contract flow rows are still produced
        Assert.All(bar.OptionStrikes!, r =>
        {
            Assert.Null(r.IvStart); Assert.Null(r.IvEnd); Assert.Null(r.IvChange);
            Assert.Null(r.DeltaStart); Assert.Null(r.DeltaEnd); Assert.Null(r.DeltaChange);
            Assert.Null(r.VegaStart); Assert.Null(r.VegaEnd); Assert.Null(r.VegaChange);
        });
    }

    [Fact]
    public void Strikes_AStaleQuoteAtTheEnd_GivesNoEndGreeksAndNoChange()
    {
        var engine = Engine(AtmCenterRule.SyntheticForwardLegacy, strikes: true);
        long id = 1;
        Step(engine, ref id, T0, 1000, null);
        // Only the future moves: at the bar end every option quote is 8 s old (> 5 s).
        var bar = Assert.Single(engine.ProcessAvailabilityGroup([("FUT", Fut(id++, T0.AddSeconds(8), 1100))]));
        var row = bar.OptionStrikes!.Single(x => x.Token == "C23000");
        Assert.NotNull(row.DeltaStart);
        Assert.Null(row.DeltaEnd); Assert.Null(row.DeltaChange);
        Assert.Null(row.VegaEnd); Assert.Null(row.VegaChange);
    }

    // ---------------------------------------------------------------- persistence

    static AdaptiveObserverDbContext Database() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    internal static AdaptiveCompletedBarPackage OneBar()
    {
        var engine = Engine(AtmCenterRule.SpotV1, strikes: true);
        long id = 1;
        Step(engine, ref id, T0, 1000, 23020, vol: .20);
        return Assert.Single(Step(engine, ref id, T0.AddSeconds(2), 1100, 23020, vol: .23));
    }

    [Fact]
    public async Task V3Rows_RoundTripEveryGreekColumn_AndVerifyOnReplay()
    {
        var package = OneBar();
        await using var db = Database();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyStrikesAsync(db, 1, package, default));
        Assert.All(db.OptionStrikeBars, r => Assert.Equal("option-strike-v3", r.MetricsVersion));

        var stored = await db.OptionStrikeBars.AsNoTracking().SingleAsync(x => x.Token == "C23000");
        var source = package.OptionStrikes!.Single(x => x.Token == "C23000");
        Assert.Equal((source.DeltaStart, source.DeltaEnd, source.DeltaChange, source.VegaStart, source.VegaEnd, source.VegaChange),
            (stored.DeltaStart, stored.DeltaEnd, stored.DeltaChange, stored.VegaStart, stored.VegaEnd, stored.VegaChange));
        Assert.NotNull(stored.DeltaChange);
        Assert.Equal(SupplementalOutcome.Verified, await service.PersistOrVerifyStrikesAsync(db, 1, package, default));

        // A divergence in a Greek column is detected and never overwritten.
        var tracked = await db.OptionStrikeBars.SingleAsync(x => x.Token == "C23000");
        tracked.VegaEnd = (tracked.VegaEnd ?? 0) + 1;
        await db.SaveChangesAsync();
        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyStrikesAsync(db, 1, package, default));
    }

    [Fact]
    public async Task HistoricalV2AndV1Rows_AreNeitherRewrittenNorRelabelled_WhenV3RowsAreWrittenBesideThem()
    {
        var package = OneBar();
        await using var db = Database();
        foreach (var version in new[] { OptionStrikeBarMetrics.PreviousMetricsVersion, OptionStrikeBarMetrics.LegacyMetricsVersion })
        {
            db.OptionStrikeBars.Add(new AdaptiveOptionStrikeBarRow
            {
                SessionId = 1, BarSeq = package.FutureBar.BarSeq, MetricsVersion = version, EndAvailableAtUtc = T0, Expiry = Expiry, Strike = 23000, Side = OptionType.Call,
                Token = "C23000", TradingSymbol = "OPT", IvStart = 20, IvEnd = 23, IvChange = 3,
            });
        }

        await db.SaveChangesAsync();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyStrikesAsync(db, 1, package, default));

        var rows = await db.OptionStrikeBars.AsNoTracking().Where(x => x.Token == "C23000").ToListAsync();
        Assert.Equal(3, rows.Count);
        var v2 = rows.Single(r => r.MetricsVersion == "option-strike-v2");
        var v1 = rows.Single(r => r.MetricsVersion == "option-strike-v1");
        Assert.All(new[] { v1, v2 }, r =>
        {
            Assert.Null(r.DeltaStart); Assert.Null(r.DeltaEnd); Assert.Null(r.DeltaChange);       // no Greeks were fabricated for history
            Assert.Null(r.VegaStart); Assert.Null(r.VegaEnd); Assert.Null(r.VegaChange);
            Assert.Equal(3d, r.IvChange);
        });
        Assert.NotNull(rows.Single(r => r.MetricsVersion == "option-strike-v3").DeltaChange);
    }

    [Fact]
    public async Task SessionRow_RoundTripsTheAtmRuleStamp_AndAnOlderSessionStaysNull()
    {
        await using var db = Database();
        AdaptiveSessionStateRow Session(string model, string? stamp, int day) => new()
        {
            TradeDate = new DateOnly(2026, 10, day), ModelVersion = model, SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0, FutureToken = "FUT", FutureSymbol = "FUT",
            FutureExpiry = Expiry, OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0, EstimatorName = "V1", CreatedAtUtc = T0, AtmRuleVersion = stamp,
        };

        db.Sessions.Add(Session(AdaptiveFixedClockV1.ModelVersion, AtmCenterRules.SpotV1Version, 8));
        db.Sessions.Add(Session("adaptive-v1", null, 7));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal("spot-atm-v1", (await db.Sessions.SingleAsync(x => x.ModelVersion == AdaptiveFixedClockV1.ModelVersion)).AtmRuleVersion);
        Assert.Null((await db.Sessions.SingleAsync(x => x.ModelVersion == "adaptive-v1")).AtmRuleVersion);
    }
}
