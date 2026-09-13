using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperTradeStrategyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: 0 = StrategyId.LegacyComposite (the enum's first member) -- this both
            // sets the column's DB-level default AND backfills every existing row to it in the
            // same ALTER TABLE, matching the live-wiring plan's own A6 requirement ("existing
            // historical PaperTrade rows backfilled to StrategyId = LegacyComposite, the only
            // strategy that has ever traded so far") without a separate UPDATE statement.
            migrationBuilder.AddColumn<int>(
                name: "StrategyId",
                table: "paper_trades",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StrategyId",
                table: "paper_trades");
        }
    }
}
