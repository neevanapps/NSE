using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddFuturesCrossoverLiveTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LivePaperTrades_AsOfDate_BarVolumeThreshold",
                table: "LivePaperTrades");

            migrationBuilder.DropIndex(
                name: "IX_LivePaperTrades_AsOfDate_BarVolumeThreshold_EntryBarIndex",
                table: "LivePaperTrades");

            migrationBuilder.DropIndex(
                name: "IX_LiveEntrySignals_AsOfDate_BarVolumeThreshold",
                table: "LiveEntrySignals");

            migrationBuilder.DropIndex(
                name: "IX_LiveEntrySignals_AsOfDate_BarVolumeThreshold_EntryBarIndex",
                table: "LiveEntrySignals");

            migrationBuilder.AddColumn<int>(
                name: "Strategy",
                table: "LivePaperTrades",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<double>(
                name: "EntryPercentile",
                table: "LiveEntrySignals",
                type: "double precision",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddColumn<int>(
                name: "Strategy",
                table: "LiveEntrySignals",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LiveFuturesCrossoverScoreBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BarIndex = table.Column<int>(type: "integer", nullable: false),
                    BarVolumeThreshold = table.Column<long>(type: "bigint", nullable: false),
                    EndTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RawScore = table.Column<double>(type: "double precision", nullable: true),
                    ScaledScore = table.Column<double>(type: "double precision", nullable: true),
                    FastMa = table.Column<double>(type: "double precision", nullable: true),
                    SlowMa = table.Column<double>(type: "double precision", nullable: true),
                    Diff = table.Column<double>(type: "double precision", nullable: true),
                    CrossedUp = table.Column<bool>(type: "boolean", nullable: false),
                    CrossedDown = table.Column<bool>(type: "boolean", nullable: false),
                    TobConfirmScore = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveFuturesCrossoverScoreBars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LivePaperTrades_AsOfDate_BarVolumeThreshold_Strategy",
                table: "LivePaperTrades",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "Strategy" });

            migrationBuilder.CreateIndex(
                name: "IX_LivePaperTrades_AsOfDate_BarVolumeThreshold_Strategy_EntryB~",
                table: "LivePaperTrades",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "Strategy", "EntryBarIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiveEntrySignals_AsOfDate_BarVolumeThreshold_Strategy",
                table: "LiveEntrySignals",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "Strategy" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveEntrySignals_AsOfDate_BarVolumeThreshold_Strategy_Entry~",
                table: "LiveEntrySignals",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "Strategy", "EntryBarIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiveFuturesCrossoverScoreBars_AsOfDate_BarVolumeThreshold",
                table: "LiveFuturesCrossoverScoreBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveFuturesCrossoverScoreBars_AsOfDate_BarVolumeThreshold_B~",
                table: "LiveFuturesCrossoverScoreBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BarIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiveFuturesCrossoverScoreBars");

            migrationBuilder.DropIndex(
                name: "IX_LivePaperTrades_AsOfDate_BarVolumeThreshold_Strategy",
                table: "LivePaperTrades");

            migrationBuilder.DropIndex(
                name: "IX_LivePaperTrades_AsOfDate_BarVolumeThreshold_Strategy_EntryB~",
                table: "LivePaperTrades");

            migrationBuilder.DropIndex(
                name: "IX_LiveEntrySignals_AsOfDate_BarVolumeThreshold_Strategy",
                table: "LiveEntrySignals");

            migrationBuilder.DropIndex(
                name: "IX_LiveEntrySignals_AsOfDate_BarVolumeThreshold_Strategy_Entry~",
                table: "LiveEntrySignals");

            migrationBuilder.DropColumn(
                name: "Strategy",
                table: "LivePaperTrades");

            migrationBuilder.DropColumn(
                name: "Strategy",
                table: "LiveEntrySignals");

            migrationBuilder.AlterColumn<double>(
                name: "EntryPercentile",
                table: "LiveEntrySignals",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "double precision",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LivePaperTrades_AsOfDate_BarVolumeThreshold",
                table: "LivePaperTrades",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_LivePaperTrades_AsOfDate_BarVolumeThreshold_EntryBarIndex",
                table: "LivePaperTrades",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "EntryBarIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiveEntrySignals_AsOfDate_BarVolumeThreshold",
                table: "LiveEntrySignals",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveEntrySignals_AsOfDate_BarVolumeThreshold_EntryBarIndex",
                table: "LiveEntrySignals",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "EntryBarIndex" },
                unique: true);
        }
    }
}
