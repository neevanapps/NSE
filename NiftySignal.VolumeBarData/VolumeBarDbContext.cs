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
    }
}
