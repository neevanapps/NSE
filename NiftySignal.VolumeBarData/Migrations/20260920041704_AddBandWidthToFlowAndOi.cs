using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddBandWidthToFlowAndOi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptionOiBars_AsOfDate_BarVolumeThreshold",
                table: "OptionOiBars");

            migrationBuilder.DropIndex(
                name: "IX_OptionOiBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "OptionOiBars");

            migrationBuilder.DropIndex(
                name: "IX_OptionBandFlowBars_AsOfDate_BarVolumeThreshold",
                table: "OptionBandFlowBars");

            migrationBuilder.DropIndex(
                name: "IX_OptionBandFlowBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "OptionBandFlowBars");

            // 2026-09-20: every row already in these tables was populated under the OLD hardcoded
            // ATM+/-1 (BandWidth=3) convention -- default must be 3, not EF's own auto-generated 0,
            // or every existing Phase 1 row silently stops matching any BandWidth==3 query.
            migrationBuilder.AddColumn<int>(
                name: "BandWidth",
                table: "OptionOiBars",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "BandWidth",
                table: "OptionBandFlowBars",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.CreateIndex(
                name: "IX_OptionOiBars_AsOfDate_BarVolumeThreshold_BandWidth",
                table: "OptionOiBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BandWidth" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionOiBars_AsOfDate_BarVolumeThreshold_BandWidth_BarIndex",
                table: "OptionOiBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BandWidth", "BarIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptionBandFlowBars_AsOfDate_BarVolumeThreshold_BandWidth",
                table: "OptionBandFlowBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BandWidth" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionBandFlowBars_AsOfDate_BarVolumeThreshold_BandWidth_Ba~",
                table: "OptionBandFlowBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BandWidth", "BarIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptionOiBars_AsOfDate_BarVolumeThreshold_BandWidth",
                table: "OptionOiBars");

            migrationBuilder.DropIndex(
                name: "IX_OptionOiBars_AsOfDate_BarVolumeThreshold_BandWidth_BarIndex",
                table: "OptionOiBars");

            migrationBuilder.DropIndex(
                name: "IX_OptionBandFlowBars_AsOfDate_BarVolumeThreshold_BandWidth",
                table: "OptionBandFlowBars");

            migrationBuilder.DropIndex(
                name: "IX_OptionBandFlowBars_AsOfDate_BarVolumeThreshold_BandWidth_Ba~",
                table: "OptionBandFlowBars");

            migrationBuilder.DropColumn(
                name: "BandWidth",
                table: "OptionOiBars");

            migrationBuilder.DropColumn(
                name: "BandWidth",
                table: "OptionBandFlowBars");

            migrationBuilder.CreateIndex(
                name: "IX_OptionOiBars_AsOfDate_BarVolumeThreshold",
                table: "OptionOiBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionOiBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "OptionOiBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BarIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptionBandFlowBars_AsOfDate_BarVolumeThreshold",
                table: "OptionBandFlowBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionBandFlowBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "OptionBandFlowBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BarIndex" },
                unique: true);
        }
    }
}
