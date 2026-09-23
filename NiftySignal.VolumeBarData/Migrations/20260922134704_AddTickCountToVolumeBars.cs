using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddTickCountToVolumeBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TickCount",
                table: "VolumeBars",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TickCount",
                table: "VolumeBars");
        }
    }
}
