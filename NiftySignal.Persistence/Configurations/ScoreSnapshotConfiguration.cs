using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence.Configurations;

public sealed class ScoreSnapshotConfiguration : IEntityTypeConfiguration<ScoreSnapshot>
{
    public void Configure(EntityTypeBuilder<ScoreSnapshot> builder)
    {
        builder.ToTable("score_snapshots");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.WeightSetVersion).HasMaxLength(100).IsRequired();

        builder.HasIndex(s => s.ComputedAt);
    }
}
