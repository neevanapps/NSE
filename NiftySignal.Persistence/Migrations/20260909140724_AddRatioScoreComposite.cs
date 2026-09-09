using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRatioScoreComposite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "RatioCompositeScore",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RatioCompositeScoreRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RatioCompositeScoreRawInstant",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RatioIsWarmedUp",
                table: "score_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "RatioIvSkew25dRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RatioNotionalVolumeRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RatioResidualDifferenceRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RatioSizedOiFlowRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "RatioSpreadAtmRaw",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RatioWeightSetVersion",
                table: "score_snapshots",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RatioCompositeScore",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "RatioCompositeScoreRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "RatioCompositeScoreRawInstant",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "RatioIsWarmedUp",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "RatioIvSkew25dRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "RatioNotionalVolumeRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "RatioResidualDifferenceRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "RatioSizedOiFlowRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "RatioSpreadAtmRaw",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "RatioWeightSetVersion",
                table: "score_snapshots");
        }
    }
}
