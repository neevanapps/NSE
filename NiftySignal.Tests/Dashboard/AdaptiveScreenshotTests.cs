using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Notifications;

namespace NiftySignal.Tests.Dashboard;

public sealed class AdaptiveScreenshotTests
{
    static readonly DateTimeOffset Cutoff = new(2026, 10, 6, 4, 0, 0, TimeSpan.Zero); // 09:30 IST
    static readonly DateOnly Day = new(2026, 10, 6);
    static AdaptiveObserverDbContext Database() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    static async Task Seed(AdaptiveObserverDbContext db, AdaptiveRuntimeStatus status = AdaptiveRuntimeStatus.Live, bool historical = false)
    {
        db.Sessions.Add(new() { Id = 1, TradeDate = Day, ModelVersion = "adaptive-v1", SourceBranch = "test", SourceCommitSha = "test",
            BuildUtc = Cutoff, FutureToken = "F", FutureSymbol = "NIFTY", FutureExpiry = Day, OpeningWindowStartUtc = Cutoff.AddMinutes(-15),
            OpeningWindowEndUtc = Cutoff, EstimatorName = "V1", CreatedAtUtc = Cutoff, IsHistoricalSeed = historical });
        db.Runtime.Add(new() { SessionId = 1, RuntimeStatus = status, LastHeartbeatUtc = Cutoff, LastCompletedBarSeq = 23, CurrentPartialBarVolume = 100 });
        for (var seq = 1; seq <= 23; seq++) db.FutureBars.Add(new() { SessionId = 1, BarSeq = seq,
            StartAvailableAtUtc = Cutoff.AddSeconds((seq - 14) * 30), EndAvailableAtUtc = Cutoff.AddSeconds((seq - 13) * 30) });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Schedule_Before0930_DoesNotQueue()
    {
        await using var db = Database(); await Seed(db);
        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, Cutoff.AddSeconds(-1), default);
        Assert.Empty(db.ScreenshotJobs);
    }

