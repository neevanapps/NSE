using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.VolumeBarData.Migrations
{
    /// <inheritdoc />
    public partial class AddOptionOiBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OptionOiBars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BarIndex = table.Column<int>(type: "integer", nullable: false),
                    BarVolumeThreshold = table.Column<long>(type: "bigint", nullable: false),
                    EndTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CallOiChangeNotional = table.Column<decimal>(type: "numeric", nullable: false),
                    PutOiChangeNotional = table.Column<decimal>(type: "numeric", nullable: false),
                    BuildupNet = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionOiBars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OptionOiBars_AsOfDate_BarVolumeThreshold",
                table: "OptionOiBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold" });

            migrationBuilder.CreateIndex(
                name: "IX_OptionOiBars_AsOfDate_BarVolumeThreshold_BarIndex",
                table: "OptionOiBars",
                columns: new[] { "AsOfDate", "BarVolumeThreshold", "BarIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OptionOiBars");
        }
    }
}
