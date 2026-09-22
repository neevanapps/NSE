using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.BacktestData.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CadenceContexts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AtmStrikeBySpot = table.Column<decimal>(type: "numeric", nullable: true),
                    AtmStrikeByFuture = table.Column<decimal>(type: "numeric", nullable: true),
                    AtmStrikeBySyntheticForward = table.Column<decimal>(type: "numeric", nullable: true),
                    NearestExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    HoursToExpiryCalendar = table.Column<double>(type: "double precision", nullable: true),
                    HoursToExpiryTrading = table.Column<double>(type: "double precision", nullable: true),
                    TicksObservedSpot = table.Column<int>(type: "integer", nullable: false),
                    TicksObservedFuture = table.Column<int>(type: "integer", nullable: false),
                    TicksObservedVix = table.Column<int>(type: "integer", nullable: false),
                    SpotCloseFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    SpotChangeFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    SpotOpenFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    SpotHighFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    SpotLowFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    SpotChangeForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    SpotOpenForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    SpotHighForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    SpotLowForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    FutureCloseFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    FutureChangeFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    FutureOpenFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    FutureHighFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    FutureLowFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    FutureChangeForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    FutureOpenForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    FutureHighForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    FutureLowForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    VixCloseFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    VixChangeFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    VixOpenFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    VixHighFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    VixLowFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    VixChangeForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    VixOpenForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    VixHighForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    VixLowForDay = table.Column<decimal>(type: "numeric", nullable: true),
                    FutureVolumeCumulativeDay = table.Column<long>(type: "bigint", nullable: true),
                    FutureVolumeDeltaThisCadence = table.Column<long>(type: "bigint", nullable: true),
                    FutureVwap = table.Column<double>(type: "double precision", nullable: true),
                    FutureOpenInterest = table.Column<long>(type: "bigint", nullable: true),
                    FutureOiChangeFromLastKnown = table.Column<long>(type: "bigint", nullable: true),
                    FutureOiChangeForDay = table.Column<long>(type: "bigint", nullable: true),
                    FutureTotalBidQty = table.Column<double>(type: "double precision", nullable: true),
                    FutureTotalAskQty = table.Column<double>(type: "double precision", nullable: true),
                    FutureDepthImbalanceFromLastCadence = table.Column<double>(type: "double precision", nullable: true),
                    FutureDepthImbalanceMean5Min = table.Column<double>(type: "double precision", nullable: true),
                    FutureDepthImbalanceMean15Min = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CadenceContexts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CadenceContexts_AsOfDate",
                table: "CadenceContexts",
                column: "AsOfDate");

            migrationBuilder.CreateIndex(
                name: "IX_CadenceContexts_Timestamp",
                table: "CadenceContexts",
                column: "Timestamp",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CadenceContexts");
        }
    }
}
