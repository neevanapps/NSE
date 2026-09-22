using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.BacktestData.Migrations
{
    /// <inheritdoc />
    public partial class AddStrikeCadenceAndBandTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StrikeBandCadenceSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CadenceContextId = table.Column<long>(type: "bigint", nullable: false),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CadenceMinutes = table.Column<int>(type: "integer", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DaysToExpiry = table.Column<int>(type: "integer", nullable: false),
                    BandDefinition = table.Column<string>(type: "text", nullable: false),
                    CallStrikeCount = table.Column<int>(type: "integer", nullable: false),
                    PutStrikeCount = table.Column<int>(type: "integer", nullable: false),
                    CallVolumeSum = table.Column<long>(type: "bigint", nullable: true),
                    PutVolumeSum = table.Column<long>(type: "bigint", nullable: true),
                    CallNotionalSum = table.Column<decimal>(type: "numeric", nullable: true),
                    PutNotionalSum = table.Column<decimal>(type: "numeric", nullable: true),
                    CallOiChangeSum = table.Column<long>(type: "bigint", nullable: true),
                    PutOiChangeSum = table.Column<long>(type: "bigint", nullable: true),
                    CallAvgIv = table.Column<double>(type: "double precision", nullable: true),
                    PutAvgIv = table.Column<double>(type: "double precision", nullable: true),
                    CallCvdProxyVolumeNet = table.Column<long>(type: "bigint", nullable: true),
                    PutCvdProxyVolumeNet = table.Column<long>(type: "bigint", nullable: true),
                    CallCvdProxyNotionalNet = table.Column<decimal>(type: "numeric", nullable: true),
                    PutCvdProxyNotionalNet = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrikeBandCadenceSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrikeCadenceSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CadenceContextId = table.Column<long>(type: "bigint", nullable: false),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DaysToExpiry = table.Column<int>(type: "integer", nullable: false),
                    StrikePrice = table.Column<decimal>(type: "numeric", nullable: false),
                    OptionType = table.Column<int>(type: "integer", nullable: false),
                    StrikeOffsetFromAtm = table.Column<int>(type: "integer", nullable: false),
                    MarkPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    BidPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    AskPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    OpenFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    HighFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    LowFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    CloseFromLastCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    VolumeDelta = table.Column<long>(type: "bigint", nullable: true),
                    NotionalDelta = table.Column<decimal>(type: "numeric", nullable: true),
                    OpenInterest = table.Column<long>(type: "bigint", nullable: true),
                    OpenInterestDelta = table.Column<long>(type: "bigint", nullable: true),
                    OiNotional = table.Column<decimal>(type: "numeric", nullable: true),
                    MarkPriceDelta = table.Column<decimal>(type: "numeric", nullable: true),
                    OiBuildup = table.Column<int>(type: "integer", nullable: true),
                    SpreadAbs = table.Column<decimal>(type: "numeric", nullable: true),
                    SpreadPctOfMid = table.Column<decimal>(type: "numeric", nullable: true),
                    CvdProxyVolumeThisCadence = table.Column<long>(type: "bigint", nullable: true),
                    CvdProxyNotionalThisCadence = table.Column<decimal>(type: "numeric", nullable: true),
                    ImpliedVolatility = table.Column<double>(type: "double precision", nullable: true),
                    Delta = table.Column<double>(type: "double precision", nullable: true),
                    Gamma = table.Column<double>(type: "double precision", nullable: true),
                    ThetaPerDay = table.Column<double>(type: "double precision", nullable: true),
                    Vega = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrikeCadenceSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StrikeBandCadenceSnapshots_AsOfDate",
                table: "StrikeBandCadenceSnapshots",
                column: "AsOfDate");

            migrationBuilder.CreateIndex(
                name: "IX_StrikeBandCadenceSnapshots_CadenceContextId_ExpiryDate_Band~",
                table: "StrikeBandCadenceSnapshots",
                columns: new[] { "CadenceContextId", "ExpiryDate", "BandDefinition", "CadenceMinutes" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StrikeCadenceSnapshots_AsOfDate",
                table: "StrikeCadenceSnapshots",
                column: "AsOfDate");

            migrationBuilder.CreateIndex(
                name: "IX_StrikeCadenceSnapshots_CadenceContextId_ExpiryDate_StrikePr~",
                table: "StrikeCadenceSnapshots",
                columns: new[] { "CadenceContextId", "ExpiryDate", "StrikePrice", "OptionType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StrikeBandCadenceSnapshots");

            migrationBuilder.DropTable(
                name: "StrikeCadenceSnapshots");
        }
    }
}
