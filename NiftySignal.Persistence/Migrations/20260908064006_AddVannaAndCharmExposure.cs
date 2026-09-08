using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVannaAndCharmExposure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CharmExposureRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CharmExposureZ",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "VannaExposureRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "VannaExposureZ",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CharmExposureRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "CharmExposureZ",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "VannaExposureRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "VannaExposureZ",
                table: "score_snapshots");
        }
    }
}
