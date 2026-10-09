using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Trading.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialTradingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_owner",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    owner_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    acquired_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_owner", x => x.run_id);
                    table.CheckConstraint("ck_run_owner_epoch", "epoch >= 1");
                });

            migrationBuilder.CreateTable(
                name: "runs",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    trade_date = table.Column<DateOnly>(type: "date", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    codec_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    state_digest_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    engine_compatibility_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    config_schema_version = table.Column<int>(type: "integer", nullable: false),
                    config_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    freeze_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    translator_options_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    readiness_spec = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    retention_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    strategies_identity = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    durable_options_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    source_manifest_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    provenance_git_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    provenance_tree_clean = table.Column<bool>(type: "boolean", nullable: true),
                    provenance_sdk = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    provenance_assembly_hashes = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    config_json = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    freeze_snapshot = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    source_manifest = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    horizon_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    completed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runs", x => x.run_id);
                    table.CheckConstraint("ck_runs_content_hash", "content_hash ~ '^[0-9A-F]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "schema_versions",
                columns: table => new
                {
                    component = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    minimum_compatible_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    codec_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    state_digest_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schema_versions", x => x.component);
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    account_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => new { x.run_id, x.account_id });
                    table.ForeignKey(
                        name: "fk_accounts_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "control_journal",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    control_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    journal_seq = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    control_key = table.Column<long>(type: "bigint", nullable: false),
                    available_at_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    available_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    after_offered_rows = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    codec_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_control_journal", x => new { x.run_id, x.control_id });
                    table.CheckConstraint("ck_control_journal_hash", "content_hash ~ '^[0-9A-F]{64}$'");
                    table.CheckConstraint("ck_control_journal_kind", "kind IN ('Operator','Health','SourceProgress','Advance')");
                    table.CheckConstraint("ck_control_journal_seq", "journal_seq >= 1");
                    table.ForeignKey(
                        name: "fk_control_journal_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "final_records",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    codec_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_final_records", x => x.run_id);
                    table.CheckConstraint("ck_final_records_hash", "content_hash ~ '^[0-9A-F]{64}$'");
                    table.ForeignKey(
                        name: "fk_final_records_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recovery_log",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    log_seq = table.Column<long>(type: "bigint", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    owner_epoch = table.Column<long>(type: "bigint", nullable: false),
                    durable_steps = table.Column<long>(type: "bigint", nullable: false),
                    durable_outputs = table.Column<long>(type: "bigint", nullable: false),
                    durable_controls = table.Column<long>(type: "bigint", nullable: false),
                    outcome = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    detail = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_log", x => new { x.run_id, x.log_seq });
                    table.CheckConstraint("ck_recovery_log_seq", "log_seq >= 1");
                    table.ForeignKey(
                        name: "fk_recovery_log_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "steps",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    step_seq = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    units = table.Column<int>(type: "integer", nullable: false),
                    engine_time_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    offered_rows = table.Column<long>(type: "bigint", nullable: false),
                    first_group_ordinal = table.Column<long>(type: "bigint", nullable: true),
                    last_group_ordinal = table.Column<long>(type: "bigint", nullable: true),
                    last_group_at_utc_ticks = table.Column<long>(type: "bigint", nullable: true),
                    last_source_id = table.Column<long>(type: "bigint", nullable: true),
                    output_from = table.Column<long>(type: "bigint", nullable: false),
                    output_to_exclusive = table.Column<long>(type: "bigint", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    codec_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_steps", x => new { x.run_id, x.step_seq });
                    table.CheckConstraint("ck_steps_hash", "content_hash ~ '^[0-9A-F]{64}$'");
                    table.CheckConstraint("ck_steps_kind", "kind IN ('DataGroup','ControlOnly','Advance','Finish')");
                    table.CheckConstraint("ck_steps_output_range", "output_from >= 0 AND output_to_exclusive >= output_from");
                    table.CheckConstraint("ck_steps_seq", "step_seq >= 0");
                    table.CheckConstraint("ck_steps_units", "units >= 1");
                    table.ForeignKey(
                        name: "fk_steps_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "variants",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    variant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    variant_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    strategy_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    strategy_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    account_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_variants", x => new { x.run_id, x.variant_id });
                    table.ForeignKey(
                        name: "fk_variants_accounts_run_id_account_id",
                        columns: x => new { x.run_id, x.account_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "run_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variants_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "runs",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "final_lines",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_final_lines", x => new { x.run_id, x.channel, x.line_no });
                    table.CheckConstraint("ck_final_lines_channel", "channel IN ('audit','summaries')");
                    table.CheckConstraint("ck_final_lines_line_no", "line_no >= 0");
                    table.ForeignKey(
                        name: "fk_final_lines_final_records_run_id",
                        column: x => x.run_id,
                        principalTable: "final_records",
                        principalColumn: "run_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "checkpoints",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    step_seq = table.Column<long>(type: "bigint", nullable: false),
                    engine_time_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    state_digest = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    state_digest_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    state_text = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_checkpoints", x => new { x.run_id, x.step_seq });
                    table.CheckConstraint("ck_checkpoints_digest", "state_digest ~ '^[0-9A-F]{64}$'");
                    table.ForeignKey(
                        name: "fk_checkpoints_steps_run_id_step_seq",
                        columns: x => new { x.run_id, x.step_seq },
                        principalTable: "steps",
                        principalColumns: new[] { "run_id", "step_seq" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "control_applications",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    control_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    step_seq = table.Column<long>(type: "bigint", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_control_applications", x => new { x.run_id, x.control_id });
                    table.CheckConstraint("ck_control_applications_ordinal", "ordinal >= 0");
                    table.ForeignKey(
                        name: "fk_control_applications_control_journal_run_id_control_id",
                        columns: x => new { x.run_id, x.control_id },
                        principalTable: "control_journal",
                        principalColumns: new[] { "run_id", "control_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_control_applications_steps_run_id_step_seq",
                        columns: x => new { x.run_id, x.step_seq },
                        principalTable: "steps",
                        principalColumns: new[] { "run_id", "step_seq" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "engine_outputs",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    step_seq = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    variant_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    at_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C"),
                    codec_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_engine_outputs", x => new { x.run_id, x.seq });
                    table.CheckConstraint("ck_engine_outputs_hash", "content_hash ~ '^[0-9A-F]{64}$'");
                    table.CheckConstraint("ck_engine_outputs_seq", "seq >= 0");
                    table.ForeignKey(
                        name: "fk_engine_outputs_steps_run_id_step_seq",
                        columns: x => new { x.run_id, x.step_seq },
                        principalTable: "steps",
                        principalColumns: new[] { "run_id", "step_seq" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "step_digests",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    step_seq = table.Column<long>(type: "bigint", nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    line_count = table.Column<long>(type: "bigint", nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_step_digests", x => new { x.run_id, x.step_seq, x.channel });
                    table.CheckConstraint("ck_step_digests_channel", "channel IN ('audit','bars','candles','opportunities','outputs','quotes','readiness','rowQuarantine','summaries')");
                    table.CheckConstraint("ck_step_digests_count", "line_count >= 0");
                    table.CheckConstraint("ck_step_digests_hash", "hash ~ '^[0-9A-F]{64}$'");
                    table.ForeignKey(
                        name: "fk_step_digests_steps_run_id_step_seq",
                        columns: x => new { x.run_id, x.step_seq },
                        principalTable: "steps",
                        principalColumns: new[] { "run_id", "step_seq" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    output_seq = table.Column<long>(type: "bigint", nullable: false),
                    variant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    opportunity_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true, collation: "C"),
                    stage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    outcome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    detail = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => new { x.run_id, x.output_seq });
                    table.ForeignKey(
                        name: "fk_audit_entries_engine_outputs_run_id_output_seq",
                        columns: x => new { x.run_id, x.output_seq },
                        principalTable: "engine_outputs",
                        principalColumns: new[] { "run_id", "seq" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "positions",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    position_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    variant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    account_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    slot_no = table.Column<int>(type: "integer", nullable: false),
                    opportunity_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    opened_output_seq = table.Column<long>(type: "bigint", nullable: false),
                    closed_output_seq = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_positions", x => new { x.run_id, x.position_id });
                    table.CheckConstraint("ck_positions_closed_after_opened", "closed_output_seq IS NULL OR closed_output_seq > opened_output_seq");
                    table.CheckConstraint("ck_positions_slot", "slot_no >= 1");
                    table.ForeignKey(
                        name: "fk_positions_accounts_run_id_account_id",
                        columns: x => new { x.run_id, x.account_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "run_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_positions_engine_outputs_run_id_closed_output_seq",
                        columns: x => new { x.run_id, x.closed_output_seq },
                        principalTable: "engine_outputs",
                        principalColumns: new[] { "run_id", "seq" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_positions_engine_outputs_run_id_opened_output_seq",
                        columns: x => new { x.run_id, x.opened_output_seq },
                        principalTable: "engine_outputs",
                        principalColumns: new[] { "run_id", "seq" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_positions_variants_run_id_variant_id",
                        columns: x => new { x.run_id, x.variant_id },
                        principalTable: "variants",
                        principalColumns: new[] { "run_id", "variant_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    order_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    variant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    opportunity_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true, collation: "C"),
                    slot_no = table.Column<int>(type: "integer", nullable: true),
                    position_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true, collation: "C"),
                    created_output_seq = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => new { x.run_id, x.order_id });
                    table.CheckConstraint("ck_orders_entry_has_slot", "kind <> 'Entry' OR slot_no >= 1");
                    table.CheckConstraint("ck_orders_exit_has_position", "kind <> 'Exit' OR position_id IS NOT NULL");
                    table.CheckConstraint("ck_orders_kind", "kind IN ('Entry','Exit')");
                    table.ForeignKey(
                        name: "fk_orders_engine_outputs_run_id_created_output_seq",
                        columns: x => new { x.run_id, x.created_output_seq },
                        principalTable: "engine_outputs",
                        principalColumns: new[] { "run_id", "seq" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_positions_run_id_position_id",
                        columns: x => new { x.run_id, x.position_id },
                        principalTable: "positions",
                        principalColumns: new[] { "run_id", "position_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_variants_run_id_variant_id",
                        columns: x => new { x.run_id, x.variant_id },
                        principalTable: "variants",
                        principalColumns: new[] { "run_id", "variant_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fills",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    fill_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    order_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    position_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    variant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    account_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    output_seq = table.Column<long>(type: "bigint", nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, collation: "C"),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    price_text = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    notional_text = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fills", x => new { x.run_id, x.fill_id });
                    table.CheckConstraint("ck_fills_quantity", "quantity > 0");
                    table.CheckConstraint("ck_fills_side", "side IN ('Buy','Sell')");
                    table.ForeignKey(
                        name: "fk_fills_accounts_run_id_account_id",
                        columns: x => new { x.run_id, x.account_id },
                        principalTable: "accounts",
                        principalColumns: new[] { "run_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fills_engine_outputs_run_id_output_seq",
                        columns: x => new { x.run_id, x.output_seq },
                        principalTable: "engine_outputs",
                        principalColumns: new[] { "run_id", "seq" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fills_orders_run_id_order_id",
                        columns: x => new { x.run_id, x.order_id },
                        principalTable: "orders",
                        principalColumns: new[] { "run_id", "order_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fills_positions_run_id_position_id",
                        columns: x => new { x.run_id, x.position_id },
                        principalTable: "positions",
                        principalColumns: new[] { "run_id", "position_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fills_variants_run_id_variant_id",
                        columns: x => new { x.run_id, x.variant_id },
                        principalTable: "variants",
                        principalColumns: new[] { "run_id", "variant_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    fill_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    component = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    amount_text = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => new { x.run_id, x.fill_id, x.component });
                    table.CheckConstraint("ck_ledger_entries_component", "component IN ('Brokerage','Stt','ExchangeCharge','SebiFee','StampDuty','Gst','Notional')");
                    table.ForeignKey(
                        name: "fk_ledger_entries_fills_run_id_fill_id",
                        columns: x => new { x.run_id, x.fill_id },
                        principalTable: "fills",
                        principalColumns: new[] { "run_id", "fill_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "schema_versions",
                columns: new[] { "component", "codec_version", "minimum_compatible_code", "schema_version", "state_digest_version" },
                values: new object[] { "trading-engine", "trading-codec/1", "trading-engine/cp6.1", 1, "trading-state/1" });

            migrationBuilder.CreateIndex(
                name: "ux_control_applications_run_id_step_seq_ordinal",
                table: "control_applications",
                columns: new[] { "run_id", "step_seq", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_control_journal_run_id_journal_seq",
                table: "control_journal",
                columns: new[] { "run_id", "journal_seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_engine_outputs_run_id_step_seq",
                table: "engine_outputs",
                columns: new[] { "run_id", "step_seq" });

            migrationBuilder.CreateIndex(
                name: "ix_engine_outputs_run_id_variant_id_at_utc_ticks",
                table: "engine_outputs",
                columns: new[] { "run_id", "variant_id", "at_utc_ticks" });

            migrationBuilder.CreateIndex(
                name: "ix_fills_run_id_account_id",
                table: "fills",
                columns: new[] { "run_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fills_run_id_order_id",
                table: "fills",
                columns: new[] { "run_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fills_run_id_output_seq",
                table: "fills",
                columns: new[] { "run_id", "output_seq" });

            migrationBuilder.CreateIndex(
                name: "ix_fills_run_id_position_id",
                table: "fills",
                columns: new[] { "run_id", "position_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fills_run_id_variant_id",
                table: "fills",
                columns: new[] { "run_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_run_id_created_output_seq",
                table: "orders",
                columns: new[] { "run_id", "created_output_seq" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_run_id_position_id",
                table: "orders",
                columns: new[] { "run_id", "position_id" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_run_id_variant_id",
                table: "orders",
                columns: new[] { "run_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_positions_run_id_account_id",
                table: "positions",
                columns: new[] { "run_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_positions_run_id_closed_output_seq",
                table: "positions",
                columns: new[] { "run_id", "closed_output_seq" });

            migrationBuilder.CreateIndex(
                name: "ix_positions_run_id_opened_output_seq",
                table: "positions",
                columns: new[] { "run_id", "opened_output_seq" });

            migrationBuilder.CreateIndex(
                name: "ux_positions_open_slot",
                table: "positions",
                columns: new[] { "run_id", "variant_id", "slot_no" },
                unique: true,
                filter: "closed_output_seq IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_runs_trade_date",
                table: "runs",
                column: "trade_date");

            migrationBuilder.CreateIndex(
                name: "ix_variants_run_id_account_id",
                table: "variants",
                columns: new[] { "run_id", "account_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "checkpoints");

            migrationBuilder.DropTable(
                name: "control_applications");

            migrationBuilder.DropTable(
                name: "final_lines");

            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "recovery_log");

            migrationBuilder.DropTable(
                name: "run_owner");

            migrationBuilder.DropTable(
                name: "schema_versions");

            migrationBuilder.DropTable(
                name: "step_digests");

            migrationBuilder.DropTable(
                name: "control_journal");

            migrationBuilder.DropTable(
                name: "final_records");

            migrationBuilder.DropTable(
                name: "fills");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "positions");

            migrationBuilder.DropTable(
                name: "engine_outputs");

            migrationBuilder.DropTable(
                name: "variants");

            migrationBuilder.DropTable(
                name: "steps");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "runs");
        }
    }
}
