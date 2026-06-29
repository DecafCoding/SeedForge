using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeedForge.Data.Migrations
{
    /// <inheritdoc />
    public partial class QueueMechanics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "Videos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptUtc",
                table: "Videos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "Videos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "ConceptJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptUtc",
                table: "ConceptJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "ConceptJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "NextAttemptUtc",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "ConceptJobs");

            migrationBuilder.DropColumn(
                name: "NextAttemptUtc",
                table: "ConceptJobs");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "ConceptJobs");
        }
    }
}
