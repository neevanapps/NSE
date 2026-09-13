using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.BacktestData.Migrations
{
    /// <inheritdoc />
    public partial class AddOiChangeNotional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OiChangeNotional",
                table: "StrikeCadenceSnapshots",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CallOiChangeNotionalSum",
                table: "StrikeBandCadenceSnapshots",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PutOiChangeNotionalSum",
                table: "StrikeBandCadenceSnapshots",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OiChangeNotional",
                table: "StrikeCadenceSnapshots");

            migrationBuilder.DropColumn(
                name: "CallOiChangeNotionalSum",
                table: "StrikeBandCadenceSnapshots");

            migrationBuilder.DropColumn(
                name: "PutOiChangeNotionalSum",
                table: "StrikeBandCadenceSnapshots");
        }
    }
}
