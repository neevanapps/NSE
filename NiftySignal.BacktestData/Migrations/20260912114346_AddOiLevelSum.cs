using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.BacktestData.Migrations
{
    /// <inheritdoc />
    public partial class AddOiLevelSum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CallOiSum",
                table: "StrikeBandCadenceSnapshots",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PutOiSum",
                table: "StrikeBandCadenceSnapshots",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CallOiSum",
                table: "StrikeBandCadenceSnapshots");

            migrationBuilder.DropColumn(
                name: "PutOiSum",
                table: "StrikeBandCadenceSnapshots");
        }
    }
}
