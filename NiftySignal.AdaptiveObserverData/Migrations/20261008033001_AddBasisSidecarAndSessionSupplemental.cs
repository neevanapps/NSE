using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddBasisSidecarAndSessionSupplemental : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adaptive_basis_supplemental_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    MetricsVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BasisStart = table.Column<double>(type: "double precision", nullable: true),
                    BasisTimeWeighted = table.Column<double>(type: "double precision", nullable: true),
                    BasisEnd = table.Column<double>(type: "double precision", nullable: true),
                    DeltaBasis = table.Column<double>(type: "double precision", nullable: true),
                    SpotAgeStartSeconds = table.Column<double>(type: "double precision", nullable: true),
                    SpotAgeEndSeconds = table.Column<double>(type: "double precision", nullable: true),
                    SpotAgeMaxSeconds = table.Column<double>(type: "double precision", nullable: true),
                    BasisStateChanges = table.Column<int>(type: "integer", nullable: false),
                    CoveredSeconds = table.Column<double>(type: "double precision", nullable: false),
                    UncoveredSeconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_basis_supplemental_bars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_basis_supplemental_bars_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adaptive_session_supplemental",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    MetricsVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SpotToken = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SpotSymbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResolutionProvenance = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_session_supplemental", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_session_supplemental_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_basis_supplemental_bars_SessionId_BarSeq_MetricsVe~",
                table: "adaptive_basis_supplemental_bars",
                columns: new[] { "SessionId", "BarSeq", "MetricsVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_session_supplemental_SessionId_MetricsVersion",
                table: "adaptive_session_supplemental",
                columns: new[] { "SessionId", "MetricsVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adaptive_basis_supplemental_bars");

            migrationBuilder.DropTable(
                name: "adaptive_session_supplemental");
        }
    }
}
