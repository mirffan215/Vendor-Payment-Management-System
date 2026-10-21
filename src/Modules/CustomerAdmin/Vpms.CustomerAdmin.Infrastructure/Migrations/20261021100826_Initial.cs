using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Vpms.BuildingBlocks.Persistence;

#nullable disable

namespace Vpms.CustomerAdmin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureTenantSecurityInfrastructure();

            migrationBuilder.EnsureSchema(
                name: "customer");

            migrationBuilder.CreateTable(
                name: "LegalEntity",
                schema: "customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    BaseCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalEntity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenant",
                schema: "customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DefaultLocale = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenant", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntity_TenantId",
                schema: "customer",
                table: "LegalEntity",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntity_TenantId_Code",
                schema: "customer",
                table: "LegalEntity",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenant_Slug",
                schema: "customer",
                table: "Tenant",
                column: "Slug",
                unique: true);
        
            migrationBuilder.EnableTenantRowLevelSecurity("customer", "Tenant", "Id");
            migrationBuilder.EnableTenantRowLevelSecurity("customer", "LegalEntity");
            migrationBuilder.GrantAppRoleOnSchema("customer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity("customer", "LegalEntity");
            migrationBuilder.DisableTenantRowLevelSecurity("customer", "Tenant");

            migrationBuilder.DropTable(
                name: "LegalEntity",
                schema: "customer");

            migrationBuilder.DropTable(
                name: "Tenant",
                schema: "customer");
        }
    }
}
