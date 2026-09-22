using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddLivePaperTrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LivePaperTrades",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BarVolumeThreshold = table.Column<long>(type: "bigint", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    EntryBarIndex = table.Column<int>(type: "integer", nullable: false),
                    EntryTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EntryScore = table.Column<double>(type: "double precision", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    StrikePrice = table.Column<decimal>(type: "numeric", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    EntryDecisionTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExitBarIndex = table.Column<int>(type: "integer", nullable: true),
                    ExitTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExitReason = table.Column<string>(type: "text", nullable: true),
                    ExitPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    ExitDecisionTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LivePaperTrades", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LivePaperTrades_AsOfDate_BarVolumeThreshold",
                table: "LivePaperTrades",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_LivePaperTrades_AsOfDate_BarVolumeThreshold_EntryBarIndex",
                table: "LivePaperTrades",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "EntryBarIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LivePaperTrades");
        }
    }
}
