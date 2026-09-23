using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Execution;
using NiftySignal.Persistence;
using NiftySignal.Rules;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// Correction (2026-09-22, see docs/VOLUME_BAR_FINDINGS.md's dated "Correction" section): coverage
/// for the two Experiment 5/6 bug fixes in <see cref="Experiment5OptionTradeSimulator"/> --
/// (1) <see cref="Experiment5OptionTradeSimulator.SelectNonOverlapping"/>, the single-open-trade
/// gate that replaces the old "one trade per qualifying bar, no overlap check" behavior that
/// produced 269-513 overlapping "trades" on a single day; (2) the premium-band [100,150] strike
/// search in <see cref="Experiment5OptionTradeSimulator.BuildTradeAsync"/>, which replaces the old
/// pure synthetic-forward ATM strike lookup.
/// </summary>
public class Experiment5OptionTradeSimulatorTests
{
    static Experiment5UnderlyingAnalyzer.QualifyingBar Qb(int barIndex, int direction = 1, decimal? atmStrike = null) => new(
        new DateOnly(2026, 9, 8), barIndex, 650, new DateTimeOffset(2026, 9, 8, 4, 0, barIndex, TimeSpan.Zero),
        100m, direction, "Mid(10:00-13:30)", 5, false, atmStrike, []);

    // --- SelectNonOverlapping (single-open-trade gate) ---

    [Fact]
    public void SelectNonOverlapping_KeepsFirstBar_SkipsBarsWithinItsHoldingHorizon()
    {
        // horizon=10: a trade opened at bar 5 holds through bar 15 (5+10). Bars 6, 10, 15 all fall
        // within [5,15] and must be skipped; bar 16 is strictly after and must be kept.
        var quals = new List<Experiment5UnderlyingAnalyzer.QualifyingBar>
        {
            Qb(5), Qb(6), Qb(10), Qb(15), Qb(16),
        };

        var result = Experiment5OptionTradeSimulator.SelectNonOverlapping(quals, horizon: 10);

        Assert.Equal([5, 16], result.Select(q => q.BarIndex));
    }

    [Fact]
    public void SelectNonOverlapping_ChainsCorrectly_ThirdTradeOpensAfterSecondClosesNotFirst()
    {
        // bar 0 opens, holds to bar 10 (horizon=10). bar 12 is past that, opens a NEW trade holding
        // to bar 22. bar 20 falls inside [12,22] (opened by bar 12, not bar 0) and must be skipped.
        var quals = new List<Experiment5UnderlyingAnalyzer.QualifyingBar>
        {
            Qb(0), Qb(12), Qb(20), Qb(23),
        };

        var result = Experiment5OptionTradeSimulator.SelectNonOverlapping(quals, horizon: 10);

        Assert.Equal([0, 12, 23], result.Select(q => q.BarIndex));
    }

