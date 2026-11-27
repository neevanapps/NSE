using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.AdaptiveObserverData.Migrations
{
    /// <inheritdoc />
    public partial class AddObserverRecoveryProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRecoveryProgressUtc",
                table: "adaptive_observer_runtime",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecoveryBarsReplayed",
                table: "adaptive_observer_runtime",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecoveryFailureCount",
                table: "adaptive_observer_runtime",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecoveryPersistedBarsAtStart",
                table: "adaptive_observer_runtime",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryPhase",
                table: "adaptive_observer_runtime",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecoveryReplayedThroughUtc",
                table: "adaptive_observer_runtime",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RecoveryTicksProcessed",
                table: "adaptive_observer_runtime",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRecoveryProgressUtc",
                table: "adaptive_observer_runtime");

            migrationBuilder.DropColumn(
                name: "RecoveryBarsReplayed",
                table: "adaptive_observer_runtime");

            migrationBuilder.DropColumn(
                name: "RecoveryFailureCount",
                table: "adaptive_observer_runtime");

            migrationBuilder.DropColumn(
                name: "RecoveryPersistedBarsAtStart",
                table: "adaptive_observer_runtime");

            migrationBuilder.DropColumn(
                name: "RecoveryPhase",
                table: "adaptive_observer_runtime");

            migrationBuilder.DropColumn(
                name: "RecoveryReplayedThroughUtc",
                table: "adaptive_observer_runtime");

            migrationBuilder.DropColumn(
                name: "RecoveryTicksProcessed",
                table: "adaptive_observer_runtime");
        }
    }
}
