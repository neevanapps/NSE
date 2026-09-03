using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstrumentTickDataGap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_gaps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_gaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "instruments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Exchange = table.Column<int>(type: "integer", nullable: false),
                    TradingSymbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InstrumentType = table.Column<int>(type: "integer", nullable: false),
                    OptionType = table.Column<int>(type: "integer", nullable: false),
                    StrikePrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Underlying = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LotSize = table.Column<int>(type: "integer", nullable: false),
                    TickSize = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instruments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Exchange = table.Column<int>(type: "integer", nullable: false),
                    ExchangeTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    OpenInterest = table.Column<long>(type: "bigint", nullable: true),
                    bid1_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    bid1_qty = table.Column<long>(type: "bigint", nullable: true),
                    bid2_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    bid2_qty = table.Column<long>(type: "bigint", nullable: true),
                    bid3_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    bid3_qty = table.Column<long>(type: "bigint", nullable: true),
                    bid4_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    bid4_qty = table.Column<long>(type: "bigint", nullable: true),
                    bid5_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    bid5_qty = table.Column<long>(type: "bigint", nullable: true),
                    ask1_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ask1_qty = table.Column<long>(type: "bigint", nullable: true),
                    ask2_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ask2_qty = table.Column<long>(type: "bigint", nullable: true),
                    ask3_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ask3_qty = table.Column<long>(type: "bigint", nullable: true),
                    ask4_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ask4_qty = table.Column<long>(type: "bigint", nullable: true),
                    ask5_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ask5_qty = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_data_gaps_EndedAt",
                table: "data_gaps",
                column: "EndedAt");

            migrationBuilder.CreateIndex(
                name: "IX_instruments_Exchange_Token_AsOfDate",
                table: "instruments",
                columns: new[] { "Exchange", "Token", "AsOfDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticks_Token_ExchangeTimestamp",
                table: "ticks",
                columns: new[] { "Token", "ExchangeTimestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_gaps");

            migrationBuilder.DropTable(
                name: "instruments");

            migrationBuilder.DropTable(
                name: "ticks");
        }
    }
}
