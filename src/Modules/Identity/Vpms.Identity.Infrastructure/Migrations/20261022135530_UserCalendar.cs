using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vpms.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UserCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Calendar",
                schema: "identity",
                table: "User",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "gregorian");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Calendar",
                schema: "identity",
                table: "User");
        }
    }
}
