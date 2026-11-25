using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddDepth5EventBasedColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AskNotionalEventSum",
                table: "adaptive_options_depth5_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BidNotionalEventSum",
                table: "adaptive_options_depth5_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FeedGapEvents",
                table: "adaptive_options_depth5_bars",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MicroDev5EventSum",
                table: "adaptive_options_depth5_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NoDepthEvents",
                table: "adaptive_options_depth5_bars",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RepeatSnapshotsIgnored",
                table: "adaptive_options_depth5_bars",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Tob5EventSum",
                table: "adaptive_options_depth5_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValidDepthEvents",
                table: "adaptive_options_depth5_bars",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValidMicroDev5Events",
                table: "adaptive_options_depth5_bars",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AskNotionalEventSum",
                table: "adaptive_futures_depth5_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BidNotionalEventSum",
                table: "adaptive_futures_depth5_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FeedGapEvents",
                table: "adaptive_futures_depth5_bars",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MicroDev5EventSum",
                table: "adaptive_futures_depth5_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NoDepthEvents",
                table: "adaptive_futures_depth5_bars",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RepeatSnapshotsIgnored",
                table: "adaptive_futures_depth5_bars",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Tob5EventSum",
                table: "adaptive_futures_depth5_bars",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValidDepthEvents",
                table: "adaptive_futures_depth5_bars",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ValidMicroDev5Events",
                table: "adaptive_futures_depth5_bars",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AskNotionalEventSum",
                table: "adaptive_options_depth5_bars");

            migrationBuilder.DropColumn(
                name: "BidNotionalEventSum",
                table: "adaptive_options_depth5_bars");

            migrationBuilder.DropColumn(
                name: "FeedGapEvents",
                table: "adaptive_options_depth5_bars");

            migrationBuilder.DropColumn(
                name: "MicroDev5EventSum",
                table: "adaptive_options_depth5_bars");

            migrationBuilder.DropColumn(
                name: "NoDepthEvents",
                table: "adaptive_options_depth5_bars");

            migrationBuilder.DropColumn(
                name: "RepeatSnapshotsIgnored",
                table: "adaptive_options_depth5_bars");

            migrationBuilder.DropColumn(
                name: "Tob5EventSum",
                table: "adaptive_options_depth5_bars");

            migrationBuilder.DropColumn(
                name: "ValidDepthEvents",
                table: "adaptive_options_depth5_bars");

            migrationBuilder.DropColumn(
                name: "ValidMicroDev5Events",
                table: "adaptive_options_depth5_bars");

            migrationBuilder.DropColumn(
                name: "AskNotionalEventSum",
                table: "adaptive_futures_depth5_bars");

            migrationBuilder.DropColumn(
                name: "BidNotionalEventSum",
                table: "adaptive_futures_depth5_bars");

            migrationBuilder.DropColumn(
                name: "FeedGapEvents",
                table: "adaptive_futures_depth5_bars");

            migrationBuilder.DropColumn(
                name: "MicroDev5EventSum",
                table: "adaptive_futures_depth5_bars");

            migrationBuilder.DropColumn(
                name: "NoDepthEvents",
                table: "adaptive_futures_depth5_bars");

            migrationBuilder.DropColumn(
                name: "RepeatSnapshotsIgnored",
                table: "adaptive_futures_depth5_bars");

            migrationBuilder.DropColumn(
                name: "Tob5EventSum",
                table: "adaptive_futures_depth5_bars");

            migrationBuilder.DropColumn(
                name: "ValidDepthEvents",
                table: "adaptive_futures_depth5_bars");

            migrationBuilder.DropColumn(
                name: "ValidMicroDev5Events",
                table: "adaptive_futures_depth5_bars");
        }
    }
}
