using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// Every completed bar gets a durable publication, in bar order: the execution service reads only bars after the last one it handled, so a bar that is never published, or published after a later one, is never
/// evaluated. Real worker, observer database on a disposable local PostgreSQL (skipped without <c>NSE_ADAPTIVE_MIGRATION_TEST_DB</c>).
/// </summary>
public sealed class AdaptiveRecoveredBarPublicationTests
{
    static readonly DateOnly Day = new(2026, 9, 25);
    static readonly DateTimeOffset At = new(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);      // 09:30 IST, the fixture's signal start

    [PgMigrationFact]
    public async Task AStart_PublishesEveryRecoveredBar_AndLiveBarsContinueWithoutAGap()
    {
        await using var rig = await Rig.CreateAsync();

        var live = await rig.StartAsync();

        var recovered = await rig.BarsAsync();
        Assert.NotEmpty(recovered);
        Assert.Equal(recovered.Select(bar => (bar, true)), await rig.PublishedAsync());
        Assert.False(live.RecoveredBarsUnpublished);

        await rig.PollAsync(live, newTicks: 5);

        var all = await rig.BarsAsync();
        Assert.True(all.Count > recovered.Count);
        Assert.Equal(all.Select(bar => (bar, true)), await rig.PublishedAsync());
    }

    [PgMigrationFact]
    public async Task WhileTheRecoveredBarsCannotBePublished_NoLaterBarOvertakesThem()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.FailPublicationsAsync("NEW.\"BarSeq\" <= 3");

        var live = await rig.StartAsync();
        Assert.True(live.RecoveredBarsUnpublished);
        await rig.PollAsync(live, newTicks: 5);

        Assert.Empty(await rig.PublishedAsync());                        // a later bar published now would hide bars 1..n from the execution service for good
        Assert.NotEmpty(live.UnpublishedBars);

        await rig.AllowPublicationsAsync();
        await rig.PollAsync(live, newTicks: 0);                          // retried without waiting for another bar to complete

        var all = await rig.BarsAsync();
        Assert.Equal(all.Select(bar => (bar, true)), await rig.PublishedAsync());
        Assert.False(live.RecoveredBarsUnpublished);
        Assert.Empty(live.UnpublishedBars);
    }

    [PgMigrationFact]
    public async Task AFailedLivePublication_IsRetriedByTheNextPoll_EvenWhenNoFurtherBarCompletes()
    {
        await using var rig = await Rig.CreateAsync();
        var live = await rig.StartAsync();
        var recovered = (await rig.BarsAsync()).Count;
        await rig.FailPublicationsAsync($"NEW.\"BarSeq\" > {recovered}");

        await rig.PollAsync(live, newTicks: 5);
        Assert.Equal(recovered, (await rig.PublishedAsync()).Count);
        Assert.NotEmpty(live.UnpublishedBars);

        await rig.AllowPublicationsAsync();
        await rig.PollAsync(live, newTicks: 0);

        var all = await rig.BarsAsync();
        Assert.Equal(all.Select(bar => (bar, true)), await rig.PublishedAsync());
    }

    sealed class Rig : IAsyncDisposable
    {
        readonly string _source = Guid.NewGuid().ToString();
        TempAdaptiveDatabase _observer = null!;
        ServiceProvider _services = null!;
        AdaptiveObserverWorker _worker = null!;
        int _ticks;
        long _sessionId;

        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig { _observer = await TempAdaptiveDatabase.CreateAsync("niftysignal_adaptive_pub_test_") };
            await rig._observer.MigrateAsync();
            var context = AdaptiveWeak2ObservationServiceTests.Context();
            var session = context.Session;
            session.WeeklyOptionExpiry = context.WeeklyOptionExpiry;
            session.BaseBarVolume = 100; session.RollingWindowBars = 10; session.LotSize = 65; session.RiskFreeRate = .065; session.StrongThreshold = .2;
            session.OptionUniverseJson = "[]";
            await using (var db = rig._observer.Context())
            {
                db.Sessions.Add(session);
                await db.SaveChangesAsync();
                db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = session.Id, LastHeartbeatUtc = At });
                await db.SaveChangesAsync();
            }

            rig._sessionId = session.Id;
            await rig.AddTicksAsync(13);
            rig._services = AdaptiveRecoveryReliabilityTests.World.Services(rig.Source, rig._observer.Context, new AdaptiveRecoveryOptions());
            rig._worker = rig._services.GetRequiredService<AdaptiveObserverWorker>();
            rig._worker.MarkHistoricalBootstrapDone(Day);
            return rig;
        }

        NiftySignalDbContext Source() => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(_source).Options);

        /// <summary>One future tick per second; each one completes a bar of 100 contracts.</summary>
        async Task AddTicksAsync(int count)
        {
            await using var source = Source();
            for (var n = 0; n < count; n++, _ticks++)
                source.Ticks.Add(new Tick
                {
                    Id = _ticks + 1, Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = At.AddSeconds(_ticks), ReceivedAt = At.AddSeconds(_ticks), LastPrice = 23000 + _ticks, Volume = 1000 + 100 * _ticks,
                });
            await source.SaveChangesAsync();
        }

        public async Task<AdaptiveObserverWorker.LiveState> StartAsync() => await _worker.TryStartOrRecoverAsync(Day, At.AddSeconds(_ticks + 5), default) ?? throw new InvalidOperationException("The session did not start.");

        public async Task PollAsync(AdaptiveObserverWorker.LiveState live, int newTicks)
        {
            await AddTicksAsync(newTicks);
            await _worker.PollLiveAsync(live, At.AddSeconds(_ticks + 5), default, forceRuntimeFlush: true);
        }

        public async Task<List<int>> BarsAsync()
        {
            await using var db = _observer.Context();
            return await db.FutureBars.Where(x => x.SessionId == _sessionId).OrderBy(x => x.BarSeq).Select(x => x.BarSeq).ToListAsync();
        }

        public async Task<List<(int, bool)>> PublishedAsync()
        {
            await using var db = _observer.Context();
            return (await db.BarPublications.AsNoTracking().Where(x => x.SessionId == _sessionId).OrderBy(x => x.BarSeq).ToListAsync()).Select(x => (x.BarSeq, x.Verified)).ToList();
        }

        public Task FailPublicationsAsync(string when) => _observer.ExecAsync(
            $"CREATE FUNCTION refuse_publication() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF {when} THEN RAISE EXCEPTION 'injected publication failure'; END IF; RETURN NEW; END $$; " +
            "CREATE TRIGGER refuse_publication BEFORE INSERT ON adaptive_bar_publications FOR EACH ROW EXECUTE FUNCTION refuse_publication()");

        public Task AllowPublicationsAsync() => _observer.ExecAsync("DROP TRIGGER refuse_publication ON adaptive_bar_publications; DROP FUNCTION refuse_publication()");

        public async ValueTask DisposeAsync()
        {
            if (_services is not null) await _services.DisposeAsync();
            await _observer.DisposeAsync();
        }
    }
}
