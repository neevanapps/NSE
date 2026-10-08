using Microsoft.EntityFrameworkCore;

namespace NiftySignal.AdaptiveObserverData;

public sealed class AdaptiveObserverDbContext(DbContextOptions<AdaptiveObserverDbContext> options) : DbContext(options)
{
    public const string DatabaseName = "niftysignal_adaptive_observer";

    public DbSet<AdaptiveSessionStateRow> Sessions => Set<AdaptiveSessionStateRow>();
    public DbSet<AdaptiveFutureBarRow> FutureBars => Set<AdaptiveFutureBarRow>();
    public DbSet<AdaptiveRollingStateRow> RollingStates => Set<AdaptiveRollingStateRow>();
    public DbSet<AdaptiveOptionBandBarRow> OptionBandBars => Set<AdaptiveOptionBandBarRow>();
    public DbSet<AdaptiveResidualAnchorComponentRow> ResidualAnchorComponents => Set<AdaptiveResidualAnchorComponentRow>();
    public DbSet<AdaptiveOptionResidualBarRow> OptionResidualBars => Set<AdaptiveOptionResidualBarRow>();
    public DbSet<AdaptiveWeak2ObservationRow> Weak2Observations => Set<AdaptiveWeak2ObservationRow>();
    public DbSet<AdaptiveObserverRuntimeRow> Runtime => Set<AdaptiveObserverRuntimeRow>();
    public DbSet<AdaptiveScreenshotJobRow> ScreenshotJobs => Set<AdaptiveScreenshotJobRow>();
    public DbSet<AdaptiveFuturesSupplementalRow> FuturesSupplemental => Set<AdaptiveFuturesSupplementalRow>();
    public DbSet<AdaptiveCommentaryEventRow> CommentaryEvents => Set<AdaptiveCommentaryEventRow>();
    public DbSet<AdaptiveCommentaryRuntimeRow> CommentaryRuntime => Set<AdaptiveCommentaryRuntimeRow>();
    public DbSet<AdaptiveCommentaryNotificationJobRow> CommentaryNotificationJobs => Set<AdaptiveCommentaryNotificationJobRow>();
    public DbSet<AdaptiveOptionsSupplementalRow> OptionsSupplemental => Set<AdaptiveOptionsSupplementalRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdaptiveScreenshotJobRow>(e =>
        {
            e.ToTable("adaptive_screenshot_jobs");
            e.HasIndex(x => new { x.SessionId, x.Kind, x.TargetBarSeq }).IsUnique();
            e.HasIndex(x => x.SessionId).IsUnique().HasFilter("\"Kind\" = 0");
            e.HasIndex(x => new { x.Status, x.NextAttemptUtc });
            e.Property(x => x.ImageSha256).HasMaxLength(64);
            e.Property(x => x.Caption).HasMaxLength(1024);
            e.Property(x => x.LastError).HasMaxLength(512);
            e.HasOne<AdaptiveSessionStateRow>().WithMany().HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AdaptiveSessionStateRow>(e =>
        {
            e.ToTable("adaptive_sessions");
            e.HasIndex(x => new { x.TradeDate, x.ModelVersion }).IsUnique();
            e.Property(x => x.ModelVersion).HasMaxLength(64);
            e.Property(x => x.SourceBranch).HasMaxLength(128);
            e.Property(x => x.SourceCommitSha).HasMaxLength(64);
            e.Property(x => x.FutureToken).HasMaxLength(32);
            e.Property(x => x.FutureSymbol).HasMaxLength(64);
            e.Property(x => x.EstimatorName).HasMaxLength(64);
        });

        modelBuilder.Entity<AdaptiveFutureBarRow>(e =>
        {
            e.ToTable("adaptive_future_bars");
            e.HasIndex(x => new { x.SessionId, x.BarSeq }).IsUnique();
            e.HasIndex(x => new { x.SessionId, x.EndAvailableAtUtc });
        });

        modelBuilder.Entity<AdaptiveRollingStateRow>(e =>
        {
            e.ToTable("adaptive_rolling_states");
            e.HasIndex(x => new { x.SessionId, x.EndBarSeq }).IsUnique();
            e.HasIndex(x => new { x.SessionId, x.EndAvailableAtUtc });
            e.Property(x => x.StrictDominanceEvolution).HasMaxLength(32);
        });

        modelBuilder.Entity<AdaptiveOptionBandBarRow>(e =>
        {
            e.ToTable("adaptive_option_band_bars");
            e.HasIndex(x => new { x.SessionId, x.BarSeq, x.Side }).IsUnique();
            e.HasIndex(x => new { x.SessionId, x.Side, x.BarSeq });
            e.Property(x => x.BandStrikes).HasMaxLength(128);
            e.Property(x => x.UnavailableReason).HasMaxLength(256);
        });

        modelBuilder.Entity<AdaptiveResidualAnchorComponentRow>(e =>
        {
            e.ToTable("adaptive_residual_anchor_components");
            e.HasIndex(x => new { x.SessionId, x.Side, x.Strike }).IsUnique();
            e.Property(x => x.Token).HasMaxLength(32);
            e.Property(x => x.TradingSymbol).HasMaxLength(64);
        });

        modelBuilder.Entity<AdaptiveOptionResidualBarRow>(e =>
        {
            e.ToTable("adaptive_option_residual_bars");
            e.HasIndex(x => new { x.SessionId, x.BarSeq, x.Variant }).IsUnique();
            e.HasIndex(x => new { x.SessionId, x.Variant, x.BarSeq });
            e.Property(x => x.Relationship).HasMaxLength(16);
            e.Property(x => x.UnavailableReason).HasMaxLength(256);
        });

        modelBuilder.Entity<AdaptiveWeak2ObservationRow>(e =>
        {
            e.ToTable("adaptive_weak2_observations");
            e.HasIndex(x => new { x.SessionId, x.TriggerBarSeq }).IsUnique();
            e.HasIndex(x => new { x.SessionId, x.Status });
            e.Property(x => x.Token).HasMaxLength(32);
            e.Property(x => x.TradingSymbol).HasMaxLength(64);
            e.Property(x => x.UnavailableReason).HasMaxLength(512);
            e.Property(x => x.SelectionPolicy).HasMaxLength(128);
        });

        modelBuilder.Entity<AdaptiveObserverRuntimeRow>(e =>
        {
            e.ToTable("adaptive_observer_runtime");
            e.HasIndex(x => x.SessionId).IsUnique();
            e.Property(x => x.LastError).HasMaxLength(2048);
        });

        modelBuilder.Entity<AdaptiveFutureBarRow>().HasOne<AdaptiveSessionStateRow>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AdaptiveRollingStateRow>().HasOne<AdaptiveSessionStateRow>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AdaptiveOptionBandBarRow>().HasOne<AdaptiveSessionStateRow>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AdaptiveResidualAnchorComponentRow>().HasOne<AdaptiveSessionStateRow>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AdaptiveOptionResidualBarRow>().HasOne<AdaptiveSessionStateRow>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AdaptiveWeak2ObservationRow>().HasOne<AdaptiveSessionStateRow>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AdaptiveObserverRuntimeRow>().HasOne<AdaptiveSessionStateRow>().WithMany()
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);

        // Sidecar (08-Oct plan section 71): versioned, keyed by session + bar, outside the parity-protected core rows.
        modelBuilder.Entity<AdaptiveFuturesSupplementalRow>(e =>
        {
            e.ToTable("adaptive_futures_supplemental_bars");
            e.HasIndex(x => new { x.SessionId, x.BarSeq, x.MetricsVersion }).IsUnique();
            e.Property(x => x.MetricsVersion).HasMaxLength(64);
            e.HasOne<AdaptiveSessionStateRow>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AdaptiveCommentaryEventRow>(e =>
        {
            e.ToTable("adaptive_commentary_events");
            e.HasIndex(x => x.EventIdentity).IsUnique();
            e.HasIndex(x => new { x.SessionId, x.BarSeq });
            e.HasIndex(x => new { x.TradeDate, x.OccurredAtUtc });
            e.HasIndex(x => new { x.SessionId, x.EventBias, x.OccurredAtUtc });
            e.HasIndex(x => new { x.SessionId, x.EventType, x.OccurredAtUtc });
            e.Property(x => x.EventType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.EventBias).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.MarketRegime).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Lifecycle).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.EvidenceAgreement).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PreviousBias).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PrimaryEvidenceJson).HasColumnType("jsonb");
            e.Property(x => x.ConfirmationEvidenceJson).HasColumnType("jsonb");
            e.Property(x => x.ContradictionEvidenceJson).HasColumnType("jsonb");
            e.Property(x => x.DataQualityJson).HasColumnType("jsonb");
            e.Property(x => x.NotificationReason).HasMaxLength(64);
            e.Property(x => x.CommentaryVersion).HasMaxLength(32);
            e.Property(x => x.EventIdentity).HasMaxLength(128);
            e.HasOne<AdaptiveSessionStateRow>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AdaptiveCommentaryNotificationJobRow>(e =>
        {
            e.ToTable("adaptive_commentary_notification_jobs");
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptUtc });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.LastError).HasMaxLength(512);
            e.HasOne<AdaptiveCommentaryEventRow>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AdaptiveCommentaryRuntimeRow>(e =>
        {
            e.ToTable("adaptive_commentary_runtime");
            e.HasIndex(x => x.SessionId).IsUnique();
            e.Property(x => x.CurrentEventType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.CurrentBias).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.CurrentRegime).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.CurrentLifecycle).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ActiveBias).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ActiveAgreement).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ActivePhase).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ActiveSupportingFamilies).HasMaxLength(128);
            e.Property(x => x.CommentaryVersion).HasMaxLength(32);
            e.HasOne<AdaptiveSessionStateRow>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AdaptiveOptionsSupplementalRow>(e =>
        {
            e.ToTable("adaptive_options_supplemental_bars");
            e.HasIndex(x => new { x.SessionId, x.BarSeq, x.MetricsVersion }).IsUnique();
            e.Property(x => x.MetricsVersion).HasMaxLength(64);
            e.Property(x => x.UnavailableReason).HasMaxLength(256);
            e.Property(x => x.CePosition).HasMaxLength(32);
            e.Property(x => x.PePosition).HasMaxLength(32);
            e.HasOne<AdaptiveSessionStateRow>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
