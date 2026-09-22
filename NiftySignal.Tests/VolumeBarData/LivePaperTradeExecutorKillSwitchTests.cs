using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// Phase G of docs/LIVE_PARITY_PLAN.md: proves the new <see cref="LiveKillSwitchState"/> switch
/// actually gates <see cref="LivePaperTradeExecutor.OpenAsync"/> (new entries only) without touching
/// <see cref="LivePaperTradeExecutor.CloseAsync"/> (an already-open position's own exit), matching
/// the same "new-entries-only" design the existing <c>KillSwitchState</c> already established for
/// the three legacy/Core-score trading engines (see <c>CLAUDE.md</c>'s "Risk" section).
/// </summary>
public class LivePaperTradeExecutorKillSwitchTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 16);
    const long Threshold = 2600L;
    const string CallToken = "999001";

    static (NiftySignalDbContext Source, VolumeBarDbContext VolumeBars) NewDbs()
    {
        var suffix = Guid.NewGuid().ToString();
        var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{suffix}").Options);
        var volumeBars = new VolumeBarDbContext(new DbContextOptionsBuilder<VolumeBarDbContext>().UseInMemoryDatabase($"volumebars-{suffix}").Options);

        source.Instruments.Add(new Instrument
        {
            Token = CallToken,
            Exchange = Exchange.Nfo,
            TradingSymbol = "NIFTY24000CE",
            InstrumentType = InstrumentType.Option,
            OptionType = OptionType.Call,
            StrikePrice = 24000m,
            ExpiryDate = new DateOnly(2026, 9, 18),
            Underlying = "NIFTY",
            AsOfDate = AsOfDate,
        });
        source.Ticks.Add(new Tick
        {
            Token = CallToken,
            Exchange = Exchange.Nfo,
            ExchangeTimestamp = AsOfDate.ToDateTime(new TimeOnly(9, 30)).ToUniversalTime(),
            ReceivedAt = AsOfDate.ToDateTime(new TimeOnly(9, 30)).ToUniversalTime(),
            LastPrice = 125.50m,
        });
        source.SaveChanges();

        return (source, volumeBars);
    }

    static LiveSignalOpened Signal(int entryBarIndex = 0) => new(
        EntryBarIndex: entryBarIndex,
        EntryTimestamp: AsOfDate.ToDateTime(new TimeOnly(9, 30)).ToUniversalTime(),
        Side: OptionType.Call,
        EntryScore: 92.0,
        EntryPercentile: 95.0);

    [Fact]
    public async Task OpenAsync_KillSwitchEnabled_OpensTradeNormally()
    {
        var (source, volumeBars) = NewDbs();
        await using var _ = source;
        await using var __ = volumeBars;

        volumeBars.LiveKillSwitchStates.Add(new LiveKillSwitchState { EntriesEnabled = true, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test" });
        await volumeBars.SaveChangesAsync();

        var messages = new List<string>();
        var trade = await LivePaperTradeExecutor.OpenAsync(
            source, volumeBars, AsOfDate, Threshold, Signal(), futurePrice: 24010m,
            decisionTimestamp: DateTimeOffset.UtcNow, log: messages.Add, CancellationToken.None);

        Assert.NotNull(trade);
        Assert.Equal(CallToken, trade!.Token);
        await volumeBars.SaveChangesAsync(); // OpenAsync only Adds -- the real caller SaveChanges's once per poll after all bars.
        Assert.Single(volumeBars.LivePaperTrades);
    }

    [Fact]
    public async Task OpenAsync_KillSwitchDisabled_DeclinesNewEntry_ButDoesNotThrow()
    {
        var (source, volumeBars) = NewDbs();
        await using var _ = source;
        await using var __ = volumeBars;

        volumeBars.LiveKillSwitchStates.Add(new LiveKillSwitchState { EntriesEnabled = false, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test" });
        await volumeBars.SaveChangesAsync();

        var messages = new List<string>();
        var trade = await LivePaperTradeExecutor.OpenAsync(
            source, volumeBars, AsOfDate, Threshold, Signal(), futurePrice: 24010m,
            decisionTimestamp: DateTimeOffset.UtcNow, log: messages.Add, CancellationToken.None);

        Assert.Null(trade);
        Assert.Empty(volumeBars.LivePaperTrades);
        Assert.Contains(messages, m => m.Contains("kill switch is DISABLED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenAsync_NoKillSwitchRowPersisted_DefaultsToEnabled()
    {
        // Mirrors NiftySignal.Domain.Entities.KillSwitchState's own "?? true" default (see
        // LiveTradingEngine.EvaluateEntryAsync) -- a migration seeds the row in production, but a
        // fresh/unseeded database (e.g. this InMemory test's own default state with no row added)
        // must fail OPEN to enabled, not silently block every entry.
        var (source, volumeBars) = NewDbs();
        await using var _ = source;
        await using var __ = volumeBars;

        var messages = new List<string>();
        var trade = await LivePaperTradeExecutor.OpenAsync(
            source, volumeBars, AsOfDate, Threshold, Signal(), futurePrice: 24010m,
            decisionTimestamp: DateTimeOffset.UtcNow, log: messages.Add, CancellationToken.None);

        Assert.NotNull(trade);
    }

    [Fact]
    public async Task CloseAsync_KillSwitchDisabled_StillClosesAlreadyOpenTrade()
    {
        // The core design decision under test: the kill switch gates NEW entries only. An
        // already-open position must keep exiting through its own normal rules regardless of the
        // switch's current state -- force-halting an open position with no defined exit is itself a
        // risk (docs/LIVE_PARITY_PLAN.md's Phase G section), so CloseAsync must never consult
        // LiveKillSwitchState at all.
        var (source, volumeBars) = NewDbs();
        await using var _ = source;
        await using var __ = volumeBars;

        // Open while enabled (normal operation)...
        volumeBars.LiveKillSwitchStates.Add(new LiveKillSwitchState { EntriesEnabled = true, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test" });
        await volumeBars.SaveChangesAsync();
        var messages = new List<string>();
        var opened = await LivePaperTradeExecutor.OpenAsync(
            source, volumeBars, AsOfDate, Threshold, Signal(), futurePrice: 24010m,
            decisionTimestamp: DateTimeOffset.UtcNow, log: messages.Add, CancellationToken.None);
        Assert.NotNull(opened);

        // ...then flip the switch off before the exit fires.
        var state = await volumeBars.LiveKillSwitchStates.SingleAsync();
        state.EntriesEnabled = false;
        await volumeBars.SaveChangesAsync();

        var closed = new LiveSignalClosed(EntryBarIndex: 0, ExitBarIndex: 5, ExitTimestamp: DateTimeOffset.UtcNow, ExitReason: "ScoreInvalidated");
        await LivePaperTradeExecutor.CloseAsync(volumeBars, source, AsOfDate, Threshold, closed, DateTimeOffset.UtcNow, messages.Add, CancellationToken.None);

        var row = await volumeBars.LivePaperTrades.SingleAsync();
        Assert.NotNull(row.ExitBarIndex);
        Assert.Equal("ScoreInvalidated", row.ExitReason);
    }

    [Fact]
    public async Task OpenAsync_KillSwitchReenabled_ResumesNormalOperation()
    {
        var (source, volumeBars) = NewDbs();
        await using var _ = source;
        await using var __ = volumeBars;

        volumeBars.LiveKillSwitchStates.Add(new LiveKillSwitchState { EntriesEnabled = false, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test" });
        await volumeBars.SaveChangesAsync();

        var messages = new List<string>();
        var declined = await LivePaperTradeExecutor.OpenAsync(
            source, volumeBars, AsOfDate, Threshold, Signal(entryBarIndex: 0), futurePrice: 24010m,
            decisionTimestamp: DateTimeOffset.UtcNow, log: messages.Add, CancellationToken.None);
        Assert.Null(declined);

        var state = await volumeBars.LiveKillSwitchStates.SingleAsync();
        state.EntriesEnabled = true;
        await volumeBars.SaveChangesAsync();

        var opened = await LivePaperTradeExecutor.OpenAsync(
            source, volumeBars, AsOfDate, Threshold, Signal(entryBarIndex: 1), futurePrice: 24010m,
            decisionTimestamp: DateTimeOffset.UtcNow, log: messages.Add, CancellationToken.None);
        Assert.NotNull(opened);
        await volumeBars.SaveChangesAsync();
        Assert.Single(volumeBars.LivePaperTrades);
    }
}
