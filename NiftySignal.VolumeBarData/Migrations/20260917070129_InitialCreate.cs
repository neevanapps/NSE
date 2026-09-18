using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VolumeBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BarIndex = table.Column<int>(type: "integer", nullable: false),
                    BarVolumeThreshold = table.Column<long>(type: "bigint", nullable: false),
                    StartTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    OpenPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    HighPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    LowPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ClosePrice = table.Column<decimal>(type: "numeric", nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    OpenInterestAtClose = table.Column<long>(type: "bigint", nullable: true),
                    VwapAtClose = table.Column<double>(type: "double precision", nullable: true),
                    FutureCvdNet = table.Column<long>(type: "bigint", nullable: true),
                    FutureDepthImbalance = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumeBars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VolumeBars_AsOfDate_BarVolumeThreshold",
                table: "VolumeBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_VolumeBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "VolumeBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BarIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VolumeBars");
        }
    }
}
