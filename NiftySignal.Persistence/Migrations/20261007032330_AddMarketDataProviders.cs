using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketDataProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSnapshot",
                table: "ticks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastTradeTimestamp",
                table: "ticks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "ticks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "NativeInstrumentKey",
                table: "instruments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "instruments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "market_data_days",
                columns: table => new
                {
                    TradeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_data_days", x => x.TradeDate);
                });

            migrationBuilder.CreateTable(
                name: "upstox_session",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Token = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsAnalyticsToken = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upstox_session", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticks_snapshot_token_received_at",
                table: "ticks",
                columns: new[] { "Token", "ReceivedAt" },
                filter: "\"IsSnapshot\" = TRUE");

            migrationBuilder.InsertData(
                table: "upstox_session",
                columns: new[] { "Id", "ExpiresAtUtc", "IsAnalyticsToken", "Token" },
                values: new object[] { 1, null, false, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ticks_snapshot_token_received_at",
                table: "ticks");

            migrationBuilder.DropTable(
                name: "market_data_days");

            migrationBuilder.DropTable(
                name: "upstox_session");

            migrationBuilder.DropColumn(
                name: "IsSnapshot",
                table: "ticks");

            migrationBuilder.DropColumn(
                name: "LastTradeTimestamp",
                table: "ticks");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "ticks");

            migrationBuilder.DropColumn(
                name: "NativeInstrumentKey",
                table: "instruments");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "instruments");
        }
    }
}
