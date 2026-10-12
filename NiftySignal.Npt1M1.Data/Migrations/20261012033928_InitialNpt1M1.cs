using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Npt1M1.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialNpt1M1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "npt1m1");

            migrationBuilder.CreateTable(
                name: "frames",
                schema: "npt1m1",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bar_seq = table.Column<int>(type: "integer", nullable: false),
                    start_available_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_available_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ready = table.Column<bool>(type: "boolean", nullable: false),
                    is_valid = table.Column<bool>(type: "boolean", nullable: false),
                    consecutive_valid_bars = table.Column<int>(type: "integer", nullable: false),
                    microdev_time_weighted = table.Column<double>(type: "double precision", nullable: true),
                    microdev_usable_s = table.Column<double>(type: "double precision", nullable: true),
                    pe_band_contract_strict_delta = table.Column<long>(type: "bigint", nullable: true),
                    band_center_strike = table.Column<double>(type: "double precision", nullable: true),
                    band_expiry = table.Column<DateOnly>(type: "date", nullable: true),
                    band_unavailable_reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    frame_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    canonical_text = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_frames", x => new { x.session_id, x.bar_seq });
                });

            migrationBuilder.CreateTable(
                name: "runtime",
                schema: "npt1m1",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_tick_id = table.Column<long>(type: "bigint", nullable: false),
                    last_bar_seq = table.Column<int>(type: "integer", nullable: false),
                    last_bar_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_heartbeat_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    polls = table.Column<long>(type: "bigint", nullable: false),
                    last_poll_ms = table.Column<double>(type: "double precision", nullable: false),
                    max_poll_ms = table.Column<double>(type: "double precision", nullable: false),
                    rows_read = table.Column<long>(type: "bigint", nullable: false),
                    divergent_frames = table.Column<long>(type: "bigint", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime", x => x.session_id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "npt1m1",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trade_date = table.Column<DateOnly>(type: "date", nullable: false),
                    model_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    freeze_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    base_bar_volume = table.Column<long>(type: "bigint", nullable: false),
                    opening_volume = table.Column<long>(type: "bigint", nullable: false),
                    bar_size_status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    decision_json = table.Column<string>(type: "text", nullable: false),
                    report_json = table.Column<string>(type: "text", nullable: false),
                    future_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    weekly_expiry = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.session_id);
                });

            migrationBuilder.CreateTable(
                name: "volume_history",
                schema: "npt1m1",
                columns: table => new
                {
                    trade_date = table.Column<DateOnly>(type: "date", nullable: false),
                    future_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    future_symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expiry = table.Column<DateOnly>(type: "date", nullable: false),
                    lot_size = table.Column<int>(type: "integer", nullable: false),
                    opening_volume = table.Column<long>(type: "bigint", nullable: false),
                    window_volume = table.Column<long>(type: "bigint", nullable: false),
                    close_volume = table.Column<long>(type: "bigint", nullable: false),
                    full_day_volume = table.Column<long>(type: "bigint", nullable: false),
                    opening_coverage_minutes = table.Column<int>(type: "integer", nullable: false),
                    window_coverage_minutes = table.Column<int>(type: "integer", nullable: false),
                    first_tick_ist = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_tick_ist = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    futures_ticks = table.Column<long>(type: "bigint", nullable: false),
                    baseline_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    computed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volume_history", x => x.trade_date);
                });

            migrationBuilder.CreateIndex(
                name: "ix_frames_end",
                schema: "npt1m1",
                table: "frames",
                column: "end_available_at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_sessions_day_model",
                schema: "npt1m1",
                table: "sessions",
                columns: new[] { "trade_date", "model_version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "frames",
                schema: "npt1m1");

            migrationBuilder.DropTable(
                name: "runtime",
                schema: "npt1m1");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "npt1m1");

            migrationBuilder.DropTable(
                name: "volume_history",
                schema: "npt1m1");
        }
    }
}
