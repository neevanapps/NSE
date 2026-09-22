using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionDepthBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OptionDepthBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BarIndex = table.Column<int>(type: "integer", nullable: false),
                    BarVolumeThreshold = table.Column<long>(type: "bigint", nullable: false),
                    EndTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CallBidQtyAvg = table.Column<double>(type: "double precision", nullable: true),
                    CallAskQtyAvg = table.Column<double>(type: "double precision", nullable: true),
                    PutBidQtyAvg = table.Column<double>(type: "double precision", nullable: true),
                    PutAskQtyAvg = table.Column<double>(type: "double precision", nullable: true),
                    CallTobBidQtyAvg = table.Column<double>(type: "double precision", nullable: true),
                    CallTobAskQtyAvg = table.Column<double>(type: "double precision", nullable: true),
                    PutTobBidQtyAvg = table.Column<double>(type: "double precision", nullable: true),
                    PutTobAskQtyAvg = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionDepthBars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptionDepthBars_AsOfDate_BarVolumeThreshold",
                table: "OptionDepthBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionDepthBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "OptionDepthBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BarIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OptionDepthBars");
        }
    }
}
