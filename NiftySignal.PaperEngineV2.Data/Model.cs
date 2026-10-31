using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NiftySignal.PaperEngineV2.Data;

/// <summary>The schema version this build writes and understands. Bump together with a reviewed migration.</summary>
public static class V2Schema
{
    public const int CurrentVersion = 2;
    public const string Component = "paper-engine-v2";
    public const string SchemaName = "pev2";
    public const string JournalSchema = "pev2-journal/1";
}

public sealed class SchemaVersionRow
{
    public string Component { get; set; } = string.Empty;

    public int SchemaVersion { get; set; }

    public string JournalSchema { get; set; } = string.Empty;
}

public sealed class FrameRow
{
    public long SessionId { get; set; }

    public int BarSeq { get; set; }

    public string FrameHash { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public long BarEndUtcTicks { get; set; }

    public long LastSourceTickId { get; set; }

    public bool Verified { get; set; }

    public long JournalSeq { get; set; }
}

public sealed class DispositionRow
{
    public long SessionId { get; set; }

    public int BarSeq { get; set; }

    public bool EntriesAllowed { get; set; }

    public int BlockReasons { get; set; }

    public int ProcessingLagMs { get; set; }

    public int RunLength { get; set; }

    public bool PreviousFrameUsable { get; set; }

    public bool ProcessedWithoutJournal { get; set; }

    public long JournalSeq { get; set; }
}

public sealed class GapRow
{
    public long Id { get; set; }

    public long SessionId { get; set; }

    public int FromBarSeq { get; set; }

    public int ToBarSeq { get; set; }

    public int Cause { get; set; }

    public int ProducerSignals { get; set; }
}

public sealed class EventRow
{
    public string EventId { get; set; } = string.Empty;

    public string StrategyId { get; set; } = string.Empty;

    public string ConfigHash { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public int BarSeq { get; set; }

    public long AtUtcTicks { get; set; }

    public string PayloadJson { get; set; } = string.Empty;
}

public sealed class ConfigRow
{
    public string ConfigHash { get; set; } = string.Empty;

    public string StrategyId { get; set; } = string.Empty;

    public int Version { get; set; }

    public string CanonicalJson { get; set; } = string.Empty;

    public long RecordedUtcTicks { get; set; }
}

public sealed class AlertOutboxRow
{
    public string AlertId { get; set; } = string.Empty;

    public string AlertClass { get; set; } = string.Empty;

    public string StrategyId { get; set; } = string.Empty;

    public string EventId { get; set; } = string.Empty;

    public string MessageText { get; set; } = string.Empty;

    /// <summary>0 Pending, 1 Sent, 2 Uncertain (never resent automatically), 3 Failed (attempts exhausted), 4 Expired (stale), 5 Sending (claimed; an interrupted claim is resolved to Uncertain).</summary>
    public int Status { get; set; }

    public int Attempts { get; set; }

    public long CreatedUtcTicks { get; set; }

    public long NextAttemptUtcTicks { get; set; }

    public long? SentUtcTicks { get; set; }

