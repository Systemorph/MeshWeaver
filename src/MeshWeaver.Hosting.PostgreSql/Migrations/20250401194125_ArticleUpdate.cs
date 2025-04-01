using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshWeaver.Hosting.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ArticleUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Created",
                table: "Articles");

            migrationBuilder.RenameColumn(
                name: "Extension",
                table: "Articles",
                newName: "VideoDescription");

            migrationBuilder.AddColumn<string>(
                name: "Transcript",
                table: "Articles",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Transcript",
                table: "Articles");

            migrationBuilder.RenameColumn(
                name: "VideoDescription",
                table: "Articles",
                newName: "Extension");

            migrationBuilder.AddColumn<DateTime>(
                name: "Created",
                table: "Articles",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }
    }
}
