using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vpms.CustomerAdmin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TenantLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StatusChangedAt",
                schema: "customer",
                table: "Tenant",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                schema: "customer",
                table: "Tenant");
        }
    }
}
