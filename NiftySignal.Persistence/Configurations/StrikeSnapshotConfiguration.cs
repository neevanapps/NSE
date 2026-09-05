using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence.Configurations;

public sealed class StrikeSnapshotConfiguration : IEntityTypeConfiguration<StrikeSnapshot>
{
    public void Configure(EntityTypeBuilder<StrikeSnapshot> builder)
    {
        builder.ToTable("strike_snapshots");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Token).HasMaxLength(32).IsRequired();

        builder.Property(s => s.StrikePrice).HasPrecision(18, 4);
        builder.Property(s => s.BidPrice).HasPrecision(18, 4);
        builder.Property(s => s.AskPrice).HasPrecision(18, 4);
        builder.Property(s => s.SpreadAbs).HasPrecision(18, 4);
        builder.Property(s => s.SpreadPctOfMid).HasPrecision(18, 4);

        // Analysis queries are "this strike over time" (how did its Greeks/spread evolve) and
        // "this cadence across strikes" (what did the band look like at one instant) -- the
        // composite covers the first directly and the second via the ComputedAt index.
        builder.HasIndex(s => new { s.Token, s.ComputedAt });
        builder.HasIndex(s => s.ComputedAt);
    }
}
