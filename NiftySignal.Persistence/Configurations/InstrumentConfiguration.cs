using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence.Configurations;

public sealed class InstrumentConfiguration : IEntityTypeConfiguration<Instrument>
{
    public void Configure(EntityTypeBuilder<Instrument> builder)
    {
        builder.ToTable("instruments");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Token).HasMaxLength(32).IsRequired();
        builder.Property(i => i.NativeInstrumentKey).HasMaxLength(128);
        builder.Property(i => i.TradingSymbol).HasMaxLength(64).IsRequired();
        builder.Property(i => i.Underlying).HasMaxLength(32).IsRequired();
        builder.Property(i => i.StrikePrice).HasPrecision(18, 4);
        builder.Property(i => i.TickSize).HasPrecision(18, 4);

        // One row per token per exchange per trading day -- the instrument master is
        // snapshotted daily (plan section 3.1), not a stable dimension table.
        builder.HasIndex(i => new { i.Exchange, i.Token, i.AsOfDate }).IsUnique();
    }
}