    [Fact]
    public void SelectNonOverlapping_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(Experiment5OptionTradeSimulator.SelectNonOverlapping([], horizon: 10));
    }

    [Fact]
    public void SelectNonOverlapping_NoOverlaps_KeepsEveryBar()
    {
        var quals = new List<Experiment5UnderlyingAnalyzer.QualifyingBar> { Qb(0), Qb(20), Qb(40) };

        var result = Experiment5OptionTradeSimulator.SelectNonOverlapping(quals, horizon: 10);

        Assert.Equal([0, 20, 40], result.Select(q => q.BarIndex));
    }

    // --- Premium-band strike selection ---

    static VolumeBarRow Bar(int index, decimal close) => new()
    {
        AsOfDate = new DateOnly(2026, 9, 8),
        BarIndex = index,
        BarVolumeThreshold = 650,
        // Wide (10-minute) bars so the signal (4:05:00) and exit ticks (a few seconds after) both
        // fall inside [dayStart, dayEnd] -- BuildTradeAsync only loads ticks in that window.
        StartTimestamp = new DateTimeOffset(2026, 9, 8, 4, 10 * index, 0, TimeSpan.Zero),
        EndTimestamp = new DateTimeOffset(2026, 9, 8, 4, 10 * (index + 1), 0, TimeSpan.Zero),
        OpenPrice = close,
        HighPrice = close,
        LowPrice = close,
        ClosePrice = close,
        Volume = 650,
        TickCount = 10,
        DurationSeconds = 1,
    };

    static Instrument CallInstrument(string token, decimal strike) => new()
    {
        Token = token,
        Exchange = Exchange.Nfo,
        TradingSymbol = token,
        InstrumentType = InstrumentType.Option,
        OptionType = OptionType.Call,
        StrikePrice = strike,
        ExpiryDate = new DateOnly(2026, 9, 9),
        Underlying = "NIFTY",
        LotSize = 65,
        TickSize = 0.05m,
        AsOfDate = new DateOnly(2026, 9, 8),
    };

    static NiftySignalDbContext NewSourceDb() =>
        new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"e5trade-{Guid.NewGuid()}").Options);

    static void AddTick(NiftySignalDbContext db, string token, DateTimeOffset ts, decimal price) =>
        db.Ticks.Add(new Tick { Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = ts, ReceivedAt = ts, LastPrice = price });

    [Fact]
    public async Task BuildTradeAsync_PicksStrikeWhoseOwnPremiumFallsInBand_NotTheOldAtmStrike()
    {
        await using var db = NewSourceDb();
        // Three candidate call strikes: 23900 (premium 200, OUT of band), 24000 (premium 120, IN
        // band -- the old "ATM" strike too, so this also exercises the tie-break-by-proximity path
        // trivially since it's the only in-band strike), 24100 (premium 40, OUT of band).
        var sig = new DateTimeOffset(2026, 9, 8, 4, 5, 0, TimeSpan.Zero);
        AddTick(db, "C23900", sig, 200m);
        AddTick(db, "C24000", sig, 120m);
        AddTick(db, "C24100", sig, 40m);
        // Exit prints for the 24000 strike only (the one that should get selected).
        AddTick(db, "C24000", sig.AddSeconds(20), 130m);
        await db.SaveChangesAsync();

        var chain = new List<Instrument> { CallInstrument("C23900", 23900m), CallInstrument("C24000", 24000m), CallInstrument("C24100", 24100m) };
        var dayBars = new List<VolumeBarRow> { Bar(0, 24000m), Bar(1, 24010m) };
        var qb = new Experiment5UnderlyingAnalyzer.QualifyingBar(
            new DateOnly(2026, 9, 8), 0, 650, sig, 24000m, Direction: -1 /* -> Call */, "Mid(10:00-13:30)", 5, false,
            AtmStrike: 24000m, []);

        var trade = await Experiment5OptionTradeSimulator.BuildTradeAsync(
            db, qb, horizon: 1, dayBars, chain, new Dictionary<int, OptionAtmBarRow>(),
            new Dictionary<(DateOnly, string), OptionPriceSeries>(), new CostsConfig(20m, 2), quantity: 65, CancellationToken.None);

        Assert.NotNull(trade);
        Assert.Equal(24000m, trade!.Strike);
        Assert.Equal(120m, trade.EntryPriceGross);
        Assert.Equal(130m, trade.ExitPriceGross);
    }

    [Fact]
    public async Task BuildTradeAsync_TieBreak_PicksInBandStrikeNearestOldAtmStrike()
    {
        await using var db = NewSourceDb();
        var sig = new DateTimeOffset(2026, 9, 8, 4, 5, 0, TimeSpan.Zero);
        // Two strikes both land in-band: 23950 (premium 110) and 24100 (premium 140). Old ATM
        // strike is 24000 -- 23950 is closer (50 away vs 100 away), so it must win the tie-break.
        AddTick(db, "C23950", sig, 110m);
        AddTick(db, "C24100", sig, 140m);
        AddTick(db, "C23950", sig.AddSeconds(20), 115m);
        AddTick(db, "C24100", sig.AddSeconds(20), 145m);
        await db.SaveChangesAsync();

        var chain = new List<Instrument> { CallInstrument("C23950", 23950m), CallInstrument("C24100", 24100m) };
        var dayBars = new List<VolumeBarRow> { Bar(0, 24000m), Bar(1, 24010m) };
        var qb = new Experiment5UnderlyingAnalyzer.QualifyingBar(
            new DateOnly(2026, 9, 8), 0, 650, sig, 24000m, Direction: -1, "Mid(10:00-13:30)", 5, false,
            AtmStrike: 24000m, []);

        var trade = await Experiment5OptionTradeSimulator.BuildTradeAsync(
            db, qb, horizon: 1, dayBars, chain, new Dictionary<int, OptionAtmBarRow>(),
            new Dictionary<(DateOnly, string), OptionPriceSeries>(), new CostsConfig(20m, 2), quantity: 65, CancellationToken.None);

        Assert.NotNull(trade);
        Assert.Equal(23950m, trade!.Strike);
    }

    [Fact]
    public async Task BuildTradeAsync_NoStrikeInBand_ReturnsNull_DoesNotForceAMismatchedStrike()
    {
        await using var db = NewSourceDb();
        var sig = new DateTimeOffset(2026, 9, 8, 4, 5, 0, TimeSpan.Zero);
        // Every candidate strike's premium is outside [100,150].
        AddTick(db, "C23900", sig, 300m);
        AddTick(db, "C24100", sig, 20m);
        await db.SaveChangesAsync();

        var chain = new List<Instrument> { CallInstrument("C23900", 23900m), CallInstrument("C24100", 24100m) };
        var dayBars = new List<VolumeBarRow> { Bar(0, 24000m), Bar(1, 24010m) };
        var qb = new Experiment5UnderlyingAnalyzer.QualifyingBar(
            new DateOnly(2026, 9, 8), 0, 650, sig, 24000m, Direction: -1, "Mid(10:00-13:30)", 5, false,
            AtmStrike: 24000m, []);

        var trade = await Experiment5OptionTradeSimulator.BuildTradeAsync(
            db, qb, horizon: 1, dayBars, chain, new Dictionary<int, OptionAtmBarRow>(),
            new Dictionary<(DateOnly, string), OptionPriceSeries>(), new CostsConfig(20m, 2), quantity: 65, CancellationToken.None);

        Assert.Null(trade);
    }

    [Fact]
    public async Task BuildTradeAsync_NoAtmStrikeAvailable_FallsBackToBandMidpointForTieBreak()
    {
        await using var db = NewSourceDb();
        var sig = new DateTimeOffset(2026, 9, 8, 4, 5, 0, TimeSpan.Zero);
        // Band midpoint is 125. 23950 (premium 110, |110-125|=15) beats 24100 (premium 140,
        // |140-125|=15)... make it unambiguous: 23950 premium 120 (dist 5) vs 24100 premium 149 (dist 24).
        AddTick(db, "C23950", sig, 120m);
        AddTick(db, "C24100", sig, 149m);
        AddTick(db, "C23950", sig.AddSeconds(20), 121m);
        AddTick(db, "C24100", sig.AddSeconds(20), 150m);
        await db.SaveChangesAsync();

        var chain = new List<Instrument> { CallInstrument("C23950", 23950m), CallInstrument("C24100", 24100m) };
        var dayBars = new List<VolumeBarRow> { Bar(0, 24000m), Bar(1, 24010m) };
        var qb = new Experiment5UnderlyingAnalyzer.QualifyingBar(
            new DateOnly(2026, 9, 8), 0, 650, sig, 24000m, Direction: -1, "Mid(10:00-13:30)", 5, false,
            AtmStrike: null /* no ATM row for this bar -- fall back to band midpoint */, []);

        var trade = await Experiment5OptionTradeSimulator.BuildTradeAsync(
            db, qb, horizon: 1, dayBars, chain, new Dictionary<int, OptionAtmBarRow>(),
            new Dictionary<(DateOnly, string), OptionPriceSeries>(), new CostsConfig(20m, 2), quantity: 65, CancellationToken.None);

        Assert.NotNull(trade);
        Assert.Equal(23950m, trade!.Strike);
    }
}
