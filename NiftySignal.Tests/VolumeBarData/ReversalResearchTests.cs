using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class ReversalResearchTests
{
    static readonly DateOnly Date = new(2026, 9, 8);
    static readonly DateTimeOffset Start = ReversalResearch.At(Date, 9, 15);
    static ReversalResearch.Print Print(int seconds, decimal price = 120, decimal bid = 119, decimal ask = 121) =>
        new(seconds, Start.AddSeconds(seconds), Start.AddSeconds(seconds), price, bid, ask, 650, 650);
    static ReversalResearch.Print PrintV(int seconds, decimal price, long cumulativeVolume, decimal bid = 119, decimal ask = 121) =>
        new(seconds, Start.AddSeconds(seconds), Start.AddSeconds(seconds), price, bid, ask, 650, 650, cumulativeVolume);

    [Fact]
    public void Cadences_BoundariesAndEmptyInterval_DoNotCarryHistory()
    {
        var bars = ReversalResearch.Cadences([Print(0, 999), Print(1, 100), Print(15, 120), Print(16, 130), Print(60, 200)],
            Start, Start.AddSeconds(60), 1, 2);
        Assert.Equal(110m, bars[0].Average);
        Assert.Equal(2, bars[0].Count);
        Assert.Equal(120m, bars[1].Slow);
        Assert.Null(bars[2].Average);
        Assert.Null(bars[3].Slow);
        Assert.False(bars[3].Up);
    }

    [Fact]
    public void Cadences_EightForty_WarmsAtTenMinutesAndNeedsPreviousGap()
    {
        var bars = ReversalResearch.Cadences(Enumerable.Range(1, 41).Select(i => Print(i * 15, 100 + i)).ToList(), Start, Start.AddSeconds(615));
        Assert.Null(bars[38].Slow);
        Assert.Equal(120.5m, bars[39].Slow);
        Assert.Equal(136.5m, bars[39].Fast);
        Assert.False(bars[39].Up);
    }

    [Fact]
    public void Fill_RequiresLaterValidQuote_AndNeverBackdatesExit()
    {
        var prints = new[] { Print(0), Print(1, bid: 0), Print(2), Print(30) };
        Assert.Equal(Start.AddSeconds(2), ReversalResearch.Fill(prints, Start, 65, true)!.Time);
        Assert.Null(ReversalResearch.Fill(prints, Start.AddSeconds(3), 65, true));
        Assert.Equal(Start.AddSeconds(30), ReversalResearch.Fill(prints, Start.AddSeconds(3), 65, false)!.Time);
    }

    [Fact]
    public void CrossSignals_GapAtCrossVersusLaterConfirmation_AreDifferentRules()
    {
        var inst = Instrument("a", 24000);
        var rows = new List<ReversalResearch.Reading>
        {
            new(Start.AddSeconds(15), 120, 1, 120, 119.9m, .1m, true, false),
            new(Start.AddSeconds(30), 124, 1, 124, 120, 4, false, false),
            new(Start.AddSeconds(45), 125, 1, 125, 120, 5, false, false)
        };
        var prints = new[] { Print(15), Print(30), Print(45) };
        Assert.Empty(ReversalResearch.CrossSignals(inst, prints, rows, "C1"));
        Assert.Equal(Start.AddSeconds(15), Assert.Single(ReversalResearch.CrossSignals(inst, prints, rows, "C0")).Time);
        Assert.Equal(Start.AddSeconds(30), Assert.Single(ReversalResearch.CrossSignals(inst, prints, rows, "C2")).Time);
    }

    [Fact]
    public void MomentumSignals_FiresOnBareTwoMinuteLookback_NoMaOrHurdle()
    {
        var inst = Instrument("a", 24000);
        // 8-bucket lookback: bar[8].Average (125) > bar[0].Average (120) -> fires at bar[8]'s End.
        // bar[9].Average (125) == bar[1].Average (125) -> not strictly greater, does not fire.
        var rows = new List<ReversalResearch.Reading>();
        for (var i = 0; i <= 9; i++)
        {
            var avg = i == 0 ? 120m : 125m;
            rows.Add(new(Start.AddSeconds((i + 1) * 15), avg, 1, avg, null, null, false, false));
        }
        var prints = Enumerable.Range(0, 10).Select(i => Print((i + 1) * 15)).ToArray();
        var signals = ReversalResearch.MomentumSignals(inst, prints, rows);
        var signal = Assert.Single(signals);
        Assert.Equal(Start.AddSeconds(9 * 15), signal.Time);
        Assert.Equal("Call", signal.Side);
    }

    [Fact]
    public void MomentumSignals_SkipsStaleOrIlliquidQuotes_AndOutOfBandPrice()
    {
        var inst = Instrument("a", 24000);
        var rows = new List<ReversalResearch.Reading>
        {
            new(Start.AddSeconds(15), 100, 1, null, null, null, false, false),
            new(Start.AddSeconds(135), 200, 1, null, null, null, false, false),
        };
        var stale = new[] { Print(15) };
        Assert.Empty(ReversalResearch.MomentumSignals(inst, stale, rows, lookback: 1));

        var outOfBand = new[] { Print(15, ask: 200), Print(135, ask: 200) };
        Assert.Empty(ReversalResearch.MomentumSignals(inst, outOfBand, rows, lookback: 1));
    }

    [Fact]
    public void MomentumDownTimes_MirrorsEntryCondition_FiresWhenAverageDropsBelowLookback()
    {
        var rows = new List<ReversalResearch.Reading>
        {
            new(Start.AddSeconds(15), 120, 1, null, null, null, false, false),
            new(Start.AddSeconds(30), 110, 1, null, null, null, false, false),
            new(Start.AddSeconds(45), 130, 1, null, null, null, false, false),
        };
        var times = ReversalResearch.MomentumDownTimes(rows, lookback: 1);
        Assert.Equal([Start.AddSeconds(30)], times);
    }

    [Fact]
    public async Task PerStrikeCadence_EntryInFirstStrike_DoesNotSkipSecondStrikeObservation()
    {
        await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Instruments.AddRange(Instrument("a", 24000), Instrument("b", 24100));
        foreach (var (token, prices) in new[] { ("a", new[] { 100m, 90, 110, 80, 80, 80 }), ("b", new[] { 100m, 90, 110, 100, 120, 80 }) })
        {
            for (int i = 0; i < prices.Length; i++)
            {
                db.Ticks.Add(new Tick { Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = Start.AddSeconds((i + 1) * 15),
                    ReceivedAt = Start.AddSeconds((i + 1) * 15), LastPrice = prices[i] });
            }
        }
        await db.SaveChangesAsync();
        var trades = await PerStrikeCadenceSimulator.SimulateDayAsync(db, Date, OptionType.Call, CancellationToken.None, fastBars: 1, slowBars: 2);
        var second = Assert.Single(trades, t => t.Token == "b");
        Assert.Equal(Start.AddSeconds(75), second.EntryTime);
    }

    [Fact]
    public void Cadences_LateReceipt_IsNotAvailableAtBoundary()
    {
        var ticks = new[] { Print(14, 100), Print(15, 999) with { Received = Start.AddSeconds(16) }, Print(30, 110) };
        var bars = ReversalResearch.Cadences(ticks, Start, Start.AddSeconds(30), 1, 2);
        Assert.Equal(100m, bars[0].Average);
        Assert.Equal(100m, ReversalResearch.Before(ticks, Start.AddSeconds(15))!.Price);
        Assert.Equal(105m, bars[1].Slow);
    }

    [Fact]
    public void Fill_LateReceipt_UsesActualAvailabilityAndRespectsEntryExpiry()
    {
        var tick = Print(2) with { Received = Start.AddSeconds(20) };
        Assert.Null(ReversalResearch.Fill([tick], Start, 65, true));
        Assert.Equal(Start.AddSeconds(20), ReversalResearch.Fill([tick], Start, 65, false)!.Time);
    }

    [Fact]
    public void SimulateGiveback_ExitsOnceRetracementClearsHalfOfPeak()
    {
        var inst = Instrument("a", 24000);
        var instruments = new Dictionary<string, Instrument> { ["a"] = inst };
        var prints = new[]
        {
            Print(0),                                    // decision tick
            Print(1, price: 121, bid: 119, ask: 121),     // entry fill: buy = 121 + .05 = 121.05
            Print(15, price: 141, bid: 140, ask: 141),    // peak favorable gain ~19.95
            Print(30, price: 131, bid: 130, ask: 131),    // gain ~9.95 -- retraced exactly 50% of peak
            Print(45, price: 200, bid: 199, ask: 201),    // must never be reached
        };
        var signals = new List<ReversalResearch.Signal> { new(Start, "A", "a", "PatternA", 0) };
        var ticks = new Dictionary<string, List<ReversalResearch.Print>> { ["a"] = prints.ToList() };

        var sim = ReversalResearch.SimulateGiveback(Date, signals, instruments, ticks, _ => [], 0.5m);

        var trade = Assert.Single(sim.Trades);
        Assert.Equal("GivebackExit", trade.Reason);
        Assert.Equal(Start.AddSeconds(30), trade.ExitDecision);
    }

    [Fact]
    public void SimulateGiveback_NoQualifyingRetracement_FallsBackToOppositePatternExit()
    {
        var inst = Instrument("a", 24000);
        var instruments = new Dictionary<string, Instrument> { ["a"] = inst };
        var prints = new[]
        {
            Print(0),
            Print(1, price: 121, bid: 119, ask: 121),   // entry fill
            Print(15, price: 141, bid: 140, ask: 141),  // peak ~19.95
            Print(30, price: 138, bid: 137, ask: 138),  // gain ~16.95 -- only ~15% given back, not 50%
            Print(35, price: 138, bid: 137, ask: 138),  // exit fill tick (>=1s after the exit decision)
        };
        var signals = new List<ReversalResearch.Signal> { new(Start, "A", "a", "PatternA", 0) };
        var ticks = new Dictionary<string, List<ReversalResearch.Print>> { ["a"] = prints.ToList() };
        var oppositeAt = Start.AddSeconds(30);

        var sim = ReversalResearch.SimulateGiveback(Date, signals, instruments, ticks, _ => [oppositeAt], 0.5m);

        var trade = Assert.Single(sim.Trades);
        Assert.Equal("PremiseReversed", trade.Reason);
        Assert.Equal(oppositeAt, trade.ExitDecision);
    }

    [Fact]
    public void SimulateGivebackFixedEntries_ReusesBaselineEntryVerbatim_OnlyExitChanges()
    {
        var inst = Instrument("a", 24000);
        var instruments = new Dictionary<string, Instrument> { ["a"] = inst };
        var prints = new[]
        {
            Print(0),
            Print(1, price: 121, bid: 119, ask: 121),    // baseline's own entry fill: buy = 121.05
            Print(15, price: 141, bid: 140, ask: 141),   // peak ~19.95
            Print(30, price: 131, bid: 130, ask: 131),   // retraced 50% -- giveback exit fires here
            Print(600, price: 200, bid: 199, ask: 201),  // baseline's own (much later) exit; never reached under giveback
        };
        var ticks = new Dictionary<string, List<ReversalResearch.Print>> { ["a"] = prints.ToList() };
        var baseline = new List<ReversalResearch.Trade>
        {
            new("A", "a", Start, Start.AddSeconds(1), Start.AddSeconds(600), Start.AddSeconds(600),
                121.05m, 200m, 65, 0, 0, 0, 0, 0, 599, "ScheduledClose", 1, 4)
        };

        var sim = ReversalResearch.SimulateGivebackFixedEntries(Date, baseline, instruments, ticks, _ => [], 0.5m);

        // Same entry count and fields as the baseline (this is the whole point of "fixed entries") --
        // only the exit differs.
        var trade = Assert.Single(sim.Trades);
        Assert.Equal(baseline[0].Decision, trade.Decision);
        Assert.Equal(baseline[0].Entry, trade.Entry);
        Assert.Equal(baseline[0].Buy, trade.Buy);
        Assert.Equal(baseline[0].EntryId, trade.EntryId);
        Assert.Equal("GivebackExit", trade.Reason);
        Assert.Equal(Start.AddSeconds(30), trade.ExitDecision);
    }

    [Fact]
    public void OptionVolumeBars_ClosesExactlyWhenCumulativeVolumeReachesThreshold()
    {
        // Cumulative volume: 100 -> 100 (no delta) -> 400 (+300) -> 700 (+300, crosses 600 here).
        var ticks = new[] { PrintV(0, 100, 100), PrintV(1, 105, 100), PrintV(2, 110, 400), PrintV(3, 115, 700) };
        var bars = ReversalResearch.OptionVolumeBars(ticks, threshold: 600);
        var bar = Assert.Single(bars);
        Assert.Equal(115m, bar.ClosePrice);
        Assert.True(bar.Volume >= 600);
    }

    [Fact]
    public void OptionVolumeBars_BelowThreshold_ProducesNoBar()
    {
        var ticks = new[] { PrintV(0, 100, 100), PrintV(1, 105, 300) };
        Assert.Empty(ReversalResearch.OptionVolumeBars(ticks, threshold: 600));
    }

    [Fact]
    public void BuildTimeBasedFutureBars_SkipsEmptyBucket_NeverFabricatesABar()
    {
        var ticks = new[] { Print(0, 100), Print(1, 110), Print(65, 200) }; // bucket [30,60) has no ticks
        var bars = ReversalResearch.BuildTimeBasedFutureBars(ticks.ToList(), Start, Start.AddSeconds(90), bucketSeconds: 30);
        Assert.Equal(2, bars.Count);
        Assert.Equal(110m, bars[0].Close);
        Assert.Equal(200m, bars[1].Close);
    }

    [Fact]
    public void ReadingsFromBars_SameFastSlowGapSemanticsAsCadences()
    {
        var bars = Enumerable.Range(1, 3).Select(i =>
            new NiftySignal.Features.VolumeBar(Start.AddSeconds(i), Start.AddSeconds(i + 1), 100 + i, 100 + i, 100 + i, 100 + i,
                600, null, null, null, null, null, null, 1)).ToList();
        var readings = ReversalResearch.ReadingsFromBars(bars, fast: 1, slow: 2);
        Assert.Equal(101m, readings[0].Fast); // only 1 bar so far, fast(1)=that bar's close
        Assert.Null(readings[0].Slow); // needs 2 bars
        Assert.Equal(102m, readings[1].Fast);
        Assert.Equal(101.5m, readings[1].Slow); // avg of bars 1-2
    }

    static Instrument Instrument(string token, decimal strike) => new()
    {
        Token = token, TradingSymbol = token, Exchange = Exchange.Nfo, Underlying = "NIFTY", AsOfDate = Date,
        InstrumentType = InstrumentType.Option, OptionType = OptionType.Call, ExpiryDate = Date, StrikePrice = strike, LotSize = 65, TickSize = .05m
    };
}
