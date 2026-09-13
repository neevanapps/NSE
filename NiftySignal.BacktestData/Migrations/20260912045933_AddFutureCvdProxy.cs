using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.BacktestData.Migrations
{
    /// <inheritdoc />
    public partial class AddFutureCvdProxy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FutureCvdProxyCumulativeDay",
                table: "CadenceContexts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FutureCvdProxyThisCadence",
                table: "CadenceContexts",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FutureCvdProxyCumulativeDay",
                table: "CadenceContexts");

            migrationBuilder.DropColumn(
                name: "FutureCvdProxyThisCadence",
                table: "CadenceContexts");
        }
    }
}
