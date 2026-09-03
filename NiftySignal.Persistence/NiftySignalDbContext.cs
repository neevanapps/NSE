using Microsoft.EntityFrameworkCore;

namespace NiftySignal.Persistence;

/// <summary>
/// EF Core code-first context for NiftySignal. Entity sets are added module-by-module
/// as each phase introduces them (see the build sequence in the project plan) rather
/// than modeled up front.
/// </summary>
public sealed class NiftySignalDbContext(DbContextOptions<NiftySignalDbContext> options)
    : DbContext(options)
{
}
