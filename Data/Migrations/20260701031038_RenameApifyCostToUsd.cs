using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeedForge.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameApifyCostToUsd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ApifyCostUnits",
                table: "Videos",
                newName: "ApifyCostUsd");

            migrationBuilder.RenameColumn(
                name: "ApifyCostUnits",
                table: "Transcripts",
                newName: "ApifyCostUsd");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ApifyCostUsd",
                table: "Videos",
                newName: "ApifyCostUnits");

            migrationBuilder.RenameColumn(
                name: "ApifyCostUsd",
                table: "Transcripts",
                newName: "ApifyCostUnits");
        }
    }
}
