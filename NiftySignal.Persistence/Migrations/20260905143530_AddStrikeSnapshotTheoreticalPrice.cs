using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStrikeSnapshotTheoreticalPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MarkPrice",
                table: "strike_snapshots",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PriceVsTheoretical",
                table: "strike_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TheoreticalPrice",
                table: "strike_snapshots",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MarkPrice",
                table: "strike_snapshots");

            migrationBuilder.DropColumn(
                name: "PriceVsTheoretical",
                table: "strike_snapshots");

            migrationBuilder.DropColumn(
                name: "TheoreticalPrice",
                table: "strike_snapshots");
        }
    }
}
