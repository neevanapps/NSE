using Microsoft.EntityFrameworkCore;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// A completely separate database from both <c>NiftySignal.Persistence.NiftySignalDbContext</c>
/// (the live schema) and <c>NiftySignal.BacktestData.BacktestAnalysisDbContext</c> (the time-cadence
/// backtest dataset) -- this one exists purely for the volume-cadence experiment, and it is never
/// read by Host, Dashboard, or the existing time-cadence backtest tools.
/// </summary>
public sealed class VolumeBarDbContext(DbContextOptions<VolumeBarDbContext> options) : DbContext(options)
{
    public DbSet<VolumeBarRow> VolumeBars => Set<VolumeBarRow>();

    public DbSet<OptionAtmBarRow> OptionAtmBars => Set<OptionAtmBarRow>();

    public DbSet<OptionBandFlowBarRow> OptionBandFlowBars => Set<OptionBandFlowBarRow>();

    public DbSet<OptionOiBarRow> OptionOiBars => Set<OptionOiBarRow>();

    public DbSet<OptionSkew25DeltaBarRow> OptionSkew25DeltaBars => Set<OptionSkew25DeltaBarRow>();

    public DbSet<OptionMaxPainBarRow> OptionMaxPainBars => Set<OptionMaxPainBarRow>();

    public DbSet<OptionDepthBarRow> OptionDepthBars => Set<OptionDepthBarRow>();

    /// <summary>Phase C of docs/LIVE_PARITY_PLAN.md -- live per-bar scores for the locked target metric, written by <see cref="TradingDaySession"/>.</summary>
    public DbSet<LiveOptionsScoreRow> LiveOptionsScoreBars => Set<LiveOptionsScoreRow>();

    /// <summary>Phase C of docs/LIVE_PARITY_PLAN.md -- live entry SIGNALS (not paper trades -- see <see cref="LiveEntrySignalRow"/>'s own doc comment) for the locked target metric.</summary>
    public DbSet<LiveEntrySignalRow> LiveEntrySignals => Set<LiveEntrySignalRow>();

    /// <summary>Phase D of docs/LIVE_PARITY_PLAN.md -- live paper trades (real strike/fill/P&amp;L) for the locked target metric, written by <see cref="LivePaperTradeExecutor"/>.</summary>
    public DbSet<LivePaperTradeRow> LivePaperTrades => Set<LivePaperTradeRow>();

    /// <summary>Phase G of docs/LIVE_PARITY_PLAN.md -- this pipeline's own new-entries-only kill switch, checked by <see cref="LivePaperTradeExecutor.OpenAsync"/> on every entry signal. See <see cref="LiveKillSwitchState"/>'s own doc comment for why this is a separate row from <c>NiftySignal.Domain.Entities.KillSwitchState</c> rather than a shared one.</summary>
    public DbSet<LiveKillSwitchState> LiveKillSwitchStates => Set<LiveKillSwitchState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VolumeBarRow>(entity =>
        {
            // Every population run's first check ("has this day already been populated at this
            // threshold?") goes through this -- see VolumeBarPopulator.PopulateDayAsync.
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold });

            // One row per (day, threshold, bar sequence position), by construction -- a duplicate
            // would mean the populator ran twice over the same day/threshold, which the check
            // above is meant to prevent.
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BarIndex }).IsUnique();
        });

        modelBuilder.Entity<OptionAtmBarRow>(entity =>
        {
            // Same identity shape as VolumeBarRow's own indexes, by design -- see
            // OptionAtmPopulator.PopulateDayAsync for the idempotency check this feeds.
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold });
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BarIndex }).IsUnique();
        });

        modelBuilder.Entity<OptionBandFlowBarRow>(entity =>
        {
            // BandWidth is part of the identity here (retrofitted 2026-09-20) -- see
            // OptionBandFlowBarRow's own doc comment for why ATM±1 and ATM±2 need to coexist.
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BandWidth });
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BandWidth, b.BarIndex }).IsUnique();
        });

        modelBuilder.Entity<OptionOiBarRow>(entity =>
        {
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BandWidth });
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BandWidth, b.BarIndex }).IsUnique();
        });

        modelBuilder.Entity<OptionSkew25DeltaBarRow>(entity =>
        {
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold });
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BarIndex }).IsUnique();
        });

        modelBuilder.Entity<OptionMaxPainBarRow>(entity =>
        {
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold });
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BarIndex }).IsUnique();
        });

        modelBuilder.Entity<OptionDepthBarRow>(entity =>
        {
            // BandWidth is part of the identity here (unlike every other options table) -- see
            // OptionDepthBarRow's own doc comment for why ATM±1 and ATM±2 need to coexist.
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BandWidth });
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BandWidth, b.BarIndex }).IsUnique();
        });

        modelBuilder.Entity<LiveOptionsScoreRow>(entity =>
        {
            // Same identity shape as VolumeBarRow's own indexes -- one live score per future bar,
            // by construction (TradingDaySession.ProcessBar's own out-of-order/re-score guard).
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold });
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.BarIndex }).IsUnique();
        });

        modelBuilder.Entity<LiveEntrySignalRow>(entity =>
        {
            // NOT unique on BarIndex alone -- EntryBarIndex identifies the signal's own row for the
            // engine's "find the open one to close" lookup, but a given day can have several signals
            // over time (one at a time, never overlapping -- enforced by TradingDaySession's own
            // single _open slot, not by a DB constraint, since "no row with ExitBarIndex IS NULL
            // other than this one" isn't expressible as a plain unique index).
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold });
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.EntryBarIndex }).IsUnique();
        });

        modelBuilder.Entity<LivePaperTradeRow>(entity =>
        {
            // Same identity shape as LiveEntrySignalRow's own indexes -- one paper trade per
            // originating signal, at most (LivePaperTradeExecutor.OpenAsync may decline to open one
            // at all, e.g. no tradeable strike/price found, but never opens two for the same signal).
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold });
            entity.HasIndex(b => new { b.AsOfDate, b.BarVolumeThreshold, b.EntryBarIndex }).IsUnique();
        });

        modelBuilder.Entity<LiveKillSwitchState>(entity =>
        {
            // Singleton row -- same convention as NiftySignal.Domain.Entities.KillSwitchState's own
            // configuration (see LiveKillSwitchState's own doc comment for why this is a SEPARATE row,
            // not that same table). Defaults to enabled so a fresh migration never silently starts a
            // new environment with entries disabled.
            entity.HasData(new LiveKillSwitchState
            {
                Id = LiveKillSwitchState.SingletonId,
                EntriesEnabled = true,
                UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedBy = "migration-seed",
            });
        });
    }
}
