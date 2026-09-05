using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence.Configurations;

public sealed class TickConfiguration : IEntityTypeConfiguration<Tick>
{
    public void Configure(EntityTypeBuilder<Tick> builder)
    {
        builder.ToTable("ticks");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Token).HasMaxLength(32).IsRequired();
        builder.Property(t => t.LastPrice).HasPrecision(18, 4);

        // Plan section 3.2: index ticks on (InstrumentToken, ExchangeTimestamp) -- this is
        // the query shape both live feature computation and backtest replay depend on.
        builder.HasIndex(t => new { t.Token, t.ExchangeTimestamp });

        // LiveDataService.PollAsync's connection-status check orders the whole table by
        // ReceivedAt with no token filter -- without this, it's a full parallel seq scan
        // (335ms measured against 3.86M rows).
        builder.HasIndex(t => t.ReceivedAt);

        builder.OwnsOne(t => t.Depth, depth =>
        {
            depth.Property(d => d.Bid1Price).HasColumnName("bid1_price").HasPrecision(18, 4);
            depth.Property(d => d.Bid1Qty).HasColumnName("bid1_qty");
            depth.Property(d => d.Bid2Price).HasColumnName("bid2_price").HasPrecision(18, 4);
            depth.Property(d => d.Bid2Qty).HasColumnName("bid2_qty");
            depth.Property(d => d.Bid3Price).HasColumnName("bid3_price").HasPrecision(18, 4);
            depth.Property(d => d.Bid3Qty).HasColumnName("bid3_qty");
            depth.Property(d => d.Bid4Price).HasColumnName("bid4_price").HasPrecision(18, 4);
            depth.Property(d => d.Bid4Qty).HasColumnName("bid4_qty");
            depth.Property(d => d.Bid5Price).HasColumnName("bid5_price").HasPrecision(18, 4);
            depth.Property(d => d.Bid5Qty).HasColumnName("bid5_qty");

            depth.Property(d => d.Ask1Price).HasColumnName("ask1_price").HasPrecision(18, 4);
            depth.Property(d => d.Ask1Qty).HasColumnName("ask1_qty");
            depth.Property(d => d.Ask2Price).HasColumnName("ask2_price").HasPrecision(18, 4);
            depth.Property(d => d.Ask2Qty).HasColumnName("ask2_qty");
            depth.Property(d => d.Ask3Price).HasColumnName("ask3_price").HasPrecision(18, 4);
            depth.Property(d => d.Ask3Qty).HasColumnName("ask3_qty");
            depth.Property(d => d.Ask4Price).HasColumnName("ask4_price").HasPrecision(18, 4);
            depth.Property(d => d.Ask4Qty).HasColumnName("ask4_qty");
            depth.Property(d => d.Ask5Price).HasColumnName("ask5_price").HasPrecision(18, 4);
            depth.Property(d => d.Ask5Qty).HasColumnName("ask5_qty");
        });
    }
}
