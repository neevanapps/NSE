using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Persistence;

static class LateStartRelationalValidation
{
    public static async Task CheckAsync(DbContextOptions<AdaptiveObserverDbContext> options, string connection, string sha, string evidence)
    {
        // Isolated date before the other fixture; those future rows cannot enter its median.
        var day = new DateOnly(2026, 7, 1);
        var at = new DateTimeOffset(2026, 7, 1, 4, 30, 0, TimeSpan.Zero);
        await using var db = new AdaptiveObserverDbContext(options);
        foreach (var (date, volume) in new[] { (day.AddDays(-2), 2000L), (day.AddDays(-1), 4000L) })
            db.Sessions.Add(new AdaptiveSessionStateRow { TradeDate = date, ModelVersion = OpeningVolumeProjectionV1.ModelVersion,
                SourceBranch = "isolated-late-start", SourceCommitSha = sha, BuildUtc = at, IsHistoricalSeed = true,
                FutureToken = "LATE-HISTORY", FutureSymbol = "LATE-HISTORY", FutureExpiry = day,
                OpeningWindowStartUtc = at.AddMinutes(-45), OpeningWindowEndUtc = at.AddMinutes(-30),
                OpeningVolume = volume, OpeningCoverageMinutes = 15,
                EstimatorName = nameof(OpeningVolumeProjectionV1), CreatedAtUtc = at });
        await db.SaveChangesAsync();
        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(connection).Options);
        source.Instruments.Add(new Instrument { Token = "LATE-FUT", TradingSymbol = "LATE-FUT", Exchange = Exchange.Nfo,
            Underlying = "NIFTY", InstrumentType = InstrumentType.Future, AsOfDate = day, ExpiryDate = day.AddDays(20), LotSize = 65 });
        source.Instruments.Add(new Instrument { Token = "LATE-CE", TradingSymbol = "LATE-CE", Exchange = Exchange.Nfo,
            Underlying = "NIFTY", InstrumentType = InstrumentType.Option, OptionType = OptionType.Call,
            AsOfDate = day, ExpiryDate = day.AddDays(1), StrikePrice = 23000, LotSize = 65 });
        source.Ticks.Add(TickAt(at, 1000000)); await source.SaveChangesAsync();
        var coordinator = new AdaptiveSessionCoordinator(new(), new Monitor(), NullLogger<AdaptiveSessionCoordinator>.Instance);
        var context = (await coordinator.TryGetOrCreateAsync(source, db, day, at, default))!;
        if (!context.Session.UsesMedianOpeningFallback || context.Session.EstimatorInputOpeningVolume != 3000
            || context.Session.MedianOpeningSampleCount != 2 || context.ResidualAnchor is not null)
            throw new Exception("PostgreSQL late-start calibration/provenance differs.");
        var persistence = new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance);
        var observations = new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);
        var recovery = new AdaptiveStateRecoveryService(new(), persistence, observations, NullLogger<AdaptiveStateRecoveryService>.Instance);
        var baseline = await recovery.RecoverAsync(source, db, context, at, default);
        if (baseline.Engine.FutureBars.Count != 0 || baseline.Engine.PartialBar.AccumulatedVolume != 0)
            throw new Exception("Late baseline fabricated offline bars.");
        for (var i = 1; i <= 10; i++) source.Ticks.Add(TickAt(at.AddSeconds(i), 1000000 + context.Session.BaseBarVolume * i));
        await source.SaveChangesAsync();
        await recovery.RecoverAsync(source, db, context, at.AddSeconds(9), default);
        var service = new AdaptiveObserverDataService(new Factory(options), NullLogger<AdaptiveObserverDataService>.Instance);
        var nine = (await service.LoadSnapshotAsync(5, captureSessionId: context.Session.Id, throughBarSeq: 9))!;
        if (nine.ConsecutiveValidBars != 9 || nine.CanUseForDecisions) throw new Exception("Nine-bar snapshot incorrectly ready.");
        await recovery.RecoverAsync(source, db, context, at.AddSeconds(10), default);
        await using (var restarted = new AdaptiveObserverDbContext(options))
        {
            var frozen = (await coordinator.TryGetOrCreateAsync(source, restarted, day, at.AddMinutes(10), default))!;
            var replay = await recovery.RecoverAsync(source, restarted, frozen, at.AddSeconds(10), default);
            if (replay.ReconciledBars != 10 || frozen.Session.EstimatorInputOpeningVolume != 3000)
                throw new Exception("Late PostgreSQL restart changed median or completed bars.");
        }
        var ten = (await service.LoadSnapshotAsync(5, captureSessionId: context.Session.Id, throughBarSeq: 10))!;
        if (ten.ConsecutiveValidBars != 10 || !ten.CanUseForDecisions || ten.Residuals.Any(x => x.IsAvailable))
            throw new Exception("Ten-bar readiness or missing residual anchor differs.");
        await File.WriteAllTextAsync(Path.Combine(evidence, "late-start-postgres.json"), JsonSerializer.Serialize(new {
            sourceSha = sha, median = 3000, samples = 2, baselineBars = 0, nineReady = false, tenReady = true,
            verifiedRestartBars = 10, missingResidualsRemainUnavailable = true }));
        Console.WriteLine("LATE START POSTGRES PASS: frozen median, zero offline bars, 9/10 readiness, ten-bar restart parity, missing residuals unavailable.");
    }
    static Tick TickAt(DateTimeOffset at, long volume) => new() { Token = "LATE-FUT", Exchange = Exchange.Nfo,
        ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 23000, Volume = volume };
    sealed class Factory(DbContextOptions<AdaptiveObserverDbContext> options) : IDbContextFactory<AdaptiveObserverDbContext>
    {
        public AdaptiveObserverDbContext CreateDbContext() => new(options);
        public Task<AdaptiveObserverDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
    sealed class Monitor : IOptionsMonitor<PricingOptions>
    {
        public PricingOptions CurrentValue { get; } = new() { RiskFreeRate = .065 };
        public PricingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PricingOptions, string?> listener) => null;
    }
}
