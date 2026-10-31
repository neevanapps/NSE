using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.PaperEngineV2.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pev2_alert_outbox",
                schema: "pev2",
                columns: table => new
                {
                    alert_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    alert_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    strategy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    event_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    message_text = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    next_attempt_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    sent_utc_ticks = table.Column<long>(type: "bigint", nullable: true),
                    telegram_message_id = table.Column<long>(type: "bigint", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_alert_outbox", x => x.alert_id);
                });

            migrationBuilder.UpdateData(
                schema: "pev2",
                table: "pev2_schema_versions",
                keyColumn: "component",
                keyValue: "paper-engine-v2",
                column: "schema_version",
                value: 2);

            migrationBuilder.CreateIndex(
                name: "IX_pev2_alert_outbox_status_next_attempt_utc_ticks",
                schema: "pev2",
                table: "pev2_alert_outbox",
                columns: new[] { "status", "next_attempt_utc_ticks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pev2_alert_outbox",
                schema: "pev2");

            migrationBuilder.UpdateData(
                schema: "pev2",
                table: "pev2_schema_versions",
                keyColumn: "component",
                keyValue: "paper-engine-v2",
                column: "schema_version",
                value: 1);
        }
    }
}
