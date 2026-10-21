using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityInfrastructure();

            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "IdentityProvider",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Authority = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ClientSecretRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    AutoProvision = table.Column<bool>(type: "bit", nullable: false),
                    AllowedEmailDomains = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProvider", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "User",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProvider_TenantId",
                schema: "identity",
                table: "IdentityProvider",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_User_TenantId",
                schema: "identity",
                table: "User",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_User_TenantId_ProviderId_ExternalSubject",
                schema: "identity",
                table: "User",
                columns: new[] { "TenantId", "ProviderId", "ExternalSubject" },
                unique: true);
        
            migrationBuilder.EnableTenantRowLevelSecurity("identity", "User");
            migrationBuilder.EnableTenantRowLevelSecurity("identity", "IdentityProvider");
            migrationBuilder.GrantAppRoleOnSchema("identity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("identity", "IdentityProvider");
            migrationBuilder.DisableTenantRowLevelSecurity("identity", "User");

            migrationBuilder.DropTable(
                name: "IdentityProvider",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "User",
                schema: "identity");
        }
    }
}
