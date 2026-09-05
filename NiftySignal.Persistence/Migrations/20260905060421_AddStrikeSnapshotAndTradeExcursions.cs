using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStrikeSnapshotAndTradeExcursions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SpotPrice",
                table: "score_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxAdverseExcursionPct",
                table: "paper_trades",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxFavourableExcursionPct",
                table: "paper_trades",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "strike_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StrikePrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    OptionType = table.Column<int>(type: "integer", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    VolumeDelta = table.Column<long>(type: "bigint", nullable: true),
                    OpenInterest = table.Column<long>(type: "bigint", nullable: true),
                    BidPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    AskPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    SpreadAbs = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    SpreadPctOfMid = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ImpliedVolatility = table.Column<double>(type: "double precision", nullable: true),
                    Delta = table.Column<double>(type: "double precision", nullable: true),
                    Gamma = table.Column<double>(type: "double precision", nullable: true),
                    ThetaPerDay = table.Column<double>(type: "double precision", nullable: true),
                    Vega = table.Column<double>(type: "double precision", nullable: true),
                    Rho = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_strike_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_strike_snapshots_ComputedAt",
                table: "strike_snapshots",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_strike_snapshots_Token_ComputedAt",
                table: "strike_snapshots",
                columns: new[] { "Token", "ComputedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "strike_snapshots");

            migrationBuilder.DropColumn(
                name: "SpotPrice",
                table: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "MaxAdverseExcursionPct",
                table: "paper_trades");

            migrationBuilder.DropColumn(
                name: "MaxFavourableExcursionPct",
                table: "paper_trades");
        }
    }
}
