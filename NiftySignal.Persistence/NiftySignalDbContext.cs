using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence;

/// <summary>
/// EF Core code-first context for NiftySignal. Entity sets are added module-by-module
/// as each phase introduces them (see the build sequence in the project plan) rather
/// than modeled up front.
/// </summary>
public sealed class NiftySignalDbContext(DbContextOptions<NiftySignalDbContext> options)
    : DbContext(options)
{
    public DbSet<Instrument> Instruments => Set<Instrument>();

    public DbSet<Tick> Ticks => Set<Tick>();

    public DbSet<DataGap> DataGaps => Set<DataGap>();

    public DbSet<KillSwitchState> KillSwitchStates => Set<KillSwitchState>();

    public DbSet<PaperTrade> PaperTrades => Set<PaperTrade>();

    public DbSet<FlatTradeSession> FlatTradeSessions => Set<FlatTradeSession>();

    public DbSet<ScoreSnapshot> ScoreSnapshots => Set<ScoreSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NiftySignalDbContext).Assembly);
    }
}