    public long? TelegramMessageId { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// The model of the <c>pev2</c> schema. Own schema and own migration history table, so it can share the trading database without touching any trading.* table. Instants are stored as UTC ticks (bigint, exact);
/// payloads are text (authoritative, canonical). Nothing here connects, creates or migrates by itself.
/// </summary>
public sealed class PaperEngineV2DbContext(DbContextOptions<PaperEngineV2DbContext> options) : DbContext(options)
{
    public const string HistoryTable = "__pev2_migrations_history";

    public DbSet<SchemaVersionRow> SchemaVersions => Set<SchemaVersionRow>();

    public DbSet<FrameRow> Frames => Set<FrameRow>();

    public DbSet<DispositionRow> Dispositions => Set<DispositionRow>();

    public DbSet<GapRow> Gaps => Set<GapRow>();

    public DbSet<EventRow> Events => Set<EventRow>();

    public DbSet<ConfigRow> Configs => Set<ConfigRow>();

    public DbSet<AlertOutboxRow> AlertOutbox => Set<AlertOutboxRow>();

    static PropertyBuilder<string> Text(PropertyBuilder<string> p, int max) => p.HasMaxLength(max).UseCollation("C");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(V2Schema.SchemaName);

        modelBuilder.Entity<SchemaVersionRow>(b =>
        {
            b.ToTable("pev2_schema_versions");
            b.HasKey(e => e.Component);
            Text(b.Property(e => e.Component), 64);
            Text(b.Property(e => e.JournalSchema), 64);
            b.HasData(new SchemaVersionRow { Component = V2Schema.Component, SchemaVersion = V2Schema.CurrentVersion, JournalSchema = V2Schema.JournalSchema });
        });

        modelBuilder.Entity<FrameRow>(b =>
        {
            b.ToTable("pev2_frames", t => t.HasCheckConstraint("ck_pev2_frames_hash", "frame_hash ~ '^[0-9a-f]{64}$'"));
            b.HasKey(e => new { e.SessionId, e.BarSeq });
            Text(b.Property(e => e.FrameHash), 64);
            b.Property(e => e.PayloadJson).HasColumnType("text");
            b.Property(e => e.JournalSeq).ValueGeneratedOnAdd().UseIdentityAlwaysColumn();
            b.HasIndex(e => e.JournalSeq).IsUnique();
            b.HasIndex(e => new { e.SessionId, e.BarEndUtcTicks });
        });

        modelBuilder.Entity<DispositionRow>(b =>
        {
            b.ToTable("pev2_dispositions");
            b.HasKey(e => new { e.SessionId, e.BarSeq });
            b.Property(e => e.JournalSeq).ValueGeneratedOnAdd().UseIdentityAlwaysColumn();
            b.HasIndex(e => e.JournalSeq).IsUnique();
        });

        modelBuilder.Entity<GapRow>(b =>
        {
            b.ToTable("pev2_gaps");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedOnAdd().UseIdentityAlwaysColumn();
            b.HasIndex(e => new { e.SessionId, e.FromBarSeq, e.ToBarSeq }).IsUnique();
        });

        modelBuilder.Entity<EventRow>(b =>
        {
            b.ToTable("pev2_events");
            b.HasKey(e => e.EventId);
            Text(b.Property(e => e.EventId), 256);
            Text(b.Property(e => e.StrategyId), 64);
            Text(b.Property(e => e.ConfigHash), 64);
            Text(b.Property(e => e.Kind), 32);
            b.Property(e => e.PayloadJson).HasColumnType("text");
            b.HasIndex(e => new { e.StrategyId, e.AtUtcTicks });
            b.HasIndex(e => e.AtUtcTicks);
        });

        modelBuilder.Entity<ConfigRow>(b =>
        {
            b.ToTable("pev2_strategy_configs");
            b.HasKey(e => e.ConfigHash);
            Text(b.Property(e => e.ConfigHash), 64);
            Text(b.Property(e => e.StrategyId), 64);
            b.Property(e => e.CanonicalJson).HasColumnType("text");
            b.HasIndex(e => new { e.StrategyId, e.Version });
        });

        modelBuilder.Entity<AlertOutboxRow>(b =>
        {
            b.ToTable("pev2_alert_outbox");
            b.HasKey(e => e.AlertId);
            Text(b.Property(e => e.AlertId), 300);
            Text(b.Property(e => e.AlertClass), 32);
            Text(b.Property(e => e.StrategyId), 64);
            Text(b.Property(e => e.EventId), 256);
            b.Property(e => e.MessageText).HasColumnType("text");
            b.Property(e => e.LastError).HasColumnType("text");
            b.HasIndex(e => new { e.Status, e.NextAttemptUtcTicks });
        });

        // snake_case column names, derived mechanically so the idempotent script reads naturally
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var p in entity.GetProperties())
                p.SetColumnName(Snake(p.Name));
    }

    internal static string Snake(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add / script</c> only: points at an unresolvable placeholder host, so those commands can never reach a database.</summary>
public sealed class PaperEngineV2DbContextFactory : IDesignTimeDbContextFactory<PaperEngineV2DbContext>
{
    public const string PlaceholderConnection = "Host=design-time.invalid;Database=niftysignal_trading_design;Timeout=1;Command Timeout=1";

    public PaperEngineV2DbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<PaperEngineV2DbContext>()
            .UseNpgsql(PlaceholderConnection, npgsql => npgsql.MigrationsHistoryTable(PaperEngineV2DbContext.HistoryTable, V2Schema.SchemaName))
            .Options);
}
