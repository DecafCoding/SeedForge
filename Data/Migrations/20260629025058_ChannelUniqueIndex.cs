using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeedForge.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChannelUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UploadsPlaylistId",
                table: "Channels",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_YouTubeChannelId",
                table: "Channels",
                column: "YouTubeChannelId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Channels_YouTubeChannelId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "UploadsPlaylistId",
                table: "Channels");
        }
    }
}
