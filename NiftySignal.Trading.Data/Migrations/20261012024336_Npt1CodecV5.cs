using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Trading.Data.Migrations
{
    /// <inheritdoc />
    public partial class Npt1CodecV5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "trading",
                table: "schema_versions",
                keyColumn: "component",
                keyValue: "trading-engine",
                columns: new[] { "codec_version", "minimum_compatible_code" },
                values: new object[] { "trading-codec/5", "trading-engine/npt1-v1" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "trading",
                table: "schema_versions",
                keyColumn: "component",
                keyValue: "trading-engine",
                columns: new[] { "codec_version", "minimum_compatible_code" },
                values: new object[] { "trading-codec/4", "trading-engine/paperlive-v1" });
        }
    }
}
