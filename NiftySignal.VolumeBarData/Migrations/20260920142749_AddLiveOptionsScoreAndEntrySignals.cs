using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveOptionsScoreAndEntrySignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiveEntrySignals",
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
                    EntryPercentile = table.Column<double>(type: "double precision", nullable: false),
                    ExitBarIndex = table.Column<int>(type: "integer", nullable: true),
                    ExitTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExitReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveEntrySignals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LiveOptionsScoreBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BarIndex = table.Column<int>(type: "integer", nullable: false),
                    BarVolumeThreshold = table.Column<long>(type: "bigint", nullable: false),
                    EndTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SessionLeg = table.Column<string>(type: "text", nullable: false),
                    RawScore = table.Column<double>(type: "double precision", nullable: true),
                    ScaledScore = table.Column<double>(type: "double precision", nullable: true),
                    Percentile = table.Column<double>(type: "double precision", nullable: true),
                    MaxPainConfirmScore = table.Column<double>(type: "double precision", nullable: true),
                    MaxPainConfirmPasses = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveOptionsScoreBars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiveEntrySignals_AsOfDate_BarVolumeThreshold",
                table: "LiveEntrySignals",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveEntrySignals_AsOfDate_BarVolumeThreshold_EntryBarIndex",
                table: "LiveEntrySignals",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "EntryBarIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiveOptionsScoreBars_AsOfDate_BarVolumeThreshold",
                table: "LiveOptionsScoreBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveOptionsScoreBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "LiveOptionsScoreBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BarIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiveEntrySignals");

            migrationBuilder.DropTable(
                name: "LiveOptionsScoreBars");
        }
    }
}
