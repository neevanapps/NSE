using Microsoft.EntityFrameworkCore;

namespace NiftySignal.Npt1M1.Data;

public sealed class Npt1M1DbContext(DbContextOptions<Npt1M1DbContext> options) : DbContext(options)
{
    public const string Schema = "npt1m1";

    public DbSet<M1SessionRow> Sessions => Set<M1SessionRow>();
    public DbSet<M1VolumeHistoryRow> VolumeHistory => Set<M1VolumeHistoryRow>();
    public DbSet<M1FrameRow> Frames => Set<M1FrameRow>();
    public DbSet<M1RuntimeRow> Runtime => Set<M1RuntimeRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<M1SessionRow>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.SessionId);
            e.Property(x => x.SessionId).HasColumnName("session_id").HasMaxLength(64);
            e.Property(x => x.TradeDate).HasColumnName("trade_date");
            e.Property(x => x.ModelVersion).HasColumnName("model_version").HasMaxLength(64);
            e.Property(x => x.FreezeFingerprint).HasColumnName("freeze_fingerprint").HasMaxLength(64);
            e.Property(x => x.BaseBarVolume).HasColumnName("base_bar_volume");
            e.Property(x => x.OpeningVolume).HasColumnName("opening_volume");
            e.Property(x => x.BarSizeStatus).HasColumnName("bar_size_status").HasMaxLength(64);
            e.Property(x => x.DecisionJson).HasColumnName("decision_json");
            e.Property(x => x.ReportJson).HasColumnName("report_json");
            e.Property(x => x.FutureToken).HasColumnName("future_token").HasMaxLength(64);
            e.Property(x => x.WeeklyExpiry).HasColumnName("weekly_expiry");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.HasIndex(x => new { x.TradeDate, x.ModelVersion }).IsUnique().HasDatabaseName("ux_sessions_day_model");
        });

        b.Entity<M1VolumeHistoryRow>(e =>
        {
            e.ToTable("volume_history");
            e.HasKey(x => x.TradeDate);
            e.Property(x => x.TradeDate).HasColumnName("trade_date");
            e.Property(x => x.FutureToken).HasColumnName("future_token").HasMaxLength(64);
            e.Property(x => x.FutureSymbol).HasColumnName("future_symbol").HasMaxLength(64);
            e.Property(x => x.Expiry).HasColumnName("expiry");
            e.Property(x => x.LotSize).HasColumnName("lot_size");
            e.Property(x => x.OpeningVolume).HasColumnName("opening_volume");
            e.Property(x => x.WindowVolume).HasColumnName("window_volume");
            e.Property(x => x.CloseVolume).HasColumnName("close_volume");
            e.Property(x => x.FullDayVolume).HasColumnName("full_day_volume");
            e.Property(x => x.OpeningCoverageMinutes).HasColumnName("opening_coverage_minutes");
            e.Property(x => x.WindowCoverageMinutes).HasColumnName("window_coverage_minutes");
            e.Property(x => x.FirstTickIst).HasColumnName("first_tick_ist").HasMaxLength(16);
            e.Property(x => x.LastTickIst).HasColumnName("last_tick_ist").HasMaxLength(16);
            e.Property(x => x.FuturesTicks).HasColumnName("futures_ticks");
            e.Property(x => x.BaselineEligible).HasColumnName("baseline_eligible");
            e.Property(x => x.ComputedAtUtc).HasColumnName("computed_at_utc");
        });

        b.Entity<M1FrameRow>(e =>
        {
            e.ToTable("frames");
            e.HasKey(x => new { x.SessionId, x.BarSeq });
            e.Property(x => x.SessionId).HasColumnName("session_id").HasMaxLength(64);
            e.Property(x => x.BarSeq).HasColumnName("bar_seq");
            e.Property(x => x.StartAvailableAtUtc).HasColumnName("start_available_at_utc");
            e.Property(x => x.EndAvailableAtUtc).HasColumnName("end_available_at_utc");
            e.Property(x => x.Ready).HasColumnName("ready");
            e.Property(x => x.IsValid).HasColumnName("is_valid");
            e.Property(x => x.ConsecutiveValidBars).HasColumnName("consecutive_valid_bars");
            e.Property(x => x.MicroDevTimeWeighted).HasColumnName("microdev_time_weighted");
            e.Property(x => x.MicroDevUsableSeconds).HasColumnName("microdev_usable_s");
            e.Property(x => x.PeBandContractStrictDelta).HasColumnName("pe_band_contract_strict_delta");
            e.Property(x => x.BandCenterStrike).HasColumnName("band_center_strike");
            e.Property(x => x.BandExpiry).HasColumnName("band_expiry");
            e.Property(x => x.BandUnavailableReason).HasColumnName("band_unavailable_reason").HasMaxLength(256);
            e.Property(x => x.FrameHash).HasColumnName("frame_hash").HasMaxLength(64);
            e.Property(x => x.CanonicalText).HasColumnName("canonical_text");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.HasIndex(x => x.EndAvailableAtUtc).HasDatabaseName("ix_frames_end");
        });

        b.Entity<M1RuntimeRow>(e =>
        {
            e.ToTable("runtime");
            e.HasKey(x => x.SessionId);
            e.Property(x => x.SessionId).HasColumnName("session_id").HasMaxLength(64);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
            e.Property(x => x.LastTickId).HasColumnName("last_tick_id");
            e.Property(x => x.LastBarSeq).HasColumnName("last_bar_seq");
            e.Property(x => x.LastBarEndUtc).HasColumnName("last_bar_end_utc");
            e.Property(x => x.LastHeartbeatUtc).HasColumnName("last_heartbeat_utc");
            e.Property(x => x.Polls).HasColumnName("polls");
            e.Property(x => x.LastPollMillis).HasColumnName("last_poll_ms");
            e.Property(x => x.MaxPollMillis).HasColumnName("max_poll_ms");
            e.Property(x => x.RowsRead).HasColumnName("rows_read");
            e.Property(x => x.DivergentFrames).HasColumnName("divergent_frames");
            e.Property(x => x.Detail).HasColumnName("detail");
        });
    }
}
