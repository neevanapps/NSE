using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpreadRatio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SpreadRatioRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpreadRatioZ",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpreadRatioRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "SpreadRatioZ",
                table: "score_snapshots");
        }
    }
}
