using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectionHealthAndVersionedCommentary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_adaptive_commentary_runtime_SessionId",
                table: "adaptive_commentary_runtime");

            migrationBuilder.AddColumn<double>(
                name: "MicroDevUsableSeconds",
                table: "adaptive_futures_supplemental_bars",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "TobUsableSeconds",
                table: "adaptive_futures_supplemental_bars",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "adaptive_projection_health",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    Component = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    Detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_projection_health", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_projection_health_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_commentary_runtime_SessionId_CommentaryVersion",
                table: "adaptive_commentary_runtime",
                columns: new[] { "SessionId", "CommentaryVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_projection_health_SessionId_Component_Version_BarS~",
                table: "adaptive_projection_health",
                columns: new[] { "SessionId", "Component", "Version", "BarSeq" },
                unique: true);

            // Event identity is now version-keyed (CommentaryVersion:SessionId:BarSeq:...). Existing rows keep their content; only the identity text gains its version prefix.
            migrationBuilder.Sql(
                "UPDATE adaptive_commentary_events SET \"EventIdentity\" = \"CommentaryVersion\" || ':' || \"EventIdentity\" WHERE \"EventIdentity\" NOT LIKE \"CommentaryVersion\" || ':%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE adaptive_commentary_events SET \"EventIdentity\" = substr(\"EventIdentity\", length(\"CommentaryVersion\") + 2) WHERE \"EventIdentity\" LIKE \"CommentaryVersion\" || ':%';");

            migrationBuilder.DropTable(
                name: "adaptive_projection_health");

            migrationBuilder.DropIndex(
                name: "IX_adaptive_commentary_runtime_SessionId_CommentaryVersion",
                table: "adaptive_commentary_runtime");

            migrationBuilder.DropColumn(
                name: "MicroDevUsableSeconds",
                table: "adaptive_futures_supplemental_bars");

            migrationBuilder.DropColumn(
                name: "TobUsableSeconds",
                table: "adaptive_futures_supplemental_bars");

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_commentary_runtime_SessionId",
                table: "adaptive_commentary_runtime",
                column: "SessionId",
                unique: true);
        }
    }
}
