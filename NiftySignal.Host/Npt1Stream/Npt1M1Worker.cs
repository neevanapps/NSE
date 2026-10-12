using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Npt1M1;
using NiftySignal.Npt1M1.Data;
using NiftySignal.Persistence;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Live;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Host.Npt1Stream;

/// <summary>EF-backed store of the finalized frames in <c>npt1m1.frames</c> (insert once; a later offer of the same identity must carry the same hash).</summary>
public sealed class EfFrameStore(IServiceScopeFactory scopes) : IM1FrameStore
{
    public async Task<bool> EnsureAsync(M1Bar bar, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Npt1M1DbContext>();
        var f = bar.Frame;
        var existing = await db.Frames.AsNoTracking().Where(x => x.SessionId == f.AdaptiveSessionId && x.BarSeq == (int)f.BarSeq).Select(x => x.FrameHash).SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            return string.Equals(existing, bar.FrameHash, StringComparison.Ordinal);
        }

        db.Frames.Add(new M1FrameRow
        {
            SessionId = f.AdaptiveSessionId,
            BarSeq = (int)f.BarSeq,
            StartAvailableAtUtc = f.StartAvailableAt.ToUniversalTime(),
            EndAvailableAtUtc = f.EndAvailableAt.ToUniversalTime(),
            Ready = f.Ready,
            IsValid = f.IsValid,
            ConsecutiveValidBars = f.ConsecutiveValidBars,
            MicroDevTimeWeighted = f.MicroDevTimeWeighted,
            MicroDevUsableSeconds = f.MicroDevUsableSeconds,
            PeBandContractStrictDelta = f.PeBandContractStrictDelta,
            BandCenterStrike = f.BandCenterStrike,
            BandExpiry = f.BandExpiry,
            BandUnavailableReason = f.BandUnavailableReason,
            FrameHash = bar.FrameHash,
            CanonicalText = M1Frames.Canonical(f),
            CreatedAtUtc = nowUtc.ToUniversalTime(),
        });
        await db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>
/// The NPT1 M1 stream's own hosted worker (docs/NPT1_M1_PRODUCER_PLAN.md): freezes the M1 session at 09:30:07 IST, replays the day's raw ticks through the M1 producer (restart mechanism and
/// warm-up), keeps polling the raw tick table, stores every finalized frame and feeds the NPT1 PAPER run. It runs on its own loop and thread, never inside the Adaptive Observer's poll loop, reads the raw tick
/// table with its own reader and shares no table, key or state with adaptive-v1. <b>PAPER ONLY</b>: nothing here can reach a broker. Registered only when an NPT1 strategy is enabled.
/// </summary>
public sealed class Npt1M1Worker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    Npt1TickReader reader,
    M1SessionBuilder builder,
    EfFrameStore frames,
    Npt1M1Telemetry telemetry,
    ILogger<Npt1M1Worker> logger) : BackgroundService
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StableOrderingLag = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RuntimeInterval = TimeSpan.FromSeconds(1);
    private const int BatchSize = 100_000;

    private readonly PaperLiveOptions _options = PaperLiveOptions.Bind(configuration);
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private PaperLiveCoordinator? _coordinator;
    private PostgresRunStore? _store;
    private DaySession? _day;
    private DateOnly? _unavailableDay;
    private DateOnly? _historyEnsuredFor;

    private sealed class DaySession
    {
        public required DateOnly Day { get; init; }
        public required M1BarProducer Producer { get; init; }
        public required Npt1M1StreamProcessor Processor { get; init; }
        public required IReadOnlyList<string> Tokens { get; init; }
        public required IPaperFeed Feed { get; init; }
        public long AfterId { get; set; }
        public bool Replayed { get; set; }
        public DateTimeOffset? LastRuntime { get; set; }
        public int LastBarSeq { get; set; }
        public DateTimeOffset? LastBarEnd { get; set; }
        public double MaxPollMs { get; set; }
    }

    public PaperLiveStatus? Status => _coordinator?.Status;

    public Npt1M1Telemetry Telemetry => telemetry;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AnyNpt1Enabled)
        {
            return;
        }

        var errors = _options.Validate().Concat(_options.ValidateRisk(PaperLiveSetup.Npt1Strategies(_options))).ToList();
        if (errors.Count > 0)
        {
            logger.LogCritical("NPT1 M1 stream NOT started, configuration invalid: {Errors}", string.Join(" ", errors));
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var ist = now.ToOffset(Ist);
                var day = DateOnly.FromDateTime(ist.Date);
                var time = TimeOnly.FromDateTime(ist.DateTime);
                var weekday = ist.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
                if (!weekday || time < new TimeOnly(9, 15) || time >= new TimeOnly(15, 35))
                {
                    if (_day is not null && (time >= new TimeOnly(15, 35) || _day.Day != day))
                    {
                        await CloseAsync(_day, stoppingToken);
                        _day = null;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                if (_unavailableDay == day)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                if (_historyEnsuredFor != day)
                {
                    // Prior-session volumes are computed from the raw ticks once per date and kept in npt1m1.volume_history. Doing it as soon as the day starts (09:15) keeps the first run's
                    // longer computation (up to 35 prior dates) out of the 09:30:07 freeze.
                    await EnsureHistoryAsync(day, now, stoppingToken);
                    _historyEnsuredFor = day;
                }

                if (_day is null || _day.Day != day)
                {
                    _day = await TryStartAsync(day, now, stoppingToken);
                    if (_day is null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                        continue;
                    }
                }

                await PollAsync(_day, now, stoppingToken);
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "NPT1 M1 stream failed; the in-memory state is discarded and rebuilt from the raw ticks (the paper run is recovered from its journal).");
                _day = null;
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task EnsureHistoryAsync(DateOnly day, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<Npt1M1DbContext>();
            var history = await builder.EnsureVolumeHistoryAsync(source, store, day, now, ct);
            logger.LogInformation("NPT1 M1 prior-session volume history ready for {Day}: {Dates} dates, {Eligible} baseline-eligible.", day, history.Count, history.Count(h => h.BaselineEligible));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "NPT1 M1 prior-session volume history could not be prepared; the session freeze will retry it.");
        }
    }

    private PaperLiveCoordinator EnsureCoordinator()
    {
        if (_coordinator is not null)
        {
            return _coordinator;
        }

        var connectionString = configuration.GetConnectionString(_options.ConnectionStringName);
        var strategies = PaperLiveSetup.Npt1Strategies(_options);
        Func<IRunStore> factory = () =>
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"ConnectionStrings:{_options.ConnectionStringName} is not set; the NPT1 paper run cannot persist and therefore does not trade.");
            }

            _store ??= new PostgresRunStore(connectionString);
            return new BlockingRunStoreAdapter(_store, TimeSpan.FromSeconds(30));
        };
        _coordinator = new PaperLiveCoordinator(_options, strategies, factory, logger, $"{Environment.MachineName}:{Environment.ProcessId}:npt1", configFactory: _ => PaperLiveSetup.Npt1Config(_options, strategies));
        return _coordinator;
    }

    private async Task<DaySession?> TryStartAsync(DateOnly day, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<Npt1M1DbContext>();
        M1SessionAttempt attempt;
        try
        {
            attempt = await builder.TryBuildAsync(source, store, day, now, _options.OptionTickSize ?? 0.05m, ct);
        }
        catch (Exception e) when (e is Npgsql.PostgresException or InvalidOperationException)
        {
            logger.LogCritical(e, "NPT1 M1 session could not be built (is the npt1m1 schema installed? it is applied from NiftySignal.Npt1M1.Data/Sql/npt1m1-schema.idempotent.sql, never implicitly).");
            _unavailableDay = day;
            return null;
        }

        if (attempt.Build is not { } build)
        {
            if (!attempt.Wait)
            {
                logger.LogWarning("NPT1 M1 stream is unavailable for {Day}: {Reason}", day, attempt.Reason);
                _unavailableDay = day;
            }

            return null;
        }

        var problem = await builder.PersistOrVerifyAsync(store, build, now, ct);
        if (problem is not null)
        {
            logger.LogCritical("NPT1 M1 stream is NOT running for {Day}: {Problem}", day, problem);
            _unavailableDay = day;
            return null;
        }

        var freeze = build.Freeze;
        var producer = new M1BarProducer(freeze);
        var contracts = freeze.Freeze.ToContracts();
        var coordinator = EnsureCoordinator();
        coordinator.StartSession(new LiveSessionInfo(M1Identity.SessionId(day), day, contracts, freeze.Freeze.FutureToken, producer.ReadinessSpec));
        var warmupEnd = M1Time.IstTime(day, 9, 30);
        var liveBoundary = _startedUtc > warmupEnd ? _startedUtc : warmupEnd;
        var feed = new CoordinatorFeed(coordinator);
        var processor = new Npt1M1StreamProcessor(frames, feed, warmupEnd, liveBoundary, telemetry, logger);
        logger.LogInformation(
            "NPT1 M1 session {Session} frozen: base bar {Base} ({Status}, opening {Opening}), {Options} options, expiry {Expiry}; live boundary {Boundary:O}.",
            M1Identity.SessionId(day), freeze.Report.BaseBarVolume, freeze.Decision.MethodStatus, freeze.Report.OpeningVolume, contracts.Count, freeze.Report.WeeklyExpiry, liveBoundary);
        return new DaySession
        {
            Day = day,
            Producer = producer,
            Processor = processor,
            Feed = feed,
            Tokens = freeze.Freeze.Options.Select(o => o.Token).Append(freeze.Freeze.FutureToken).Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    private async Task PollAsync(DaySession s, DateTimeOffset now, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var rows = 0;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
            while (true)
            {
                var batch = await reader.ReadAfterIdAsync(source, s.Tokens, s.AfterId, s.Day, BatchSize, ct);
                foreach (var row in batch)
                {
                    s.AfterId = row.Id;
                    s.Producer.Offer(row);
                }

                rows += batch.Count;
                var cut = batch.Count >= BatchSize;
                if (!cut)
                {
                    s.Producer.AdvanceSource(now);                   // every row received before now has been offered: the stable boundary may move to now - the ordering lag
                }

                var groups = s.Producer.Release();
                if (groups.Count > 0)
                {
                    await s.Processor.HandleAsync(groups, ct);
                    foreach (var bar in groups.SelectMany(g => g.Bars))
                    {
                        s.LastBarSeq = (int)bar.Event.BarSeq;
                        s.LastBarEnd = bar.Event.AvailableAt;
                    }
                }

                if (!cut)
                {
                    break;
                }
            }
        }

        if (!s.Replayed)
        {
            s.Replayed = true;
            logger.LogInformation("NPT1 M1 replay of {Day} complete: {Rows} rows, {Bars} M1 bars, {Skipped} catch-up groups withheld from the engine.", s.Day, rows, telemetry.BarsProduced, telemetry.GroupsSkippedCatchUp);
        }

        // Every group at or before the stable cutoff has been handled: the paper run may advance its clock (deadlines, the 15:00 terminal protocol).
        s.Feed.OnHeartbeat(now - StableOrderingLag);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        telemetry.Poll(elapsed, rows);
        s.MaxPollMs = Math.Max(s.MaxPollMs, elapsed);
        if (s.LastRuntime is not { } last || now - last >= RuntimeInterval)
        {
            s.LastRuntime = now;
            await WriteRuntimeAsync(s, now, elapsed, "Running", null, ct);
        }
    }

    private async Task CloseAsync(DaySession s, CancellationToken ct)
    {
        try
        {
            await PollAsync(s, DateTimeOffset.UtcNow + StableOrderingLag, ct);
            await s.Processor.HandleAsync(s.Producer.Finish(), ct);
            s.Feed.Finish();
            await WriteRuntimeAsync(s, DateTimeOffset.UtcNow, 0, "Closed", null, ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "NPT1 M1 session-close finalization failed.");
        }
    }

    private async Task WriteRuntimeAsync(DaySession s, DateTimeOffset now, double lastPollMs, string status, string? detail, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Npt1M1DbContext>();
            var id = M1Identity.SessionId(s.Day);
            var row = await db.Runtime.SingleOrDefaultAsync(r => r.SessionId == id, ct);
            if (row is null)
            {
                row = new M1RuntimeRow { SessionId = id, Status = status };
                db.Runtime.Add(row);
            }

            row.Status = status;
            row.LastTickId = s.AfterId;
            row.LastBarSeq = s.LastBarSeq;
            row.LastBarEndUtc = s.LastBarEnd?.ToUniversalTime();
            row.LastHeartbeatUtc = now.ToUniversalTime();
            row.Polls = telemetry.Polls;
            row.LastPollMillis = lastPollMs;
            row.MaxPollMillis = s.MaxPollMs;
            row.RowsRead = telemetry.RowsRead;
            row.DivergentFrames = telemetry.DivergentFrames;
            row.Detail = detail ?? _coordinator?.Status.Detail;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "NPT1 M1 runtime row could not be written (operational only; trading does not depend on it).");
        }
    }

    public override void Dispose()
    {
        _coordinator?.Dispose();
        _store?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }
}
