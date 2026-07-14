using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeedForge.Data.Migrations
{
    /// <inheritdoc />
    public partial class IdeaDisposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Disposition",
                table: "Ideas",
                type: "TEXT",
                nullable: false,
                defaultValue: "Undecided"); // hand-fixed: EF scaffolds "" for string columns, which no longer parses as IdeaDisposition

            migrationBuilder.AddColumn<DateTime>(
                name: "DispositionAtUtc",
                table: "Ideas",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Disposition",
                table: "Ideas");

            migrationBuilder.DropColumn(
                name: "DispositionAtUtc",
                table: "Ideas");
        }
    }
}
