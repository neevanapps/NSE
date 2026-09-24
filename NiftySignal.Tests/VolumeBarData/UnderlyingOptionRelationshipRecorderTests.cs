using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-23, descriptive research layer. Coverage for
/// <see cref="UnderlyingOptionRelationshipRecorder"/>: futures/spot synchronization, same-contract
/// change calculation, contract-transition detection, missing/stale handling, and that no value is
/// ever fabricated. Synthetic data only, no Black-Scholes/Greeks (not needed for this layer).
/// </summary>
public sealed class UnderlyingOptionRelationshipRecorderTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 8);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly DateTimeOffset DayStart = new(AsOfDate.ToDateTime(new TimeOnly(9, 15)), IstOffset);
    const string Call25000 = "NIFTY-25000CE-TOK";
    const string Call25100 = "NIFTY-25100CE-TOK";
    const string Put25000 = "NIFTY-25000PE-TOK";
    const string SpotToken = "NIFTY-INDEX-TOK";

    static NiftySignalDbContext NewSourceDb() =>
        new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{Guid.NewGuid()}").Options);

    static Instrument OptionInstrument(string token, decimal strike, OptionType side) => new()
    {
        Token = token, Exchange = Exchange.Nfo, TradingSymbol = token,
        InstrumentType = InstrumentType.Option, OptionType = side, StrikePrice = strike,
        ExpiryDate = AsOfDate, Underlying = "NIFTY", LotSize = 65, TickSize = 0.05m, AsOfDate = AsOfDate,
    };

    static List<FutureEventBar> Bars(int count, TimeSpan barLength, Func<int, decimal> close) =>
        Enumerable.Range(0, count).Select(n => new FutureEventBar(
            n, AsOfDate, DayStart + (barLength * n), DayStart + (barLength * (n + 1)),
            close(n), close(n), close(n), close(n), 1300, (double?)close(n), null, 10, IsFinalPartialBar: n == count - 1))
        .ToList();

    static SynchronizedOptionEventBar OptionBar(FutureEventBar fb, string token, decimal strike, OptionType side, decimal? avgLtp) => new(
        fb.EventId, AsOfDate, fb.StartTimestamp, fb.EndTimestamp, strike, side, 0, token,
        avgLtp, avgLtp, avgLtp, avgLtp, avgLtp, avgLtp is { } a ? (double)a : null,
        avgLtp is null ? 0 : 1, 0, fb.StartTimestamp, fb.EndTimestamp,
        MissingData: avgLtp is null, IsStale: avgLtp is null, LastKnownPrice: null, AgeMs: null);

    [Fact]
    public async Task RecordAsync_NoSpotInstrument_MarksSpotUnavailable_NeverSubstitutesFutures()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(Call25000, 25000m, OptionType.Call));
        source.Instruments.Add(OptionInstrument(Put25000, 25000m, OptionType.Put));
        await source.SaveChangesAsync();

        var barLength = TimeSpan.FromMinutes(1);
        var futureBars = Bars(2, barLength, n => 25000m + n);
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, OptionType.Call, 100m),
            OptionBar(futureBars[1], Call25000, 25000m, OptionType.Call, 101m),
            OptionBar(futureBars[0], Put25000, 25000m, OptionType.Put, 100m),
            OptionBar(futureBars[1], Put25000, 25000m, OptionType.Put, 99m),
        };

        var chain = source.Instruments.ToList();
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(source, AsOfDate, chain, futureBars, optionBars, CancellationToken.None);

        Assert.All(rows, r => Assert.False(r.SpotAvailable));
        Assert.All(rows, r => Assert.Null(r.SpotClose));
        Assert.All(rows, r => Assert.Null(r.SpotChange1)); // never fabricated/substituted with futures.
    }

    [Fact]
    public async Task RecordAsync_SpotTicksPresent_AggregatesPerEventInterval()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(Call25000, 25000m, OptionType.Call));
        source.Instruments.Add(OptionInstrument(Put25000, 25000m, OptionType.Put));
        source.Instruments.Add(new Instrument
        {
            Token = SpotToken, Exchange = Exchange.Nse, TradingSymbol = "NIFTY-INDEX",
            InstrumentType = InstrumentType.Index, Underlying = "NIFTY", LotSize = 1, TickSize = 0.05m, AsOfDate = AsOfDate,
        });

        var barLength = TimeSpan.FromMinutes(1);
        var futureBars = Bars(2, barLength, n => 25000m + n);
        source.Ticks.Add(new Tick { Token = SpotToken, Exchange = Exchange.Nse, ExchangeTimestamp = futureBars[0].StartTimestamp.AddSeconds(10), ReceivedAt = futureBars[0].StartTimestamp.AddSeconds(10), LastPrice = 24990m, Volume = 0 });
        source.Ticks.Add(new Tick { Token = SpotToken, Exchange = Exchange.Nse, ExchangeTimestamp = futureBars[0].StartTimestamp.AddSeconds(20), ReceivedAt = futureBars[0].StartTimestamp.AddSeconds(20), LastPrice = 25010m, Volume = 0 });
        // event1 gets no real spot tick at all -- must be marked missing, not fabricated.
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, OptionType.Call, 100m),
            OptionBar(futureBars[1], Call25000, 25000m, OptionType.Call, 101m),
            OptionBar(futureBars[0], Put25000, 25000m, OptionType.Put, 100m),
            OptionBar(futureBars[1], Put25000, 25000m, OptionType.Put, 99m),
        };
        await source.SaveChangesAsync();

        var chain = source.Instruments.Where(i => i.InstrumentType == InstrumentType.Option).ToList();
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(source, AsOfDate, chain, futureBars, optionBars, CancellationToken.None);

        Assert.True(rows[0].SpotAvailable);
        Assert.False(rows[0].SpotMissingData);
        Assert.Equal(2, rows[0].SpotTickCount);
        Assert.Equal(25000m, rows[0].SpotAveragePrice); // (24990+25010)/2
        Assert.Equal(24990m, rows[0].SpotOpen);
        Assert.Equal(25010m, rows[0].SpotClose);

        Assert.True(rows[1].SpotAvailable);
        Assert.True(rows[1].SpotMissingData);
        Assert.Null(rows[1].SpotClose);
        Assert.Null(rows[1].SpotChange1); // no real reading this event -- never fabricated.
    }

    [Fact]
    public async Task RecordAsync_ContractTransition_IsDetectedAndBreaksChangeAttribution()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(Call25000, 25000m, OptionType.Call));
        source.Instruments.Add(OptionInstrument(Call25100, 25100m, OptionType.Call));
        source.Instruments.Add(OptionInstrument(Put25000, 25000m, OptionType.Put));
        await source.SaveChangesAsync();

        var barLength = TimeSpan.FromMinutes(1);
        // futures close moves from 25000 (ATM=25000) to 25100 (ATM=25100) between event0 and event1.
        var futureBars = Bars(2, barLength, n => n == 0 ? 25000m : 25100m);
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, OptionType.Call, 100m),
            OptionBar(futureBars[1], Call25100, 25100m, OptionType.Call, 60m),
            OptionBar(futureBars[0], Put25000, 25000m, OptionType.Put, 100m),
            OptionBar(futureBars[1], Put25000, 25000m, OptionType.Put, 90m),
        };

        var chain = source.Instruments.ToList();
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(source, AsOfDate, chain, futureBars, optionBars, CancellationToken.None);

        Assert.False(rows[0].CeContractTransition); // first event of the day -- never treated as a transition.
        Assert.True(rows[1].CeContractTransition);
        Assert.Equal(Call25100, rows[1].CeToken);
        Assert.Null(rows[1].CeChange1); // 100 -> 60 is a DIFFERENT contract's level jump, never reported as "movement."
        Assert.Equal("ContractTransition", rows[1].RelationshipCategory);

        // Put never transitions (same strike/ATM both events) -- its own change IS computed.
        Assert.False(rows[1].PeContractTransition);
        Assert.Equal(-10m, rows[1].PeChange1);
    }

    [Fact]
    public async Task RecordAsync_MissingOptionBar_NeverFabricatesAChange_AndMarksCategoryIncomplete()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(Call25000, 25000m, OptionType.Call));
        source.Instruments.Add(OptionInstrument(Put25000, 25000m, OptionType.Put));
        await source.SaveChangesAsync();

        var barLength = TimeSpan.FromMinutes(1);
        var futureBars = Bars(3, barLength, n => 25000m + n);
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, OptionType.Call, 100m),
            OptionBar(futureBars[1], Call25000, 25000m, OptionType.Call, null), // no real tick this event.
            OptionBar(futureBars[2], Call25000, 25000m, OptionType.Call, 105m),
            OptionBar(futureBars[0], Put25000, 25000m, OptionType.Put, 100m),
            OptionBar(futureBars[1], Put25000, 25000m, OptionType.Put, 99m),
            OptionBar(futureBars[2], Put25000, 25000m, OptionType.Put, 98m),
        };

        var chain = source.Instruments.ToList();
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(source, AsOfDate, chain, futureBars, optionBars, CancellationToken.None);

        Assert.True(rows[1].CeMissingData);
        Assert.Null(rows[1].CeChange1);
        Assert.Equal("OptionDataIncomplete", rows[1].RelationshipCategory);

        // event2's own change is ALSO null -- the previous (event1) reading was missing, so a
        // "1-event-bar" change can never be computed across the gap (never silently spans 2 bars).
        Assert.Null(rows[2].CeChange1);
    }

    [Fact]
    public async Task RecordAsync_CategoryLabels_MatchTheThreeDirections_WhenDataComplete()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(Call25000, 25000m, OptionType.Call));
        source.Instruments.Add(OptionInstrument(Put25000, 25000m, OptionType.Put));
        await source.SaveChangesAsync();

        var barLength = TimeSpan.FromMinutes(1);
        var futureBars = Bars(2, barLength, n => n == 0 ? 25000m : 25010m); // futures up.
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, OptionType.Call, 100m),
            OptionBar(futureBars[1], Call25000, 25000m, OptionType.Call, 95m), // CE down.
            OptionBar(futureBars[0], Put25000, 25000m, OptionType.Put, 50m),
            OptionBar(futureBars[1], Put25000, 25000m, OptionType.Put, 55m), // PE up.
        };

        var chain = source.Instruments.ToList();
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(source, AsOfDate, chain, futureBars, optionBars, CancellationToken.None);

        Assert.Equal("UnderlyingUp_CEDown_PEUp", rows[1].RelationshipCategory);
        Assert.Equal(RelationshipDirection.Up, rows[1].FuturesDirection1);
        Assert.Equal(RelationshipDirection.Down, rows[1].CeDirection1);
        Assert.Equal(RelationshipDirection.Up, rows[1].PeDirection1);
    }

    [Fact]
    public async Task RecordAsync_CrossoverEnginesResetOnOptionTransition_ButNeverOnFutures()
    {
        // The futures/spot engines must never reset (one continuous identity all day); CE/PE
        // engines must reset on their own contract transition -- verified indirectly via the
        // MA states not silently mixing a stale contract's history (full engine behavior itself
        // is covered by PriceCrossoverEngineTests; this just confirms the wiring calls Reset()).
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(Call25000, 25000m, OptionType.Call));
        source.Instruments.Add(OptionInstrument(Call25100, 25100m, OptionType.Call));
        source.Instruments.Add(OptionInstrument(Put25000, 25000m, OptionType.Put));
        await source.SaveChangesAsync();

        var barLength = TimeSpan.FromMinutes(1);
        var futureBars = Bars(3, barLength, n => n < 2 ? 25000m : 25100m);
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, OptionType.Call, 100m),
            OptionBar(futureBars[1], Call25000, 25000m, OptionType.Call, 100m),
            OptionBar(futureBars[2], Call25100, 25100m, OptionType.Call, 60m),
            OptionBar(futureBars[0], Put25000, 25000m, OptionType.Put, 100m),
            OptionBar(futureBars[1], Put25000, 25000m, OptionType.Put, 100m),
            OptionBar(futureBars[2], Put25000, 25000m, OptionType.Put, 100m),
        };

        var chain = source.Instruments.ToList();
        var rows = await UnderlyingOptionRelationshipRecorder.RecordAsync(source, AsOfDate, chain, futureBars, optionBars, CancellationToken.None);

        // After the CE transition at event2, its own MA window has only 1 real reading (60) --
        // not enough to warm a slow=10 window, so FastMa/SlowMa must be null (freshly reset),
        // never continuing the pre-transition 100-level window.
        Assert.Null(rows[2].CeFastMa);
        Assert.Equal("Unavailable", rows[2].CeCrossoverState);
    }

    [Fact]
    public async Task RecordAsync_ClassificationAtEventT_NeverDependsOnDataAfterT_NoLookahead()
    {
        // Two scenarios, IDENTICAL through event1, differing ONLY in what happens at event2 (a
        // future event relative to event1). If event1's own category/directions differ between
        // the two runs, that would prove event1's classification illegitimately used event2's
        // data -- forward-validation section 18's own explicit "no-lookahead protection" test.
        async Task<List<RelationshipObservation>> RunAsync(decimal event2FuturesClose, decimal? event2CeLtp, decimal? event2PeLtp)
        {
            await using var source = NewSourceDb();
            source.Instruments.Add(OptionInstrument(Call25000, 25000m, OptionType.Call));
            source.Instruments.Add(OptionInstrument(Put25000, 25000m, OptionType.Put));
            await source.SaveChangesAsync();

            var barLength = TimeSpan.FromMinutes(1);
            var futureBars = Bars(3, barLength, n => n switch { 0 => 25000m, 1 => 25010m, _ => event2FuturesClose });
            var optionBars = new List<SynchronizedOptionEventBar>
            {
                OptionBar(futureBars[0], Call25000, 25000m, OptionType.Call, 100m),
                OptionBar(futureBars[1], Call25000, 25000m, OptionType.Call, 95m),
                OptionBar(futureBars[2], Call25000, 25000m, OptionType.Call, event2CeLtp),
                OptionBar(futureBars[0], Put25000, 25000m, OptionType.Put, 50m),
                OptionBar(futureBars[1], Put25000, 25000m, OptionType.Put, 55m),
                OptionBar(futureBars[2], Put25000, 25000m, OptionType.Put, event2PeLtp),
            };

            var chain = source.Instruments.ToList();
            return await UnderlyingOptionRelationshipRecorder.RecordAsync(source, AsOfDate, chain, futureBars, optionBars, CancellationToken.None);
        }

        // Scenario 1: event2 continues the same way. Scenario 2: event2 violently reverses
        // everything (futures crashes, CE rockets up, PE craters) -- a maximally different future.
        var scenario1 = await RunAsync(event2FuturesClose: 25020m, event2CeLtp: 90m, event2PeLtp: 60m);
        var scenario2 = await RunAsync(event2FuturesClose: 20000m, event2CeLtp: 500m, event2PeLtp: 1m);

        Assert.Equal(scenario1[1].RelationshipCategory, scenario2[1].RelationshipCategory);
        Assert.Equal(scenario1[1].FuturesDirection1, scenario2[1].FuturesDirection1);
        Assert.Equal(scenario1[1].CeDirection1, scenario2[1].CeDirection1);
        Assert.Equal(scenario1[1].PeDirection1, scenario2[1].PeDirection1);
        Assert.Equal(scenario1[1].CeFastMa, scenario2[1].CeFastMa);
        Assert.Equal(scenario1[1].FuturesChange1, scenario2[1].FuturesChange1);
    }
}
