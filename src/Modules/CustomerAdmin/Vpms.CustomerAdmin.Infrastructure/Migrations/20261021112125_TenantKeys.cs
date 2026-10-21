using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.CustomerAdmin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TenantKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantKey",
                schema: "customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    KekId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    WrappedKey = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantKey", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantKey_TenantId",
                schema: "customer",
                table: "TenantKey",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantKey_TenantId_Purpose_Version",
                schema: "customer",
                table: "TenantKey",
                columns: new[] { "TenantId", "Purpose", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_TenantKey_Active",
                schema: "customer",
                table: "TenantKey",
                columns: new[] { "TenantId", "Purpose" },
                unique: true,
                filter: "[Status] = 'Active'");
        
            migrationBuilder.EnableTenantRowLevelSecurity("customer", "TenantKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("customer", "TenantKey");

            migrationBuilder.DropTable(
                name: "TenantKey",
                schema: "customer");
        }
    }
}
