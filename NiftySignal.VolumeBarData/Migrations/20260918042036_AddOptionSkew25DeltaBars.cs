using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionSkew25DeltaBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OptionSkew25DeltaBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BarIndex = table.Column<int>(type: "integer", nullable: false),
                    BarVolumeThreshold = table.Column<long>(type: "bigint", nullable: false),
                    EndTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SyntheticForward = table.Column<decimal>(type: "numeric", nullable: true),
                    Call25DeltaStrike = table.Column<decimal>(type: "numeric", nullable: true),
                    Call25DeltaIv = table.Column<double>(type: "double precision", nullable: true),
                    Put25DeltaStrike = table.Column<decimal>(type: "numeric", nullable: true),
                    Put25DeltaIv = table.Column<double>(type: "double precision", nullable: true),
                    SkewRatio = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionSkew25DeltaBars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptionSkew25DeltaBars_AsOfDate_BarVolumeThreshold",
                table: "OptionSkew25DeltaBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionSkew25DeltaBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "OptionSkew25DeltaBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BarIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OptionSkew25DeltaBars");
        }
    }
}
