using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.PaperEngineV2.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPaperEngineV2Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pev2");

            migrationBuilder.CreateTable(
                name: "pev2_dispositions",
                schema: "pev2",
                columns: table => new
                {
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    bar_seq = table.Column<int>(type: "integer", nullable: false),
                    entries_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    block_reasons = table.Column<int>(type: "integer", nullable: false),
                    processing_lag_ms = table.Column<int>(type: "integer", nullable: false),
                    run_length = table.Column<int>(type: "integer", nullable: false),
                    previous_frame_usable = table.Column<bool>(type: "boolean", nullable: false),
                    processed_without_journal = table.Column<bool>(type: "boolean", nullable: false),
                    journal_seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_dispositions", x => new { x.session_id, x.bar_seq });
                });

            migrationBuilder.CreateTable(
                name: "pev2_events",
                schema: "pev2",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    strategy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    config_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    bar_seq = table.Column<int>(type: "integer", nullable: false),
                    at_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "pev2_frames",
                schema: "pev2",
                columns: table => new
                {
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    bar_seq = table.Column<int>(type: "integer", nullable: false),
                    frame_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    bar_end_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    last_source_tick_id = table.Column<long>(type: "bigint", nullable: false),
                    verified = table.Column<bool>(type: "boolean", nullable: false),
                    journal_seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_frames", x => new { x.session_id, x.bar_seq });
                    table.CheckConstraint("ck_pev2_frames_hash", "frame_hash ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "pev2_gaps",
                schema: "pev2",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    from_bar_seq = table.Column<int>(type: "integer", nullable: false),
                    to_bar_seq = table.Column<int>(type: "integer", nullable: false),
                    cause = table.Column<int>(type: "integer", nullable: false),
                    producer_signals = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_gaps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pev2_schema_versions",
                schema: "pev2",
                columns: table => new
                {
                    component = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    journal_schema = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_schema_versions", x => x.component);
                });

            migrationBuilder.CreateTable(
                name: "pev2_strategy_configs",
                schema: "pev2",
                columns: table => new
                {
                    config_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    strategy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    version = table.Column<int>(type: "integer", nullable: false),
                    canonical_json = table.Column<string>(type: "text", nullable: false),
                    recorded_utc_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_strategy_configs", x => x.config_hash);
                });

            migrationBuilder.InsertData(
                schema: "pev2",
                table: "pev2_schema_versions",
                columns: new[] { "component", "journal_schema", "schema_version" },
                values: new object[] { "paper-engine-v2", "pev2-journal/1", 1 });

            migrationBuilder.CreateIndex(
                name: "IX_pev2_dispositions_journal_seq",
                schema: "pev2",
                table: "pev2_dispositions",
                column: "journal_seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pev2_events_at_utc_ticks",
                schema: "pev2",
                table: "pev2_events",
                column: "at_utc_ticks");

            migrationBuilder.CreateIndex(
                name: "IX_pev2_events_strategy_id_at_utc_ticks",
                schema: "pev2",
                table: "pev2_events",
                columns: new[] { "strategy_id", "at_utc_ticks" });

            migrationBuilder.CreateIndex(
                name: "IX_pev2_frames_journal_seq",
                schema: "pev2",
                table: "pev2_frames",
                column: "journal_seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pev2_frames_session_id_bar_end_utc_ticks",
                schema: "pev2",
                table: "pev2_frames",
                columns: new[] { "session_id", "bar_end_utc_ticks" });

            migrationBuilder.CreateIndex(
                name: "IX_pev2_gaps_session_id_from_bar_seq_to_bar_seq",
                schema: "pev2",
                table: "pev2_gaps",
                columns: new[] { "session_id", "from_bar_seq", "to_bar_seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pev2_strategy_configs_strategy_id_version",
                schema: "pev2",
                table: "pev2_strategy_configs",
                columns: new[] { "strategy_id", "version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pev2_dispositions",
                schema: "pev2");

            migrationBuilder.DropTable(
                name: "pev2_events",
                schema: "pev2");

            migrationBuilder.DropTable(
                name: "pev2_frames",
                schema: "pev2");

            migrationBuilder.DropTable(
                name: "pev2_gaps",
                schema: "pev2");

            migrationBuilder.DropTable(
                name: "pev2_schema_versions",
                schema: "pev2");

            migrationBuilder.DropTable(
                name: "pev2_strategy_configs",
                schema: "pev2");
        }
    }
}
