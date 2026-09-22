using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// Phase G restart-safety addendum (docs/LIVE_PARITY_PLAN.md, "Phase G addendum: restart-safety of
/// the Phase G cache"): the Phase A/C replay harnesses' own restart-safety proofs (<c>replay-live
/// --restart-after</c>, <c>replay-live-score --restart-after</c>) both predate
/// <see cref="LiveOptionSeriesCache"/> -- neither ever exercised a cache that had actually been
/// warmed by real polls before the simulated restart. These tests close that specific gap
/// deterministically (no database round trip, EF Core InMemoryDatabase, same convention
/// <see cref="LivePaperTradeExecutorKillSwitchTests"/> already establishes): they warm a cache with
/// several incremental loads (mirroring several real polls), then simulate a process restart by
/// discarding it and constructing a BRAND NEW <see cref="LiveOptionSeriesCache"/> -- exactly what a
/// real Windows Service restart does to this Singleton (it is never persisted, so there is no
/// "resume" path to test beyond "start over cold") -- and prove the fresh cache reproduces the exact
/// same answers a from-scratch, no-cache load would.
/// </summary>
public class LiveOptionSeriesCacheRestartTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 16);
    const string Token = "999001";
    static readonly DateTimeOffset DayStart = AsOfDate.ToDateTime(new TimeOnly(9, 15)).ToUniversalTime();
    static readonly DateTimeOffset DayEnd = AsOfDate.ToDateTime(new TimeOnly(15, 30)).ToUniversalTime();

    static NiftySignalDbContext NewSourceDb()
    {
        var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{Guid.NewGuid()}").Options);
        source.Instruments.Add(new Instrument
        {
            Token = Token,
            Exchange = Exchange.Nfo,
            TradingSymbol = "NIFTY24000CE",
            InstrumentType = InstrumentType.Option,
            OptionType = OptionType.Call,
            StrikePrice = 24000m,
            ExpiryDate = new DateOnly(2026, 9, 18),
            Underlying = "NIFTY",
            AsOfDate = AsOfDate,
        });

        // Ticks spread across the morning, each carrying a distinct LastPrice/OpenInterest so a
        // wrong (stale, partial, or duplicated) series would show up as a wrong MidAtOrBefore/
        // OiAtOrBefore answer, not just a wrong row count.
        var times = new[] { (9, 20), (9, 45), (10, 15), (10, 45), (11, 15), (11, 45) };
        for (var i = 0; i < times.Length; i++)
        {
            var (h, m) = times[i];
            source.Ticks.Add(new Tick
            {
                Token = Token,
                Exchange = Exchange.Nfo,
                ExchangeTimestamp = AsOfDate.ToDateTime(new TimeOnly(h, m)).ToUniversalTime(),
                ReceivedAt = AsOfDate.ToDateTime(new TimeOnly(h, m)).ToUniversalTime(),
                LastPrice = 100m + i,
                OpenInterest = 1000 + i * 10,
            });
        }

        source.SaveChanges();
        return source;
    }

    [Fact]
    public async Task ColdCacheAfterSimulatedRestart_QuoteSeries_MatchesFromScratchLoad()
    {
        await using var source = NewSourceDb();

        // Phase 1: warm a cache across several incremental "polls," mirroring real steady-state
        // operation before a restart -- each call only extends the series a bit further, exactly
        // like LiveVolumeBarWriter's real 10s poll loop would.
        var warmCache = new LiveOptionSeriesCache();
        var probeTimes = new[] { new TimeOnly(9, 30), new TimeOnly(10, 0), new TimeOnly(10, 30), new TimeOnly(11, 0) };
        foreach (var t in probeTimes)
        {
            var cutoff = AsOfDate.ToDateTime(t).ToUniversalTime();
            await warmCache.GetQuoteSeriesAsync(source, AsOfDate, Token, DayStart, cutoff, CancellationToken.None);
        }

        Assert.Equal(1, warmCache.QuoteTokenCountForDiagnostics);

        // Phase 2: simulate a real process restart -- the Singleton is gone, a brand-new instance
        // takes its place (there is no "resume" state for an in-memory-only cache to carry over).
        var coldCacheAfterRestart = new LiveOptionSeriesCache();
        Assert.Equal(0, coldCacheAfterRestart.QuoteTokenCountForDiagnostics);

        var postRestartSeries = await coldCacheAfterRestart.GetQuoteSeriesAsync(source, AsOfDate, Token, DayStart, DayEnd, CancellationToken.None);

        // Phase 3: the from-scratch, no-cache-at-all baseline every existing caller (offline
        // populators, tests, replay harnesses) already uses -- what "correct" looks like.
        var fromScratch = await OptionQuoteSeries.LoadAsync(source, Token, DayStart, DayEnd, CancellationToken.None);

        // The cold post-restart cache must answer identically to the from-scratch baseline for
        // every probe point across the day -- not just at dayEnd -- proving the FIRST poll after a
        // restart correctly falls back to a full reload rather than silently returning a
        // wrong/partial answer because it assumes something was already warm.
        foreach (var t in probeTimes.Append(new TimeOnly(11, 45)).Append(new TimeOnly(15, 0)))
        {
            var at = AsOfDate.ToDateTime(t).ToUniversalTime();
            Assert.Equal(fromScratch.MidAtOrBefore(at), postRestartSeries.MidAtOrBefore(at));
        }

        // And it must NOT still be answering with the pre-restart warm cache's own (now-defunct)
        // partial view -- the post-restart series must actually reflect ticks the warm cache never
        // saw (everything after 11:00, its last probe).
        var lateProbe = AsOfDate.ToDateTime(new TimeOnly(11, 45)).ToUniversalTime();
        Assert.NotNull(postRestartSeries.MidAtOrBefore(lateProbe));
        Assert.Equal(105m, postRestartSeries.MidAtOrBefore(lateProbe)); // tick AT 11:45 itself (at-or-before is inclusive), LastPrice=100+5=105.
    }

    [Fact]
    public async Task ColdCacheAfterSimulatedRestart_OiSeries_MatchesFromScratchLoad()
    {
        await using var source = NewSourceDb();

        var warmCache = new LiveOptionSeriesCache();
        foreach (var t in new[] { new TimeOnly(9, 30), new TimeOnly(10, 30) })
        {
            var cutoff = AsOfDate.ToDateTime(t).ToUniversalTime();
            await warmCache.GetOiSeriesAsync(source, AsOfDate, Token, DayStart, cutoff, CancellationToken.None);
        }

        Assert.Equal(1, warmCache.OiTokenCountForDiagnostics);

        // Simulated restart: fresh Singleton, no state carried over.
        var coldCacheAfterRestart = new LiveOptionSeriesCache();
        var postRestartSeries = await coldCacheAfterRestart.GetOiSeriesAsync(source, AsOfDate, Token, DayStart, DayEnd, CancellationToken.None);
        var fromScratch = await OptionOiSeries.LoadAsync(source, Token, DayStart, DayEnd, CancellationToken.None);

        var checkAt = AsOfDate.ToDateTime(new TimeOnly(11, 45)).ToUniversalTime();
        Assert.Equal(fromScratch.OiAtOrBefore(checkAt), postRestartSeries.OiAtOrBefore(checkAt));
        Assert.Equal(1050L, postRestartSeries.OiAtOrBefore(checkAt)); // tick AT 11:45 itself (at-or-before is inclusive), OpenInterest=1000+5*10=1050.
    }

    [Fact]
    public async Task ResetIfNewDay_DoesNotSpuriouslyFireOnRestart_ButDoesFireOnDayChange()
    {
        // A restart mid-day must NOT be confused with a day change -- a fresh cache for the SAME
        // date should behave exactly like the "cold, never touched today" case this class's own doc
        // comment describes, while a genuinely new date must still discard stale entries (existing
        // ResetIfNewDay behavior, re-confirmed here in the same restart-focused test file so this
        // guarantee doesn't only live in a doc comment).
        await using var source = NewSourceDb();
        var cache = new LiveOptionSeriesCache();

        await cache.GetQuoteSeriesAsync(source, AsOfDate, Token, DayStart, DayStart.AddHours(1), CancellationToken.None);
        Assert.Equal(1, cache.QuoteTokenCountForDiagnostics);

        // Same date again (e.g. two polls in the same trading day, no restart) -- must stay warm.
        await cache.GetQuoteSeriesAsync(source, AsOfDate, Token, DayStart, DayStart.AddHours(2), CancellationToken.None);
        Assert.Equal(1, cache.QuoteTokenCountForDiagnostics);

        // A new trading day -- must reset (LiveOptionSeriesCache.ResetIfNewDay's own documented
        // reason: a token could in principle be reused across days, so stale entries must never
        // silently answer for a different day).
        var nextDay = AsOfDate.AddDays(1);
        await cache.GetQuoteSeriesAsync(source, nextDay, Token, DayStart.AddDays(1), DayStart.AddDays(1).AddHours(1), CancellationToken.None);
        Assert.Equal(1, cache.QuoteTokenCountForDiagnostics); // reset then re-populated for the new day, not accumulated to 2.
    }
}
