using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-23 correctness-review investigation (docs/Price_Based_Findings.md, "Investigating the
/// zero-trade result" section). Tests the four invariants the user's own investigation explicitly
/// asked to be locked down with tests: (1) a trade never switches option token after entry, (2) a
/// signal window never silently contains prices from multiple contracts, (3) ATM changes are
/// explicitly handled (never implicitly mixed), (4) Reset() behavior is deterministic.
/// <see cref="PriceCrossoverEngineTests"/> already covers (4) directly at the engine level
/// (Reset_ClearsWindowAndEmaState_.../Reset_ThenFullRewarm_...); this file covers (1)-(3).
/// </summary>
public class PriceCrossoverSignalArchitectureTests
{
    // --- (3) ATM changes explicitly handled: the pure decision function, exhaustive truth table ---

    [Fact]
    public void ShouldResetOnStrikeChange_SameStrike_ReturnsFalse()
        => Assert.False(SignalArchitectureAudit.ShouldResetOnStrikeChange(23500m, 23500m));

    [Fact]
    public void ShouldResetOnStrikeChange_DifferentStrike_ReturnsTrue()
        => Assert.True(SignalArchitectureAudit.ShouldResetOnStrikeChange(23500m, 23450m));

    [Fact]
    public void ShouldResetOnStrikeChange_NoPriorStrikeYet_NeverTreatedAsAChange()
        // Null "last" means "nothing seen yet" -- the very first bar must never look like a roll,
        // matching PriceCrossoverEngine's own "no fabricated warm-up" convention.
        => Assert.False(SignalArchitectureAudit.ShouldResetOnStrikeChange(null, 23500m));

    [Fact]
    public void ShouldResetOnStrikeChange_NoCurrentStrikeThisBar_NeverTreatedAsAChange()
        // A bar with no resolvable ATM (missing data) must not silently reset state that's still valid.
        => Assert.False(SignalArchitectureAudit.ShouldResetOnStrikeChange(23500m, null));

    // --- (1) Trade instrument immutability + (2) signal window never mixes contracts, end to end ---

    static VolumeBarRow Bar(int index, decimal futureClose, DateTimeOffset end) => new()
    {
        AsOfDate = new DateOnly(2026, 9, 8),
        BarIndex = index,
        BarVolumeThreshold = 650,
        StartTimestamp = end.AddMinutes(-5),
        EndTimestamp = end,
        OpenPrice = futureClose,
        HighPrice = futureClose,
        LowPrice = futureClose,
        ClosePrice = futureClose,
        Volume = 650,
        TickCount = 10,
        DurationSeconds = 1,
    };

    static Instrument PutInstrument(string token, decimal strike) => new()
    {
        Token = token,
        Exchange = Exchange.Nfo,
        TradingSymbol = token,
        InstrumentType = InstrumentType.Option,
        OptionType = OptionType.Put,
        StrikePrice = strike,
        ExpiryDate = new DateOnly(2026, 9, 9),
        Underlying = "NIFTY",
        LotSize = 65,
        TickSize = 0.05m,
        AsOfDate = new DateOnly(2026, 9, 8),
    };

    static NiftySignalDbContext NewSourceDb() =>
        new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"src-{Guid.NewGuid()}").Options);

    static VolumeBarDbContext NewVolumeBarDb() =>
        new(new DbContextOptionsBuilder<VolumeBarDbContext>().UseInMemoryDatabase($"vb-{Guid.NewGuid()}").Options);

    static void AddTick(NiftySignalDbContext db, string token, DateTimeOffset ts, decimal price) =>
        db.Ticks.Add(new Tick { Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = ts, ReceivedAt = ts, LastPrice = price });

    /// <summary>
    /// The exact scenario the user's own example described ("don't buy 24500CE and track 24400CE"):
    /// the ATM strike genuinely rolls to a DIFFERENT contract on the very last bar, while a position
    /// opened on the ORIGINAL contract is still held. The new ATM contract's price is set to an
    /// absurd, unmistakable value (5.00, vs the original contract's own real continuing price of
    /// 600.00) specifically so that if entry/exit price tracking ever silently switched tokens, this
    /// test would fail loudly rather than pass by coincidence.
    /// </summary>
    [Fact]
    public async Task SimulatePriceCrossoverDayAsync_TradeStaysOnEntryToken_EvenWhenAtmRollsToADifferentContractBeforeExit()
    {
        await using var source = NewSourceDb();
        await using var volumeBars = NewVolumeBarDb();

        var dayStart = new DateTimeOffset(2026, 9, 8, 4, 0, 0, TimeSpan.Zero); // 09:30 IST
        DateTimeOffset At(int minutesFromStart) => dayStart.AddMinutes(minutesFromStart);

        // Strike A (23500, the ORIGINAL/entry contract): flat [100,100,100] primes the window with
        // zero diff, then a jump to 500 fires CrossedUp (fastBars=1/slowBars=2/threshold=0, so ANY
        // sign flip qualifies) -- opens a Put position at A. Its own price keeps moving for real
        // (600 on the final bar) -- this is what the exit price MUST equal.
        AddTick(source, "PA", At(0), 100m);
        AddTick(source, "PA", At(5), 100m);
        AddTick(source, "PA", At(10), 100m);
        AddTick(source, "PA", At(15), 500m); // entry bar
        AddTick(source, "PA", At(20), 600m); // final bar -- the CORRECT exit price

        // Strike B (23400, becomes ATM on the FINAL bar only): a deliberately absurd price that must
        // NEVER appear as this trade's exit price.
        AddTick(source, "PB", At(20), 5m);

        await source.SaveChangesAsync();
        source.Instruments.AddRange(PutInstrument("PA", 23500m), PutInstrument("PB", 23400m));
        await source.SaveChangesAsync();

        // Future stays pinned to A's strike (23500) for bars 0-3, then jumps to sit exactly on B's
        // strike (23400) for the final bar -- forces the SIGNAL's ATM selection to roll right at the
        // moment the position is being held, the precise scenario under test.
        volumeBars.VolumeBars.AddRange(
            Bar(0, 23500m, At(0)),
            Bar(1, 23500m, At(5)),
            Bar(2, 23500m, At(10)),
            Bar(3, 23500m, At(15)),
            Bar(4, 23400m, At(20)));
        await volumeBars.SaveChangesAsync();

        var trades = await TradeSimulator.SimulatePriceCrossoverDayAsync(
            source, volumeBars, new DateOnly(2026, 9, 8), barVolumeThreshold: 650,
            side: PriceCrossoverSide.Put, fastBars: 1, slowBars: 2, thresholdFraction: 0.0,
            cancellationToken: CancellationToken.None);

        var trade = Assert.Single(trades);
        Assert.Equal(23500m, trade.StrikePrice); // entered on A, not B
        Assert.Equal(500m, trade.EntryPrice); // A's own entry price
        Assert.Equal(600m, trade.ExitPrice); // A's own final price -- NOT B's 5.00
    }
}
