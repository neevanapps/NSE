using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScoreSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "score_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OiBuildupNetRaw = table.Column<double>(type: "double precision", nullable: true),
                    PcrRaw = table.Column<double>(type: "double precision", nullable: true),
                    FuturesBasisRaw = table.Column<double>(type: "double precision", nullable: true),
                    IvSkewRaw = table.Column<double>(type: "double precision", nullable: true),
                    PriceMomentumRaw = table.Column<double>(type: "double precision", nullable: true),
                    DepthImbalanceRaw = table.Column<double>(type: "double precision", nullable: true),
                    OiBuildupNetZ = table.Column<double>(type: "double precision", nullable: true),
                    PcrZ = table.Column<double>(type: "double precision", nullable: true),
                    FuturesBasisZ = table.Column<double>(type: "double precision", nullable: true),
                    IvSkewZ = table.Column<double>(type: "double precision", nullable: true),
                    PriceMomentumZ = table.Column<double>(type: "double precision", nullable: true),
                    DepthImbalanceZ = table.Column<double>(type: "double precision", nullable: true),
                    CompositeScore = table.Column<double>(type: "double precision", nullable: true),
                    IsWarmedUp = table.Column<bool>(type: "boolean", nullable: false),
                    WeightSetVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_score_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_score_snapshots_ComputedAt",
                table: "score_snapshots",
                column: "ComputedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "score_snapshots");
        }
    }
}
