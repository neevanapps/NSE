using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Host;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptiveWeak2ObservationServiceTests
{
    [Fact]
    public async Task TriggerAndReplay_FreezePolicyAndIdentityWithoutDuplicates()
    {
        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var context = Context();
        var states = new[]
        {
            AdaptiveCoreParityTests.MakeFlowState(10, .30, 1, 1, null),
            AdaptiveCoreParityTests.MakeFlowState(11, .26, -1, 1, "Weakening"),
            AdaptiveCoreParityTests.MakeFlowState(12, .22, 1, 1, "Weakening"),
        };
        AdaptiveWeak2Classifier.Apply(states, .20);
        db.FutureBars.AddRange(states.Select(s => new AdaptiveFutureBarRow
        {
            SessionId = context.Session.Id,
            BarSeq = s.Bar.BarSeq,
            StartAvailableAtUtc = s.Bar.StartAvailableAtUtc,
            EndAvailableAtUtc = s.Bar.EndAvailableAtUtc,
            Close = s.Bar.Close,
        }));
        await db.SaveChangesAsync();
        var package = new AdaptiveCompletedBarPackage(states[2].Bar, states[2],
            new OptionBandBarResult(null, false, null, null, null, null, "unavailable"), [], true);
        var service = new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);

        await service.ProcessPackageAsync(source, db, context, package, default);
        db.ChangeTracker.Clear(); // Fresh tracked state, as after process restart.
        await service.ProcessPackageAsync(source, db, context, package, default);

        var row = Assert.Single(await db.Weak2Observations.ToListAsync());
        Assert.Equal(AdaptiveWeak2ObservationService.SelectionPolicyV1, row.SelectionPolicy);
        Assert.Equal(10, row.StrongBaseBarSeq);
        Assert.Equal(11, row.Weak1BarSeq);
        Assert.Equal(12, row.TriggerBarSeq);
        Assert.Equal(17, row.H5TargetBarSeq);
        Assert.Equal(AdaptiveObservationStatus.PendingH5, row.Status);

        await service.FinalizeSessionAsync(source, db, context, default);
        Assert.Equal(AdaptiveObservationStatus.Unavailable, row.Status);
        Assert.Contains("H5 did not complete", row.UnavailableReason);
        Assert.Equal(AdaptiveWeak2ObservationService.SelectionPolicyV1, row.SelectionPolicy);
    }

    internal static AdaptiveObserverSessionContext Context()
    {
        var day = new DateOnly(2026, 9, 25);
        var at = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);
        var session = new AdaptiveSessionStateRow
        {
            Id = 1, TradeDate = day, ModelVersion = OpeningVolumeProjectionV1.ModelVersion,
            SourceBranch = "test", SourceCommitSha = "test", BuildUtc = at,
            FutureToken = "FUT", FutureSymbol = "NIFTYFUT", FutureExpiry = day.AddDays(4),
            OpeningWindowStartUtc = at.AddMinutes(-15), OpeningWindowEndUtc = at,
            EstimatorName = nameof(OpeningVolumeProjectionV1), CreatedAtUtc = at,
        };
        return new AdaptiveObserverSessionContext(session,
            new AdaptiveSessionDefinition(day, "FUT", "NIFTYFUT", day.AddDays(4), 65, 100),
            day.AddDays(4), [], null, at);
    }
}
