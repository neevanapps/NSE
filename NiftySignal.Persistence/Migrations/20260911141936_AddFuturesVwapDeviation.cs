using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFuturesVwapDeviation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "FuturesVwap",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FuturesVwapDeviationRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FuturesVwapDeviationZ",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FuturesVwap",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "FuturesVwapDeviationRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "FuturesVwapDeviationZ",
                table: "score_snapshots");
        }
    }
}
