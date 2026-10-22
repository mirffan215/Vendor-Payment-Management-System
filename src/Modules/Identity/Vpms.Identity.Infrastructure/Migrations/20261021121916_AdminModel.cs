using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AdminModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingRoleAssignment",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingRoleAssignment", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingRoleAssignment_TenantId",
                schema: "identity",
                table: "PendingRoleAssignment",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingRoleAssignment_TenantId_Email_RoleId_LegalEntityId",
                schema: "identity",
                table: "PendingRoleAssignment",
                columns: new[] { "TenantId", "Email", "RoleId", "LegalEntityId" },
                unique: true,
                filter: "[LegalEntityId] IS NOT NULL");
        
            migrationBuilder.EnableTenantRowLevelSecurity("identity", "PendingRoleAssignment");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("identity", "PendingRoleAssignment");

            migrationBuilder.DropTable(
                name: "PendingRoleAssignment",
                schema: "identity");
        }
    }
}
