using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddSpotAtmRuleAndStrikeGreeks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AtmRuleVersion",
                table: "adaptive_sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DeltaChange",
                table: "adaptive_option_strike_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DeltaEnd",
                table: "adaptive_option_strike_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DeltaStart",
                table: "adaptive_option_strike_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "VegaChange",
                table: "adaptive_option_strike_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "VegaEnd",
                table: "adaptive_option_strike_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "VegaStart",
                table: "adaptive_option_strike_bars",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AtmRuleVersion",
                table: "adaptive_sessions");

            migrationBuilder.DropColumn(
                name: "DeltaChange",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "DeltaEnd",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "DeltaStart",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "VegaChange",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "VegaEnd",
                table: "adaptive_option_strike_bars");

            migrationBuilder.DropColumn(
                name: "VegaStart",
                table: "adaptive_option_strike_bars");
        }
    }
}
