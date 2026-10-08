using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddFuturesSupplementalSidecar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adaptive_futures_supplemental_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    MetricsVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TobStart = table.Column<double>(type: "double precision", nullable: true),
                    TobTimeWeighted = table.Column<double>(type: "double precision", nullable: true),
                    TobEnd = table.Column<double>(type: "double precision", nullable: true),
                    TobChange = table.Column<double>(type: "double precision", nullable: true),
                    TobMin = table.Column<double>(type: "double precision", nullable: true),
                    TobMax = table.Column<double>(type: "double precision", nullable: true),
                    MicroDevStart = table.Column<double>(type: "double precision", nullable: true),
                    MicroDevTimeWeighted = table.Column<double>(type: "double precision", nullable: true),
                    MicroDevEnd = table.Column<double>(type: "double precision", nullable: true),
                    MicroDevChange = table.Column<double>(type: "double precision", nullable: true),
                    Ofi = table.Column<long>(type: "bigint", nullable: true),
                    OfiTransitions = table.Column<int>(type: "integer", nullable: false),
                    BookStateChanges = table.Column<int>(type: "integer", nullable: false),
                    InvalidBookEvents = table.Column<int>(type: "integer", nullable: false),
                    ValidBookSeconds = table.Column<double>(type: "double precision", nullable: false),
                    InvalidBookSeconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_futures_supplemental_bars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_futures_supplemental_bars_adaptive_sessions_Sessio~",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_futures_supplemental_bars_SessionId_BarSeq_Metrics~",
                table: "adaptive_futures_supplemental_bars",
                columns: new[] { "SessionId", "BarSeq", "MetricsVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adaptive_futures_supplemental_bars");
        }
    }
}
