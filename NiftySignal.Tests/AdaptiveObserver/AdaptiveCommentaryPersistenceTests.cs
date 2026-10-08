using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserver.Commentary;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>08-Oct plan Slice 4B: commentary persistence, checkpoint, replay idempotence and the persisted-rows-only frame.</summary>
public sealed class AdaptiveCommentaryPersistenceTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 4, 30, 0, TimeSpan.Zero);
    const long BaseVolume = 1000;

    sealed record Spec(int Seq, int Flow, long? RollOi, double Bar, double Roll, string? Evolution = null, long? Ofi = null, double? Residual = null);

    static AdaptiveObserverDbContext Database() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options);

    static AdaptiveSessionStateRow Session() => new()
    {
        Id = 1, TradeDate = new DateOnly(2026, 10, 8), ModelVersion = "adaptive-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0,
        FutureToken = "FUT", FutureSymbol = "FUT", FutureExpiry = new DateOnly(2026, 10, 29), OpeningWindowStartUtc = T0.AddMinutes(-15),
        OpeningWindowEndUtc = T0, EstimatorName = "V1", CreatedAtUtc = T0, BaseBarVolume = BaseVolume,
    };

    static AdaptiveCommentaryService Service() => new(new AdaptiveCommentaryFrameLoader(), NullLogger<AdaptiveCommentaryService>.Instance);

    /// <summary>Persists one valid completed bar (and its rolling row once ten bars exist) shaped by the spec, as the core observer would.</summary>
    static async Task Seed(AdaptiveObserverDbContext db, Spec s)
    {
        var end = T0.AddMinutes(s.Seq); var start = end.AddMinutes(-1);
        db.FutureBars.Add(new()
        {
            SessionId = 1, BarSeq = s.Seq, StartAvailableAtUtc = start, EndAvailableAtUtc = end, Volume = BaseVolume, TradeUpdates = 5,
            Open = 23000, High = 23010, Low = 22990, Close = 23000 + s.Bar, DurationSeconds = 60, BarPriceDisplacement = s.Bar,
            StrictDelta = s.Flow * 100, EnrichedDelta = s.Flow * 110,
        });
        if (s.Seq >= 10)
            db.RollingStates.Add(new()
            {
                SessionId = 1, StartBarSeq = s.Seq - 9, EndBarSeq = s.Seq, StartAvailableAtUtc = start.AddMinutes(-9), EndAvailableAtUtc = end,
                PriceDisplacement = s.Roll, StrictDelta = s.Flow * 1000, EnrichedDelta = s.Flow * 1100, OiChange = s.RollOi, Efficiency = 0.5,
                RollingStrictAbsDeltaChange = 0, StrictDominanceEvolution = s.Evolution, State = AdaptiveStateKind.Normal,
            });
        if (s.Ofi is { } ofi)
            db.FuturesSupplemental.Add(new() { SessionId = 1, BarSeq = s.Seq, MetricsVersion = FuturesMicrostructureBar.MetricsVersion, Ofi = ofi, MicroDevTimeWeighted = Math.Sign(ofi) * 0.1 });
        db.OptionResidualBars.Add(new()
        {
            SessionId = 1, BarSeq = s.Seq, Variant = ResidualVariant.AtmPlusMinus2, EndAvailableAtUtc = end, Relationship = "NEUTRAL",
            IsAvailable = s.Residual is not null, DirectionalResidualPct = s.Residual ?? 0, CEResidualPct = s.Residual ?? 0,
        });
        await db.SaveChangesAsync();
    }

    static Spec[] Scenario() =>
    [
        .. Enumerable.Range(1, 9).Select(i => new Spec(i, -1, 50, -2, -5)),                       // warm-up: readiness not met => silent
        new(10, -1, 50, -2, -5),                                                                   // ten valid bars: SellerExpansion New (LOW)
        new(11, -1, 50, -2, -5),                                                                   // unchanged continuation
        new(12, -1, 50, -2, -5, Evolution: "Strengthening", Ofi: -400, Residual: -2.0),            // Strengthening (new supporting families)
        new(13, -1, 50, -2, -5, Evolution: "Strengthening", Ofi: -400, Residual: -2.0),            // same phase => silent
        new(14, 0, 0, 0, 0),                                                                       // nothing material => Resolved
        new(15, 0, 0, 0, 0),                                                                       // silent
        new(16, 1, 50, 2, 5),                                                                      // BuyerExpansion New
    ];

    static async Task<AdaptiveObserverDbContext> Seeded(params Spec[] specs)
    {
        var db = Database(); db.Sessions.Add(Session()); await db.SaveChangesAsync();
        foreach (var s in specs) await Seed(db, s);
        return db;
    }

    static string Digest(IEnumerable<AdaptiveCommentaryEventRow> rows) => JsonSerializer.Serialize(rows.OrderBy(x => x.BarSeq)
        .Select(x => new { x.BarSeq, x.EventType, x.EventBias, x.Lifecycle, x.EvidenceAgreement, x.MarketRegime, x.PreviousBias, x.BiasChanged, x.EventIdentity,
            x.PrimaryEvidenceJson, x.ConfirmationEvidenceJson, x.DataQualityJson, x.RenderedCommentary, x.ShouldNotifyTelegram, x.CreatedAtUtc }));

    [Fact]
    public async Task OnlyLifecycleEventsArePersisted_AndSilentBarsStillAdvanceTheCheckpoint()
    {
        await using var db = await Seeded(Scenario());
        var service = Service();
        Assert.Equal(16, await service.ProcessThroughAsync(db, Session(), 16, default));

        var events = await db.CommentaryEvents.AsNoTracking().OrderBy(x => x.BarSeq).ToListAsync();
        Assert.Equal([10, 12, 14, 16], events.Select(x => x.BarSeq));
        Assert.Equal([CommentaryLifecycle.New, CommentaryLifecycle.Strengthening, CommentaryLifecycle.Resolved, CommentaryLifecycle.New], events.Select(x => x.Lifecycle));
        Assert.Equal([CommentaryEventType.SellerExpansion, CommentaryEventType.SellerExpansion, CommentaryEventType.SellerExpansion, CommentaryEventType.BuyerExpansion],
            events.Select(x => x.EventType));
        var runtime = await db.CommentaryRuntime.AsNoTracking().SingleAsync();
        Assert.Equal(16, runtime.LastEvaluatedBarSeq);                     // bars 1-9, 11, 13, 15 persisted nothing yet advanced the checkpoint
        Assert.Equal(events[^1].Id, runtime.CurrentEventId);
        Assert.Equal(CommentaryEventType.BuyerExpansion, runtime.CurrentEventType);
        Assert.Equal(events[0].Id, events[1].PreviousEventId);
        Assert.Equal(events[^1].EventIdentity, $"1:16:BuyerExpansion:New:Long");
    }

    [Fact]
    public async Task StructuredEvidenceIsPersisted_NotOnlyProse()
    {
        await using var db = await Seeded(Scenario());
        await Service().ProcessThroughAsync(db, Session(), 12, default);
        var strengthening = await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.BarSeq == 12);
        var primary = JsonDocument.Parse(strengthening.PrimaryEvidenceJson).RootElement;
        Assert.Contains(primary.EnumerateArray(), i => i.GetProperty("metric").GetString() == "Roll OI Δ" && i.GetProperty("value").GetString() == "+50");
        var confirmations = JsonDocument.Parse(strengthening.ConfirmationEvidenceJson).RootElement;
        Assert.Contains(confirmations.EnumerateArray(), i => i.GetProperty("family").GetString() == "Book" && i.GetProperty("metric").GetString() == "OFI");
        Assert.Contains(confirmations.EnumerateArray(), i => i.GetProperty("family").GetString() == "Residual");
        Assert.Contains("Supported by Book", strengthening.RenderedCommentary);
        Assert.Equal(CommentaryEvaluator.Version, strengthening.CommentaryVersion);
    }

    [Fact]
    public async Task ReplayingTheSameBarsNeverDuplicatesAnEvent_AndRebuildsTheCheckpoint()
    {
        await using var db = await Seeded(Scenario());
        var service = Service();
        await service.ProcessThroughAsync(db, Session(), 16, default);
        var before = Digest(await db.CommentaryEvents.AsNoTracking().ToListAsync());
        var runtimeBefore = await db.CommentaryRuntime.AsNoTracking().SingleAsync();

        // Losing the operational checkpoint (it is rebuildable): a full replay verifies the existing rows and recreates identical state.
        db.CommentaryRuntime.RemoveRange(db.CommentaryRuntime); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(16, await service.ProcessThroughAsync(db, Session(), 16, default));

        Assert.Equal(4, await db.CommentaryEvents.CountAsync());
        Assert.Equal(before, Digest(await db.CommentaryEvents.AsNoTracking().ToListAsync()));
        var runtimeAfter = await db.CommentaryRuntime.AsNoTracking().SingleAsync();
        Assert.Equal((runtimeBefore.LastEvaluatedBarSeq, runtimeBefore.CurrentEventId, runtimeBefore.CurrentRegime, runtimeBefore.CurrentEventType, runtimeBefore.ActivePhase),
            (runtimeAfter.LastEvaluatedBarSeq, runtimeAfter.CurrentEventId, runtimeAfter.CurrentRegime, runtimeAfter.CurrentEventType, runtimeAfter.ActivePhase));
        Assert.Equal(0, service.MismatchCount);

        // A second call with nothing new evaluates nothing and persists nothing.
        Assert.Equal(0, await service.ProcessThroughAsync(db, Session(), 16, default));
    }

    [Fact]
    public async Task RestartMidSession_ContinuesFromTheCheckpoint_WithoutAFalseNewEvent()
    {
        await using var straight = await Seeded(Scenario());
        await Service().ProcessThroughAsync(straight, Session(), 16, default);

        await using var restarted = await Seeded(Scenario());
        await Service().ProcessThroughAsync(restarted, Session(), 12, default);        // the process "stops" while SellerExpansion is Strengthening
        restarted.ChangeTracker.Clear();
        var secondProcess = Service();                                                  // new service instance: no in-memory state at all
        await secondProcess.ProcessThroughAsync(restarted, Session(), 13, default);     // bar 13 is a continuation: must NOT become a new event
        Assert.Equal(2, await restarted.CommentaryEvents.CountAsync());   // bars 10 (New) and 12 (Strengthening); 13 added nothing
        await secondProcess.ProcessAllCompletedAsync(restarted, Session(), default);

        Assert.Equal(Digest(await straight.CommentaryEvents.AsNoTracking().ToListAsync()), Digest(await restarted.CommentaryEvents.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task ATamperedStoredEvent_IsReportedAndLeftUntouched_OnReplay()
    {
        await using var db = await Seeded(Scenario());
        var service = Service();
        await service.ProcessThroughAsync(db, Session(), 16, default);
        var stored = await db.CommentaryEvents.FirstAsync(x => x.BarSeq == 10);
        stored.RenderedCommentary = "tampered"; await db.SaveChangesAsync();
        db.CommentaryRuntime.RemoveRange(db.CommentaryRuntime); await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        await service.ProcessThroughAsync(db, Session(), 16, default);
        Assert.Equal(1, service.MismatchCount);
        Assert.Equal("tampered", (await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.BarSeq == 10)).RenderedCommentary);
        Assert.Equal(4, await db.CommentaryEvents.CountAsync());
    }

    [Fact]
    public async Task MissingBar_StopsEvaluationAndResumesLater_AndFailuresNeverThrow()
    {
        await using var db = Database(); db.Sessions.Add(Session()); await db.SaveChangesAsync();
        foreach (var s in Scenario().Take(11)) await Seed(db, s);
        var service = Service();
        Assert.Equal(11, await service.ProcessThroughAsync(db, Session(), 14, default));        // bars 12-14 not persisted yet: stops at 11
        Assert.Equal(11, (await db.CommentaryRuntime.AsNoTracking().SingleAsync()).LastEvaluatedBarSeq);
        foreach (var s in Scenario().Skip(11)) await Seed(db, s);
        Assert.Equal(5, await service.ProcessThroughAsync(db, Session(), 16, default));

        var disposed = Database(); await disposed.DisposeAsync();
        Assert.Equal(0, await Service().ProcessThroughAsync(disposed, Session(), 5, default));   // unusable context: logged, never thrown
    }

    // ---- frame builder: persisted rows only ----

    [Fact]
    public async Task Frame_IsBuiltOnlyFromPersistedRows_AndUsesAdjacentResidualDeltas()
    {
        await using var db = await Seeded(
            Enumerable.Range(1, 11).Select(i => new Spec(i, -1, 50, -2, -5, Ofi: -100, Residual: i == 10 ? null : -1.0 * i)).ToArray());
        db.OptionsSupplemental.Add(new() { SessionId = 1, BarSeq = 11, MetricsVersion = OptionsSupplementalBar.MetricsVersion, CenterStrike = 23000,
            CePosition = "CallWriting", PePosition = "PutLongBuild", CeDeltaIv = -1.0, PeDeltaIv = 2.0, IvSkewEnd = 1.5, VolPcr = 1.2, RollVolPcr = 1.1 });
        db.OptionBandBars.AddRange(
            new AdaptiveOptionBandBarRow { SessionId = 1, BarSeq = 11, Side = OptionType.Call, BandStrikes = "23000", StartAvailableAtUtc = T0, EndAvailableAtUtc = T0, BandAvailable = true, ContractStrictDelta = -77 },
            new AdaptiveOptionBandBarRow { SessionId = 1, BarSeq = 11, Side = OptionType.Put, BandStrikes = "23000", StartAvailableAtUtc = T0, EndAvailableAtUtc = T0, BandAvailable = true, ContractStrictDelta = 88 });
        db.ResidualAnchorComponents.Add(new() { SessionId = 1, Side = OptionType.Call, Strike = 23000, Token = "C", TradingSymbol = "C", QuoteTimestampUtc = T0, Price0930 = 100 });
        db.ResidualAnchorComponents.Add(new() { SessionId = 1, Side = OptionType.Put, Strike = 23000, Token = "P", TradingSymbol = "P", QuoteTimestampUtc = T0, Price0930 = 100 });
        await db.SaveChangesAsync();

        var frame = (await new AdaptiveCommentaryFrameLoader().LoadAsync(db, Session(), 11, default))!;
        Assert.True(frame.ReadinessMet);
        Assert.Equal(-100, frame.StrictDelta); Assert.Equal(-1000, frame.RollingStrictDelta); Assert.Equal(50, frame.RollOiDelta);
        Assert.Equal(-100, frame.Ofi); Assert.Equal(-0.1, frame.MicroDev);
        Assert.Equal("CallWriting", frame.CePosition); Assert.Equal(-77, frame.CeStrictDelta); Assert.Equal(88, frame.PeStrictDelta);
        Assert.Equal(1.5, frame.IvSkew);
        Assert.Null(frame.DeltaBasis);                                      // basis is deliberately unavailable until a freshness rule is approved
        // The previous bar (10) had no residual, so the adjacent delta is unavailable even though bar 9 was valid (no bridging).
        Assert.True(frame.ResidualAvailable); Assert.Equal(-11.0, frame.DirectionalResidualPct);
        Assert.Null(frame.AdjacentDirectionalResidualDelta); Assert.Null(frame.CeResidualDeltaPct);

        // Missing sidecars simply leave their fields unavailable.
        var early = (await new AdaptiveCommentaryFrameLoader().LoadAsync(db, Session(), 9, default))!;
        Assert.False(early.ReadinessMet);                                   // only nine valid bars at that point
        Assert.Null(early.CePosition); Assert.Null(early.RollingStrictDelta);
        Assert.Null(await new AdaptiveCommentaryFrameLoader().LoadAsync(db, Session(), 99, default));
    }
}
