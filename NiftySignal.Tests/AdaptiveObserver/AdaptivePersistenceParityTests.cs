using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptivePersistenceParityTests
{
    [Theory]
    [InlineData("Vwap")]
    [InlineData("TradeUpdates")]
    [InlineData("RollingEfficiency")]
    [InlineData("BandStrikes")]
    [InlineData("UnavailableReason")]
    public async Task Replay_DetectsPreviouslyUncheckedPersistedFields(string corruptedField)
    {
        await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var context = AdaptiveWeak2ObservationServiceTests.Context();
        db.Sessions.Add(context.Session);
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = context.Session.Id, LastHeartbeatUtc = context.SignalStartUtc });
        await db.SaveChangesAsync();
        var flow = AdaptiveCoreParityTests.MakeFlowState(12, .22, 1, 1, "Weakening");
        var package = new AdaptiveCompletedBarPackage(flow.Bar, flow,
            new OptionBandBarResult(null, false, null, null, null, null, "missing quotes"), [], false);
        var service = new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance);
        Assert.True((await service.PersistOrVerifyAsync(db, context.Session, package, context.SignalStartUtc, default)).Inserted);
        db.ChangeTracker.Clear();
        Assert.True((await service.PersistOrVerifyAsync(db, context.Session, package, context.SignalStartUtc, default)).VerifiedExisting);
        Assert.Single(await db.FutureBars.ToListAsync());

        switch (corruptedField)
        {
            case "Vwap": (await db.FutureBars.SingleAsync()).Vwap += 1; break;
            case "TradeUpdates": (await db.FutureBars.SingleAsync()).TradeUpdates += 1; break;
            case "RollingEfficiency": (await db.RollingStates.SingleAsync()).Efficiency += .1; break;
            case "BandStrikes": (await db.OptionBandBars.SingleAsync(x => x.Side == OptionType.Call)).BandStrikes = "changed"; break;
            case "UnavailableReason": (await db.OptionBandBars.SingleAsync(x => x.Side == OptionType.Call)).UnavailableReason = "changed"; break;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PersistOrVerifyAsync(db, context.Session, package, context.SignalStartUtc, default));
        Assert.Contains("Persisted history will not be overwritten", ex.Message);
    }
}
