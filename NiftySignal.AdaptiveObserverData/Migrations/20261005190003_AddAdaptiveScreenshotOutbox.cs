using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddAdaptiveScreenshotOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adaptive_screenshot_jobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: true),
                    TradeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TargetBarSeq = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TriggeredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ImageSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Caption = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    TelegramMessageId = table.Column<long>(type: "bigint", nullable: true),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_screenshot_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_screenshot_jobs_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_screenshot_jobs_SessionId",
                table: "adaptive_screenshot_jobs",
                column: "SessionId",
                unique: true,
                filter: "\"Kind\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_screenshot_jobs_SessionId_Kind_TargetBarSeq",
                table: "adaptive_screenshot_jobs",
                columns: new[] { "SessionId", "Kind", "TargetBarSeq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_screenshot_jobs_Status_NextAttemptUtc",
                table: "adaptive_screenshot_jobs",
                columns: new[] { "Status", "NextAttemptUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adaptive_screenshot_jobs");
        }
    }
}
