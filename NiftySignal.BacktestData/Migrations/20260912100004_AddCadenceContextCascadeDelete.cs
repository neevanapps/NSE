using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.BacktestData.Migrations
{
    /// <inheritdoc />
    public partial class AddCadenceContextCascadeDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_StrikeBandCadenceSnapshots_CadenceContexts_CadenceContextId",
                table: "StrikeBandCadenceSnapshots",
                column: "CadenceContextId",
                principalTable: "CadenceContexts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StrikeCadenceSnapshots_CadenceContexts_CadenceContextId",
                table: "StrikeCadenceSnapshots",
                column: "CadenceContextId",
                principalTable: "CadenceContexts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StrikeBandCadenceSnapshots_CadenceContexts_CadenceContextId",
                table: "StrikeBandCadenceSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_StrikeCadenceSnapshots_CadenceContexts_CadenceContextId",
                table: "StrikeCadenceSnapshots");
        }
    }
}
