using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionsDepth5Sidecar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adaptive_options_depth5_attestation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    MetricsVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenCount = table.Column<int>(type: "integer", nullable: false),
                    Digests = table.Column<string>(type: "text", nullable: false),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_options_depth5_attestation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_options_depth5_attestation_adaptive_sessions_Sessi~",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adaptive_options_depth5_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MetricsVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TradingSymbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Expiry = table.Column<DateOnly>(type: "date", nullable: false),
                    Strike = table.Column<double>(type: "double precision", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("PK_adaptive_options_depth5_bars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_options_depth5_bars_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_options_depth5_attestation_SessionId_BarSeq_Metric~",
                table: "adaptive_options_depth5_attestation",
                columns: new[] { "SessionId", "BarSeq", "MetricsVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_options_depth5_bars_SessionId_BarSeq_Token_Metrics~",
                table: "adaptive_options_depth5_bars",
                columns: new[] { "SessionId", "BarSeq", "Token", "MetricsVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adaptive_options_depth5_attestation");

            migrationBuilder.DropTable(
                name: "adaptive_options_depth5_bars");
        }
    }
}
