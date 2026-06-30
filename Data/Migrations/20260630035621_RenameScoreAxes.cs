using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeedForge.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameScoreAxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SciFiPotential",
                table: "IdeaScores",
                newName: "Potential");

            migrationBuilder.RenameColumn(
                name: "FormulaFit",
                table: "IdeaScores",
                newName: "Suitability");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Potential",
                table: "IdeaScores",
                newName: "SciFiPotential");

            migrationBuilder.RenameColumn(
                name: "Suitability",
                table: "IdeaScores",
                newName: "FormulaFit");
        }
    }
}
