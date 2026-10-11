using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NiftySignal.Trading.Data;

/// <summary>
/// The model of the <c>trading</c> schema (contract section A.19): every table lives in its own schema with its own migration history, so it never touches the
/// tables of the other contexts that may share a database. Nothing here connects, creates or migrates anything by itself: the context is only constructed by a
/// store that is handed an explicit connection and by the design-time factory (which uses an unreachable placeholder).
/// </summary>
public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<SchemaVersionEntity> SchemaVersions => Set<SchemaVersionEntity>();

    public DbSet<RunEntity> Runs => Set<RunEntity>();

    public DbSet<RunOwnerEntity> RunOwners => Set<RunOwnerEntity>();

    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();

    public DbSet<VariantEntity> Variants => Set<VariantEntity>();

    public DbSet<ControlJournalEntity> ControlJournal => Set<ControlJournalEntity>();

    public DbSet<ControlApplicationEntity> ControlApplications => Set<ControlApplicationEntity>();

    public DbSet<StepEntity> Steps => Set<StepEntity>();

    public DbSet<StepDigestEntity> StepDigests => Set<StepDigestEntity>();

    public DbSet<CheckpointEntity> Checkpoints => Set<CheckpointEntity>();

    public DbSet<OutputEntity> Outputs => Set<OutputEntity>();

    public DbSet<PositionEntity> Positions => Set<PositionEntity>();

    public DbSet<OrderEntity> Orders => Set<OrderEntity>();

    public DbSet<FillEntity> Fills => Set<FillEntity>();

    public DbSet<LedgerEntryEntity> LedgerEntries => Set<LedgerEntryEntity>();

    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();

    public DbSet<RejectionEntity> Rejections => Set<RejectionEntity>();

    public DbSet<PositionSnapshotEntity> PositionSnapshots => Set<PositionSnapshotEntity>();

    public DbSet<RiskCheckpointEntity> RiskCheckpoints => Set<RiskCheckpointEntity>();

    public DbSet<FinalRecordEntity> FinalRecords => Set<FinalRecordEntity>();

    public DbSet<FinalLineEntity> FinalLines => Set<FinalLineEntity>();

    public DbSet<RecoveryLogEntity> RecoveryLog => Set<RecoveryLogEntity>();

    /// <summary>The nine verification channels retained for every committed step (the CP5 channels).</summary>
    public static readonly IReadOnlyList<string> Channels = new[] { "audit", "bars", "candles", "opportunities", "outputs", "quotes", "readiness", "rowQuarantine", "summaries" };

    public static readonly IReadOnlyList<string> ControlKinds = new[] { "Operator", "Health", "SourceProgress", "Advance" };

    public static readonly IReadOnlyList<string> StepKinds = new[] { "DataGroup", "ControlOnly", "Advance", "Finish" };

    public static readonly IReadOnlyList<string> LedgerComponents = new[] { "Brokerage", "Stt", "ExchangeCharge", "SebiFee", "StampDuty", "Gst", "Notional" };

    /// <summary>The PostgreSQL schema that holds every table of this model (and its migration history).</summary>
    public const string SchemaName = "trading";

    private const string HashPattern = "'^[0-9A-F]{64}$'";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<SchemaVersionEntity>(b =>
        {
            b.ToTable("schema_versions");
            b.HasKey(e => e.Component);
            b.Property(e => e.Component).HasMaxLength(64);
            b.Property(e => e.MinimumCompatibleCode).HasMaxLength(200);
            b.Property(e => e.CodecVersion).HasMaxLength(64);
            b.Property(e => e.StateDigestVersion).HasMaxLength(64);
            // The migration itself inserts the row this build expects, so a database that was not created by these migrations is detectable.
            b.HasData(SchemaCompatibility.Expected());
        });

        modelBuilder.Entity<RunEntity>(b =>
        {
            b.ToTable("runs", t => t.HasCheckConstraint("ck_runs_content_hash", $"content_hash ~ {HashPattern}"));
            b.HasKey(e => e.RunId);
            Id(b.Property(e => e.RunId));
            Text(b.Property(e => e.CodecVersion), 64);
            Text(b.Property(e => e.StateDigestVersion), 64);
            Text(b.Property(e => e.EngineCompatibilityId), 128);
            b.Property(e => e.ConfigHash).HasMaxLength(128);
            b.Property(e => e.FreezeHash).HasMaxLength(128);
            b.Property(e => e.TranslatorOptionsHash).HasMaxLength(128);
            b.Property(e => e.ReadinessSpec).HasColumnType("text");
            b.Property(e => e.RetentionHash).HasMaxLength(128);
            b.Property(e => e.StrategiesIdentity).HasColumnType("text");
            b.Property(e => e.DurableOptionsHash).HasMaxLength(128);
            b.Property(e => e.SourceManifestHash).HasMaxLength(128);
            b.Property(e => e.ProvenanceGitCommit).HasMaxLength(64);
            b.Property(e => e.ProvenanceSdk).HasMaxLength(64);
            b.Property(e => e.ProvenanceAssemblyHashes).HasColumnType("text");
            b.Property(e => e.ConfigJson).HasColumnType("text");
            b.Property(e => e.FreezeSnapshot).HasColumnType("text");
            b.Property(e => e.SourceManifest).HasColumnType("text");
            Hash(b.Property(e => e.ContentHash));
            b.HasIndex(e => e.TradeDate);
            b.HasMany(e => e.Variants).WithOne().HasForeignKey(v => v.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RunOwnerEntity>(b =>
        {
            // Deliberately no foreign key to runs: ownership is taken BEFORE the run row exists.
            b.ToTable("run_owner", t => t.HasCheckConstraint("ck_run_owner_epoch", "epoch >= 1"));
            b.HasKey(e => e.RunId);
            Id(b.Property(e => e.RunId));
            Text(b.Property(e => e.OwnerId), 200);
        });

        modelBuilder.Entity<AccountEntity>(b =>
        {
            b.ToTable("accounts");
            b.HasKey(e => new { e.RunId, e.AccountId });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.AccountId));
            b.HasOne<RunEntity>().WithMany().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VariantEntity>(b =>
        {
            // The identity of a variant is (StrategyId, VariantId): VariantId is unique only inside its strategy family.
            b.ToTable("variants");
            b.HasKey(e => new { e.RunId, e.StrategyId, e.VariantId });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.VariantId));
            Id(b.Property(e => e.AccountId));
            b.Property(e => e.VariantHash).HasMaxLength(128);
            Id(b.Property(e => e.StrategyId));
            Text(b.Property(e => e.StrategyVersion), 200);
            b.HasOne<AccountEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.AccountId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ControlJournalEntity>(b =>
        {
            b.ToTable("control_journal", t =>
            {
                t.HasCheckConstraint("ck_control_journal_hash", $"content_hash ~ {HashPattern}");
                t.HasCheckConstraint("ck_control_journal_kind", "kind IN (" + Quoted(ControlKinds) + ")");
                t.HasCheckConstraint("ck_control_journal_seq", "journal_seq >= 1");
            });
            b.HasKey(e => new { e.RunId, e.ControlId });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.ControlId));
            Text(b.Property(e => e.Kind), 32);
            b.Property(e => e.Payload).HasColumnType("text");
            Hash(b.Property(e => e.ContentHash));
            Text(b.Property(e => e.CodecVersion), 64);
            b.HasIndex(e => new { e.RunId, e.JournalSeq }).IsUnique();
            b.HasOne<RunEntity>().WithMany().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StepEntity>(b =>
        {
            b.ToTable("steps", t =>
            {
                t.HasCheckConstraint("ck_steps_hash", $"content_hash ~ {HashPattern}");
                t.HasCheckConstraint("ck_steps_kind", "kind IN (" + Quoted(StepKinds) + ")");
                t.HasCheckConstraint("ck_steps_seq", "step_seq >= 0");
                t.HasCheckConstraint("ck_steps_units", "units >= 1");
                t.HasCheckConstraint("ck_steps_output_range", "output_from >= 0 AND output_to_exclusive >= output_from");
            });
            b.HasKey(e => new { e.RunId, e.StepSeq });
            Id(b.Property(e => e.RunId));
            Text(b.Property(e => e.Kind), 32);
            Hash(b.Property(e => e.ContentHash));
            Text(b.Property(e => e.CodecVersion), 64);
            b.HasOne<RunEntity>().WithMany().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(e => e.Digests).WithOne().HasForeignKey(d => new { d.RunId, d.StepSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StepDigestEntity>(b =>
        {
            b.ToTable("step_digests", t =>
            {
                t.HasCheckConstraint("ck_step_digests_hash", $"hash ~ {HashPattern}");
                t.HasCheckConstraint("ck_step_digests_channel", "channel IN (" + Quoted(Channels) + ")");
                t.HasCheckConstraint("ck_step_digests_line_count", "line_count >= 0");
            });
            b.HasKey(e => new { e.RunId, e.StepSeq, e.Channel });
            Id(b.Property(e => e.RunId));
            Text(b.Property(e => e.Channel), 32);
            Hash(b.Property(e => e.Hash));
        });

        modelBuilder.Entity<ControlApplicationEntity>(b =>
        {
            b.ToTable("control_applications", t => t.HasCheckConstraint("ck_control_applications_ordinal", "ordinal >= 0"));
            b.HasKey(e => new { e.RunId, e.ControlId });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.ControlId));
            b.HasIndex(e => new { e.RunId, e.StepSeq, e.Ordinal }).IsUnique();
            b.HasOne<ControlJournalEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.ControlId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<StepEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.StepSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CheckpointEntity>(b =>
        {
            b.ToTable("checkpoints", t => t.HasCheckConstraint("ck_checkpoints_digest", $"state_digest ~ {HashPattern}"));
            b.HasKey(e => new { e.RunId, e.StepSeq });
            Id(b.Property(e => e.RunId));
            Hash(b.Property(e => e.StateDigest));
            Text(b.Property(e => e.StateDigestVersion), 64);
            b.Property(e => e.StateText).HasColumnType("text");
            b.HasOne<StepEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.StepSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OutputEntity>(b =>
        {
            b.ToTable("engine_outputs", t =>
            {
                t.HasCheckConstraint("ck_engine_outputs_hash", $"content_hash ~ {HashPattern}");
                t.HasCheckConstraint("ck_engine_outputs_seq", "seq >= 0");
            });
            b.HasKey(e => new { e.RunId, e.Seq });
            Id(b.Property(e => e.RunId));
            Text(b.Property(e => e.Type), 128);
            Id(b.Property(e => e.StrategyId));
            Id(b.Property(e => e.VariantId));
            b.Property(e => e.Payload).HasColumnType("text");
            Hash(b.Property(e => e.ContentHash));
            Text(b.Property(e => e.CodecVersion), 64);
            b.HasIndex(e => new { e.RunId, e.StepSeq });
            b.HasIndex(e => new { e.RunId, e.StrategyId, e.VariantId, e.AtUtcTicks });
            b.HasOne<StepEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.StepSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PositionEntity>(b =>
        {
            b.ToTable("positions", t =>
            {
                t.HasCheckConstraint("ck_positions_slot", "slot_no >= 0");
                t.HasCheckConstraint("ck_positions_closed_after_opened", "closed_output_seq IS NULL OR closed_output_seq > opened_output_seq");
                t.HasCheckConstraint("ck_positions_direction", "direction IN ('Long','Short')");
                t.HasCheckConstraint("ck_positions_option_type", "option_type IN ('CE','PE')");
                t.HasCheckConstraint("ck_positions_quantity", "entry_quantity > 0 AND lot_size > 0");
                t.HasCheckConstraint("ck_positions_close_complete",
                    "(closed_output_seq IS NULL AND exit_utc_ticks IS NULL AND final_exit_reason IS NULL AND net_pnl_text IS NULL) " +
                    "OR (closed_output_seq IS NOT NULL AND exit_utc_ticks IS NOT NULL AND final_exit_reason IS NOT NULL AND net_pnl_text IS NOT NULL AND gross_pnl_text IS NOT NULL AND total_costs_text IS NOT NULL)");
            });
            b.HasKey(e => new { e.RunId, e.PositionId });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.PositionId));
            Id(b.Property(e => e.StrategyId));
            Id(b.Property(e => e.VariantId));
            Id(b.Property(e => e.AccountId));
            Id(b.Property(e => e.OpportunityId));
            Text(b.Property(e => e.Direction), 8);
            Text(b.Property(e => e.OptionType), 2);
            Text(b.Property(e => e.Token), 64);
            Text(b.Property(e => e.Symbol), 128);
            Text(b.Property(e => e.StrikeText), 64);
            Text(b.Property(e => e.AvgEntryPriceText), 64);
            Id(b.Property(e => e.SourceSessionId));
            Text(b.Property(e => e.EntryQuoteBidText), 64);
            Text(b.Property(e => e.EntryQuoteAskText), 64);
            Text(b.Property(e => e.StopLevelText), 64);
            Text(b.Property(e => e.Tp1LevelText), 64);
            Id(b.Property(e => e.ExitSessionId));
            Text(b.Property(e => e.FinalExitReason), 32);
            Text(b.Property(e => e.AvgExitPriceText), 64);
            Text(b.Property(e => e.ExitQuoteBidText), 64);
            Text(b.Property(e => e.Tp1State), 32);
            Text(b.Property(e => e.GrossPnlText), 64);
            Text(b.Property(e => e.TotalCostsText), 64);
            Text(b.Property(e => e.NetPnlText), 64);
            Text(b.Property(e => e.MfeText), 64);
            Text(b.Property(e => e.MaeText), 64);
            Text(b.Property(e => e.MfePctText), 64);
            Text(b.Property(e => e.MaePctText), 64);
            Text(b.Property(e => e.HighestBidText), 64);
            Text(b.Property(e => e.LowestBidText), 64);
            Text(b.Property(e => e.ExcursionCoverage), 16);
            Text(b.Property(e => e.MfeAtFirstExitText), 64);
            Text(b.Property(e => e.MaeAtFirstExitText), 64);
            // Capacity is PER VARIANT SLOT inside one run: at most one open position per (run, strategy, variant, slot). Never database-wide and never per family.
            b.HasIndex(e => new { e.RunId, e.StrategyId, e.VariantId, e.SlotNo }).IsUnique().HasFilter("closed_output_seq IS NULL").HasDatabaseName("ux_positions_open_slot");
            b.HasIndex(e => new { e.RunId, e.StrategyId });
            b.HasOne<VariantEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.StrategyId, e.VariantId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<AccountEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.AccountId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OutputEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.OpenedOutputSeq }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OutputEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.ClosedOutputSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PositionSnapshotEntity>(b =>
        {
            b.ToTable("position_snapshots", t =>
            {
                t.HasCheckConstraint("ck_position_snapshots_status", "status IN ('Open','Closed','Unresolved')");
                t.HasCheckConstraint("ck_position_snapshots_quantity", "remaining_quantity >= 0 AND reserved_exit_quantity >= 0");
            });
            b.HasKey(e => new { e.RunId, e.PositionId });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.PositionId));
            Id(b.Property(e => e.StrategyId));
            Id(b.Property(e => e.VariantId));
            Text(b.Property(e => e.Status), 16);
            Text(b.Property(e => e.Tp1State), 32);
            Text(b.Property(e => e.LastBidText), 64);
            Text(b.Property(e => e.UnrealizedPnlText), 64);
            Text(b.Property(e => e.MfeText), 64);
            Text(b.Property(e => e.MaeText), 64);
            Text(b.Property(e => e.MfePctText), 64);
            Text(b.Property(e => e.MaePctText), 64);
            Text(b.Property(e => e.HighestBidText), 64);
            Text(b.Property(e => e.LowestBidText), 64);
            Text(b.Property(e => e.ExcursionCoverage), 16);
            b.HasIndex(e => new { e.RunId, e.Status });
            b.HasOne<PositionEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.PositionId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<StepEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.SourceStepSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RejectionEntity>(b =>
        {
            b.ToTable("rejections", t => t.HasCheckConstraint("ck_rejections_bar_seq", "bar_seq >= 0"));
            b.HasKey(e => new { e.RunId, e.OutputSeq });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.StrategyId));
            Id(b.Property(e => e.VariantId));
            Id(b.Property(e => e.OpportunityId));
            Id(b.Property(e => e.DecisionId));
            Id(b.Property(e => e.SourceSessionId));
            Text(b.Property(e => e.Stage), 32);
            Text(b.Property(e => e.Reasons), 512);
            b.HasIndex(e => new { e.RunId, e.StrategyId, e.VariantId });
            b.HasIndex(e => new { e.RunId, e.SourceSessionId, e.BarSeq });
            b.HasOne<OutputEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.OutputSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RiskCheckpointEntity>(b =>
        {
            b.ToTable("risk_checkpoints", t => t.HasCheckConstraint("ck_risk_checkpoints_hash", $"counters_hash ~ {HashPattern}"));
            b.HasKey(e => new { e.RunId, e.StepSeq });
            Id(b.Property(e => e.RunId));
            b.Property(e => e.CountersText).HasColumnType("text");
            Hash(b.Property(e => e.CountersHash));
            b.HasOne<StepEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.StepSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderEntity>(b =>
        {
            b.ToTable("orders", t =>
            {
                t.HasCheckConstraint("ck_orders_kind", "kind IN ('Entry','Exit')");
                t.HasCheckConstraint("ck_orders_exit_has_position", "kind <> 'Exit' OR position_id IS NOT NULL");
                t.HasCheckConstraint("ck_orders_entry_has_slot", "kind <> 'Entry' OR slot_no >= 0");
            });
            b.HasKey(e => new { e.RunId, e.OrderId });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.OrderId));
            Id(b.Property(e => e.StrategyId));
            Id(b.Property(e => e.VariantId));
            Text(b.Property(e => e.Kind), 16);
            Id(b.Property(e => e.OpportunityId));
            Id(b.Property(e => e.PositionId));
            b.HasIndex(e => new { e.RunId, e.PositionId });
            b.HasOne<VariantEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.StrategyId, e.VariantId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PositionEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.PositionId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OutputEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.CreatedOutputSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FillEntity>(b =>
        {
            b.ToTable("fills", t =>
            {
                t.HasCheckConstraint("ck_fills_side", "side IN ('Buy','Sell')");
                t.HasCheckConstraint("ck_fills_quantity", "quantity > 0");
            });
            b.HasKey(e => new { e.RunId, e.FillId });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.FillId));
            Id(b.Property(e => e.OrderId));
            Id(b.Property(e => e.PositionId));
            Id(b.Property(e => e.StrategyId));
            Id(b.Property(e => e.VariantId));
            Id(b.Property(e => e.AccountId));
            Text(b.Property(e => e.Side), 8);
            Text(b.Property(e => e.PriceText), 64);
            Text(b.Property(e => e.NotionalText), 64);
            b.HasIndex(e => new { e.RunId, e.OrderId });
            b.HasIndex(e => new { e.RunId, e.PositionId });
            b.HasOne<OrderEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.OrderId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PositionEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.PositionId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<VariantEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.StrategyId, e.VariantId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<AccountEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.AccountId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OutputEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.OutputSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerEntryEntity>(b =>
        {
            b.ToTable("ledger_entries", t => t.HasCheckConstraint("ck_ledger_entries_component", "component IN (" + Quoted(LedgerComponents) + ")"));
            b.HasKey(e => new { e.RunId, e.FillId, e.Component });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.FillId));
            Text(b.Property(e => e.Component), 32);
            Text(b.Property(e => e.AmountText), 64);
            b.HasOne<FillEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.FillId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditEntryEntity>(b =>
        {
            b.ToTable("audit_entries");
            b.HasKey(e => new { e.RunId, e.OutputSeq });
            Id(b.Property(e => e.RunId));
            Id(b.Property(e => e.StrategyId));
            Id(b.Property(e => e.VariantId));
            Id(b.Property(e => e.OpportunityId));
            Text(b.Property(e => e.Stage), 200);
            Text(b.Property(e => e.Outcome), 200);
            b.Property(e => e.Detail).HasColumnType("text");
            b.HasOne<OutputEntity>().WithMany().HasForeignKey(e => new { e.RunId, e.OutputSeq }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinalRecordEntity>(b =>
        {
            b.ToTable("final_records", t => t.HasCheckConstraint("ck_final_records_hash", $"content_hash ~ {HashPattern}"));
            b.HasKey(e => e.RunId);
            Id(b.Property(e => e.RunId));
            Hash(b.Property(e => e.ContentHash));
            Text(b.Property(e => e.CodecVersion), 64);
            b.HasOne<RunEntity>().WithMany().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinalLineEntity>(b =>
        {
            b.ToTable("final_lines", t =>
            {
                t.HasCheckConstraint("ck_final_lines_channel", "channel IN ('audit','summaries')");
                t.HasCheckConstraint("ck_final_lines_line_no", "line_no >= 0");
            });
            b.HasKey(e => new { e.RunId, e.Channel, e.LineNo });
            Id(b.Property(e => e.RunId));
            Text(b.Property(e => e.Channel), 16);
            b.Property(e => e.Text).HasColumnType("text");
            b.HasOne<FinalRecordEntity>().WithMany().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RecoveryLogEntity>(b =>
        {
            b.ToTable("recovery_log", t => t.HasCheckConstraint("ck_recovery_log_seq", "log_seq >= 1"));
            b.HasKey(e => new { e.RunId, e.LogSeq });
            Id(b.Property(e => e.RunId));
            Text(b.Property(e => e.Outcome), 128);
            b.Property(e => e.Detail).HasColumnType("text");
            b.HasOne<RunEntity>().WithMany().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        ApplyNamingConvention(modelBuilder);
    }

    private static void Id(PropertyBuilder property) => property.HasMaxLength(256);

    private static void Text(PropertyBuilder property, int length) => property.HasMaxLength(length);

    /// <summary>64 upper-case hexadecimal characters (SHA-256 of the UTF-8 bytes of the canonical text); the check constraint rejects anything else.</summary>
    private static void Hash(PropertyBuilder property) => property.HasMaxLength(64).IsFixedLength();

    private static string Quoted(IEnumerable<string> values) => string.Join(",", values.Select(v => "'" + v + "'"));

    /// <summary>snake_case for columns, keys, foreign keys and indexes, so the SQL reads the same everywhere and no name depends on the .NET identifier case.</summary>
    private static void ApplyNamingConvention(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Snake(property.Name));

                // Ordinal, culture-independent comparison of every text column (identifiers, hashes, payloads): the "C" collation always exists in PostgreSQL.
                // Set per column (not as a model default) so that no database-level statement is ever generated.
                if (property.ClrType == typeof(string))
                {
                    property.SetCollation("C");
                }
            }

            if (entity.FindPrimaryKey() is { } key)
            {
                key.SetName("pk_" + table);
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                var principal = fk.PrincipalEntityType.GetTableName()!;
                fk.SetConstraintName($"fk_{table}_{principal}_{string.Join("_", fk.Properties.Select(p => Snake(p.Name)))}");
            }

            foreach (var index in entity.GetIndexes())
            {
                if (index.GetDatabaseName() is { } name && name.StartsWith("ux_", StringComparison.Ordinal))
                {
                    continue;
                }

                index.SetDatabaseName($"{(index.IsUnique ? "ux" : "ix")}_{table}_{string.Join("_", index.Properties.Select(p => Snake(p.Name)))}");
            }
        }
    }

    internal static string Snake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
