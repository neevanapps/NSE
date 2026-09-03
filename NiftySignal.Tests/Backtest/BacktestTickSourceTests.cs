using Microsoft.EntityFrameworkCore;
using NiftySignal.Backtest;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.Backtest;

public class BacktestTickSourceTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 1, 9, 15, 0, TimeSpan.Zero);

    static NiftySignalDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    static Tick MakeTick(string token, DateTimeOffset exchangeTimestamp, decimal lastPrice) => new()
    {
        Token = token,
        Exchange = Exchange.Nfo,
        ExchangeTimestamp = exchangeTimestamp,
        ReceivedAt = exchangeTimestamp,
        LastPrice = lastPrice,
    };

    static async Task<List<Tick>> ReplayAsync(BacktestTickSource source)
    {
        var result = new List<Tick>();
        await foreach (var tick in source.ReadTicksAsync(CancellationToken.None))
        {
            result.Add(tick);
        }
        return result;
    }

    [Fact]
    public async Task ReadTicksAsync_ReplaysInExchangeTimestampOrder_NotInsertionOrder()
    {
        await using var db = NewInMemoryContext();
        // Inserted out of chronological order on purpose.
        db.Ticks.AddRange(
            MakeTick("1", Start.AddSeconds(30), 103),
            MakeTick("1", Start.AddSeconds(0), 100),
            MakeTick("1", Start.AddSeconds(10), 101));
        await db.SaveChangesAsync();

        var source = new BacktestTickSource(db, Start, Start.AddMinutes(5));
        var replayed = await ReplayAsync(source);

        Assert.Equal([100m, 101m, 103m], replayed.Select(t => t.LastPrice));
    }

    [Fact]
    public async Task ReadTicksAsync_ExcludesTicksOutsideTheGivenTimeRange()
    {
        await using var db = NewInMemoryContext();
        db.Ticks.AddRange(
            MakeTick("1", Start.AddMinutes(-1), 90),   // before range
            MakeTick("1", Start, 100),                  // in range
            MakeTick("1", Start.AddMinutes(4), 110),    // in range
            MakeTick("1", Start.AddMinutes(5), 120));   // at/after exclusive upper bound
        await db.SaveChangesAsync();

        var source = new BacktestTickSource(db, Start, Start.AddMinutes(5));
        var replayed = await ReplayAsync(source);

        Assert.Equal([100m, 110m], replayed.Select(t => t.LastPrice));
    }

    [Fact]
    public async Task ReadTicksAsync_ReplaysEveryInstrument_WhenNoTokenFilterGiven()
    {
        await using var db = NewInMemoryContext();
        db.Ticks.AddRange(
            MakeTick("CE-25000", Start, 180),
            MakeTick("PE-25000", Start.AddSeconds(1), 175));
        await db.SaveChangesAsync();

        var source = new BacktestTickSource(db, Start, Start.AddMinutes(5));
        var replayed = await ReplayAsync(source);

        Assert.Equal(2, replayed.Count);
    }

    [Fact]
    public async Task ReadTicksAsync_FiltersToOnlyTheGivenInstrumentTokens_WhenSpecified()
    {
        await using var db = NewInMemoryContext();
        db.Ticks.AddRange(
            MakeTick("CE-25000", Start, 180),
            MakeTick("PE-25000", Start.AddSeconds(1), 175),
            MakeTick("CE-25050", Start.AddSeconds(2), 160));
        await db.SaveChangesAsync();

        var source = new BacktestTickSource(db, Start, Start.AddMinutes(5), instrumentTokens: ["CE-25000", "CE-25050"]);
        var replayed = await ReplayAsync(source);

        Assert.Equal(2, replayed.Count);
        Assert.DoesNotContain(replayed, t => t.Token == "PE-25000");
    }

    [Fact]
    public async Task ReadTicksAsync_BreaksTiesByInsertionOrder_ForIdenticalExchangeTimestamps()
    {
        await using var db = NewInMemoryContext();
        var same = Start.AddSeconds(5);
        db.Ticks.AddRange(
            MakeTick("1", same, 100),
            MakeTick("1", same, 101),
            MakeTick("1", same, 102));
        await db.SaveChangesAsync();

        var source = new BacktestTickSource(db, Start, Start.AddMinutes(5));
        var replayed = await ReplayAsync(source);

        // All three share a timestamp -- the Id tiebreak should still produce a
        // deterministic, stable order matching insertion (ascending Id) rather than
        // whatever order the store happens to return.
        Assert.Equal([100m, 101m, 102m], replayed.Select(t => t.LastPrice));
    }
}
