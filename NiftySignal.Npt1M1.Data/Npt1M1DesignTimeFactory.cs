using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NiftySignal.Npt1M1.Data;

/// <summary>Design-time only (<c>dotnet ef migrations add / script</c>): a placeholder connection, never used to connect by this tooling.</summary>
public sealed class Npt1M1DesignTimeFactory : IDesignTimeDbContextFactory<Npt1M1DbContext>
{
    public Npt1M1DbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<Npt1M1DbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only;Username=design;Password=design", o => o.MigrationsHistoryTable("__ef_migrations_history", Npt1M1DbContext.Schema))
            .Options);
}
