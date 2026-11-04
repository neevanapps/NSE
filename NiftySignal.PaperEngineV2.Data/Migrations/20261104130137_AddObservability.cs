using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.PaperEngineV2.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddObservability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pev2_live_marks",
                schema: "pev2",
                columns: table => new
                {
                    strategy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    position_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    trade_date = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    mark_seq = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    published_utc_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_live_marks", x => new { x.strategy_id, x.position_id });
                });

            migrationBuilder.CreateTable(
                name: "pev2_opportunities",
                schema: "pev2",
                columns: table => new
                {
                    opportunity_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    strategy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    version = table.Column<int>(type: "integer", nullable: false),
                    config_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    trade_date = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    bar_seq = table.Column<int>(type: "integer", nullable: false),
                    stage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    resolved_bar_seq = table.Column<int>(type: "integer", nullable: false),
                    decision_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    category = table.Column<int>(type: "integer", nullable: false),
                    category_name = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false, collation: "C"),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    detail = table.Column<string>(type: "text", nullable: false),
                    in_position = table.Column<bool>(type: "boolean", nullable: false),
                    order_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    order_attempted = table.Column<bool>(type: "boolean", nullable: false),
                    order_result = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, collation: "C"),
                    intent_id = table.Column<string>(type: "text", nullable: true),
                    token = table.Column<string>(type: "text", nullable: true),
                    event_ref = table.Column<string>(type: "text", nullable: true),
                    frame_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    entries_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    coverage = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    recorded_utc_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_opportunities", x => x.opportunity_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pev2_opportunities_trade_date_category",
                schema: "pev2",
                table: "pev2_opportunities",
                columns: new[] { "trade_date", "category" });

            migrationBuilder.CreateIndex(
                name: "IX_pev2_opportunities_trade_date_strategy_id_bar_seq",
                schema: "pev2",
                table: "pev2_opportunities",
                columns: new[] { "trade_date", "strategy_id", "bar_seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pev2_live_marks",
                schema: "pev2");

            migrationBuilder.DropTable(
                name: "pev2_opportunities",
                schema: "pev2");
        }
    }
}
