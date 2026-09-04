using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence.Configurations;

public sealed class FlatTradeSessionConfiguration : IEntityTypeConfiguration<FlatTradeSession>
{
    public void Configure(EntityTypeBuilder<FlatTradeSession> builder)
    {
        builder.ToTable("flattrade_session");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Token).HasMaxLength(200);
        builder.Property(s => s.ClientId).HasMaxLength(50);

        // Singleton row -- there is exactly one current session, not one per something.
        builder.HasData(new FlatTradeSession
        {
            Id = FlatTradeSession.SingletonId,
            Token = null,
            ClientId = null,
            IssuedAt = null,
        });
    }
}
