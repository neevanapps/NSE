using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddStrikeBookMetricsAndFuturesGrossOfi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "GrossOfi",
                table: "adaptive_option_strike_bars",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MicroDevTimeWeighted",
                table: "adaptive_option_strike_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NetOfi",
                table: "adaptive_option_strike_bars",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OfiTransitions",
                table: "adaptive_option_strike_bars",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TobTimeWeighted",
                table: "adaptive_option_strike_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TobUsableSeconds",
                table: "adaptive_option_strike_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GrossOfi",
                table: "adaptive_futures_supplemental_bars",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrossOfi",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "MicroDevTimeWeighted",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "NetOfi",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "OfiTransitions",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "TobTimeWeighted",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "TobUsableSeconds",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "GrossOfi",
                table: "adaptive_futures_supplemental_bars");
        }
    }
}
