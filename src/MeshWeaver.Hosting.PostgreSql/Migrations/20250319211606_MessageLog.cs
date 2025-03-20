using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshWeaver.Hosting.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class MessageLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Delivery",
                table: "Messages");

            migrationBuilder.AddColumn<string>(
                name: "Message",
                table: "Messages",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Message",
                table: "Messages");

            migrationBuilder.AddColumn<string>(
                name: "Delivery",
                table: "Messages",
                type: "jsonb",
                nullable: true);
        }
    }
}
