using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddDepthImbalanceSumBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DepthImbalanceSumBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BarIndex = table.Column<int>(type: "integer", nullable: false),
                    BarVolumeThreshold = table.Column<long>(type: "bigint", nullable: false),
                    BandWidth = table.Column<int>(type: "integer", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    EndTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FutureClosePrice = table.Column<decimal>(type: "numeric", nullable: false),
                    RawQtyDiffSum = table.Column<double>(type: "double precision", nullable: true),
                    NotionalDiffSum = table.Column<double>(type: "double precision", nullable: true),
                    AtmToken = table.Column<string>(type: "text", nullable: false),
                    AtmStrike = table.Column<decimal>(type: "numeric", nullable: false),
                    DaysToExpiry = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepthImbalanceSumBars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DepthImbalanceSumBars_AsOfDate_BarVolumeThreshold_BandWidt~1",
                table: "DepthImbalanceSumBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BandWidth", "Side", "BarIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DepthImbalanceSumBars_AsOfDate_BarVolumeThreshold_BandWidth~",
                table: "DepthImbalanceSumBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BandWidth", "Side" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DepthImbalanceSumBars");
        }
    }
}
