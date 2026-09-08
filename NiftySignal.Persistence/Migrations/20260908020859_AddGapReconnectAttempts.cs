using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGapReconnectAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastReason",
                table: "data_gaps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReconnectAttempts",
                table: "data_gaps",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastReason",
                table: "data_gaps");

            migrationBuilder.DropColumn(
                name: "ReconnectAttempts",
                table: "data_gaps");
        }
    }
}
