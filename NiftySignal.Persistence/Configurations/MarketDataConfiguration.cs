using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence.Configurations;

public sealed class MarketDataDayConfiguration : IEntityTypeConfiguration<MarketDataDay>
{
    public void Configure(EntityTypeBuilder<MarketDataDay> builder)
    {
        builder.ToTable("market_data_days");
        builder.HasKey(x => x.TradeDate);
    }
}

public sealed class UpstoxSessionConfiguration : IEntityTypeConfiguration<UpstoxSession>
{
    public void Configure(EntityTypeBuilder<UpstoxSession> builder)
    {
        builder.ToTable("upstox_session");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Token).HasMaxLength(4096);
        builder.HasData(new UpstoxSession());
    }
}
