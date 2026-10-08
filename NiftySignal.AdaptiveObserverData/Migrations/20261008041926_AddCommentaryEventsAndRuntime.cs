using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentaryEventsAndRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adaptive_commentary_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    TradeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EventBias = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MarketRegime = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Lifecycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EvidenceAgreement = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PreviousEventId = table.Column<long>(type: "bigint", nullable: true),
                    PreviousBias = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BiasChanged = table.Column<bool>(type: "boolean", nullable: false),
                    PrimaryEvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    ConfirmationEvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    ContradictionEvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    DataQualityJson = table.Column<string>(type: "jsonb", nullable: false),
                    RenderedCommentary = table.Column<string>(type: "text", nullable: false),
                    ShouldNotifyTelegram = table.Column<bool>(type: "boolean", nullable: false),
                    NotificationReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CommentaryVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EventIdentity = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_commentary_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_commentary_events_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adaptive_commentary_runtime",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    LastEvaluatedBarSeq = table.Column<int>(type: "integer", nullable: false),
                    CurrentEventId = table.Column<long>(type: "bigint", nullable: true),
                    CurrentEventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CurrentBias = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CurrentRegime = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CurrentLifecycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastTelegramBarSeq = table.Column<int>(type: "integer", nullable: true),
                    ActiveBias = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ActiveStartedBarSeq = table.Column<int>(type: "integer", nullable: true),
                    ActiveAgreement = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ActiveSupportingFamilies = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ActivePhase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CommentaryVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_commentary_runtime", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_commentary_runtime_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_commentary_events_EventIdentity",
                table: "adaptive_commentary_events",
                column: "EventIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_commentary_events_SessionId_BarSeq",
                table: "adaptive_commentary_events",
                columns: new[] { "SessionId", "BarSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_commentary_events_SessionId_EventBias_OccurredAtUtc",
                table: "adaptive_commentary_events",
                columns: new[] { "SessionId", "EventBias", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_commentary_events_SessionId_EventType_OccurredAtUtc",
                table: "adaptive_commentary_events",
                columns: new[] { "SessionId", "EventType", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_commentary_events_TradeDate_OccurredAtUtc",
                table: "adaptive_commentary_events",
                columns: new[] { "TradeDate", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_commentary_runtime_SessionId",
                table: "adaptive_commentary_runtime",
                column: "SessionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adaptive_commentary_events");

            migrationBuilder.DropTable(
                name: "adaptive_commentary_runtime");
        }
    }
}
