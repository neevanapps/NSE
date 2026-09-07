using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperTradeQuantity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default 65 (1 lot) backfills the 3 trades already in prod from before this column
            // existed -- they all ran under ruleset "live-v1-2026-09-04", before LotsPerTrade was
            // introduced, when entry quantity was always exactly one lot (LotSize).
            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "paper_trades",
                type: "integer",
                nullable: false,
                defaultValue: 65);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "paper_trades");
        }
    }
}
