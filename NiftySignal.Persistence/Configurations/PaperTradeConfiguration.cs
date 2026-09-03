using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence.Configurations;

public sealed class PaperTradeConfiguration : IEntityTypeConfiguration<PaperTrade>
{
    public void Configure(EntityTypeBuilder<PaperTrade> builder)
    {
        builder.ToTable("paper_trades");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.InstrumentToken).HasMaxLength(32).IsRequired();
        builder.Property(t => t.TradingSymbol).HasMaxLength(64).IsRequired();
        builder.Property(t => t.RulesetVersion).HasMaxLength(64).IsRequired();
        builder.Property(t => t.ScoreWeightsVersion).HasMaxLength(64).IsRequired();

        builder.Property(t => t.EntryPrice).HasPrecision(18, 4);
        builder.Property(t => t.PartialExitPrice).HasPrecision(18, 4);
        builder.Property(t => t.ExitPrice).HasPrecision(18, 4);
        builder.Property(t => t.GrossPnl).HasPrecision(18, 4);
        builder.Property(t => t.NetPnl).HasPrecision(18, 4);

        // Performance reporting (plan section 9) always queries by a time range and
        // frequently filters to just the open trades (ExitTime IS NULL).
        builder.HasIndex(t => t.EntryTime);
        builder.HasIndex(t => t.ExitTime);
    }
}
