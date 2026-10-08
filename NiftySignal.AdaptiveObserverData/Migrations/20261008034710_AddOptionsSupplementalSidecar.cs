using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionsSupplementalSidecar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adaptive_options_supplemental_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    MetricsVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ObservationStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CenterStrike = table.Column<double>(type: "double precision", nullable: true),
                    UnavailableReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CeOiStart = table.Column<long>(type: "bigint", nullable: true),
                    CeOiEnd = table.Column<long>(type: "bigint", nullable: true),
                    CeOiDelta = table.Column<long>(type: "bigint", nullable: true),
                    PeOiStart = table.Column<long>(type: "bigint", nullable: true),
                    PeOiEnd = table.Column<long>(type: "bigint", nullable: true),
                    PeOiDelta = table.Column<long>(type: "bigint", nullable: true),
                    CeMidStart = table.Column<double>(type: "double precision", nullable: true),
                    CeMidEnd = table.Column<double>(type: "double precision", nullable: true),
                    CeMidDelta = table.Column<double>(type: "double precision", nullable: true),
                    PeMidStart = table.Column<double>(type: "double precision", nullable: true),
                    PeMidEnd = table.Column<double>(type: "double precision", nullable: true),
                    PeMidDelta = table.Column<double>(type: "double precision", nullable: true),
                    CePosition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PePosition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CeIvStart = table.Column<double>(type: "double precision", nullable: true),
                    CeIvEnd = table.Column<double>(type: "double precision", nullable: true),
                    CeDeltaIv = table.Column<double>(type: "double precision", nullable: true),
                    PeIvStart = table.Column<double>(type: "double precision", nullable: true),
                    PeIvEnd = table.Column<double>(type: "double precision", nullable: true),
                    PeDeltaIv = table.Column<double>(type: "double precision", nullable: true),
                    IvSkewStart = table.Column<double>(type: "double precision", nullable: true),
                    IvSkewEnd = table.Column<double>(type: "double precision", nullable: true),
                    DeltaSkew = table.Column<double>(type: "double precision", nullable: true),
                    BarCeQuantity = table.Column<long>(type: "bigint", nullable: true),
                    BarPeQuantity = table.Column<long>(type: "bigint", nullable: true),
                    VolPcr = table.Column<double>(type: "double precision", nullable: true),
                    RollCeQuantity = table.Column<long>(type: "bigint", nullable: true),
                    RollPeQuantity = table.Column<long>(type: "bigint", nullable: true),
                    RollVolPcr = table.Column<double>(type: "double precision", nullable: true),
                    DayCeVolume = table.Column<long>(type: "bigint", nullable: false),
                    DayPeVolume = table.Column<long>(type: "bigint", nullable: false),
                    UniverseTokenCount = table.Column<int>(type: "integer", nullable: false),
                    TokensObserved = table.Column<int>(type: "integer", nullable: false),
                    CeMicroDevTimeWeighted = table.Column<double>(type: "double precision", nullable: true),
                    CeOfi = table.Column<long>(type: "bigint", nullable: true),
                    PeMicroDevTimeWeighted = table.Column<double>(type: "double precision", nullable: true),
                    PeOfi = table.Column<long>(type: "bigint", nullable: true),
                    CeActivityPerSecond = table.Column<double>(type: "double precision", nullable: true),
                    PeActivityPerSecond = table.Column<double>(type: "double precision", nullable: true),
                    StraddleMidStart = table.Column<double>(type: "double precision", nullable: true),
                    StraddleMidEnd = table.Column<double>(type: "double precision", nullable: true),
                    StraddleDelta = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_options_supplemental_bars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_options_supplemental_bars_adaptive_sessions_Sessio~",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_options_supplemental_bars_SessionId_BarSeq_Metrics~",
                table: "adaptive_options_supplemental_bars",
                columns: new[] { "SessionId", "BarSeq", "MetricsVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adaptive_options_supplemental_bars");
        }
    }
}
