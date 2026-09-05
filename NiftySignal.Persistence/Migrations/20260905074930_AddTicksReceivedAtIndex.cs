using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicksReceivedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ticks_ReceivedAt",
                table: "ticks",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ticks_ReceivedAt",
                table: "ticks");
        }
    }
}
