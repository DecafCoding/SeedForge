using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeedForge.Data.Migrations
{
    /// <inheritdoc />
    public partial class VideoProcessedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessedAtUtc",
                table: "Videos",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessedAtUtc",
                table: "Videos");
        }
    }
}
