using Microsoft.EntityFrameworkCore;

namespace NiftySignal.BacktestData;

/// <summary>
/// A completely separate database from <c>NiftySignal.Persistence.NiftySignalDbContext</c> --
/// the live schema behind Host/Dashboard. This one exists purely so backtesting has a
/// leakage-safe dataset to work from, and it is never read by Host or Dashboard.
/// </summary>
public sealed class BacktestAnalysisDbContext(DbContextOptions<BacktestAnalysisDbContext> options)
    : DbContext(options)
{
    public DbSet<CadenceContext> CadenceContexts => Set<CadenceContext>();

    public DbSet<StrikeCadenceSnapshot> StrikeCadenceSnapshots => Set<StrikeCadenceSnapshot>();

    public DbSet<StrikeBandCadenceSnapshot> StrikeBandCadenceSnapshots => Set<StrikeBandCadenceSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CadenceContext>(entity =>
        {
            // Every population run's first check ("has this day already been populated?")
            // goes through AsOfDate -- see CadencePopulator.PopulateDayAsync.
            entity.HasIndex(c => c.AsOfDate);

            // One row per cadence boundary, by construction -- a duplicate Timestamp would mean
            // the populator ran twice over the same window, which the AsOfDate check above is
            // meant to prevent. The unique index makes that guarantee enforced, not just assumed.
            entity.HasIndex(c => c.Timestamp).IsUnique();
        });

        modelBuilder.Entity<StrikeCadenceSnapshot>(entity =>
        {
            entity.HasIndex(c => c.AsOfDate);

            // One row per (cadence, strike, option type, expiry), by construction -- the natural
            // query pattern (band membership filter, per-strike time series) goes through this.
            entity.HasIndex(c => new { c.CadenceContextId, c.ExpiryDate, c.StrikePrice, c.OptionType }).IsUnique();

            // Real FK with cascade delete (2026-09-12, fixing a found duplication bug) -- without
            // this, deleting CadenceContexts to force a repopulate (the standard pattern this
            // project uses after every schema/logic change) leaves every child row behind,
            // pointing at a now-deleted CadenceContextId. The unique index above can't catch that:
            // it's scoped per CadenceContextId, and each repopulate mints fresh Ids, so the old
            // orphaned rows and the new rows never collide on the same key -- they silently
            // accumulate as duplicates instead. Confirmed directly: 48,000 ATM rows found where
            // 24,000 were expected, exactly 2x, matching the two Table 2/3 populate runs so far.
            entity.HasOne<CadenceContext>().WithMany().HasForeignKey(c => c.CadenceContextId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StrikeBandCadenceSnapshot>(entity =>
        {
            entity.HasIndex(c => c.AsOfDate);

            // One row per (bucket, expiry, band, cadence granularity) -- both 5 and 15-minute
            // rows coexist, distinguished by CadenceMinutes.
            entity.HasIndex(c => new { c.CadenceContextId, c.ExpiryDate, c.BandDefinition, c.CadenceMinutes }).IsUnique();

            // Same cascade-delete fix as StrikeCadenceSnapshot above, same reason.
            entity.HasOne<CadenceContext>().WithMany().HasForeignKey(c => c.CadenceContextId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
