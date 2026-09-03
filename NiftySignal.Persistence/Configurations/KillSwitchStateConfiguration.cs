using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Persistence.Configurations;

public sealed class KillSwitchStateConfiguration : IEntityTypeConfiguration<KillSwitchState>
{
    public void Configure(EntityTypeBuilder<KillSwitchState> builder)
    {
        builder.ToTable("kill_switch_state");

        builder.HasKey(k => k.Id);

        builder.Property(k => k.UpdatedBy).HasMaxLength(200).IsRequired();

        // Singleton row -- there is exactly one kill switch, not one per something.
        builder.HasData(new KillSwitchState
        {
            Id = KillSwitchState.SingletonId,
            EntriesEnabled = true,
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedBy = "migration-seed",
        });
    }
}
