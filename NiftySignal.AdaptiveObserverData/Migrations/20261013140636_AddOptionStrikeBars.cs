using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionStrikeBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adaptive_option_strike_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    BarSeq = table.Column<int>(type: "integer", nullable: false),
                    MetricsVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EndAvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Expiry = table.Column<DateOnly>(type: "date", nullable: false),
                    Strike = table.Column<double>(type: "double precision", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TradingSymbol = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PriceStart = table.Column<double>(type: "double precision", nullable: true),
                    PriceEnd = table.Column<double>(type: "double precision", nullable: true),
                    PriceChange = table.Column<double>(type: "double precision", nullable: true),
                    TotalQuantity = table.Column<long>(type: "bigint", nullable: false),
                    StrictBuy = table.Column<long>(type: "bigint", nullable: false),
                    StrictSell = table.Column<long>(type: "bigint", nullable: false),
                    StrictUnknown = table.Column<long>(type: "bigint", nullable: false),
                    StrictDelta = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedBuy = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedSell = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedUnknown = table.Column<long>(type: "bigint", nullable: false),
                    EnrichedDelta = table.Column<long>(type: "bigint", nullable: false),
                    TradeUpdates = table.Column<long>(type: "bigint", nullable: false),
                    OiStart = table.Column<long>(type: "bigint", nullable: true),
                    OiEnd = table.Column<long>(type: "bigint", nullable: true),
                    OiDelta = table.Column<long>(type: "bigint", nullable: true),
                    IvStart = table.Column<double>(type: "double precision", nullable: true),
                    IvEnd = table.Column<double>(type: "double precision", nullable: true),
                    IvChange = table.Column<double>(type: "double precision", nullable: true),
                    ActivityPerSecond = table.Column<double>(type: "double precision", nullable: true),
                    QuoteAgeEndSeconds = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adaptive_option_strike_bars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_adaptive_option_strike_bars_adaptive_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "adaptive_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_option_strike_bars_SessionId_BarSeq_Token_MetricsV~",
                table: "adaptive_option_strike_bars",
                columns: new[] { "SessionId", "BarSeq", "Token", "MetricsVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adaptive_option_strike_bars_SessionId_Expiry_Strike_Side_Ba~",
                table: "adaptive_option_strike_bars",
                columns: new[] { "SessionId", "Expiry", "Strike", "Side", "BarSeq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adaptive_option_strike_bars");
        }
    }
}
