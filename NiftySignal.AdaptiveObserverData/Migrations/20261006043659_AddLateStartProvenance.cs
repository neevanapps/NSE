using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddLateStartProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EstimatorInputOpeningVolume",
                table: "adaptive_sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MedianOpeningSampleCount",
                table: "adaptive_sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ObservationStartUtc",
                table: "adaptive_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpeningCoverageMinutes",
                table: "adaptive_sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsesMedianOpeningFallback",
                table: "adaptive_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EstimatorInputOpeningVolume",
                table: "adaptive_sessions");

            migrationBuilder.DropColumn(
                name: "MedianOpeningSampleCount",
                table: "adaptive_sessions");

            migrationBuilder.DropColumn(
                name: "ObservationStartUtc",
                table: "adaptive_sessions");

            migrationBuilder.DropColumn(
                name: "OpeningCoverageMinutes",
                table: "adaptive_sessions");

            migrationBuilder.DropColumn(
                name: "UsesMedianOpeningFallback",
                table: "adaptive_sessions");
        }
    }
}
