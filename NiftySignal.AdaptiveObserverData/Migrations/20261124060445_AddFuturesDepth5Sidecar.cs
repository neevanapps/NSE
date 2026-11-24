using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddFuturesDepth5Sidecar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adaptive_futures_depth5_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    MetricsVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BarSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Ofi5 = table.Column<long>(type: "bigint", nullable: true),
                    GrossOfi5 = table.Column<long>(type: "bigint", nullable: true),
                    Ofi5Transitions = table.Column<int>(type: "integer", nullable: false),
                    Ofi5DirectTransitions = table.Column<int>(type: "integer", nullable: false),
                    Ofi5BridgedTransitions = table.Column<int>(type: "integer", nullable: false),
                    Ofi5BridgedNet = table.Column<long>(type: "bigint", nullable: true),
                    Ofi5BridgedGapSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Ofi5BaselineResets = table.Column<int>(type: "integer", nullable: false),
                    Tob5TimeWeighted = table.Column<double>(type: "double precision", nullable: true),
                    MicroDev5TimeWeighted = table.Column<double>(type: "double precision", nullable: true),
                    DepthImb5TimeWeighted = table.Column<double>(type: "double precision", nullable: true),
                    BidNotionalTimeWeighted = table.Column<double>(type: "double precision", nullable: true),
                    AskNotionalTimeWeighted = table.Column<double>(type: "double precision", nullable: true),
                    Depth5UsableSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Depth5StaleSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Depth5InvalidSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Depth5SupersededSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Depth5NoDepthSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Depth5UnobservedSeconds = table.Column<double>(type: "double precision", nullable: false),
                    SnapshotEvents = table.Column<int>(type: "integer", nullable: false),
                    SupersededMarkers = table.Column<int>(type: "integer", nullable: false),
                    InvalidSnapshots = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_futures_depth5_bars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_futures_depth5_bars_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_futures_depth5_bars_SessionId_BarSeq_MetricsVersion",
                table: "adaptive_futures_depth5_bars",
                columns: new[] { "SessionId", "BarSeq", "MetricsVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adaptive_futures_depth5_bars");
        }
    }
}
