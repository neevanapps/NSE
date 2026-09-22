using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddDepthBandWidth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptionDepthBars_AsOfDate_BarVolumeThreshold",
                table: "OptionDepthBars");

            migrationBuilder.DropIndex(
                name: "IX_OptionDepthBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "OptionDepthBars");

            migrationBuilder.AddColumn<int>(
                name: "BandWidth",
                table: "OptionDepthBars",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_OptionDepthBars_AsOfDate_BarVolumeThreshold_BandWidth",
                table: "OptionDepthBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BandWidth" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionDepthBars_AsOfDate_BarVolumeThreshold_BandWidth_BarIn~",
                table: "OptionDepthBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BandWidth", "BarIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OptionDepthBars_AsOfDate_BarVolumeThreshold_BandWidth",
                table: "OptionDepthBars");

            migrationBuilder.DropIndex(
                name: "IX_OptionDepthBars_AsOfDate_BarVolumeThreshold_BandWidth_BarIn~",
                table: "OptionDepthBars");

            migrationBuilder.DropColumn(
                name: "BandWidth",
                table: "OptionDepthBars");

            migrationBuilder.CreateIndex(
                name: "IX_OptionDepthBars_AsOfDate_BarVolumeThreshold",
                table: "OptionDepthBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionDepthBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "OptionDepthBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BarIndex" },
                unique: true);
        }
    }
}
