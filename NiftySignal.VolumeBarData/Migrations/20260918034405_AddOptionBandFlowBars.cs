using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionBandFlowBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OptionBandFlowBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BarIndex = table.Column<int>(type: "integer", nullable: false),
                    BarVolumeThreshold = table.Column<long>(type: "bigint", nullable: false),
                    EndTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CallNotionalVolume = table.Column<decimal>(type: "numeric", nullable: false),
                    PutNotionalVolume = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionBandFlowBars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptionBandFlowBars_AsOfDate_BarVolumeThreshold",
                table: "OptionBandFlowBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionBandFlowBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "OptionBandFlowBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BarIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OptionBandFlowBars");
        }
    }
}
