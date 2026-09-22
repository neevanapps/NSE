using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.BacktestData.Migrations
{
    /// <inheritdoc />
    public partial class AddFutureCvdProxyRollingNet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FutureCvdProxyNet15Min",
                table: "CadenceContexts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FutureCvdProxyNet5Min",
                table: "CadenceContexts",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FutureCvdProxyNet15Min",
                table: "CadenceContexts");

            migrationBuilder.DropColumn(
                name: "FutureCvdProxyNet5Min",
                table: "CadenceContexts");
        }
    }
}
