using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.BacktestData.Migrations
{
    /// <inheritdoc />
    public partial class WidenStrikeBandAndAddVannaCharm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CharmPerDay",
                table: "StrikeCadenceSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Vanna",
                table: "StrikeCadenceSnapshots",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CharmPerDay",
                table: "StrikeCadenceSnapshots");

            migrationBuilder.DropColumn(
                name: "Vanna",
                table: "StrikeCadenceSnapshots");
        }
    }
}
