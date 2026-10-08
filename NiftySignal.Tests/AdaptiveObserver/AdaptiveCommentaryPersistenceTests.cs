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
        Assert.Equal(16, await service.ProcessThroughAsync(db, Session(), 16, CommentaryNotificationMode.Suppress, default));

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
        Assert.Equal(events[^1].EventIdentity, $"commentary-v1:1:16:BuyerExpansion:New:Long");
    }

    [Fact]
    public async Task StructuredEvidenceIsPersisted_NotOnlyProse()
    {
        await using var db = await Seeded(Scenario());
        await Service().ProcessThroughAsync(db, Session(), 12, CommentaryNotificationMode.Suppress, default);
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
        await service.ProcessThroughAsync(db, Session(), 16, CommentaryNotificationMode.Suppress, default);
        var before = Digest(await db.CommentaryEvents.AsNoTracking().ToListAsync());
        var runtimeBefore = await db.CommentaryRuntime.AsNoTracking().SingleAsync();

        // Losing the operational checkpoint (it is rebuildable): a full replay verifies the existing rows and recreates identical state.
        db.CommentaryRuntime.RemoveRange(db.CommentaryRuntime); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(16, await service.ProcessThroughAsync(db, Session(), 16, CommentaryNotificationMode.Suppress, default));

        Assert.Equal(4, await db.CommentaryEvents.CountAsync());
        Assert.Equal(before, Digest(await db.CommentaryEvents.AsNoTracking().ToListAsync()));
        var runtimeAfter = await db.CommentaryRuntime.AsNoTracking().SingleAsync();
        Assert.Equal((runtimeBefore.LastEvaluatedBarSeq, runtimeBefore.CurrentEventId, runtimeBefore.CurrentRegime, runtimeBefore.CurrentEventType, runtimeBefore.ActivePhase),
            (runtimeAfter.LastEvaluatedBarSeq, runtimeAfter.CurrentEventId, runtimeAfter.CurrentRegime, runtimeAfter.CurrentEventType, runtimeAfter.ActivePhase));
        Assert.Equal(0, service.MismatchCount);

        // A second call with nothing new evaluates nothing and persists nothing.
        Assert.Equal(0, await service.ProcessThroughAsync(db, Session(), 16, CommentaryNotificationMode.Suppress, default));
    }

    [Fact]
    public async Task RestartMidSession_ContinuesFromTheCheckpoint_WithoutAFalseNewEvent()
    {
        await using var straight = await Seeded(Scenario());
        await Service().ProcessThroughAsync(straight, Session(), 16, CommentaryNotificationMode.Suppress, default);

        await using var restarted = await Seeded(Scenario());
        await Service().ProcessThroughAsync(restarted, Session(), 12, CommentaryNotificationMode.Suppress, default);        // the process "stops" while SellerExpansion is Strengthening
        restarted.ChangeTracker.Clear();
        var secondProcess = Service();                                                  // new service instance: no in-memory state at all
        await secondProcess.ProcessThroughAsync(restarted, Session(), 13, CommentaryNotificationMode.Suppress, default);     // bar 13 is a continuation: must NOT become a new event
        Assert.Equal(2, await restarted.CommentaryEvents.CountAsync());   // bars 10 (New) and 12 (Strengthening); 13 added nothing
        await secondProcess.ProcessAllCompletedAsync(restarted, Session(), default);

        Assert.Equal(Digest(await straight.CommentaryEvents.AsNoTracking().ToListAsync()), Digest(await restarted.CommentaryEvents.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task ATamperedStoredEvent_StopsProjection_AtThatBar_AndStaysStoppedAfterRestart()
    {
        await using var db = await Seeded(Scenario());
        var service = Service();
        await service.ProcessThroughAsync(db, Session(), 16, CommentaryNotificationMode.Suppress, default);
        var stored = await db.CommentaryEvents.FirstAsync(x => x.BarSeq == 10);
        stored.RenderedCommentary = "tampered"; await db.SaveChangesAsync();
        db.CommentaryRuntime.RemoveRange(db.CommentaryRuntime); await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        var evaluated = await service.ProcessThroughAsync(db, Session(), 16, CommentaryNotificationMode.EnqueueFinalBar, default);
        Assert.Equal(9, evaluated);                                                  // silent warm-up bars 1-9 only; bar 10 is the mismatch
        Assert.Equal(1, service.MismatchCount);
        Assert.Equal("tampered", (await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.BarSeq == 10)).RenderedCommentary);   // history never mutated
        Assert.Equal(4, await db.CommentaryEvents.CountAsync());
        Assert.Equal(9, (await db.CommentaryRuntime.AsNoTracking().SingleAsync()).LastEvaluatedBarSeq);                              // checkpoint did not pass the mismatch
        var marker = await db.ProjectionHealth.AsNoTracking().SingleAsync();
        Assert.Equal((AdaptiveProjectionComponents.Commentary, CommentaryEvaluator.Version, 10), (marker.Component, marker.Version, marker.BarSeq));
        Assert.Contains("RenderedCommentary", marker.Detail);

        // A restarted process (new service instance, fresh change tracking) stays stopped: nothing is evaluated, the checkpoint does not move.
        db.ChangeTracker.Clear();
        Assert.Equal(0, await Service().ProcessThroughAsync(db, Session(), 16, CommentaryNotificationMode.EnqueueFinalBar, default));
        Assert.Equal(0, await Service().ProcessAllCompletedAsync(db, Session(), default));
        Assert.Equal(9, (await db.CommentaryRuntime.AsNoTracking().SingleAsync()).LastEvaluatedBarSeq);
        Assert.Empty(await db.CommentaryNotificationJobs.ToListAsync());
    }

    [Fact]
    public async Task CatchUp_NeverCreatesNotificationJobs_ButALaterLiveBarMay()
    {
        // Bar 11 is a Flipped (eligible) reversal of the bar-10 Short expansion; bar 12 flips back to Short and arrives LIVE after the catch-up.
        Spec[] bars = [.. Enumerable.Range(1, 9).Select(i => new Spec(i, -1, 50, -2, -5)), new(10, -1, 50, -2, -5), new(11, 1, -5, 2, 5), new(12, -1, 50, -2, -5)];
        await using var db = await Seeded(bars.Take(11).ToArray());
        var service = Service();
        // First deployment mid-session: history (including an eligible Flipped at bar 11) is reconstructed with ZERO jobs.
        Assert.Equal(11, await service.ProcessAllCompletedAsync(db, Session(), default));
        var history = await db.CommentaryEvents.AsNoTracking().ToListAsync();
        Assert.Contains(history, e => e.Lifecycle == CommentaryLifecycle.Flipped && e.ShouldNotifyTelegram);
        Assert.Empty(await db.CommentaryNotificationJobs.ToListAsync());

        // A live bar afterwards that is genuinely eligible does queue a job.
        await Seed(db, bars[11]);
        await service.ProcessThroughAsync(db, Session(), 12, CommentaryNotificationMode.EnqueueFinalBar, default);
        var live = await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.BarSeq == 12);
        Assert.True(live.ShouldNotifyTelegram); Assert.Equal(CommentaryLifecycle.Flipped, live.Lifecycle);
        var job = await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync();
        Assert.Equal(live.Id, job.EventId);

        // Restart: catch-up verifies, creates nothing. Deleting the checkpoint and rebuilding still creates no backlog.
        await Service().ProcessAllCompletedAsync(db, Session(), default);
        db.CommentaryRuntime.RemoveRange(db.CommentaryRuntime); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Service().ProcessAllCompletedAsync(db, Session(), default);
        Assert.Single(await db.CommentaryNotificationJobs.ToListAsync());
        Assert.Equal(history.Count + 1, await db.CommentaryEvents.CountAsync());
    }

    [Fact]
    public async Task LiveCallThatMustCatchUpFirst_QueuesOnlyTheFinalBar()
    {
        // Checkpoint lost (or lagging) while the live observer completes bar 12: bars before it are catch-up inside the same call and never queue.
        Spec[] bars = [.. Enumerable.Range(1, 9).Select(i => new Spec(i, -1, 50, -2, -5)), new(10, -1, 50, -2, -5), new(11, 1, -5, 2, 5), new(12, -1, 50, -2, -5)];
        await using var db = await Seeded(bars);
        await Service().ProcessThroughAsync(db, Session(), 12, CommentaryNotificationMode.EnqueueFinalBar, default);
        var events = await db.CommentaryEvents.AsNoTracking().OrderBy(x => x.BarSeq).ToListAsync();
        Assert.Contains(events, e => e.BarSeq == 11 && e.Lifecycle == CommentaryLifecycle.Flipped && e.ShouldNotifyTelegram);   // eligible, but history
        var queuedBars = await db.CommentaryNotificationJobs.AsNoTracking()
            .Join(db.CommentaryEvents.AsNoTracking(), j => j.EventId, e => e.Id, (j, e) => e.BarSeq).ToListAsync();
        Assert.DoesNotContain(queuedBars, b => b < 12);
    }

    [Fact]
    public async Task CommentaryIsVersionKeyed_V1HistoryIsImmutable_AndAnotherVersionCoexists()
    {
        await using var db = await Seeded(Scenario());
        await Service().ProcessThroughAsync(db, Session(), 16, CommentaryNotificationMode.Suppress, default);
        var v1Events = await db.CommentaryEvents.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var v1Digest = Digest(v1Events);

        // A hypothetical later version reuses the same bars/types with its own identity and runtime: no unique-index collision.
        db.CommentaryEvents.Add(CloneAs(v1Events[0], "commentary-v2"));
        db.CommentaryRuntime.Add(new AdaptiveCommentaryRuntimeRow { SessionId = 1, CommentaryVersion = "commentary-v2", LastEvaluatedBarSeq = 3 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(2, await db.CommentaryRuntime.CountAsync());
        Assert.Equal(v1Digest, Digest(await db.CommentaryEvents.AsNoTracking().Where(x => x.CommentaryVersion == "commentary-v1").ToListAsync()));
        Assert.StartsWith("commentary-v1:", v1Events[0].EventIdentity);
        Assert.StartsWith("commentary-v2:", (await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.CommentaryVersion == "commentary-v2")).EventIdentity);

        // The v1 service keeps reading/advancing only its own runtime and events; the v2 rows are untouched.
        await Service().ProcessAllCompletedAsync(db, Session(), default);
        Assert.Equal(4, await db.CommentaryEvents.CountAsync(x => x.CommentaryVersion == "commentary-v1"));
        Assert.Equal(3, (await db.CommentaryRuntime.AsNoTracking().SingleAsync(x => x.CommentaryVersion == "commentary-v2")).LastEvaluatedBarSeq);
    }

    static AdaptiveCommentaryEventRow CloneAs(AdaptiveCommentaryEventRow e, string version) => new()
    {
        SessionId = e.SessionId, TradeDate = e.TradeDate, BarSeq = e.BarSeq, OccurredAtUtc = e.OccurredAtUtc, EventType = e.EventType, EventBias = e.EventBias,
        MarketRegime = e.MarketRegime, Lifecycle = e.Lifecycle, EvidenceAgreement = e.EvidenceAgreement, Severity = e.Severity, PreviousBias = e.PreviousBias,
        BiasChanged = e.BiasChanged, PrimaryEvidenceJson = e.PrimaryEvidenceJson, ConfirmationEvidenceJson = e.ConfirmationEvidenceJson,
        ContradictionEvidenceJson = e.ContradictionEvidenceJson, DataQualityJson = e.DataQualityJson, RenderedCommentary = e.RenderedCommentary,
        CommentaryVersion = version, EventIdentity = version + e.EventIdentity[e.CommentaryVersion.Length..], CreatedAtUtc = e.CreatedAtUtc,
    };

    [Fact]
    public async Task KnownInvalidSidecar_IsNotConsumedByCommentary_AndMissingStaysDistinct()
    {
        await using var db = await Seeded(Scenario().Take(12).ToArray());
        var loader = new AdaptiveCommentaryFrameLoader();
        Assert.Equal(-400, (await loader.LoadAsync(db, Session(), 12, default))!.Ofi);          // matching/usable sidecar is consumed
        db.ProjectionHealth.Add(new AdaptiveProjectionHealthRow
        {
            SessionId = 1, Component = AdaptiveProjectionComponents.FuturesSupplemental, Version = FuturesMicrostructureBar.MetricsVersion, BarSeq = 12,
            Detail = "Ofi: persisted=-400, recomputed=7", DetectedAtUtc = T0,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var invalid = await loader.LoadAsync(db, Session(), 12, default);
        Assert.Null(invalid!.Ofi); Assert.Null(invalid.MicroDev);                                // known-invalid is withheld from commentary
        Assert.Null((await loader.LoadAsync(db, Session(), 11, default))!.Ofi);                  // missing sidecar: unavailable
        Assert.Equal(-400, (await db.FuturesSupplemental.AsNoTracking().SingleAsync(x => x.BarSeq == 12)).Ofi);   // stored row untouched
    }

    [Fact]
    public async Task MissingBar_StopsEvaluationAndResumesLater_AndFailuresNeverThrow()
    {
        await using var db = Database(); db.Sessions.Add(Session()); await db.SaveChangesAsync();
        foreach (var s in Scenario().Take(11)) await Seed(db, s);
        var service = Service();
        Assert.Equal(11, await service.ProcessThroughAsync(db, Session(), 14, CommentaryNotificationMode.Suppress, default));        // bars 12-14 not persisted yet: stops at 11
        Assert.Equal(11, (await db.CommentaryRuntime.AsNoTracking().SingleAsync()).LastEvaluatedBarSeq);
        foreach (var s in Scenario().Skip(11)) await Seed(db, s);
        Assert.Equal(5, await service.ProcessThroughAsync(db, Session(), 16, CommentaryNotificationMode.Suppress, default));

        var disposed = Database(); await disposed.DisposeAsync();
        Assert.Equal(0, await Service().ProcessThroughAsync(disposed, Session(), 5, CommentaryNotificationMode.Suppress, default));   // unusable context: logged, never thrown
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
