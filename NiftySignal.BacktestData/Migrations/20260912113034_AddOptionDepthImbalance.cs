using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.BacktestData.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionDepthImbalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DepthImbalanceFromLastCadence",
                table: "StrikeCadenceSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TotalAskQty",
                table: "StrikeCadenceSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TotalBidQty",
                table: "StrikeCadenceSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CallDepthImbalanceAvg",
                table: "StrikeBandCadenceSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PutDepthImbalanceAvg",
                table: "StrikeBandCadenceSnapshots",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DepthImbalanceFromLastCadence",
                table: "StrikeCadenceSnapshots");

            migrationBuilder.DropColumn(
                name: "TotalAskQty",
                table: "StrikeCadenceSnapshots");

            migrationBuilder.DropColumn(
                name: "TotalBidQty",
                table: "StrikeCadenceSnapshots");

            migrationBuilder.DropColumn(
                name: "CallDepthImbalanceAvg",
                table: "StrikeBandCadenceSnapshots");

            migrationBuilder.DropColumn(
                name: "PutDepthImbalanceAvg",
                table: "StrikeBandCadenceSnapshots");
        }
    }
}
