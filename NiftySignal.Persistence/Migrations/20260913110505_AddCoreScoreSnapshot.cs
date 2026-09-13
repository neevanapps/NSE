using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoreScoreSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoreScoreSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DepthImbalanceRaw = table.Column<double>(type: "double precision", nullable: true),
                    ItmSkewRaw = table.Column<double>(type: "double precision", nullable: true),
                    FutureCvdNet5MinRaw = table.Column<double>(type: "double precision", nullable: true),
                    NotionalVolumeRatioRaw = table.Column<double>(type: "double precision", nullable: true),
                    GammaExposureRaw = table.Column<double>(type: "double precision", nullable: true),
                    TrendReversion15mRaw = table.Column<double>(type: "double precision", nullable: true),
                    BasisChangeRaw = table.Column<double>(type: "double precision", nullable: true),
                    OiChangeDiff15mRaw = table.Column<double>(type: "double precision", nullable: true),
                    DepthImbalanceSigned = table.Column<double>(type: "double precision", nullable: true),
                    ItmSkewSigned = table.Column<double>(type: "double precision", nullable: true),
                    FutureCvdNet5MinSigned = table.Column<double>(type: "double precision", nullable: true),
                    NotionalVolumeRatioSigned = table.Column<double>(type: "double precision", nullable: true),
                    GammaExposureSigned = table.Column<double>(type: "double precision", nullable: true),
                    TrendReversion15mSigned = table.Column<double>(type: "double precision", nullable: true),
                    BasisChangeSigned = table.Column<double>(type: "double precision", nullable: true),
                    OiChangeDiff15mSigned = table.Column<double>(type: "double precision", nullable: true),
                    CoreScoreRawInstant = table.Column<double>(type: "double precision", nullable: true),
                    CoreScoreRaw = table.Column<double>(type: "double precision", nullable: true),
                    CoreScore = table.Column<double>(type: "double precision", nullable: true),
                    IsWarmedUp = table.Column<bool>(type: "boolean", nullable: false),
                    WeightSetVersion = table.Column<string>(type: "text", nullable: true),
                    CoreScoreFast = table.Column<double>(type: "double precision", nullable: true),
                    CoreScoreSlow = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoreScoreSnapshots", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoreScoreSnapshots");
        }
    }
}
