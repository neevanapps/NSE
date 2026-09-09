using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NiftySignal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameIvSkewRawToOneSigma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IvSkewRaw",
                table: "score_snapshots",
                newName: "IvSkewOneSigmaRaw");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IvSkewOneSigmaRaw",
                table: "score_snapshots",
                newName: "IvSkewRaw");
        }
    }
}
