using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCvdProxyGammaFlipStraddleRichness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CvdProxyRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CvdProxyZ",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GammaFlipLevel",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "StraddleRichnessRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "StraddleRichnessZ",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CvdProxyRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "CvdProxyZ",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "GammaFlipLevel",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "StraddleRichnessRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "StraddleRichnessZ",
                table: "score_snapshots");
        }
    }
}
