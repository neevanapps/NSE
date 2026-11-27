using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.PaperEngineV2.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pev2_execution_journal",
                schema: "pev2",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    trade_date = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    at_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_execution_journal", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pev2_execution_state",
                schema: "pev2",
                columns: table => new
                {
                    component = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    heartbeat_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    trade_date = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, collation: "C"),
                    snapshot_json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pev2_execution_state", x => x.component);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pev2_execution_journal_trade_date_id",
                schema: "pev2",
                table: "pev2_execution_journal",
                columns: new[] { "trade_date", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pev2_execution_journal",
                schema: "pev2");

            migrationBuilder.DropTable(
                name: "pev2_execution_state",
                schema: "pev2");
        }
    }
}
