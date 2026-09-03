using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperTrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "paper_trades",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstrumentToken = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TradingSymbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    EntryTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    EntryScore = table.Column<double>(type: "double precision", nullable: false),
                    HasPartiallyBooked = table.Column<bool>(type: "boolean", nullable: false),
                    PartialExitTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PartialExitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ExitTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ExitReason = table.Column<int>(type: "integer", nullable: false),
                    GrossPnl = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    NetPnl = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    VixAtEntry = table.Column<double>(type: "double precision", nullable: true),
                    RulesetVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ScoreWeightsVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_trades", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_paper_trades_EntryTime",
                table: "paper_trades",
                column: "EntryTime");

            migrationBuilder.CreateIndex(
                name: "IX_paper_trades_ExitTime",
                table: "paper_trades",
                column: "ExitTime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paper_trades");
        }
    }
}
