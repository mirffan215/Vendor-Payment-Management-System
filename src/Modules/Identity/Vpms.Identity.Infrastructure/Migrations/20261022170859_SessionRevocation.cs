using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vpms.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SessionRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SessionsValidFrom",
                schema: "identity",
                table: "User",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionsValidFrom",
                schema: "identity",
                table: "User");
        }
    }
}