    [Fact]
    public async Task Schedule_InitialAndEveryFiveAfterBoundary_CatchesUpWithoutDuplicates()
    {
        await using var db = Database(); await Seed(db);
        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, Cutoff.AddMinutes(10), default);
        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, Cutoff.AddMinutes(11), default);
        var jobs = await db.ScreenshotJobs.OrderBy(x => x.TargetBarSeq).ToListAsync();
        Assert.Equal(new[] { 13, 18, 23 }, jobs.Select(x => x.TargetBarSeq));
        Assert.Equal(AdaptiveScreenshotKind.Initialization, jobs[0].Kind);
        Assert.All(jobs.Skip(1), x => Assert.Equal(AdaptiveScreenshotKind.FiveBars, x.Kind));
        Assert.Equal(Cutoff, jobs[0].TriggeredAtUtc);
        Assert.Equal(Cutoff.AddSeconds(150), jobs[1].TriggeredAtUtc);
    }

    [Theory]
    [InlineData(AdaptiveRuntimeStatus.Calibrating, false)]
    [InlineData(AdaptiveRuntimeStatus.Rebuilding, false)]
    [InlineData(AdaptiveRuntimeStatus.Degraded, false)]
    [InlineData(AdaptiveRuntimeStatus.Live, true)]
    public async Task Schedule_NotReadyOrHistorical_DoesNotQueue(AdaptiveRuntimeStatus status, bool historical)
    {
        await using var db = Database(); await Seed(db, status, historical);
        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, Cutoff.AddMinutes(10), default);
        Assert.Empty(db.ScreenshotJobs);
    }

    [Fact]
    public async Task Schedule_ZeroOpeningBars_InitialZeroThenFive()
    {
        await using var db = Database(); await Seed(db);
        foreach (var bar in db.FutureBars) bar.EndAvailableAtUtc = Cutoff.AddSeconds(bar.BarSeq);
        await db.SaveChangesAsync();
        await AdaptiveScreenshotSchedule.EnqueueAsync(db, Day, Cutoff.AddMinutes(1), default);
        Assert.Equal(new[] { 0, 5, 10, 15, 20 }, await db.ScreenshotJobs.OrderBy(x => x.TargetBarSeq).Select(x => x.TargetBarSeq).ToArrayAsync());
    }

    [Fact]
    public async Task Recovery_InterruptedCaptureRetriesButUploadIsUncertain()
    {
        await using var db = Database();
        var a = AdaptiveScreenshotSchedule.New(null, Day, 0, AdaptiveScreenshotKind.PreLiveTest, Cutoff, Cutoff); a.Status = AdaptiveScreenshotStatus.Capturing;
        var b = AdaptiveScreenshotSchedule.New(null, Day, 0, AdaptiveScreenshotKind.PreLiveTest, Cutoff, Cutoff); b.Status = AdaptiveScreenshotStatus.Sending;
        var c = AdaptiveScreenshotSchedule.New(null, Day, 0, AdaptiveScreenshotKind.PreLiveTest, Cutoff, Cutoff); c.Status = AdaptiveScreenshotStatus.Sent; c.TelegramMessageId = 99;
        db.AddRange(a, b, c); await db.SaveChangesAsync();
        await AdaptiveScreenshotSchedule.RecoverInterruptedAsync(db, default);
        Assert.Equal(AdaptiveScreenshotStatus.Pending, a.Status);
        Assert.Equal(AdaptiveScreenshotStatus.DeliveryUncertain, b.Status);
        Assert.Equal(AdaptiveScreenshotStatus.Sent, c.Status);
        Assert.Equal(99, c.TelegramMessageId);
    }

    [Fact]
    public async Task Read_PinnedOlderSession_UsesBoundaryNotLatestAndDoesNotMutateRuntime()
    {
        var options = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AdaptiveObserverDbContext(options); await Seed(db);
        var service = new AdaptiveObserverDataService(new Factory(options), NullLogger<AdaptiveObserverDataService>.Instance);
        var snapshot = await service.LoadSnapshotAsync(10, captureSessionId: 1, throughBarSeq: 18);
        Assert.NotNull(snapshot);
        Assert.Equal(18, snapshot.Runtime.LastCompletedBarSeq);
        Assert.Equal(18, snapshot.Futures.First().Bar.BarSeq);
        Assert.Equal(0, snapshot.Runtime.CurrentPartialBarVolume);
        Assert.Equal(23, (await db.Runtime.SingleAsync()).LastCompletedBarSeq);
        Assert.Equal(100, (await db.Runtime.SingleAsync()).CurrentPartialBarVolume);
        Assert.Equal(20, snapshot.Residuals.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadSnapshotAsync(10, captureSessionId: 1, throughBarSeq: 24));
    }

    [Theory]
    [InlineData(TelegramDocumentOutcome.Sent, AdaptiveScreenshotStatus.Sent)]
    [InlineData(TelegramDocumentOutcome.Uncertain, AdaptiveScreenshotStatus.DeliveryUncertain)]
    public async Task Processor_AcknowledgedOrUncertain_DoesNotResend(TelegramDocumentOutcome outcome, AdaptiveScreenshotStatus status)
    {
        await using var db = Database();
        var job = AdaptiveScreenshotSchedule.New(null, Day, 0, AdaptiveScreenshotKind.PreLiveTest, Cutoff, Cutoff);
        db.Add(job); await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "adaptive-screenshot-test-" + Guid.NewGuid());
        try {
            var renderer = new Renderer(); var sender = new Sender(outcome);
            var processor = new AdaptiveScreenshotProcessor(renderer, sender, Options.Create(new AdaptiveScreenshotOptions { OutputDirectory = root }), new Clock());
            await processor.ProcessOneAsync(db, Cutoff, default);
            await processor.ProcessOneAsync(db, Cutoff.AddMinutes(10), default);
            Assert.Equal(status, job.Status); Assert.Equal(1, sender.Count); Assert.Equal(1, renderer.Count);
            Assert.NotNull(job.ImageSha256);
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Processor_Rejected_RetriesOriginalImageNotRecaptured()
    {
        await using var db = Database(); db.Add(AdaptiveScreenshotSchedule.New(null, Day, 0, AdaptiveScreenshotKind.PreLiveTest, Cutoff, Cutoff)); await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "adaptive-screenshot-test-" + Guid.NewGuid());
        try {
            var renderer = new Renderer(); var sender = new Sender(TelegramDocumentOutcome.Rejected);
            var processor = new AdaptiveScreenshotProcessor(renderer, sender, Options.Create(new AdaptiveScreenshotOptions { OutputDirectory = root }), new Clock());
            await processor.ProcessOneAsync(db, Cutoff, default);
            await processor.ProcessOneAsync(db, Cutoff.AddSeconds(30), default);
            Assert.Equal(1, sender.Count);
            sender.Outcome = TelegramDocumentOutcome.Sent;
            await processor.ProcessOneAsync(db, Cutoff.AddSeconds(61), default);
            Assert.Equal(2, sender.Count); Assert.Equal(1, renderer.Count);
            Assert.Equal(AdaptiveScreenshotStatus.Sent, (await db.ScreenshotJobs.SingleAsync()).Status);
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://user:password@localhost:5210")]
    [InlineData("http://localhost:5210/path")]
    public void Capture_RejectsNonLocalOrigin(string url) => Assert.Throws<InvalidOperationException>(() => new AdaptiveScreenshotOptions { BaseUrl = url }.LocalUri());

    [Fact]
    public async Task Processor_TelegramRejection_PausesOtherQueuedUploads()
    {
        await using var db = Database();
        db.Add(AdaptiveScreenshotSchedule.New(null, Day, 0, AdaptiveScreenshotKind.PreLiveTest, Cutoff, Cutoff));
        db.Add(AdaptiveScreenshotSchedule.New(null, Day, 0, AdaptiveScreenshotKind.PreLiveTest, Cutoff, Cutoff)); await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "adaptive-screenshot-test-" + Guid.NewGuid());
        try {
            var renderer = new Renderer(); var sender = new Sender(TelegramDocumentOutcome.Rejected);
            var clock = new Clock();
            var processor = new AdaptiveScreenshotProcessor(renderer, sender, Options.Create(new AdaptiveScreenshotOptions { OutputDirectory = root }), clock);
            sender.OnSend = () => clock.UtcNow = Cutoff.AddSeconds(20);
            await processor.ProcessOneAsync(db, Cutoff, default);
            await processor.ProcessOneAsync(db, Cutoff.AddSeconds(1), default);
            await processor.ProcessOneAsync(db, Cutoff.AddSeconds(70), default);
            Assert.Equal(1, sender.Count);
            Assert.Equal(1, renderer.Count);
            Assert.Equal(Cutoff.AddSeconds(80), (await db.ScreenshotJobs.OrderBy(x=>x.Id).FirstAsync()).NextAttemptUtc);
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow = Cutoff;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    sealed class Factory(DbContextOptions<AdaptiveObserverDbContext> options) : IDbContextFactory<AdaptiveObserverDbContext>
    {
        public AdaptiveObserverDbContext CreateDbContext() => new(options);
        public Task<AdaptiveObserverDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    sealed class Renderer : IAdaptiveScreenshotRenderer
    {
        public int Count;
        public async Task CaptureAsync(AdaptiveScreenshotJobRow job, string path, CancellationToken ct) { Count++; Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, [137,80,78,71,13,10,26,10], ct); }
    }
    sealed class Sender(TelegramDocumentOutcome outcome) : ITelegramDocumentSender
    {
        public int Count; public TelegramDocumentOutcome Outcome = outcome; public Action? OnSend;
        public Task<TelegramDocumentResult> SendAsync(string file, string caption, CancellationToken ct) { Count++; OnSend?.Invoke(); return Task.FromResult(new TelegramDocumentResult(Outcome, Outcome == TelegramDocumentOutcome.Sent ? 123 : null,
            Error: Outcome == TelegramDocumentOutcome.Rejected ? "Telegram rejected upload (HTTP 429)." : null)); }
    }
}
