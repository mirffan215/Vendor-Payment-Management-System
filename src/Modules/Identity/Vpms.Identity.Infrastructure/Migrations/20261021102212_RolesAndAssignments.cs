using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RolesAndAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Role",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Role", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignment",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssignedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RolePermission",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermission", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolePermission_Role_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "identity",
                        principalTable: "Role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Role_TenantId",
                schema: "identity",
                table: "Role",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Role_TenantId_Name",
                schema: "identity",
                table: "Role",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignment_TenantId",
                schema: "identity",
                table: "RoleAssignment",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignment_TenantId_UserId",
                schema: "identity",
                table: "RoleAssignment",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignment_TenantId_UserId_RoleId_LegalEntityId",
                schema: "identity",
                table: "RoleAssignment",
                columns: new[] { "TenantId", "UserId", "RoleId", "LegalEntityId" },
                unique: true,
                filter: "[LegalEntityId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermission_RoleId_Permission",
                schema: "identity",
                table: "RolePermission",
                columns: new[] { "RoleId", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RolePermission_TenantId",
                schema: "identity",
                table: "RolePermission",
                column: "TenantId");
        
            migrationBuilder.EnableTenantRowLevelSecurity("identity", "Role");
            migrationBuilder.EnableTenantRowLevelSecurity("identity", "RolePermission");
            migrationBuilder.EnableTenantRowLevelSecurity("identity", "RoleAssignment");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("identity", "RoleAssignment");
            migrationBuilder.DisableTenantRowLevelSecurity("identity", "RolePermission");
            migrationBuilder.DisableTenantRowLevelSecurity("identity", "Role");

            migrationBuilder.DropTable(
                name: "RoleAssignment",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "RolePermission",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "Role",
                schema: "identity");
        }
    }
}
