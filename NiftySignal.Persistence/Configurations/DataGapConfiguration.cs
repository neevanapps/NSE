using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence.Configurations;

public sealed class DataGapConfiguration : IEntityTypeConfiguration<DataGap>
{
    public void Configure(EntityTypeBuilder<DataGap> builder)
    {
        builder.ToTable("data_gaps");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.Reason).HasMaxLength(500).IsRequired();

        // Finding the currently-open gap (EndedAt IS NULL) is the query the ingestion
        // worker runs on every reconnect attempt.
        builder.HasIndex(g => g.EndedAt);
    }
}
