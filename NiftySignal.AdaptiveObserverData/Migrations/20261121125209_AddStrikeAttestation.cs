using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddStrikeAttestation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adaptive_strike_attestation",
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
                    table.PrimaryKey("PK_adaptive_strike_attestation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_strike_attestation_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_strike_attestation_SessionId_BarSeq_MetricsVersion",
                table: "adaptive_strike_attestation",
                columns: new[] { "SessionId", "BarSeq", "MetricsVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adaptive_strike_attestation");
        }
    }
}
